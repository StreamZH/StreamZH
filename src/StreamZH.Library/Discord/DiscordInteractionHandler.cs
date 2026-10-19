// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only
// Portions based on the request flow of Requestrr (https://github.com/thomst08/requestrr, GPL-3.0): search → pick a
// title → pick seasons → request, channel and role restrictions; reimplemented for StreamZH.

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Library.Discord;

/// <summary>
/// Answers the bot's slash commands and the clicks on its menus and buttons. Every answer is ephemeral (only the
/// person who used the command sees it). Slow work (TMDB search, adding a request) is deferred first, so Discord gets
/// an answer within its 3 seconds. Permissions are checked again on every click.
/// </summary>
public sealed class DiscordInteractionHandler(
    DiscordBotSettings settings,
    IDiscordApi api,
    IRequestGateway requests,
    IDiscordLinkCodes linkCodes,
    DiscordLinkRepository links,
    UserRepository users,
    RequestRepository requestRepository,
    TimeProvider time,
    ILogger<DiscordInteractionHandler> logger)
{
    public const int MaxResults = 5;
    public const int MaxListedRequests = 10;

    /// <summary>Wrong /link codes per Discord user within <see cref="LinkAttemptWindow"/> before /link is refused for a while.</summary>
    public const int MaxLinkAttempts = 5;

    public static readonly TimeSpan LinkAttemptWindow = TimeSpan.FromMinutes(10);

    // Custom ids of the components: "szh:pick:m", "szh:req:m:603", "szh:seasons:1399" (Discord allows 100 characters).
    private const string PickPrefix = "szh:pick:";
    private const string RequestPrefix = "szh:req:";
    private const string SeasonsPrefix = "szh:seasons:";
    private const string AllSeasons = "all";
    private const int MaxSelectOptions = 25;

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _linkAttempts = new(StringComparer.Ordinal);

    private sealed record Requester(int? UserId, string? Username);

    public async Task HandleAsync(DiscordInteraction interaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var answered = false;
        try
        {
            if (interaction.Type == DiscordInteraction.ApplicationCommand)
            {
                answered = await CommandAsync(interaction, () => answered = true, cancellationToken);
            }
            else if (interaction.Type == DiscordInteraction.MessageComponent)
            {
                answered = await ComponentAsync(interaction, () => answered = true, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DiscordApiException ex) when (ex.Code == DiscordApiException.UnknownInteraction)
        {
            // The interaction expired (answered too late, or twice); nothing left to tell the user.
            logger.LogDebug("Discord interaction expired before StreamZH answered");
        }
        catch (Exception ex)
        {
            var message = ex is RequestGatewayException ? ex.Message : "Something went wrong. Try again in a moment.";
            if (ex is DiscordApiException apiError)
            {
                // Discord could not be reached or refused an answer: the message says enough, no stack trace.
                logger.LogWarning("Discord command /{Command} failed: {Error}", interaction.CommandName ?? interaction.CustomId, apiError.Message);
            }
            else if (ex is not RequestGatewayException)
            {
                logger.LogWarning(ex, "Discord command /{Command} failed", interaction.CommandName ?? interaction.CustomId);
            }

            await TryShowErrorAsync(interaction, answered, message, cancellationToken);
        }
    }

    // ---- Commands ----

    private async Task<bool> CommandAsync(DiscordInteraction interaction, Action deferred, CancellationToken ct)
    {
        var values = settings.Values;
        var name = interaction.CommandName ?? string.Empty;
        if (name.Length > 0 && name == values.MovieCommand)
        {
            return await SearchAsync(interaction, values, MediaType.Movie, deferred, ct);
        }

        if (name.Length > 0 && name == values.TvCommand)
        {
            return await SearchAsync(interaction, values, MediaType.Series, deferred, ct);
        }

        switch (name)
        {
            case DiscordCommandSet.Requests:
                await ListRequestsAsync(interaction, values, ct);
                return true;
            case DiscordCommandSet.Link:
                await LinkAsync(interaction, ct);
                return true;
            case DiscordCommandSet.Help:
                await ReplyAsync(interaction, Help(interaction, values), ct);
                return true;
            default:
                await ReplyAsync(interaction, Text("This command is not available any more."), ct);
                return true;
        }
    }

    private async Task<bool> SearchAsync(DiscordInteraction interaction, DiscordSettingsValues values, MediaType type, Action deferred, CancellationToken ct)
    {
        if (Refusal(interaction, values, out _) is { } refusal)
        {
            await ReplyAsync(interaction, Text(refusal), ct);
            return true;
        }

        var query = interaction.Options.GetValueOrDefault(DiscordCommandSet.TitleOption)?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            await ReplyAsync(interaction, Text("Enter a title to search for."), ct);
            return true;
        }

        query = DiscordText.Cut(query, DiscordCommandSet.MaxTitleLength);
        await api.RespondAsync(interaction.Id, interaction.Token, DiscordInteractionResponse.DeferEphemeral(), ct);
        deferred();

        var results = (await requests.SearchAsync(type, query, ct)).Take(MaxResults).ToList();
        if (results.Count == 0)
        {
            await EditAsync(interaction, Text($"Nothing found for \"{DiscordText.Md(query)}\". Check the spelling or try the original title."), ct);
            return true;
        }

        var key = type == MediaType.Movie ? "m" : "s";
        var embeds = results.Select(ResultEmbed).ToList();
        var options = results
            .Select(r => new DiscordSelectOption(
                DiscordText.Cut(DiscordText.Name(r.Title, r.Year), 100),
                r.TmdbId.ToString(CultureInfo.InvariantCulture),
                DiscordText.Cut(DiscordText.State(r.State), 100)))
            .ToList();
        await EditAsync(
            interaction,
            new DiscordMessage
            {
                Content = $"{results.Count} result{(results.Count == 1 ? string.Empty : "s")} for \"{DiscordText.Md(query)}\". Choose one:",
                Embeds = embeds,
                Components = [DiscordComponent.Row(DiscordComponent.Select(PickPrefix + key, "Choose a title", options))],
            },
            ct);
        return true;
    }

    private async Task ListRequestsAsync(DiscordInteraction interaction, DiscordSettingsValues values, CancellationToken ct)
    {
        if (Refusal(interaction, values, out var requester) is { } refusal)
        {
            await ReplyAsync(interaction, Text(refusal), ct);
            return;
        }

        var list = requestRepository.ForRequester(requester.UserId, interaction.User.Id, MaxListedRequests);
        if (list.Count == 0)
        {
            var hint = values.MovieCommand.Length > 0 ? $" Use /{values.MovieCommand} to request a movie." : string.Empty;
            await ReplyAsync(interaction, Text("You have no requests yet." + hint), ct);
            return;
        }

        var lines = list.Select(r =>
        {
            var seasons = DiscordText.Seasons(r.MediaType, r.Seasons);
            var parts = new[] { $"**{DiscordText.Md(DiscordText.Name(r.Title, r.Year))}**", seasons, DiscordText.Status(r.Status), DiscordText.Relative(r.Created) };
            return "• " + string.Join(" · ", parts.Where(p => p.Length > 0));
        });
        await ReplyAsync(
            interaction,
            new DiscordMessage
            {
                Embeds =
                [
                    new DiscordEmbed
                    {
                        Title = requester.UserId is null ? "Your requests" : $"Your requests ({DiscordText.Cut(requester.Username, 60)})",
                        Description = DiscordText.Cut(string.Join('\n', lines), 4000),
                        Color = DiscordText.Accent,
                    },
                ],
            },
            ct);
    }

    private async Task LinkAsync(DiscordInteraction interaction, CancellationToken ct)
    {
        var discordId = interaction.User.Id;
        var now = time.GetUtcNow();
        var attempts = _linkAttempts.GetValueOrDefault(discordId);
        if (now - attempts.Since > LinkAttemptWindow)
        {
            attempts = (0, now);
        }

        if (attempts.Count >= MaxLinkAttempts)
        {
            await ReplyAsync(interaction, Text("Too many wrong codes. Wait a few minutes, then create a new code in your StreamZH profile."), ct);
            return;
        }

        var code = interaction.Options.GetValueOrDefault(DiscordCommandSet.CodeOption) ?? string.Empty;
        if (!linkCodes.TryRedeem(code, out var userId) || users.FindById(userId) is not { Enabled: true } user)
        {
            _linkAttempts[discordId] = (attempts.Count + 1, attempts.Since == default ? now : attempts.Since);
            await ReplyAsync(
                interaction,
                Text("That code is not valid or has expired. In StreamZH open your profile → Discord → Create code, then run /link with the new code within 10 minutes."),
                ct);
            return;
        }

        _linkAttempts.TryRemove(discordId, out _);
        links.Set(user.Id, discordId, interaction.User.DisplayName);
        logger.LogInformation("Discord account {DiscordName} linked to StreamZH user {Username}", interaction.User.DisplayName, user.Username);
        await ReplyAsync(
            interaction,
            Text($"Linked. Your requests from Discord now count as **{DiscordText.Md(user.Username)}** in StreamZH."),
            ct);
    }

    private DiscordMessage Help(DiscordInteraction interaction, DiscordSettingsValues values)
    {
        var lines = new List<string>();
        if (values.MovieCommand.Length > 0)
        {
            lines.Add($"**/{values.MovieCommand} title** – search a movie and request it");
        }

        if (values.TvCommand.Length > 0)
        {
            lines.Add($"**/{values.TvCommand} title** – search a series, pick seasons and request them");
        }

        lines.Add("**/requests** – your requests and where they are");
        lines.Add("**/link code** – link your StreamZH account (create the code in your StreamZH profile)");

        var linked = links.ForDiscord(interaction.User.Id) is { } link && users.FindById(link.UserId) is { Enabled: true };
        var footer = linked
            ? "Your Discord account is linked to StreamZH: your requests use your StreamZH account."
            : values.AllowGuests
                ? "Your Discord account is not linked: requests wait for an admin's approval."
                : "Link your StreamZH account with /link before you request.";
        if (values.DmRequesters)
        {
            footer += " You get a direct message when a request is approved, declined or available.";
        }

        return new DiscordMessage
        {
            Embeds = [new DiscordEmbed { Title = "Requesting with StreamZH", Description = string.Join('\n', lines), Color = DiscordText.Accent, Footer = new DiscordEmbedFooter(footer) }],
        };
    }

    // ---- Components ----

    private async Task<bool> ComponentAsync(DiscordInteraction interaction, Action deferred, CancellationToken ct)
    {
        var values = settings.Values;
        var id = interaction.CustomId ?? string.Empty;
        if (!id.StartsWith("szh:", StringComparison.Ordinal))
        {
            return false;
        }

        if (Refusal(interaction, values, out var requester) is { } refusal)
        {
            await UpdateAsync(interaction, Text(refusal) with { Components = [] }, ct);
            return true;
        }

        if (id.StartsWith(PickPrefix, StringComparison.Ordinal)
            && TypeOf(id[PickPrefix.Length..]) is { } pickType
            && interaction.Values.FirstOrDefault() is { } picked
            && int.TryParse(picked, NumberStyles.None, CultureInfo.InvariantCulture, out var pickedId))
        {
            await DeferUpdateAsync(interaction, deferred, ct);
            await ShowTitleAsync(interaction, values, pickType, pickedId, ct);
            return true;
        }

        if (id.StartsWith(RequestPrefix, StringComparison.Ordinal) && id[RequestPrefix.Length..].Split(':') is [var key, var tmdb]
            && TypeOf(key) is { } requestType
            && int.TryParse(tmdb, NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId))
        {
            await DeferUpdateAsync(interaction, deferred, ct);
            await SubmitAsync(interaction, requester, requestType, tmdbId, null, ct);
            return true;
        }

        if (id.StartsWith(SeasonsPrefix, StringComparison.Ordinal)
            && int.TryParse(id[SeasonsPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var seriesId)
            && interaction.Values.Count > 0)
        {
            List<int>? seasons = null;
            if (!interaction.Values.Contains(AllSeasons))
            {
                seasons = [];
                foreach (var value in interaction.Values)
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var season))
                    {
                        return false;
                    }

                    seasons.Add(season);
                }

                if (values.SeriesSeasons == DiscordSeasonModes.All || (values.SeriesSeasons == DiscordSeasonModes.Single && seasons.Count != 1))
                {
                    await UpdateAsync(interaction, Text("Choose the seasons again; this server allows other season choices now.") with { Components = [] }, ct);
                    return true;
                }
            }
            else if (values.SeriesSeasons == DiscordSeasonModes.Single)
            {
                await UpdateAsync(interaction, Text("Request one season at a time on this server.") with { Components = [] }, ct);
                return true;
            }

            await DeferUpdateAsync(interaction, deferred, ct);
            await SubmitAsync(interaction, requester, MediaType.Series, seriesId, seasons, ct);
            return true;
        }

        return false;
    }

    private async Task ShowTitleAsync(DiscordInteraction interaction, DiscordSettingsValues values, MediaType type, int tmdbId, CancellationToken ct)
    {
        var details = await requests.DetailsAsync(type, tmdbId, ct);
        if (details is null)
        {
            await EditAsync(interaction, Text("TMDB does not know this title any more.") with { Components = [] }, ct);
            return;
        }

        var candidate = details.Candidate;
        var embed = ResultEmbed(candidate) with { Footer = new DiscordEmbedFooter(DiscordText.State(candidate.State)) };
        var key = type == MediaType.Movie ? "m" : "s";

        if (candidate.State.Available)
        {
            await EditAsync(interaction, Message($"**{DiscordText.Md(candidate.Title)}** is already available.", embed), ct);
            return;
        }

        if (type == MediaType.Movie)
        {
            if (candidate.State.Requested is { } status)
            {
                await EditAsync(interaction, Message($"**{DiscordText.Md(candidate.Title)}** was already requested: {DiscordText.Status(status).ToLowerInvariant()}.", embed), ct);
                return;
            }

            await EditAsync(
                interaction,
                Message("Request this movie?", embed) with
                {
                    Components = [DiscordComponent.Row(DiscordComponent.Button("Request", $"{RequestPrefix}{key}:{tmdbId.ToString(CultureInfo.InvariantCulture)}", style: 3))],
                },
                ct);
            return;
        }

        // Series: seasons that are available or requested already cannot be chosen again.
        var open = details.Seasons.Where(s => s.SeasonNumber > 0 && !s.Available && s.Requested is null).OrderBy(s => s.SeasonNumber).ToList();
        if (details.Seasons.Count > 0 && open.Count == 0)
        {
            await EditAsync(interaction, Message($"Every season of **{DiscordText.Md(candidate.Title)}** is available or requested already.", embed), ct);
            return;
        }

        var seasonLines = details.Seasons.Where(s => s.SeasonNumber > 0).OrderBy(s => s.SeasonNumber).Select(s =>
            $"Season {s.SeasonNumber.ToString(CultureInfo.InvariantCulture)}: {(s.Available ? "available" : s.Requested is { } r ? DiscordText.Status(r).ToLowerInvariant() : "not requested")}");
        embed = embed with { Fields = [new DiscordEmbedField("Seasons", DiscordText.Cut(string.Join('\n', seasonLines), 1024))] };

        var tmdb = tmdbId.ToString(CultureInfo.InvariantCulture);
        if (values.SeriesSeasons == DiscordSeasonModes.All || open.Count == 0)
        {
            await EditAsync(
                interaction,
                Message("Request this series (all seasons)?", embed) with
                {
                    Components = [DiscordComponent.Row(DiscordComponent.Button("Request all seasons", $"{RequestPrefix}{key}:{tmdb}", style: 3))],
                },
                ct);
            return;
        }

        var single = values.SeriesSeasons == DiscordSeasonModes.Single;
        var options = new List<DiscordSelectOption>();
        if (!single)
        {
            options.Add(new DiscordSelectOption("All seasons", AllSeasons, "Every season, including future ones"));
        }

        options.AddRange(open.Take(MaxSelectOptions - options.Count).Select(s => new DiscordSelectOption(
            DiscordText.Cut(s.Name is { Length: > 0 } name ? name : $"Season {s.SeasonNumber.ToString(CultureInfo.InvariantCulture)}", 100),
            s.SeasonNumber.ToString(CultureInfo.InvariantCulture),
            s.EpisodeCount > 0 ? $"{s.EpisodeCount.ToString(CultureInfo.InvariantCulture)} episodes" : null)));
        await EditAsync(
            interaction,
            Message(single ? "Which season do you want?" : "Which seasons do you want? You can choose several.", embed) with
            {
                Components =
                [
                    DiscordComponent.Row(DiscordComponent.Select(
                        SeasonsPrefix + tmdb, single ? "Choose a season" : "Choose seasons", options, 1, single ? 1 : options.Count)),
                ],
            },
            ct);
    }

    private async Task SubmitAsync(DiscordInteraction interaction, Requester requester, MediaType type, int tmdbId, IReadOnlyList<int>? seasons, CancellationToken ct)
    {
        var draft = new DiscordRequestDraft(type, tmdbId, seasons, requester.UserId, interaction.User.Id, interaction.User.DisplayName);
        var outcome = await requests.CreateAsync(draft, ct);
        var request = outcome.Request;
        var name = request is null ? "This title" : $"**{DiscordText.Md(DiscordText.Name(request.Title, request.Year))}**";
        var notify = settings.Values.DmRequesters ? " You'll get a direct message when it is available." : string.Empty;
        var (text, color) = outcome.Kind switch
        {
            RequestOutcomeKind.Created when request?.Status == RequestStatus.Pending =>
                ($"Requested {name}. An admin will look at it.{notify}", DiscordText.Amber),
            RequestOutcomeKind.Created => ($"Requested {name}. It is approved and StreamZH takes care of it.{notify}", DiscordText.Green),
            RequestOutcomeKind.AlreadyRequested =>
                ($"{name} was already requested{(request is null ? string.Empty : $": {DiscordText.Status(request.Status).ToLowerInvariant()}")}.", DiscordText.Grey),
            RequestOutcomeKind.AlreadyAvailable => ($"{name} is already available.", DiscordText.Green),
            RequestOutcomeKind.LimitReached => ("You reached your request limit.", DiscordText.Red),
            RequestOutcomeKind.NotAllowed => ("Your account may not request titles.", DiscordText.Red),
            RequestOutcomeKind.NotFound => ("TMDB does not know this title any more.", DiscordText.Red),
            _ => ("This request was refused.", DiscordText.Red),
        };

        if (outcome.Message is { Length: > 0 } message && outcome.Kind != RequestOutcomeKind.Created)
        {
            text = DiscordText.Md(message);
        }
        else if (outcome.Message is { Length: > 0 } extra)
        {
            text += " " + DiscordText.Md(extra);
        }

        if (outcome.Kind == RequestOutcomeKind.Created && request is not null)
        {
            logger.LogInformation(
                "Discord request by {DiscordName}{Account}: {Title} ({Status})",
                interaction.User.DisplayName,
                requester.Username is { } username ? $" ({username})" : " (guest)",
                DiscordText.Name(request.Title, request.Year),
                request.Status);
        }

        var embed = request is null
            ? null
            : new DiscordEmbed
            {
                Title = DiscordText.Cut(DiscordText.Name(request.Title, request.Year), 256),
                Url = DiscordText.TmdbUrl(request.MediaType, request.TmdbId),
                Color = color,
                Thumbnail = DiscordText.PosterUrl(request.PosterPath) is { } poster ? new DiscordEmbedMedia(poster) : null,
                Footer = new DiscordEmbedFooter(string.Join(" · ", new[] { DiscordText.Seasons(request.MediaType, request.Seasons), DiscordText.Status(request.Status) }.Where(p => p.Length > 0))),
            };
        await EditAsync(interaction, new DiscordMessage { Content = text, Embeds = embed is null ? [] : [embed], Components = [] }, ct);
    }

    // ---- Permissions ----

    /// <summary>Why this person may not request here (null = allowed); <paramref name="requester"/> is the linked StreamZH user or a guest.</summary>
    private string? Refusal(DiscordInteraction interaction, DiscordSettingsValues values, out Requester requester)
    {
        requester = new Requester(null, null);
        if (interaction.GuildId is { } guild && values.GuildIds.Count > 0 && !values.GuildIds.Contains(guild))
        {
            return "StreamZH requests are not set up for this server.";
        }

        if (!interaction.InGuild && !values.AllowDirectMessages)
        {
            return "Requests do not work in direct messages. Use the command in the server.";
        }

        if (interaction.InGuild && values.ChannelIds.Count > 0 && (interaction.ChannelId is null || !values.ChannelIds.Contains(interaction.ChannelId)))
        {
            return "Requests work in " + string.Join(", ", values.ChannelIds.Take(5).Select(c => $"<#{c}>")) + ".";
        }

        if (links.ForDiscord(interaction.User.Id) is { } link && users.FindById(link.UserId) is { } user)
        {
            // A disabled account stays blocked here too (it does not fall back to guest access).
            if (!user.Enabled)
            {
                return "Your StreamZH account is disabled. Ask an admin to enable it.";
            }

            requester = new Requester(user.Id, user.Username);
            return null;
        }

        if (!values.AllowGuests)
        {
            return "Link your StreamZH account first: in StreamZH open your profile → Discord → Create code, then run /link with that code here.";
        }

        if (values.GuestRoleIds.Count > 0)
        {
            if (!interaction.InGuild)
            {
                return "Without a linked StreamZH account, requests only work in the server.";
            }

            if (!interaction.MemberRoles.Any(values.GuestRoleIds.Contains))
            {
                return "You need one of these roles to request: " + string.Join(", ", values.GuestRoleIds.Take(5).Select(r => $"<@&{r}>"))
                    + ". Or link your StreamZH account with /link.";
            }
        }

        return null;
    }

    // ---- Answers ----

    private static MediaType? TypeOf(string key) => key switch
    {
        "m" => MediaType.Movie,
        "s" => MediaType.Series,
        _ => null,
    };

    private static DiscordEmbed ResultEmbed(RequestCandidate result) => new()
    {
        Title = DiscordText.Cut(DiscordText.Name(result.Title, result.Year), 256),
        Url = DiscordText.TmdbUrl(result.MediaType, result.TmdbId),
        Description = DiscordText.Cut(result.Overview, 300),
        Color = result.State.Available ? DiscordText.Green : result.State.Requested is { } s ? DiscordText.StatusColor(s) : DiscordText.Accent,
        Thumbnail = DiscordText.PosterUrl(result.PosterPath) is { } poster ? new DiscordEmbedMedia(poster) : null,
        Footer = new DiscordEmbedFooter(
            result.Rating is { } rating and > 0
                ? $"{DiscordText.State(result.State)} · ★ {rating.ToString("0.0", CultureInfo.InvariantCulture)}"
                : DiscordText.State(result.State)),
    };

    private static DiscordMessage Text(string text) => new() { Content = text, Flags = DiscordMessage.Ephemeral };

    private static DiscordMessage Message(string text, DiscordEmbed embed) => new() { Content = text, Embeds = [embed], Components = [] };

    private Task ReplyAsync(DiscordInteraction interaction, DiscordMessage message, CancellationToken ct) =>
        api.RespondAsync(interaction.Id, interaction.Token, new DiscordInteractionResponse(4, message with { Flags = DiscordMessage.Ephemeral }), ct);

    /// <summary>Replaces the message a button or menu sits on, at once.</summary>
    private Task UpdateAsync(DiscordInteraction interaction, DiscordMessage message, CancellationToken ct) =>
        api.RespondAsync(interaction.Id, interaction.Token, new DiscordInteractionResponse(7, message with { Flags = null }), ct);

    private async Task DeferUpdateAsync(DiscordInteraction interaction, Action deferred, CancellationToken ct)
    {
        await api.RespondAsync(interaction.Id, interaction.Token, DiscordInteractionResponse.DeferUpdate(), ct);
        deferred();
    }

    private Task EditAsync(DiscordInteraction interaction, DiscordMessage message, CancellationToken ct) =>
        api.EditOriginalAsync(interaction.ApplicationId, interaction.Token, message with { Flags = null, Embeds = message.Embeds ?? [] }, ct);

    private async Task TryShowErrorAsync(DiscordInteraction interaction, bool answered, string message, CancellationToken ct)
    {
        try
        {
            if (answered)
            {
                await EditAsync(interaction, new DiscordMessage { Content = message, Components = [] }, ct);
            }
            else
            {
                await ReplyAsync(interaction, Text(message), ct);
            }
        }
        catch (DiscordApiException ex)
        {
            logger.LogDebug("Could not tell the Discord user about the error ({Error})", ex.Message);
        }
    }
}
