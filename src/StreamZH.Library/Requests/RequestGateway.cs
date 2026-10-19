// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Data;
using StreamZH.Library.Discord;
using StreamZH.Library.Metadata;
using StreamZH.Library.Search;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Requests;

/// <summary>
/// The Discord bot's view of Discover and requests (<see cref="IRequestGateway"/>): search and details come from
/// <see cref="DiscoverService"/>, requests go through <see cref="RequestService"/> with source Discord, as the linked
/// StreamZH user or as a guest.
/// </summary>
public sealed class RequestGateway(
    IServiceScopeFactory scopes,
    UserRepository users,
    MetadataConfig metadata,
    AutomationHold hold,
    ILogger<RequestGateway> logger) : IRequestGateway
{
    /// <summary>The bot shows up to five results.</summary>
    public const int MaxResults = 5;

    public const string TmdbProblem = "TMDB cannot be reached right now. Try again in a moment.";

    public const string HoldMessage = "It was added to the library, but StreamZH does not search automatically right now (side-by-side mode).";

    public async Task<IReadOnlyList<RequestCandidate>> SearchAsync(MediaType mediaType, string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await using var scope = scopes.CreateAsyncScope();
        var discover = scope.ServiceProvider.GetRequiredService<DiscoverService>();
        try
        {
            var result = await Tmdb(() => discover.SearchAsync(query, 1, mediaType, cancellationToken));
            return [.. result.Results.Take(MaxResults).Select(Candidate)];
        }
        catch (TmdbNotFound)
        {
            return [];
        }
    }

    public async Task<RequestCandidateDetails?> DetailsAsync(MediaType mediaType, int tmdbId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var discover = scope.ServiceProvider.GetRequiredService<DiscoverService>();
        try
        {
            var details = await Tmdb(() => discover.DetailsAsync(mediaType, tmdbId, metadata.CertificationCountry, cancellationToken));
            return new RequestCandidateDetails(
                Candidate(details.Entry),
                [.. details.Seasons.Select(s => new RequestSeasonInfo(s.SeasonNumber, s.Name, s.EpisodeCount, s.Available, s.Requested))]);
        }
        catch (TmdbNotFound)
        {
            return null;
        }
    }

    public async Task<RequestOutcome> CreateAsync(DiscordRequestDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        RequestActor actor;
        if (draft.UserId is { } userId)
        {
            if (users.FindAccount(userId) is not { Enabled: true } account)
            {
                return new RequestOutcome(RequestOutcomeKind.NotAllowed, null, "Your StreamZH account cannot make requests.");
            }

            actor = new RequestActor(account.Id, account.Role, RequestSource.Discord, draft.DiscordUserId, draft.DiscordName);
        }
        else
        {
            actor = RequestActor.DiscordGuest(draft.DiscordUserId, draft.DiscordName);
        }

        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<RequestService>();
        try
        {
            var request = await service.CreateAsync(actor, new CreateRequestInput(draft.MediaType, draft.TmdbId, draft.Seasons), cancellationToken);
            var message = (request.Status is RequestStatus.Processing or RequestStatus.Approved) && hold.IsOn ? HoldMessage : null;
            return new RequestOutcome(RequestOutcomeKind.Created, request, message);
        }
        catch (RequestException ex)
        {
            return ex.Error switch
            {
                RequestError.AlreadyRequested => new RequestOutcome(RequestOutcomeKind.AlreadyRequested, ex.Existing, ex.Message),
                RequestError.AlreadyAvailable => new RequestOutcome(RequestOutcomeKind.AlreadyAvailable, null, ex.Message),
                RequestError.LimitReached => new RequestOutcome(RequestOutcomeKind.LimitReached, null, ex.Message),
                RequestError.Disabled or RequestError.Forbidden => new RequestOutcome(RequestOutcomeKind.NotAllowed, null, ex.Message),
                RequestError.NotFound => new RequestOutcome(RequestOutcomeKind.NotFound, null, ex.Message),
                _ => new RequestOutcome(RequestOutcomeKind.Invalid, null, ex.Message),
            };
        }
        catch (TmdbException ex)
        {
            logger.LogWarning("Discord request for TMDB {Id} failed: {Error}", draft.TmdbId, ex.Message);
            throw new RequestGatewayException(TmdbProblem, ex);
        }
    }

    private async Task<T> Tmdb<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (TmdbException ex) when (ex.StatusCode == 404)
        {
            throw new TmdbNotFound();
        }
        catch (TmdbException ex)
        {
            logger.LogWarning("Discord search or details failed: {Error}", ex.Message);
            throw new RequestGatewayException(
                ex.Message.StartsWith("TMDB is not set up", StringComparison.Ordinal) ? "StreamZH has no TMDB key yet; an admin has to add one." : TmdbProblem,
                ex);
        }
    }

    private static RequestCandidate Candidate(DiscoverEntry e) => new(
        e.MediaType,
        e.TmdbId,
        e.Title,
        e.Year,
        e.Overview,
        e.PosterPath,
        e.Rating,
        new RequestTitleState(e.State.InLibrary, e.State.Available, e.State.Requested, e.State.RequestId));

    private sealed class TmdbNotFound : Exception;
}
