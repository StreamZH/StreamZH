// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Library.Discord;

/// <summary>The slash commands the bot registers, built from Settings → Discord.</summary>
public static class DiscordCommandSet
{
    public const string Requests = "requests";
    public const string Link = "link";
    public const string Help = "help";

    public const string TitleOption = "title";
    public const string CodeOption = "code";

    public const int MaxTitleLength = 100;

    /// <summary>Interaction context: used in a server.</summary>
    public const int GuildContext = 0;

    /// <summary>Interaction context: direct message with the bot.</summary>
    public const int BotDmContext = 1;

    /// <summary>
    /// The commands. Guild commands (servers listed in the settings) only exist in those servers; global commands also
    /// reach direct messages for /link and /help, and for the request commands when direct messages are allowed.
    /// </summary>
    public static IReadOnlyList<DiscordCommandDefinition> Build(DiscordSettingsValues settings, bool global)
    {
        ArgumentNullException.ThrowIfNull(settings);
        IReadOnlyList<int>? requestContexts = global ? settings.AllowDirectMessages ? [GuildContext, BotDmContext] : [GuildContext] : null;
        IReadOnlyList<int>? anywhere = global ? [GuildContext, BotDmContext] : null;

        var commands = new List<DiscordCommandDefinition>();
        if (settings.MovieCommand.Length > 0)
        {
            commands.Add(new DiscordCommandDefinition(
                settings.MovieCommand,
                "Search a movie and request it",
                [new DiscordCommandOption(TitleOption, "Movie title to search for", MaxLength: MaxTitleLength)]) { Contexts = requestContexts });
        }

        if (settings.TvCommand.Length > 0)
        {
            commands.Add(new DiscordCommandDefinition(
                settings.TvCommand,
                "Search a series and request it",
                [new DiscordCommandOption(TitleOption, "Series title to search for", MaxLength: MaxTitleLength)]) { Contexts = requestContexts });
        }

        commands.Add(new DiscordCommandDefinition(Requests, "Show your requests and where they are", []) { Contexts = requestContexts });
        commands.Add(new DiscordCommandDefinition(
            Link,
            "Link your StreamZH account (code from your StreamZH profile)",
            [new DiscordCommandOption(CodeOption, "The code shown in your StreamZH profile", MaxLength: 12)]) { Contexts = anywhere });
        commands.Add(new DiscordCommandDefinition(Help, "How requesting with StreamZH works", []) { Contexts = anywhere });
        return commands;
    }
}
