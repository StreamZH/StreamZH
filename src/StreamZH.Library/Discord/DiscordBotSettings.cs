// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using StreamZH.Data;

namespace StreamZH.Library.Discord;

/// <summary>How a series request picks its seasons in Discord (Requestrr's "TV show restrictions").</summary>
public static class DiscordSeasonModes
{
    /// <summary>All seasons or any set of seasons (default).</summary>
    public const string Choose = "choose";

    /// <summary>Only the whole series.</summary>
    public const string All = "all";

    /// <summary>One season per request.</summary>
    public const string Single = "single";

    public static readonly IReadOnlyList<string> Values = [Choose, All, Single];
}

/// <summary>Settings → Discord as stored; the bot token is never part of this record (see <see cref="DiscordBotSettings.Token"/>).</summary>
/// <param name="Enabled">The bot runs while this is on and a token is saved.</param>
/// <param name="HasToken">A bot token is saved (the token itself is never shown again).</param>
/// <param name="ApplicationId">Application (client) id from the Developer Portal; filled from the token when empty. Needed for the invite link.</param>
/// <param name="GuildIds">Servers whose members may use the bot; commands are registered there. Empty = every server the bot is in (global commands).</param>
/// <param name="ChannelIds">Channels where request commands work; empty = every channel.</param>
/// <param name="AllowGuests">Discord users without a linked StreamZH account may request (requests wait for approval).</param>
/// <param name="GuestRoleIds">Guests need one of these Discord roles; empty = any member.</param>
/// <param name="NotifyChannelId">Channel that gets approved / declined / available messages; null = none.</param>
/// <param name="DmRequesters">Requesters get a direct message when their request is approved, declined or available.</param>
/// <param name="AllowDirectMessages">Request commands also work in a direct message with the bot (only with global commands).</param>
/// <param name="MovieCommand">Name of the movie command (without "/"); empty = no movie command.</param>
/// <param name="TvCommand">Name of the series command; empty = no series command.</param>
/// <param name="SeriesSeasons">See <see cref="DiscordSeasonModes"/>.</param>
/// <param name="StatusText">The bot's status line in Discord.</param>
public sealed record DiscordSettingsValues(
    bool Enabled,
    bool HasToken,
    string? ApplicationId,
    IReadOnlyList<string> GuildIds,
    IReadOnlyList<string> ChannelIds,
    bool AllowGuests,
    IReadOnlyList<string> GuestRoleIds,
    string? NotifyChannelId,
    bool DmRequesters,
    bool AllowDirectMessages,
    string MovieCommand,
    string TvCommand,
    string SeriesSeasons,
    string StatusText);

/// <summary>Fields to change; null keeps the current value.</summary>
/// <param name="BotToken">null or the mask keeps the saved token, "" removes it, anything else replaces it.</param>
/// <param name="NotifyChannelId">"" removes the channel.</param>
public sealed record DiscordSettingsChange
{
    public bool? Enabled { get; init; }
    public string? BotToken { get; init; }
    public string? ApplicationId { get; init; }
    public IReadOnlyList<string>? GuildIds { get; init; }
    public IReadOnlyList<string>? ChannelIds { get; init; }
    public bool? AllowGuests { get; init; }
    public IReadOnlyList<string>? GuestRoleIds { get; init; }
    public string? NotifyChannelId { get; init; }
    public bool? DmRequesters { get; init; }
    public bool? AllowDirectMessages { get; init; }
    public string? MovieCommand { get; init; }
    public string? TvCommand { get; init; }
    public string? SeriesSeasons { get; init; }
    public string? StatusText { get; init; }
}

/// <summary>
/// Settings → Discord (0.10), read from the config table on every use. The bot token is stored like the other secrets
/// (Usenet passwords, indexer API keys) and only leaves this class through <see cref="Token"/> for the Discord client.
/// Saving signals <see cref="ChangeToken"/>, so the running bot reconnects with the new values.
/// </summary>
public sealed partial class DiscordBotSettings(ConfigRepository config)
{
    /// <summary>What the API shows instead of a saved token; sending it back keeps the token.</summary>
    public const string Mask = "********";

    public const string DefaultMovieCommand = "movie";
    public const string DefaultTvCommand = "tv";
    public const string DefaultStatusText = "/help";
    public const int MaxStatusTextLength = 128;
    public const int MaxIds = 50;

    /// <summary>Commands StreamZH always registers; the movie and series commands cannot take these names.</summary>
    public static readonly IReadOnlyList<string> FixedCommands = ["requests", "link", "help"];

    private readonly Lock _lock = new();
    private CancellationTokenSource _changed = new();

    /// <summary>Cancelled when the settings were saved (a new token is handed out right after).</summary>
    public CancellationToken ChangeToken
    {
        get
        {
            lock (_lock)
            {
                return _changed.Token;
            }
        }
    }

    public DiscordSettingsValues Values => new(
        Bool(ConfigKeys.DiscordEnabled, false),
        Token is not null,
        Text(ConfigKeys.DiscordApplicationId),
        List(ConfigKeys.DiscordGuildIds),
        List(ConfigKeys.DiscordChannelIds),
        Bool(ConfigKeys.DiscordAllowGuests, false),
        List(ConfigKeys.DiscordGuestRoleIds),
        Text(ConfigKeys.DiscordNotifyChannelId),
        Bool(ConfigKeys.DiscordDmRequesters, true),
        Bool(ConfigKeys.DiscordAllowDirectMessages, false),
        config.Get(ConfigKeys.DiscordMovieCommand) ?? DefaultMovieCommand,
        config.Get(ConfigKeys.DiscordTvCommand) ?? DefaultTvCommand,
        config.Get(ConfigKeys.DiscordSeriesSeasons) is { } mode && DiscordSeasonModes.Values.Contains(mode) ? mode : DiscordSeasonModes.Choose,
        config.Get(ConfigKeys.DiscordStatusText) ?? DefaultStatusText);

    /// <summary>The saved bot token, null when none. Never log or return it.</summary>
    public string? Token => Text(ConfigKeys.DiscordBotToken);

    /// <summary>Checks the change against the current values; field errors are keyed by camelCase names.</summary>
    public Dictionary<string, string[]> Validate(DiscordSettingsChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var errors = new Dictionary<string, string[]>();
        var current = Values;

        if (NewToken(change.BotToken) is { Length: > 0 } token && !LooksLikeToken(token))
        {
            errors["botToken"] = ["This does not look like a bot token. Copy it from Bot → Reset Token in the Discord Developer Portal."];
        }

        if (change.ApplicationId is { Length: > 0 } applicationId && !IsSnowflake(applicationId.Trim()))
        {
            errors["applicationId"] = ["Enter the application id (a number of 17 to 20 digits) from General Information in the Developer Portal."];
        }

        CheckIds(errors, "guildIds", change.GuildIds, "server");
        CheckIds(errors, "channelIds", change.ChannelIds, "channel");
        CheckIds(errors, "guestRoleIds", change.GuestRoleIds, "role");

        if (change.NotifyChannelId is { Length: > 0 } channel && !IsSnowflake(channel.Trim()))
        {
            errors["notifyChannelId"] = ["Enter a channel id (right-click the channel → Copy Channel ID with developer mode on)."];
        }

        var movie = change.MovieCommand?.Trim().ToLowerInvariant() ?? current.MovieCommand;
        var tv = change.TvCommand?.Trim().ToLowerInvariant() ?? current.TvCommand;
        CheckCommand(errors, "movieCommand", movie);
        CheckCommand(errors, "tvCommand", tv);
        if (movie.Length > 0 && movie == tv && !errors.ContainsKey("tvCommand"))
        {
            errors["tvCommand"] = ["The movie and series commands need different names."];
        }

        if (change.SeriesSeasons is { } seasons && !DiscordSeasonModes.Values.Contains(seasons))
        {
            errors["seriesSeasons"] = ["Use choose, all or single."];
        }

        if (change.StatusText is { } status && status.Trim().Length > MaxStatusTextLength)
        {
            errors["statusText"] = [$"At most {MaxStatusTextLength} characters."];
        }

        return errors;
    }

    /// <summary>Saves a change that passed <see cref="Validate"/> and tells the bot to pick it up.</summary>
    public void Update(DiscordSettingsChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Enabled is { } enabled)
        {
            config.Set(ConfigKeys.DiscordEnabled, enabled ? "true" : "false");
        }

        if (NewToken(change.BotToken) is { } token)
        {
            config.Set(ConfigKeys.DiscordBotToken, token);
        }

        SetText(ConfigKeys.DiscordApplicationId, change.ApplicationId);
        SetList(ConfigKeys.DiscordGuildIds, change.GuildIds);
        SetList(ConfigKeys.DiscordChannelIds, change.ChannelIds);
        SetBool(ConfigKeys.DiscordAllowGuests, change.AllowGuests);
        SetList(ConfigKeys.DiscordGuestRoleIds, change.GuestRoleIds);
        SetText(ConfigKeys.DiscordNotifyChannelId, change.NotifyChannelId);
        SetBool(ConfigKeys.DiscordDmRequesters, change.DmRequesters);
        SetBool(ConfigKeys.DiscordAllowDirectMessages, change.AllowDirectMessages);
        if (change.MovieCommand is { } movie)
        {
            config.Set(ConfigKeys.DiscordMovieCommand, movie.Trim().ToLowerInvariant());
        }

        if (change.TvCommand is { } tv)
        {
            config.Set(ConfigKeys.DiscordTvCommand, tv.Trim().ToLowerInvariant());
        }

        if (change.SeriesSeasons is { } seasons)
        {
            config.Set(ConfigKeys.DiscordSeriesSeasons, seasons);
        }

        if (change.StatusText is { } status)
        {
            config.Set(ConfigKeys.DiscordStatusText, status.Trim());
        }

        SignalChange();
    }

    /// <summary>Remembers the application id the bot learned from Discord (only while none is set), without restarting the bot.</summary>
    public void RememberApplicationId(string applicationId)
    {
        if (Text(ConfigKeys.DiscordApplicationId) is null && IsSnowflake(applicationId))
        {
            config.Set(ConfigKeys.DiscordApplicationId, applicationId);
        }
    }

    /// <summary>
    /// Invite link with the scopes and permissions the bot needs: bot + applications.commands; View Channels, Send
    /// Messages, Embed Links (<see cref="InvitePermissions"/>).
    /// </summary>
    public static string? InviteUrl(string? applicationId) =>
        applicationId is { Length: > 0 } id && IsSnowflake(id)
            ? $"https://discord.com/oauth2/authorize?client_id={id}&scope=bot%20applications.commands&permissions={InvitePermissions}"
            : null;

    /// <summary>VIEW_CHANNEL (1 &lt;&lt; 10) + SEND_MESSAGES (1 &lt;&lt; 11) + EMBED_LINKS (1 &lt;&lt; 14).</summary>
    public const long InvitePermissions = (1L << 10) | (1L << 11) | (1L << 14);

    public static bool IsSnowflake(string? value) => value is not null && SnowflakePattern().IsMatch(value);

    /// <summary>Three dot-separated parts of URL-safe base64, 50 to 120 characters (Discord's bot token format).</summary>
    public static bool LooksLikeToken(string token) =>
        token.Length is >= 50 and <= 120 && TokenPattern().IsMatch(token) && token.Count(c => c == '.') == 2;

    /// <summary>The token to store: null = keep, "" = remove. "Bot " in front (copied from a header) is dropped.</summary>
    private static string? NewToken(string? given)
    {
        if (given is null || given == Mask)
        {
            return null;
        }

        var token = given.Trim();
        return token.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase) ? token[4..].Trim() : token;
    }

    private static void CheckIds(Dictionary<string, string[]> errors, string field, IReadOnlyList<string>? ids, string what)
    {
        if (ids is null)
        {
            return;
        }

        if (ids.Count > MaxIds)
        {
            errors[field] = [$"At most {MaxIds} entries."];
        }
        else if (ids.FirstOrDefault(id => !IsSnowflake(id?.Trim())) is { } wrong)
        {
            errors[field] = [$"\"{Shorten(wrong)}\" is not a {what} id. Ids are numbers of 17 to 20 digits (Copy ID with developer mode on)."];
        }
    }

    private static void CheckCommand(Dictionary<string, string[]> errors, string field, string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        if (!CommandPattern().IsMatch(name))
        {
            errors[field] = ["Use 1 to 32 lowercase letters, digits, - or _ (no spaces), or leave it empty to turn the command off."];
        }
        else if (FixedCommands.Contains(name))
        {
            errors[field] = [$"/{name} is one of StreamZH's own commands; choose another name."];
        }
    }

    private static string Shorten(string? value) => value is null ? string.Empty : value.Length > 30 ? value[..30] + "…" : value;

    private void SignalChange()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _changed;
            _changed = new CancellationTokenSource();
        }

        old.Cancel();
        old.Dispose();
    }

    private bool Bool(string key, bool fallback) => config.Get(key) switch
    {
        "true" => true,
        "false" => false,
        _ => fallback,
    };

    private string? Text(string key) => config.Get(key) is { Length: > 0 } value ? value : null;

    private List<string> List(string key) =>
        config.Get(key) is { Length: > 0 } value ? [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [];

    private void SetBool(string key, bool? value)
    {
        if (value is { } v)
        {
            config.Set(key, v ? "true" : "false");
        }
    }

    private void SetText(string key, string? value)
    {
        if (value is not null)
        {
            config.Set(key, value.Trim());
        }
    }

    private void SetList(string key, IReadOnlyList<string>? values)
    {
        if (values is not null)
        {
            config.Set(key, string.Join(',', values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)));
        }
    }

    [GeneratedRegex(@"^\d{17,20}$")]
    private static partial Regex SnowflakePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^[a-z0-9_-]{1,32}$")]
    private static partial Regex CommandPattern();
}
