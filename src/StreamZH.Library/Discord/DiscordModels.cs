// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamZH.Library.Discord;

// The small part of Discord's API v10 the bot uses: users, guilds, channels, roles, slash commands, interactions and
// messages with embeds and components. Field names follow Discord (snake_case on the wire, see DiscordJson).

public sealed record DiscordUser(string Id, string Username, string? GlobalName = null)
{
    /// <summary>What people see: the display name, else the user name.</summary>
    public string DisplayName => GlobalName is { Length: > 0 } name ? name : Username;
}

public sealed record DiscordGuild(string Id, string Name);

/// <param name="Type">0 = text, 5 = announcement, 2 = voice, 4 = category …</param>
public sealed record DiscordChannel(string Id, string Name, int Type, string? ParentId = null)
{
    public bool IsText => Type is 0 or 5;
}

public sealed record DiscordRole(string Id, string Name, bool Managed = false);

public sealed record DiscordApplication(string Id, string Name);

/// <summary>Where the gateway is and how many new sessions may still start today.</summary>
public sealed record DiscordGatewayInfo(string Url, int SessionStartsRemaining, TimeSpan SessionStartsResetAfter);

/// <summary>A slash command as registered (CHAT_INPUT, type 1).</summary>
public sealed record DiscordCommandDefinition(string Name, string Description, IReadOnlyList<DiscordCommandOption> Options)
{
    public int Type => 1;

    /// <summary>Interaction contexts: 0 = guild, 1 = DM with the bot. Null = Discord's default.</summary>
    public IReadOnlyList<int>? Contexts { get; init; }
}

/// <param name="Type">3 = string.</param>
public sealed record DiscordCommandOption(string Name, string Description, int Type = 3, bool Required = true, int? MaxLength = null);

public sealed record DiscordEmbedMedia(string Url);

public sealed record DiscordEmbedFooter(string Text);

public sealed record DiscordEmbedField(string Name, string Value, bool Inline = false);

public sealed record DiscordEmbed
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Url { get; init; }
    public int? Color { get; init; }
    public DiscordEmbedMedia? Thumbnail { get; init; }
    public DiscordEmbedMedia? Image { get; init; }
    public DiscordEmbedFooter? Footer { get; init; }
    public IReadOnlyList<DiscordEmbedField>? Fields { get; init; }
}

public sealed record DiscordSelectOption(string Label, string Value, string? Description = null, bool? Default = null);

/// <summary>
/// One message component: action row (1) holding buttons (2) or one string select (3). Button styles: 1 primary,
/// 2 secondary, 3 success, 4 danger, 5 link.
/// </summary>
public sealed record DiscordComponent
{
    public int Type { get; init; }
    public IReadOnlyList<DiscordComponent>? Components { get; init; }
    public int? Style { get; init; }
    public string? Label { get; init; }
    public string? CustomId { get; init; }
    public string? Url { get; init; }
    public bool? Disabled { get; init; }
    public IReadOnlyList<DiscordSelectOption>? Options { get; init; }
    public string? Placeholder { get; init; }
    public int? MinValues { get; init; }
    public int? MaxValues { get; init; }

    public static DiscordComponent Row(params DiscordComponent[] components) => new() { Type = 1, Components = components };

    public static DiscordComponent Button(string label, string customId, int style = 1, bool disabled = false) =>
        new() { Type = 2, Style = style, Label = label, CustomId = customId, Disabled = disabled ? true : null };

    public static DiscordComponent Select(string customId, string placeholder, IReadOnlyList<DiscordSelectOption> options, int minValues = 1, int maxValues = 1) =>
        new() { Type = 3, CustomId = customId, Placeholder = placeholder, Options = options, MinValues = minValues, MaxValues = maxValues };
}

/// <summary>Who a message may ping: nobody unless listed.</summary>
public sealed record DiscordAllowedMentions(IReadOnlyList<string> Parse, IReadOnlyList<string>? Users = null);

public sealed record DiscordMessage
{
    /// <summary>Flag of a message only the user who ran the command sees.</summary>
    public const int Ephemeral = 64;

    public string? Content { get; init; }
    public IReadOnlyList<DiscordEmbed>? Embeds { get; init; }
    public IReadOnlyList<DiscordComponent>? Components { get; init; }
    public int? Flags { get; init; }

    /// <summary>Defaults to "ping nobody" so a title or name can never mention @everyone.</summary>
    public DiscordAllowedMentions AllowedMentions { get; init; } = new([]);
}

/// <summary>
/// Answer to an interaction. Types: 4 message, 5 deferred message ("thinking…"), 6 deferred update of the component's
/// message, 7 update of the component's message.
/// </summary>
public sealed record DiscordInteractionResponse(int Type, DiscordMessage? Data = null)
{
    public static DiscordInteractionResponse DeferEphemeral() => new(5, new DiscordMessage { Flags = DiscordMessage.Ephemeral });

    public static DiscordInteractionResponse DeferUpdate() => new(6);
}

/// <summary>An interaction from the gateway (slash command or a click on a button/select).</summary>
/// <param name="Type">2 = application command, 3 = message component.</param>
/// <param name="MemberRoles">Roles of the member in the guild; empty in DMs.</param>
/// <param name="Options">Command options by name (string values).</param>
/// <param name="Values">Chosen values of a select menu.</param>
public sealed record DiscordInteraction(
    string Id,
    string ApplicationId,
    int Type,
    string Token,
    string? GuildId,
    string? ChannelId,
    DiscordUser User,
    IReadOnlyList<string> MemberRoles,
    string? CommandName,
    IReadOnlyDictionary<string, string> Options,
    string? CustomId,
    IReadOnlyList<string> Values)
{
    public const int ApplicationCommand = 2;
    public const int MessageComponent = 3;

    public bool InGuild => GuildId is not null;

    /// <summary>Reads INTERACTION_CREATE's data; null when it is not a command or component interaction.</summary>
    public static DiscordInteraction? Parse(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var type = typeElement.GetInt32();
        if (type is not (ApplicationCommand or MessageComponent))
        {
            return null;
        }

        var roles = new List<string>();
        DiscordUser? user = null;
        if (d.TryGetProperty("member", out var member) && member.ValueKind == JsonValueKind.Object)
        {
            if (member.TryGetProperty("user", out var memberUser))
            {
                user = DiscordJson.ReadUser(memberUser);
            }

            if (member.TryGetProperty("roles", out var roleList) && roleList.ValueKind == JsonValueKind.Array)
            {
                roles.AddRange(roleList.EnumerateArray().Select(r => r.GetString()).OfType<string>());
            }
        }

        if (user is null && d.TryGetProperty("user", out var dmUser))
        {
            user = DiscordJson.ReadUser(dmUser);
        }

        if (user is null)
        {
            return null;
        }

        string? commandName = null;
        string? customId = null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = new List<string>();
        if (d.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            commandName = DiscordJson.String(data, "name");
            customId = DiscordJson.String(data, "custom_id");
            if (data.TryGetProperty("options", out var optionList) && optionList.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in optionList.EnumerateArray())
                {
                    if (DiscordJson.String(option, "name") is { } name && option.TryGetProperty("value", out var value))
                    {
                        options[name] = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
                    }
                }
            }

            if (data.TryGetProperty("values", out var valueList) && valueList.ValueKind == JsonValueKind.Array)
            {
                values.AddRange(valueList.EnumerateArray().Select(v => v.GetString()).OfType<string>());
            }
        }

        return new DiscordInteraction(
            DiscordJson.String(d, "id") ?? string.Empty,
            DiscordJson.String(d, "application_id") ?? string.Empty,
            type,
            DiscordJson.String(d, "token") ?? string.Empty,
            DiscordJson.String(d, "guild_id"),
            DiscordJson.String(d, "channel_id"),
            user,
            roles,
            commandName,
            options,
            customId,
            values);
    }
}

/// <summary>JSON settings for Discord's wire format and small readers.</summary>
public static class DiscordJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public static DiscordUser? ReadUser(JsonElement element) =>
        String(element, "id") is { } id ? new DiscordUser(id, String(element, "username") ?? id, String(element, "global_name")) : null;
}
