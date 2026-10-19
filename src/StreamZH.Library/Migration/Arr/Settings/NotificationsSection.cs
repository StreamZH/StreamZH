// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using StreamZH.Data;
using StreamZH.Library.Notifications;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Connections (/notification) → Settings → Connect: Plex, Emby/Jellyfin, Kodi, Discord, Telegram, Pushover, ntfy, Gotify,
/// Email, Webhook and Apprise with the same triggers (grab, import, upgrade, rename, health). Matched by name. Passwords
/// and tokens the old app masks are needed again: the item asks for them (<see cref="MigrationItem.NeedsSecret"/>); without
/// them the connection is created switched off. Other kinds (Slack, Trakt, custom scripts …) are skipped.
/// </summary>
public sealed class NotificationsSection(string source) : ArrSettingsSection(source)
{
    /// <summary>Option: what Radarr's/Sonarr's "Emby / Jellyfin" connection becomes when its name does not say ("jellyfin" or "emby").</summary>
    public const string MediaBrowserOption = "notifications.mediaBrowser";

    private static readonly string[] DiscordGrab =
        ["overview", "rating", "genres", "quality", "group", "size", "links", "release", "poster", "fanart", "indexer", "customFormats", "customFormatScore", "tags"];

    private static readonly string[] DiscordImport =
        ["overview", "rating", "genres", "quality", "codecs", "group", "size", "languages", "subtitles", "links", "release", "poster", "fanart", "tags", "customFormats", "customFormatScore"];

    private static readonly string[] SonarrDiscordImport =
        ["overview", "rating", "genres", "quality", "codecs", "group", "size", "languages", "subtitles", "links", "release", "poster", "fanart", "customFormats", "customFormatScore"];

    public override string Key => "notifications";

    public override string Label => "Connections";

    public override string Description =>
        "Media servers and notifications (Plex, Jellyfin, Emby, Kodi, Discord, Telegram, Pushover, ntfy, Gotify, email, webhooks, Apprise) with their triggers. " +
        "Passwords and tokens must be entered again.";

    public override int Order => 400;

    public override bool DefaultSelected => false;

    protected override async Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken)
    {
        var plan = new SettingsPlan();
        var repository = reader.Service<ConnectionRepository>();
        foreach (var notification in await reader.GetListAsync("/notification", cancellationToken))
        {
            var id = ArrJson.Int(notification, "id") ?? 0;
            var key = ItemKey(id);
            var name = ArrJson.Str(notification, "name")?.Trim() is { Length: > 0 } n ? n : $"{reader.AppName} connection {id}";
            if (name.Length > ConnectionService.MaxNameLength)
            {
                name = name[..ConnectionService.MaxNameLength].TrimEnd();
            }

            var implementation = ArrJson.Str(notification, "implementation") ?? string.Empty;
            var mapping = Map(reader, notification, implementation, name);
            if (mapping is null)
            {
                var kind = ArrJson.Str(notification, "implementationName") ?? implementation;
                plan.Skip(key, name, $"StreamZH cannot send to {kind} yet.", kind);
                continue;
            }

            var type = ConnectCatalog.Find(mapping.Implementation)!;
            var triggers = Triggers(notification);
            var existing = repository.FindByName(name);
            if (existing is not null && !existing.Implementation.Equals(type.Key, StringComparison.Ordinal))
            {
                plan.Skip(key, name, $"StreamZH has a {ConnectCatalog.Find(existing.Implementation)?.Name ?? existing.Implementation} connection with this name.", type.Name);
                continue;
            }

            // Masked secrets: typed in the wizard (item key, or item key + "." + field for further ones), else kept from an
            // existing StreamZH connection, else missing.
            var missing = new List<string>();
            var needsSecret = false;
            foreach (var secret in mapping.MaskedSecrets)
            {
                var stored = existing is not null && new ConnectionSettings(existing.Settings).Text(secret) is not null;
                needsSecret |= !stored;
                var typed = reader.Context.Secret($"{key}.{secret}") ?? (secret == mapping.MaskedSecrets[0] ? reader.Context.Secret(key) : null);
                if (typed is not null)
                {
                    mapping.Settings[secret] = JsonSerializer.SerializeToElement(typed);
                }
                else if (!stored)
                {
                    missing.Add(secret);
                }
            }

            var input = new ConnectionInput(name, type.Key, ArrJson.Bool(notification, "enable", true), triggers, mapping.Settings);
            var errors = new Dictionary<string, string[]>();
            _ = ConnectionSettings.Normalize(type, mapping.Settings, existing?.Settings, errors);
            foreach (var secret in missing)
            {
                errors.Remove(secret);
            }

            var detail = $"{type.Name} · {On(triggers)}";
            if (errors.Count > 0)
            {
                plan.Skip(key, name, "StreamZH cannot use its settings: " + string.Join(" ", errors.Values.SelectMany(v => v)), detail);
                continue;
            }

            var labels = string.Join(", ", missing.Select(f => type.Fields.First(x => x.Name == f).Label.ToLowerInvariant()));
            var warnings = new List<string>(mapping.Notes);
            if (missing.Count > 0)
            {
                warnings.Add($"{reader.AppName} does not hand out the {labels}. Enter it here, or later in Settings → Connect; until then the connection stays off.");
            }

            var existingId = existing?.Id;
            if (existing is not null)
            {
                var normalized = ConnectionSettings.Normalize(type, mapping.Settings, existing.Settings, []);
                if (ConnectionRepository.SerializeSettings(normalized) == ConnectionRepository.SerializeSettings(existing.Settings)
                    && Filter(type, triggers) == existing.Triggers
                    && existing.Enable == (input.Enable && missing.Count == 0))
                {
                    plan.Add(new MigrationItem(
                        key, name, MigrationAction.Link, detail, "Already the same.", warnings.Count == 0 ? null : string.Join(" ", warnings), needsSecret));
                    continue;
                }
            }

            var action = existing is null ? MigrationAction.Create : MigrationAction.Update;
            var item = new MigrationItem(key, name, action, detail, Warning: warnings.Count == 0 ? null : string.Join(" ", warnings), NeedsSecret: needsSecret);
            plan.Add(item, _ => Save(reader, type, input, existingId, missing.Count > 0));
        }

        return plan;
    }

    /// <summary>Through the Connect service (full validation); with secrets missing straight to the store, switched off.</summary>
    private static void Save(ArrReader reader, ConnectType type, ConnectionInput input, int? existingId, bool missingSecret)
    {
        var service = reader.Service<ConnectionService>();
        if (!missingSecret)
        {
            if (existingId is { } id)
            {
                service.Update(id, input);
            }
            else
            {
                service.Create(input);
            }

            return;
        }

        var repository = reader.Service<ConnectionRepository>();
        var stored = existingId is { } sid ? repository.Find(sid) : null;
        var settings = ConnectionSettings.Normalize(type, input.Settings, stored?.Settings, []);
        var definition = new ConnectionDefinition(stored?.Id ?? 0, input.Name!.Trim(), type.Key, false, Filter(type, input.Triggers), settings);
        if (stored is null)
        {
            repository.Add(definition, DateTime.UtcNow);
        }
        else
        {
            repository.Update(definition);
        }
    }

    /// <summary>The triggers the type can react to (like the Connect service does).</summary>
    private static ConnectionTriggers Filter(ConnectType type, ConnectionTriggers t)
    {
        bool On(bool value, string trigger) => value && type.Triggers.Contains(trigger);
        return new ConnectionTriggers(
            On(t.OnGrab, ConnectTriggers.Grab),
            On(t.OnImport, ConnectTriggers.Import),
            On(t.OnUpgrade, ConnectTriggers.Upgrade),
            On(t.OnRename, ConnectTriggers.Rename),
            On(t.OnDownloadFailure, ConnectTriggers.DownloadFailure),
            On(t.OnHealthIssue, ConnectTriggers.HealthIssue),
            On(t.OnHealthRestored, ConnectTriggers.HealthRestored),
            t.IncludeHealthWarnings && (type.Triggers.Contains(ConnectTriggers.HealthIssue) || type.Triggers.Contains(ConnectTriggers.HealthRestored)));
    }

    /// <summary>Radarr/Sonarr triggers: onDownload is StreamZH's import.</summary>
    public static ConnectionTriggers Triggers(JsonElement notification) => new(
        OnGrab: ArrJson.Bool(notification, "onGrab"),
        OnImport: ArrJson.Bool(notification, "onDownload"),
        OnUpgrade: ArrJson.Bool(notification, "onUpgrade"),
        OnRename: ArrJson.Bool(notification, "onRename"),
        OnHealthIssue: ArrJson.Bool(notification, "onHealthIssue"),
        OnHealthRestored: ArrJson.Bool(notification, "onHealthRestored"),
        IncludeHealthWarnings: ArrJson.Bool(notification, "includeHealthWarnings"));

    private static string On(ConnectionTriggers t)
    {
        var on = new List<string>();
        if (t.OnGrab)
        {
            on.Add("grab");
        }

        if (t.OnImport)
        {
            on.Add("import");
        }

        if (t.OnUpgrade)
        {
            on.Add("upgrade");
        }

        if (t.OnRename)
        {
            on.Add("rename");
        }

        if (t.OnHealthIssue || t.OnHealthRestored)
        {
            on.Add("health");
        }

        return on.Count == 0 ? "no triggers" : string.Join(", ", on);
    }

    /// <summary>A Radarr/Sonarr connection as StreamZH settings, or null for kinds StreamZH does not have.</summary>
    public sealed class Mapping(string implementation)
    {
        public string Implementation { get; } = implementation;

        public Dictionary<string, JsonElement> Settings { get; } = new(StringComparer.Ordinal);

        /// <summary>StreamZH secret fields whose value the old app masked.</summary>
        public List<string> MaskedSecrets { get; } = [];

        public List<string> Notes { get; } = [];
    }

    public static Mapping? Map(ArrReader reader, JsonElement notification, string implementation, string name)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var radarr = reader.IsRadarr;
        JsonElement? F(string field) => ArrJson.Field(notification, field);
        Mapping m;

        void Text(string target, string field)
        {
            if (ArrJson.Text(F(field)) is { } value && value.Trim().Length > 0)
            {
                m.Settings[target] = JsonSerializer.SerializeToElement(value.Trim());
            }
        }

        void Secret(string target, string field)
        {
            var value = F(field);
            if (ArrJson.IsMasked(value))
            {
                m.MaskedSecrets.Add(target);
            }
            else
            {
                Text(target, field);
            }
        }

        void Number(string target, string field)
        {
            if (ArrJson.Int(F(field)) is { } value)
            {
                m.Settings[target] = JsonSerializer.SerializeToElement(value);
            }
        }

        void Flag(string target, string field)
        {
            if (F(field) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value)
            {
                m.Settings[target] = JsonSerializer.SerializeToElement(value.GetBoolean());
            }
        }

        void List(string target, string field)
        {
            var items = ArrJson.Strings(F(field));
            if (items.Count > 0)
            {
                m.Settings[target] = JsonSerializer.SerializeToElement(string.Join(",", items));
            }
        }

        void Path(string target, string field)
        {
            // The old app's path → StreamZH's (the media server's side stays as it is).
            if (ArrJson.Text(F(field))?.Trim() is { Length: > 0 } path)
            {
                m.Settings[target] = JsonSerializer.SerializeToElement(reader.Context.Paths.Map(path));
            }
        }

        void Select(string target, string value) => m.Settings[target] = JsonSerializer.SerializeToElement(value);

        string Url(bool ssl, string host, int? port, string? urlBase)
        {
            var scheme = ssl ? "https" : "http";
            var portPart = port is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var basePart = string.IsNullOrWhiteSpace(urlBase) ? string.Empty : "/" + urlBase.Trim().Trim('/');
            return $"{scheme}://{host.Trim()}{portPart}{basePart}";
        }

        switch (implementation)
        {
            case "PlexServer":
                m = new Mapping(ConnectCatalog.Plex);
                Text("host", "host");
                Number("port", "port");
                Flag("useSsl", "useSsl");
                Secret("authToken", "authToken");
                Flag("updateLibrary", "updateLibrary");
                Path("mapFrom", "mapFrom");
                Text("mapTo", "mapTo");
                if (ArrJson.Text(F("urlBase"))?.Trim() is { Length: > 0 })
                {
                    m.Notes.Add("StreamZH talks to Plex without a URL base.");
                }

                break;

            case "MediaBrowser":
            {
                var kind = name.Contains("emby", StringComparison.OrdinalIgnoreCase) ? ConnectCatalog.Emby
                    : name.Contains("jellyfin", StringComparison.OrdinalIgnoreCase) ? ConnectCatalog.Jellyfin
                    : reader.Context.Option(MediaBrowserOption, ConnectCatalog.Jellyfin) == ConnectCatalog.Emby ? ConnectCatalog.Emby : ConnectCatalog.Jellyfin;
                m = new Mapping(kind);
                if (ArrJson.Text(F("host"))?.Trim() is { Length: > 0 } host)
                {
                    Select("url", Url(ArrJson.Text(F("useSsl")) == "true", host, ArrJson.Int(F("port")), ArrJson.Text(F("urlBase"))));
                }

                Secret("apiKey", "apiKey");
                Flag("updateLibrary", "updateLibrary");
                if (kind == ConnectCatalog.Emby)
                {
                    Flag("sendNotifications", "notify");
                }

                Path("mapFrom", "mapFrom");
                Text("mapTo", "mapTo");
                m.Notes.Add(kind == ConnectCatalog.Emby
                    ? $"{reader.AppName}'s \"Emby / Jellyfin\" connection becomes an Emby connection."
                    : $"{reader.AppName}'s \"Emby / Jellyfin\" connection becomes a Jellyfin connection (choose Emby in the wizard if it is Emby).");
                break;
            }

            case "Xbmc":
                m = new Mapping(ConnectCatalog.Kodi);
                Text("host", "host");
                Number("port", "port");
                Flag("useSsl", "useSsl");
                Text("username", "username");
                Secret("password", "password");
                Flag("notify", "notify");
                Number("displayTime", "displayTime");
                Flag("updateLibrary", "updateLibrary");
                Flag("cleanLibrary", "cleanLibrary");
                Flag("alwaysUpdate", "alwaysUpdate");
                break;

            case "Discord":
                m = new Mapping(ConnectCatalog.Discord);
                Secret("webHookUrl", "webHookUrl");
                Text("username", "username");
                Text("avatar", "avatar");
                DiscordFields(m, "grabFields", F("grabFields"), DiscordGrab);
                DiscordFields(m, "importFields", F("importFields"), radarr ? DiscordImport : SonarrDiscordImport);
                break;

            case "Telegram":
                m = new Mapping(ConnectCatalog.Telegram);
                Secret("botToken", "botToken");
                Text("chatId", "chatId");
                Number("topicId", "topicId");
                Flag("sendSilently", "sendSilently");
                if (F("metadataLinks") is { ValueKind: JsonValueKind.Array } links)
                {
                    m.Settings["includeLinks"] = JsonSerializer.SerializeToElement(links.GetArrayLength() > 0);
                }

                break;

            case "Pushover":
                m = new Mapping(ConnectCatalog.Pushover);
                Secret("apiKey", "apiKey");
                Secret("userKey", "userKey");
                List("devices", "devices");
                if (ArrJson.Int(F("priority")) is { } priority)
                {
                    Select("priority", Math.Clamp(priority, -2, 2).ToString(CultureInfo.InvariantCulture));
                }

                Number("retry", "retry");
                Number("expire", "expire");
                Text("sound", "sound");
                break;

            case "Ntfy":
                m = new Mapping(ConnectCatalog.Ntfy);
                Text("serverUrl", "serverUrl");
                Secret("accessToken", "accessToken");
                Text("username", "userName");
                Secret("password", "password");
                if (ArrJson.Int(F("priority")) is { } ntfyPriority)
                {
                    Select("priority", Math.Clamp(ntfyPriority, 1, 5).ToString(CultureInfo.InvariantCulture));
                }

                List("topics", "topics");
                List("tags", "tags");
                Text("clickUrl", "clickUrl");
                break;

            case "Gotify":
                m = new Mapping(ConnectCatalog.Gotify);
                Text("server", "server");
                Secret("appToken", "appToken");
                if (ArrJson.Int(F("priority")) is { } gotifyPriority)
                {
                    Select("priority", gotifyPriority switch { <= 0 => "0", <= 3 => "2", <= 6 => "5", _ => "8" });
                }

                Flag("includePoster", radarr ? "includeMoviePoster" : "includeSeriesPoster");
                break;

            case "Email":
                m = new Mapping(ConnectCatalog.Email);
                Text("server", "server");
                Number("port", "port");
                if (ArrJson.Int(F("useEncryption")) is { } encryption)
                {
                    Select("useEncryption", encryption switch { 1 => "always", 2 => "never", _ => "preferred" });
                }
                else if (F("requireEncryption") is { ValueKind: JsonValueKind.True })
                {
                    Select("useEncryption", "always");
                }

                Text("username", "username");
                Secret("password", "password");
                Text("from", "from");
                List("to", "to");
                List("cc", "cc");
                List("bcc", "bcc");
                break;

            case "Webhook":
                m = new Mapping(ConnectCatalog.Webhook);
                Text("url", "url");
                if (ArrJson.Int(F("method")) is { } method)
                {
                    Select("method", method == 2 ? "PUT" : "POST");
                }

                Text("username", "username");
                Secret("password", "password");
                if (F("headers") is { ValueKind: JsonValueKind.Array } headers)
                {
                    var lines = headers.EnumerateArray()
                        .Select(h => (Key: ArrJson.Str(h, "key"), Value: ArrJson.Str(h, "value")))
                        .Where(h => !string.IsNullOrWhiteSpace(h.Key))
                        .Select(h => $"{h.Key!.Trim()}: {h.Value?.Trim()}")
                        .ToList();
                    if (lines.Count > 0)
                    {
                        Select("headers", string.Join("\n", lines));
                    }
                }

                break;

            case "Apprise":
                m = new Mapping(ConnectCatalog.Apprise);
                Text("serverUrl", "serverUrl");
                Text("configurationKey", "configurationKey");
                Secret("statelessUrls", "statelessUrls");
                if (ArrJson.Int(F("notificationType")) is { } appriseType)
                {
                    Select("notificationType", appriseType switch { 1 => "success", 2 => "warning", 3 => "failure", _ => "info" });
                }

                List("tags", "tags");
                Text("username", "authUsername");
                Secret("password", "authPassword");
                break;

            default:
                return null;
        }

        return m;
    }

    /// <summary>Discord embed fields: Radarr/Sonarr send enum numbers; StreamZH keeps the ones it can show.</summary>
    private static void DiscordFields(Mapping m, string target, JsonElement? value, string[] names)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array)
        {
            return;
        }

        var known = ConnectCatalog.DiscordFields.Select(f => f.Value).ToHashSet(StringComparer.Ordinal);
        var fields = array.EnumerateArray()
            .Select(v => ArrJson.Int(v) is { } i && i >= 0 && i < names.Length ? names[i] : ArrJson.Text(v))
            .OfType<string>()
            .Select(n => known.FirstOrDefault(k => k.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct()
            .ToArray();
        m.Settings[target] = JsonSerializer.SerializeToElement(fields);
    }
}
