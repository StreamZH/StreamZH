// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.Discord;

namespace StreamZH.Host.Status;

/// <summary>
/// Status-window notices of the Discord bot (0.10): the token was rejected (error), the bot cannot connect (warning,
/// once it has failed for a while), and missing access or permissions in a server or the notify channel (warning).
/// Each disappears once the cause is gone.
/// </summary>
public static class DiscordNotices
{
    public const string Key = "discord-bot";
    public const string PermissionKey = "discord-permissions";

    private const string Link = "/settings/discord";

    public static void Add(List<OngoingNotice> notices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<DiscordBotStatusHolder>()?.Current is not { } status || status.State == DiscordBotStates.Off)
        {
            return;
        }

        if (status.State == DiscordBotStates.Error)
        {
            notices.Add(new OngoingNotice(Key, NoticeLevels.Error, "Discord bot stopped", status.Error, Link, false));
        }
        else if (status.State == DiscordBotStates.Reconnecting)
        {
            notices.Add(new OngoingNotice(Key, NoticeLevels.Warn, "Discord bot cannot connect", Join(status.Error, "StreamZH keeps trying"), Link, false));
        }

        if (status.PermissionProblem is { Length: > 0 } problem && status.State != DiscordBotStates.Error)
        {
            notices.Add(new OngoingNotice(PermissionKey, NoticeLevels.Warn, "Discord bot is missing access", problem, Link, false));
        }
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
