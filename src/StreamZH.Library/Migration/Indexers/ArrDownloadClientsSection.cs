// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Library.Downloads.Torrent;

namespace StreamZH.Library.Migration.Indexers;

/// <summary>
/// The download clients and remote path mappings of Radarr or Sonarr. 0.11: qBittorrent, Transmission and Deluge become
/// StreamZH download clients (matched by kind, host and port, so Radarr and Sonarr sharing one client give one client and
/// a second run updates it). Radarr and Sonarr do not hand out passwords: a client with one asks for it
/// (<see cref="MigrationItem.NeedsSecret"/>); without it the client is added switched off. StreamZH uses its own
/// category ("streamzh") so the old apps and StreamZH can run side by side without importing each other's torrents.
/// Remote path mappings are taken over with the local path mapped like the library paths. Usenet clients still create
/// nothing: StreamZH downloads Usenet by itself (a SABnzbd client's <see cref="MigrationItem.Detail"/> is SABnzbd's
/// address, so the wizard can offer to take over SABnzbd with it).
/// </summary>
public sealed class ArrDownloadClientsSection(string source) : IMigrationSection
{
    public const string SectionKey = "downloadClients";

    public const string TakeOverSabnzbd = "StreamZH downloads by itself. Take over SABnzbd's servers and categories from SABnzbd (Settings → Migration → SABnzbd).";

    public const string UnsupportedTorrentClient = "StreamZH works with qBittorrent, Transmission and Deluge, or its own torrent engine.";

    private static readonly Dictionary<string, string> Implementations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["QBittorrent"] = DownloadClientDefinition.QBittorrent,
        ["Transmission"] = DownloadClientDefinition.Transmission,
        ["Deluge"] = DownloadClientDefinition.Deluge,
    };

    public string Source { get; } = source;

    public string Key => SectionKey;

    public string Label => "Download clients";

    public string Description =>
        "qBittorrent, Transmission and Deluge with their remote path mappings. Type in the passwords; the old app does not hand them out. Usenet downloaders are not needed: StreamZH downloads by itself.";

    public int Order => 361;

    public bool DefaultSelected => true;

    public bool Advanced => false;

    public static string ItemKey(int id) => string.Create(CultureInfo.InvariantCulture, $"{SectionKey}:{id}");

    public static string MappingKey(int id) => string.Create(CultureInfo.InvariantCulture, $"{SectionKey}:mapping:{id}");

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(context, cancellationToken);
        var warnings = new List<string>();
        if (plan.Any(p => p.Client is not null))
        {
            warnings.Add("StreamZH adds its torrents with its own category \"streamzh\", so it never touches the torrents of "
                + $"{(Source == MigrationSources.Sonarr ? "Sonarr" : "Radarr")} while both run.");
        }

        return new MigrationSectionPlan([.. plan.Select(p => p.Item)], warnings);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(context, cancellationToken);
        var service = context.Services.GetService<DownloadClientService>();
        var mappings = context.Services.GetService<RemotePathMappingRepository>();
        var done = 0;
        foreach (var entry in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(done++, plan.Count, entry.Item.Label);
            if (entry.Item.Action == MigrationAction.Skip)
            {
                run.Skipped(entry.Item.Label, entry.Item.Reason ?? TakeOverSabnzbd);
                continue;
            }

            if (context.IsExcluded(entry.Item.Key))
            {
                run.Skipped(entry.Item.Label, "Left out in the wizard.");
                continue;
            }

            try
            {
                if (entry.Client is { } client && service is not null)
                {
                    TakeOverClient(context, run, service, entry, client);
                }
                else if (entry.Mapping is { } mapping && mappings is not null)
                {
                    if (entry.Item.Action == MigrationAction.Link)
                    {
                        run.Linked(entry.Item.Label);
                    }
                    else
                    {
                        mappings.Add(mapping.Host, mapping.RemotePath, mapping.LocalPath);
                        run.Created(entry.Item.Label);
                    }
                }
            }
            catch (ValidationFailedException ex)
            {
                run.Failed(entry.Item.Label, string.Join(" ", ex.Errors.SelectMany(e => e.Value)));
            }
            catch (LibraryException ex)
            {
                run.Failed(entry.Item.Label, ex.Message);
            }
        }
    }

    private void TakeOverClient(MigrationContext context, MigrationSectionRun run, DownloadClientService service, PlanEntry entry, OldClient old)
    {
        var match = Match(service.All(), old);
        var secret = context.Secret(entry.Item.Key);
        var settings = new Dictionary<string, JsonElement>(match?.Settings ?? DownloadClientCatalog.Defaults(DownloadClientCatalog.Find(old.Implementation)!), StringComparer.Ordinal)
        {
            [DownloadClientCatalog.Host] = JsonSerializer.SerializeToElement(old.Host),
            [DownloadClientCatalog.Port] = JsonSerializer.SerializeToElement(old.Port),
            [DownloadClientCatalog.UseSsl] = JsonSerializer.SerializeToElement(old.UseSsl),
            [DownloadClientCatalog.UrlBase] = JsonSerializer.SerializeToElement(old.UrlBase),
            [DownloadClientCatalog.AddPaused] = JsonSerializer.SerializeToElement(old.AddPaused),
        };
        if (old.Implementation != DownloadClientDefinition.Deluge)
        {
            settings[DownloadClientCatalog.Username] = JsonSerializer.SerializeToElement(old.Username);
        }

        if (secret is not null)
        {
            settings[DownloadClientCatalog.Password] = JsonSerializer.SerializeToElement(secret);
        }
        else if (!old.PasswordMasked)
        {
            settings[DownloadClientCatalog.Password] = JsonSerializer.SerializeToElement(old.Password);
        }

        var missingSecret = entry.Item.NeedsSecret && secret is null;
        if (match is null)
        {
            var name = service.All().Any(c => string.Equals(c.Name, old.Name, StringComparison.OrdinalIgnoreCase))
                ? $"{old.Name} ({(Source == MigrationSources.Sonarr ? "Sonarr" : "Radarr")})"
                : old.Name;
            var created = service.Create(new DownloadClientDefinition(
                0, name, old.Implementation, DownloadProtocol.Torrent, old.Enable && !missingSecret, Math.Clamp(old.Priority, DownloadClientService.MinPriority, DownloadClientService.MaxPriority), settings)
            {
                RemoveCompleted = old.RemoveCompleted,
            });
            context.Ids.Set("downloadClient", old.Id, created.Id);
            run.Created(entry.Item.Label);
            if (missingSecret)
            {
                run.Warn("Added switched off: type in the password in Settings → Download clients, then switch it on.", entry.Item.Label);
            }

            return;
        }

        var updated = service.Update(match.Id, match with { Settings = settings, Enable = match.Enable && !(missingSecret && IsEmptySecret(match)) });
        context.Ids.Set("downloadClient", old.Id, updated.Id);
        run.Updated(entry.Item.Label);
    }

    private static bool IsEmptySecret(DownloadClientDefinition client) => string.IsNullOrEmpty(client.Text(DownloadClientCatalog.Password));

    private async Task<List<PlanEntry>> PlanAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var api = context.Services.GetRequiredService<ArrApiClient>();
        var service = context.Services.GetService<DownloadClientService>();
        var existing = service?.All() ?? [];
        var plan = new List<PlanEntry>();
        using (var document = await api.GetJsonAsync(Source, context.Connection, "/downloadclient", cancellationToken))
        {
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
                    {
                        plan.Add(PlanClient(entry, id, existing, service));
                    }
                }
            }
        }

        // Older versions without remote path mappings answer 404: nothing to take over then.
        try
        {
            using var mappings = await api.GetJsonAsync(Source, context.Connection, "/remotepathmapping", cancellationToken);
            if (mappings.RootElement.ValueKind == JsonValueKind.Array)
            {
                var stored = context.Services.GetService<RemotePathMappingRepository>()?.All() ?? [];
                foreach (var entry in mappings.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
                    {
                        plan.Add(PlanMapping(context, entry, id, stored));
                    }
                }
            }
        }
        catch (ValidationFailedException)
        {
        }

        return plan;
    }

    private PlanEntry PlanClient(JsonElement entry, int id, IReadOnlyList<DownloadClientDefinition> existing, DownloadClientService? service)
    {
        var fields = Fields(entry);
        var name = Text(entry, "name") ?? $"Download client {id.ToString(CultureInfo.InvariantCulture)}";
        var implementation = Text(entry, "implementation") ?? string.Empty;
        var enabled = !entry.TryGetProperty("enable", out var enable) || enable.ValueKind != JsonValueKind.False;
        var key = ItemKey(id);
        if (string.Equals(implementation, "Sabnzbd", StringComparison.OrdinalIgnoreCase))
        {
            var host = Field(fields, "host") ?? "localhost";
            var sabPort = Field(fields, "port") ?? "8080";
            var ssl = Field(fields, "useSsl") is "true" or "True" or "1";
            var urlBase = (Field(fields, "urlBase") ?? string.Empty).Trim().Trim('/');
            var category = Field(fields, Source == MigrationSources.Sonarr ? "tvCategory" : "movieCategory");
            var url = $"{(ssl ? "https" : "http")}://{host}:{sabPort}{(urlBase.Length > 0 ? "/" + urlBase : string.Empty)}";
            var label = $"SABnzbd at {host}:{sabPort}{(string.IsNullOrWhiteSpace(category) ? string.Empty : $", category {category}")}{(enabled ? string.Empty : " (switched off)")}";
            return new PlanEntry(new MigrationItem(key, label, MigrationAction.Skip, url, TakeOverSabnzbd), null, null);
        }

        var protocol = Text(entry, "protocol") ?? string.Empty;
        if (!Implementations.TryGetValue(implementation, out var ours))
        {
            return new PlanEntry(new MigrationItem(
                key,
                $"{name} ({implementation})",
                MigrationAction.Skip,
                null,
                protocol == "torrent" ? UnsupportedTorrentClient : "StreamZH downloads by itself with its Usenet servers (Settings → Usenet)."), null, null);
        }

        if (service is null || !service.Schema().Any(i => i.Implementation == ours))
        {
            return new PlanEntry(new MigrationItem(key, $"{name} ({implementation})", MigrationAction.Skip, null, "Torrent clients are not available in this installation."), null, null);
        }

        var defaults = DownloadClientCatalog.Find(ours)!;
        var password = Field(fields, "password") ?? string.Empty;
        var old = new OldClient(
            id,
            name,
            ours,
            enabled,
            entry.TryGetProperty("priority", out var p) && p.TryGetInt32(out var priority) ? priority : 1,
            !entry.TryGetProperty("removeCompletedDownloads", out var rc) || rc.ValueKind != JsonValueKind.False,
            (Field(fields, "host") ?? "localhost").Trim(),
            int.TryParse(Field(fields, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : defaults.Field(DownloadClientCatalog.Port)!.Default.GetInt32(),
            Field(fields, "useSsl") is "true" or "True" or "1",
            Field(fields, "urlBase") ?? (defaults.Field(DownloadClientCatalog.UrlBase)?.Default.GetString() ?? string.Empty),
            Field(fields, "username") ?? string.Empty,
            password == DownloadClientCatalog.Masked ? string.Empty : password,
            password == DownloadClientCatalog.Masked,
            Field(fields, "addPaused") is "true" or "True" || Field(fields, "initialState") == "2");

        var match = Match(existing, old);
        var needsSecret = old.PasswordMasked && (match is null || IsEmptySecret(match));
        var detail = $"{defaults.Name} at {old.Host}:{old.Port.ToString(CultureInfo.InvariantCulture)}{(old.UseSsl ? " (SSL)" : string.Empty)}{(enabled ? string.Empty : " · switched off")}";
        if (match is not null)
        {
            detail += $" · updates \"{match.Name}\"";
        }

        string? warning = needsSecret
            ? match is null ? "Type in the password. Without it the client is added switched off." : "Type in the password; StreamZH has none stored for this client yet."
            : null;
        return new PlanEntry(
            new MigrationItem(key, $"{name} ({defaults.Name})", match is null ? MigrationAction.Create : MigrationAction.Update, detail, Warning: warning, NeedsSecret: needsSecret),
            old,
            null);
    }

    private static PlanEntry PlanMapping(MigrationContext context, JsonElement entry, int id, IReadOnlyList<RemotePathMapping> stored)
    {
        var host = (Text(entry, "host") ?? string.Empty).Trim();
        var remote = (Text(entry, "remotePath") ?? string.Empty).Trim();
        var oldLocal = (Text(entry, "localPath") ?? string.Empty).Trim();
        var key = MappingKey(id);
        context.Paths.Use(oldLocal);
        var local = context.Paths.Map(oldLocal);
        var label = $"Remote path {host}: {remote} → {local}";
        if (host.Length == 0 || remote.Length == 0 || local.Length == 0)
        {
            return new PlanEntry(new MigrationItem(key, label, MigrationAction.Skip, null, "Incomplete mapping."), null, null);
        }

        if (!Directory.Exists(local))
        {
            return new PlanEntry(new MigrationItem(key, label, MigrationAction.Skip, null,
                $"{local} does not exist on this computer. Add a path mapping for {oldLocal} in the wizard, or add the remote path mapping later."), null, null);
        }

        var same = stored.FirstOrDefault(m => string.Equals(m.Host, host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.RemotePath.TrimEnd('/', '\\'), remote.TrimEnd('/', '\\'), StringComparison.Ordinal));
        if (same is not null)
        {
            return new PlanEntry(new MigrationItem(key, label, MigrationAction.Link, $"exists as {same.Host}: {same.RemotePath} → {same.LocalPath}", "Already set up."), null, null);
        }

        return new PlanEntry(new MigrationItem(key, label, MigrationAction.Create), null, new RemotePathMapping(0, host, WithSeparator(remote), WithSeparator(local)));
    }

    private static string WithSeparator(string path) =>
        path.EndsWith('/') || path.EndsWith('\\') ? path : path + (path.Contains('\\', StringComparison.Ordinal) && !path.StartsWith('/') ? '\\' : '/');

    private static DownloadClientDefinition? Match(IReadOnlyList<DownloadClientDefinition> existing, OldClient old) =>
        existing.FirstOrDefault(c => string.Equals(c.Implementation, old.Implementation, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Text(DownloadClientCatalog.Host)?.Trim(), old.Host, StringComparison.OrdinalIgnoreCase)
            && c.Number(DownloadClientCatalog.Port) == old.Port);

    private static Dictionary<string, JsonElement> Fields(JsonElement entry)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (entry.TryGetProperty("fields", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in list.EnumerateArray())
            {
                if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    fields[name.GetString()!] = field.TryGetProperty("value", out var value) ? value.Clone() : default;
                }
            }
        }

        return fields;
    }

    private static string? Field(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record OldClient(
        int Id,
        string Name,
        string Implementation,
        bool Enable,
        int Priority,
        bool RemoveCompleted,
        string Host,
        int Port,
        bool UseSsl,
        string UrlBase,
        string Username,
        string Password,
        bool PasswordMasked,
        bool AddPaused);

    private sealed record PlanEntry(MigrationItem Item, OldClient? Client, RemotePathMapping? Mapping);
}
