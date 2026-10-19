// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Requests;

namespace StreamZH.Library.Discord;

/// <summary>Library and request state of a title, as the discover service reports it.</summary>
/// <param name="InLibrary">The movie/series is in the library (monitored or not).</param>
/// <param name="Available">Movie: has a file. Series: every aired episode has a file.</param>
/// <param name="Requested">Status of the open request for it, if any.</param>
public sealed record RequestTitleState(bool InLibrary, bool Available, RequestStatus? Requested, int? RequestId);

/// <summary>One search result (TMDB title).</summary>
/// <param name="PosterPath">TMDB poster path ("/abc.jpg"); Discord fetches it from TMDB's image server itself.</param>
public sealed record RequestCandidate(
    MediaType MediaType, int TmdbId, string Title, int? Year, string? Overview, string? PosterPath, double? Rating, RequestTitleState State);

/// <summary>A season with its state (series only).</summary>
/// <param name="Available">Every aired episode of the season has a file.</param>
/// <param name="Requested">Status of an open request that covers the season, if any.</param>
public sealed record RequestSeasonInfo(int SeasonNumber, string? Name, int EpisodeCount, bool Available, RequestStatus? Requested);

/// <summary>Details of one title; <see cref="Seasons"/> is empty for movies and leaves out specials (season 0).</summary>
public sealed record RequestCandidateDetails(RequestCandidate Candidate, IReadOnlyList<RequestSeasonInfo> Seasons);

/// <summary>A request from Discord.</summary>
/// <param name="Seasons">null = all seasons (or a movie).</param>
/// <param name="UserId">The linked StreamZH user (limits, auto-approve of their role); null = Discord guest (pending, needs approval).</param>
public sealed record DiscordRequestDraft(
    MediaType MediaType, int TmdbId, IReadOnlyList<int>? Seasons, int? UserId, string DiscordUserId, string DiscordName);

public enum RequestOutcomeKind
{
    /// <summary>Stored; <see cref="RequestOutcome.Request"/> holds it (pending, or approved when auto-approved).</summary>
    Created,

    /// <summary>An open request for the title (and seasons) exists; <see cref="RequestOutcome.Request"/> is it.</summary>
    AlreadyRequested,

    /// <summary>Already in the library and available.</summary>
    AlreadyAvailable,

    /// <summary>The user's request limit is used up; the message says until when.</summary>
    LimitReached,

    /// <summary>The user may not request (disabled, no permission, requests switched off).</summary>
    NotAllowed,

    NotFound,

    /// <summary>Anything else that was refused; the message says why.</summary>
    Invalid,
}

/// <param name="Message">A short sentence for the user (limit and its reset time, why it was refused); null = the bot's default text.</param>
public sealed record RequestOutcome(RequestOutcomeKind Kind, MediaRequest? Request = null, string? Message = null);

/// <summary>
/// What the Discord bot needs from discover and requests (agent F's RequestService and discover service). The bot only
/// talks to this interface; <see cref="RequestsNotReadyGateway"/> stands in until the adapter to those services is
/// registered.
/// </summary>
public interface IRequestGateway
{
    /// <summary>Searches TMDB (movies or series), best matches first, with library and request state.</summary>
    Task<IReadOnlyList<RequestCandidate>> SearchAsync(MediaType mediaType, string query, CancellationToken cancellationToken);

    /// <summary>Details incl. seasons with their state; null when TMDB does not know the id.</summary>
    Task<RequestCandidateDetails?> DetailsAsync(MediaType mediaType, int tmdbId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a request with source Discord: as the linked user (their limits and auto-approve), or as a guest
    /// (RequestedBy null, DiscordUserId/DiscordName set, always pending). Publishes RequestChanged like a web request.
    /// </summary>
    Task<RequestOutcome> CreateAsync(DiscordRequestDraft draft, CancellationToken cancellationToken);
}

/// <summary>The gateway could not do it; <see cref="Exception.Message"/> is a short sentence the bot shows the user (TMDB down, requests off).</summary>
public sealed class RequestGatewayException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Placeholder until the requests service is wired in: searching works nowhere, every action says so.</summary>
public sealed class RequestsNotReadyGateway : IRequestGateway
{
    public const string Message = "Requests are not available in this StreamZH version yet.";

    public Task<IReadOnlyList<RequestCandidate>> SearchAsync(MediaType mediaType, string query, CancellationToken cancellationToken) =>
        throw new RequestGatewayException(Message);

    public Task<RequestCandidateDetails?> DetailsAsync(MediaType mediaType, int tmdbId, CancellationToken cancellationToken) =>
        throw new RequestGatewayException(Message);

    public Task<RequestOutcome> CreateAsync(DiscordRequestDraft draft, CancellationToken cancellationToken) =>
        Task.FromResult(new RequestOutcome(RequestOutcomeKind.NotAllowed, null, Message));
}
