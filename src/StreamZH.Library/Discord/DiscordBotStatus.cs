// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Library.Discord;

/// <summary>Where the bot is (API strings, lowercase).</summary>
public static class DiscordBotStates
{
    /// <summary>Switched off, or no token saved.</summary>
    public const string Off = "off";

    public const string Connecting = "connecting";

    public const string Connected = "connected";

    /// <summary>The connection was lost; the bot retries after <see cref="DiscordBotStatus.NextRetry"/>.</summary>
    public const string Reconnecting = "reconnecting";

    /// <summary>Retrying cannot help (wrong token, refused intents); the bot waits for new settings.</summary>
    public const string Error = "error";
}

/// <summary>A snapshot of the bot for Settings → Discord and System → Status.</summary>
/// <param name="Error">Why the bot is not connected (never contains the token).</param>
/// <param name="PermissionProblem">The last message to the notify channel or a requester failed for missing access/permissions.</param>
public sealed record DiscordBotStatus(
    string State,
    string? BotName,
    string? BotId,
    string? ApplicationId,
    int Guilds,
    string? Error,
    DateTimeOffset? Since,
    DateTimeOffset? NextRetry,
    string? PermissionProblem)
{
    public static DiscordBotStatus Off { get; } = new(DiscordBotStates.Off, null, null, null, 0, null, null, null, null);

    public bool IsConnected => State == DiscordBotStates.Connected;
}

/// <summary>The bot's current status; the bot service writes it, the API and the status window read it.</summary>
public sealed class DiscordBotStatusHolder
{
    private DiscordBotStatus _status = DiscordBotStatus.Off;

    /// <summary>Raised after every change (the host turns it into OngoingChanged).</summary>
    public event Action? Changed;

    public DiscordBotStatus Current => Volatile.Read(ref _status);

    public void Set(DiscordBotStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var old = Interlocked.Exchange(ref _status, status);
        if (old != status)
        {
            Changed?.Invoke();
        }
    }

    public void Update(Func<DiscordBotStatus, DiscordBotStatus> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        DiscordBotStatus old, next;
        do
        {
            old = Volatile.Read(ref _status);
            next = change(old);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _status, next, old), old));

        if (old != next)
        {
            Changed?.Invoke();
        }
    }
}
