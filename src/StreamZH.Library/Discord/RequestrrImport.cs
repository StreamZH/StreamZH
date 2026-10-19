// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only
// Portions based on Requestrr (https://github.com/thomst08/requestrr, GPL-3.0): the shape of config/settings.json
// (ChatClients.Discord, TvShows.Restrictions, Movies/TvShows.Client) and the meaning of its values.

using System.Text.Json;

namespace StreamZH.Library.Discord;

/// <summary>One setting the import would change.</summary>
/// <param name="Field">camelCase name of the Discord setting.</param>
public sealed record RequestrrImportRow(string Field, string Label, string Current, string Imported);

/// <param name="Change">What applying saves (null fields keep the current value).</param>
/// <param name="HasToken">The file has a bot token (never shown); it is only taken over when asked to.</param>
public sealed record RequestrrImportPreview(
    DiscordSettingsChange Change, IReadOnlyList<RequestrrImportRow> Rows, IReadOnlyList<string> Warnings, bool HasToken, string? Version);

public sealed class RequestrrImportException(string message) : Exception(message);

/// <summary>
/// Reads Requestrr's config/settings.json and maps its Discord settings onto Settings → Discord. Requestrr has no user
/// accounts, so everybody who used it becomes a guest here: guests are allowed and Requestrr's movie and TV roles become
/// the roles guests need. Download clients, Overseerr/Ombi and Requestrr's own login are not taken over.
/// </summary>
public static class RequestrrImport
{
    public const int MaxLength = 1024 * 1024;

    public static RequestrrImportPreview Preview(string json, DiscordSettingsValues current, bool takeToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new RequestrrImportException("Paste the content of Requestrr's config/settings.json or choose the file.");
        }

        if (json.Length > MaxLength)
        {
            throw new RequestrrImportException("This file is too large for a Requestrr settings.json.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            throw new RequestrrImportException("This is not valid JSON. Paste the whole content of Requestrr's config/settings.json.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Get(root, "ChatClients") is not { ValueKind: JsonValueKind.Object } chatClients
                || Get(chatClients, "Discord") is not { ValueKind: JsonValueKind.Object } discord)
            {
                throw new RequestrrImportException("No Discord settings found. This does not look like Requestrr's config/settings.json.");
            }

            var warnings = new List<string>();
            var version = Text(Get(root, "Version"));

            var token = Text(Get(discord, "BotToken"));
            var hasToken = token is { Length: > 0 };
            var clientId = Text(Get(discord, "ClientId"));
            var status = Text(Get(discord, "StatusMessage"));
            var channels = Ids(Get(discord, "MonitoredChannels"), "monitored channel", warnings);
            var movieRoles = Ids(Get(discord, "MovieRoles"), "movie role", warnings);
            var tvRoles = Ids(Get(discord, "TvShowRoles"), "TV role", warnings);
            var directMessages = Bool(Get(discord, "EnableRequestsThroughDirectMessages"));
            var notifyRequesters = Bool(Get(discord, "AutomaticallyNotifyRequesters")) ?? true;
            var mode = Text(Get(discord, "NotificationMode")) ?? "PrivateMessages";
            var notificationChannels = Ids(Get(discord, "NotificationChannels"), "notification channel", warnings);

            var roles = movieRoles.Concat(tvRoles).Distinct(StringComparer.Ordinal).ToList();
            if (movieRoles.Count > 0 && tvRoles.Count > 0 && !movieRoles.Order().SequenceEqual(tvRoles.Order()))
            {
                warnings.Add("Requestrr had different roles for movies and TV. StreamZH has one list: members with any of these roles may request both.");
            }

            var dm = notifyRequesters && mode.Equals("PrivateMessages", StringComparison.OrdinalIgnoreCase);
            string? notifyChannel = null;
            if (notifyRequesters && mode.Equals("Channels", StringComparison.OrdinalIgnoreCase))
            {
                notifyChannel = notificationChannels.FirstOrDefault();
                if (notificationChannels.Count > 1)
                {
                    warnings.Add($"Requestrr posted to {notificationChannels.Count} notification channels; StreamZH posts to one (the first).");
                }
            }

            var movieCommand = Client(root, "Movies") is "disabled" ? string.Empty : null;
            var tvCommand = Client(root, "TvShows") is "disabled" ? string.Empty : null;
            if (Client(root, "Music") is { } music && music != "disabled")
            {
                warnings.Add("Music requests from Requestrr are not taken over; StreamZH does not request music.");
            }

            var seasons = Text(Get(Get(root, "TvShows"), "Restrictions")) switch
            {
                { } r when r.Equals("AllSeasons", StringComparison.OrdinalIgnoreCase) => DiscordSeasonModes.All,
                { } r when r.Equals("SingleSeason", StringComparison.OrdinalIgnoreCase) => DiscordSeasonModes.Single,
                { } r when r.Equals("None", StringComparison.OrdinalIgnoreCase) => DiscordSeasonModes.Choose,
                _ => null,
            };

            if (clientId is { Length: > 0 } && !DiscordBotSettings.IsSnowflake(clientId))
            {
                warnings.Add("Requestrr's client id is not a Discord id and is left out.");
                clientId = null;
            }

            if (status is { Length: > DiscordBotSettings.MaxStatusTextLength })
            {
                status = status[..DiscordBotSettings.MaxStatusTextLength];
            }

            if (hasToken && takeToken && !DiscordBotSettings.LooksLikeToken(token!))
            {
                warnings.Add("The bot token in the file does not look like a Discord bot token and is left out.");
                takeToken = false;
            }

            if (hasToken && takeToken)
            {
                warnings.Add("Stop Requestrr before you switch the StreamZH bot on: two programs with the same token both answer every command.");
            }
            else if (hasToken)
            {
                warnings.Add("The bot token is not taken over. Tick \"Take over the bot token\" to use Requestrr's bot, or create a new bot for StreamZH.");
            }

            warnings.Add("Requestrr has no user accounts, so its users request as guests (approval needed). Linked StreamZH accounts use their own permissions.");

            var change = new DiscordSettingsChange
            {
                BotToken = hasToken && takeToken ? token : null,
                ApplicationId = clientId,
                ChannelIds = channels,
                AllowGuests = true,
                GuestRoleIds = roles,
                NotifyChannelId = notifyChannel ?? string.Empty,
                DmRequesters = dm,
                AllowDirectMessages = directMessages,
                MovieCommand = movieCommand,
                TvCommand = tvCommand,
                SeriesSeasons = seasons,
                StatusText = status,
            };

            var rows = new List<RequestrrImportRow>();
            if (change.BotToken is not null)
            {
                rows.Add(new("botToken", "Bot token", current.HasToken ? "saved" : "none", "Requestrr's token"));
            }

            Row(rows, "applicationId", "Application id", current.ApplicationId, change.ApplicationId);
            Row(rows, "channelIds", "Request channels", List(current.ChannelIds, "every channel"), List(channels, "every channel"));
            Row(rows, "allowGuests", "Unlinked users may request", YesNo(current.AllowGuests), YesNo(true));
            Row(rows, "guestRoleIds", "Roles guests need", List(current.GuestRoleIds, "any member"), List(roles, "any member"));
            Row(rows, "dmRequesters", "Direct message to requesters", YesNo(current.DmRequesters), YesNo(dm));
            Row(rows, "notifyChannelId", "Notify channel", current.NotifyChannelId ?? "none", notifyChannel ?? "none");
            if (directMessages is { } allowDm)
            {
                Row(rows, "allowDirectMessages", "Requests in direct messages", YesNo(current.AllowDirectMessages), YesNo(allowDm));
            }

            if (movieCommand is not null)
            {
                Row(rows, "movieCommand", "Movie command", Command(current.MovieCommand), Command(movieCommand));
            }

            if (tvCommand is not null)
            {
                Row(rows, "tvCommand", "Series command", Command(current.TvCommand), Command(tvCommand));
            }

            if (seasons is not null)
            {
                Row(rows, "seriesSeasons", "Series seasons", current.SeriesSeasons, seasons);
            }

            if (status is not null)
            {
                Row(rows, "statusText", "Bot status text", current.StatusText, status);
            }

            return new RequestrrImportPreview(change, rows, warnings, hasToken, version);
        }
    }

    private static void Row(List<RequestrrImportRow> rows, string field, string label, string? current, string? imported)
    {
        if (imported is not null && current != imported)
        {
            rows.Add(new RequestrrImportRow(field, label, current ?? "none", imported.Length == 0 ? "none" : imported));
        }
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Command(string name) => name.Length == 0 ? "off" : "/" + name;

    private static string List(IReadOnlyList<string> ids, string empty) => ids.Count == 0 ? empty : string.Join(", ", ids);

    private static string? Client(JsonElement root, string section) => Text(Get(Get(root, section), "Client"))?.ToLowerInvariant();

    /// <summary>A property by name, ignoring case (Requestrr writes PascalCase, older files sometimes differ).</summary>
    private static JsonElement? Get(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? Text(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString()?.Trim(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        _ => null,
    };

    private static bool? Bool(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    /// <summary>Ids from an array of strings or numbers (Requestrr stored both over its versions), or a space separated string (1.0).</summary>
    private static List<string> Ids(JsonElement? element, string what, List<string> warnings)
    {
        IEnumerable<string?> raw = element switch
        {
            { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(e => Text(e)),
            { ValueKind: JsonValueKind.String } text => (text.GetString() ?? string.Empty).Split([' ', ','], StringSplitOptions.RemoveEmptyEntries),
            _ => [],
        };

        var ids = new List<string>();
        foreach (var id in raw.Select(r => r?.Trim()).Where(r => !string.IsNullOrEmpty(r)))
        {
            if (DiscordBotSettings.IsSnowflake(id))
            {
                if (!ids.Contains(id!))
                {
                    ids.Add(id!);
                }
            }
            else
            {
                warnings.Add($"\"{DiscordText.Cut(id, 30)}\" is not a {what} id and is left out (StreamZH needs ids, not names).");
            }
        }

        if (ids.Count > DiscordBotSettings.MaxIds)
        {
            warnings.Add($"Only the first {DiscordBotSettings.MaxIds} {what}s are taken over.");
            ids = ids[..DiscordBotSettings.MaxIds];
        }

        return ids;
    }
}
