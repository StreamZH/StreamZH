// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Library.Discord;

public sealed record DiscordTestChannel(string Id, string Name);

public sealed record DiscordTestRole(string Id, string Name);

/// <summary>A server the bot is in, with its text channels and roles (for choosing them by name in the settings).</summary>
public sealed record DiscordTestGuild(string Id, string Name, IReadOnlyList<DiscordTestChannel> Channels, IReadOnlyList<DiscordTestRole> Roles, string? Error);

/// <param name="Error">Why the test failed (token rejected, Discord unreachable); null = success.</param>
public sealed record DiscordTestResult(
    bool Ok, string? BotName, string? BotId, string? ApplicationId, string? InviteUrl, IReadOnlyList<DiscordTestGuild> Guilds, string? Error);

/// <summary>Settings → Discord → Test: signs in over REST with a token and lists the servers, channels and roles the bot sees.</summary>
public sealed class DiscordBotTester(IDiscordApi api)
{
    /// <summary>Servers listed with their channels and roles; more are listed by name only.</summary>
    public const int MaxDetailedGuilds = 10;

    public async Task<DiscordTestResult> TestAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        DiscordUser bot;
        DiscordApplication application;
        IReadOnlyList<DiscordGuild> guilds;
        try
        {
            bot = await api.GetCurrentUserAsync(token, cancellationToken);
            application = await api.GetApplicationAsync(token, cancellationToken);
            guilds = await api.GetGuildsAsync(token, cancellationToken);
        }
        catch (DiscordApiException ex)
        {
            return new DiscordTestResult(false, null, null, null, null, [], ex.Message);
        }

        var result = new List<DiscordTestGuild>();
        foreach (var (guild, index) in guilds.Select((g, i) => (g, i)))
        {
            if (index >= MaxDetailedGuilds)
            {
                result.Add(new DiscordTestGuild(guild.Id, guild.Name, [], [], null));
                continue;
            }

            try
            {
                var channels = await api.GetChannelsAsync(token, guild.Id, cancellationToken);
                var roles = await api.GetRolesAsync(token, guild.Id, cancellationToken);
                result.Add(new DiscordTestGuild(
                    guild.Id,
                    guild.Name,
                    [.. channels.Where(c => c.IsText).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new DiscordTestChannel(c.Id, c.Name))],
                    [.. roles.Where(r => !r.Managed && r.Id != guild.Id).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => new DiscordTestRole(r.Id, r.Name))],
                    null));
            }
            catch (DiscordApiException ex)
            {
                result.Add(new DiscordTestGuild(guild.Id, guild.Name, [], [], ex.Message));
            }
        }

        var applicationId = application.Id.Length > 0 ? application.Id : null;
        return new DiscordTestResult(true, bot.DisplayName, bot.Id, applicationId, DiscordBotSettings.InviteUrl(applicationId), result, null);
    }
}
