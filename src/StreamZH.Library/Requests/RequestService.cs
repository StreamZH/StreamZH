// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only
// Portions based on Overseerr (MIT, Copyright (c) 2020 sct): the request lifecycle (pending → approved → processing →
// partially available / available), duplicate checks per season and request quotas follow its request service.

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Library.Search;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Requests;

/// <summary>
/// The life of a movie or series request (0.10): create (validation, duplicates, limits, auto-approve), approve (add to
/// the library or use the existing item, monitor the requested seasons, search unless side-by-side mode holds automatic
/// grabs), decline, cancel, retry. Every change publishes <see cref="RequestChanged"/>.
/// </summary>
public sealed class RequestService(
    RequestRepository requests,
    UserRepository users,
    RequestSettings settings,
    IRequestDefaults defaults,
    ITmdbClient tmdb,
    LibraryService library,
    MovieRepository movies,
    SeriesRepository series,
    RootFolderRepository rootFolders,
    AutomationHold hold,
    JobQueue jobs,
    RequestAvailability availability,
    IEventBus events,
    TimeProvider time,
    ILogger<RequestService> logger)
{
    public const int MaxNoteLength = 1000;
    public const int MaxReasonLength = 1000;

    /// <summary>Season numbers above this are typos.</summary>
    public const int MaxSeasonNumber = 1000;

    public const string HoldNote = "Side-by-side mode is on: StreamZH added it to the library but does not search automatically.";

    // One change at a time: two admins approving the same request, or a create racing the duplicate check. Held only for
    // the database work, never while waiting for TMDB or the library (1.0 RC): a slow TMDB must not block other requests.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Adding one title to the library, per media type and TMDB id (few entries: one per requested title).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(MediaType, int), SemaphoreSlim> TitleGates = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Creates a request. Seasons the series already has with files or that open requests cover are left out; when nothing
    /// is left the existing request (<see cref="RequestError.AlreadyRequested"/>) or <see cref="RequestError.AlreadyAvailable"/>
    /// is reported. Auto-approved requests are processed at once.
    /// </summary>
    /// <exception cref="RequestException">Invalid, disabled, forbidden, limit reached, duplicate or already available.</exception>
    public async Task<MediaRequest> CreateAsync(RequestActor actor, CreateRequestInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(input);
        var config = settings.Values;
        if (!config.Enabled)
        {
            throw new RequestException("Requests are switched off.", RequestError.Disabled);
        }

        var user = CurrentUser(actor);
        var note = Validate(input);

        // TMDB first (outside the gate): title, year, poster and the seasons that exist.
        string title;
        int year;
        string? poster;
        IReadOnlyList<int> knownSeasons = [];
        if (input.MediaType == MediaType.Movie)
        {
            var movie = await Lookup(() => tmdb.GetMovieAsync(input.TmdbId, cancellationToken));
            (title, year, poster) = (movie.Title, movie.Year, movie.PosterPath);
        }
        else
        {
            var show = await Lookup(() => tmdb.GetSeriesAsync(input.TmdbId, cancellationToken));
            (title, year, poster) = (show.Title, show.Year, show.PosterPath);
            knownSeasons = [.. show.SeasonNumbers.Where(s => s > 0).Distinct().Order()];
            if (input.Seasons?.FirstOrDefault(s => !knownSeasons.Contains(s)) is { } unknown and > 0)
            {
                throw new RequestException($"{title} has no season {unknown}.", RequestError.Invalid, field: "seasons");
            }
        }

        await Gate.WaitAsync(cancellationToken);
        MediaRequest created;
        try
        {
            var seasons = Remaining(input, knownSeasons, title);
            CheckLimit(actor, user, input.MediaType);

            var now = Now;
            created = new MediaRequest
            {
                MediaType = input.MediaType,
                TmdbId = input.TmdbId,
                Title = title,
                Year = year > 0 ? year : null,
                PosterPath = poster,
                Seasons = seasons,
                Status = RequestStatus.Pending,
                Source = actor.Source,
                RequestedBy = user?.Id,
                DiscordUserId = Clip(actor.DiscordUserId, 32),
                DiscordName = Clip(actor.DiscordName, 100),
                Note = note,
                Created = now,
                Updated = now,
            };
            created = created with { Id = requests.Add(created) };
        }
        finally
        {
            Gate.Release();
        }

        logger.LogInformation(
            "New request {Id}: {Type} '{Title}'{Seasons} by {User}",
            created.Id,
            created.MediaType == MediaType.Movie ? "movie" : "series",
            created.Title,
            created.Seasons is { } list ? $" seasons {string.Join(", ", list)}" : string.Empty,
            user?.Name ?? actor.DiscordName ?? "API");
        await events.PublishAsync(new RequestChanged(created.Id, created.Status), cancellationToken);

        var autoApprove = !actor.IsGuest && (user is null ? actor.IsAdmin : UserRequestPolicy.For(user, defaults).AutoApprove);
        var result = created;
        if (autoApprove)
        {
            try
            {
                // DecidedBy stays empty: nobody decided (the Discord bot sends no "approved" message then).
                result = await ApproveCoreAsync(created.Id, null, new ApproveRequestInput(), automatic: true, cancellationToken);
            }
            catch (RequestException ex) when (ex.Error is RequestError.Invalid or RequestError.Conflict)
            {
                // E.g. no root folder yet: the request waits for an admin, who sees the same message when approving.
                logger.LogWarning("Request {Id} '{Title}' could not be approved automatically: {Error}", created.Id, created.Title, ex.Message);
                result = requests.Find(created.Id) ?? created;
            }
        }

        // After the automatic approval, so the message says whether it waits for an admin.
        await events.PublishAsync(new RequestActivity(RequestActivityKind.Created, created.Id), cancellationToken);
        return result;
    }

    /// <summary>Approves a pending request and processes it (admins).</summary>
    public async Task<MediaRequest> ApproveAsync(RequestActor actor, int id, ApproveRequestInput? input = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireAdmin(actor);
        return await ApproveCoreAsync(id, actor.UserId is > 0 ? actor.UserId : null, input ?? new ApproveRequestInput(), automatic: false, cancellationToken);
    }

    /// <summary>Declines a pending request (admins).</summary>
    public async Task<MediaRequest> DeclineAsync(RequestActor actor, int id, string? reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireAdmin(actor);
        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (text?.Length > MaxReasonLength)
        {
            throw new RequestException($"Use at most {MaxReasonLength} characters.", RequestError.Invalid, field: "reason");
        }

        MediaRequest declined;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var request = Find(id);
            if (request.Status != RequestStatus.Pending)
            {
                throw new RequestException("Only pending requests can be declined.", RequestError.Conflict);
            }

            declined = request with
            {
                Status = RequestStatus.Declined, Reason = text, DecidedBy = actor.UserId is > 0 ? actor.UserId : null, Decided = Now, Updated = Now,
            };
            requests.Update(declined);
        }
        finally
        {
            Gate.Release();
        }

        logger.LogInformation("Request {Id} '{Title}' declined{Reason}", declined.Id, declined.Title, text is null ? string.Empty : $": {text}");
        await events.PublishAsync(new RequestChanged(declined.Id, declined.Status), cancellationToken);
        await events.PublishAsync(new RequestActivity(RequestActivityKind.Declined, declined.Id), cancellationToken);
        return declined;
    }

    /// <summary>Removes a request: admins any request, users their own pending ones. The library item stays.</summary>
    public async Task DeleteAsync(RequestActor actor, int id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        MediaRequest request;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            request = Find(id);
            if (!actor.IsAdmin)
            {
                if (!IsOwn(actor, request))
                {
                    // Somebody else's request: as if it did not exist.
                    throw new RequestException("Request not found.", RequestError.NotFound);
                }

                if (request.Status != RequestStatus.Pending)
                {
                    throw new RequestException("Only pending requests can be cancelled.", RequestError.Forbidden);
                }
            }

            requests.Delete(id);
        }
        finally
        {
            Gate.Release();
        }

        logger.LogInformation("Request {Id} '{Title}' removed", request.Id, request.Title);
        await events.PublishAsync(new RequestChanged(request.Id, request.Status), cancellationToken);
    }

    /// <summary>Processes a failed request again (admins).</summary>
    public async Task<MediaRequest> RetryAsync(RequestActor actor, int id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireAdmin(actor);
        MediaRequest request;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            request = Find(id);
            if (request.Status != RequestStatus.Failed)
            {
                throw new RequestException("Only failed requests can be retried.", RequestError.Conflict);
            }

            request = request with { Status = RequestStatus.Approved, Reason = null, ItemId = null, Updated = Now };
            requests.Update(request);
        }
        finally
        {
            Gate.Release();
        }

        // Outside the gate: adding the title waits for TMDB (up to 20 s) and must not block other requests.
        return await ProcessAsync(request, cancellationToken);
    }

    /// <summary>True when the actor may see the request (admins: all; others: their own).</summary>
    public static bool CanSee(RequestActor actor, MediaRequest request)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        return actor.IsAdmin || IsOwn(actor, request);
    }

    private static bool IsOwn(RequestActor actor, MediaRequest request) =>
        (actor.UserId is > 0 && request.RequestedBy == actor.UserId)
        || (actor.IsGuest && actor.DiscordUserId is { } discord && request.RequestedBy is null && request.DiscordUserId == discord);

    private async Task<MediaRequest> ApproveCoreAsync(int id, int? decidedBy, ApproveRequestInput input, bool automatic, CancellationToken cancellationToken)
    {
        MediaRequest request;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            request = Find(id);
            if (request.Status != RequestStatus.Pending)
            {
                throw new RequestException("Only pending requests can be approved.", RequestError.Conflict);
            }

            var seasons = request.Seasons;
            if (input.Seasons is not null)
            {
                if (request.MediaType == MediaType.Movie)
                {
                    throw new RequestException("Movies have no seasons.", RequestError.Invalid, field: "seasons");
                }

                seasons = ValidSeasons(input.Seasons);
            }

            var rootFolderId = RootFolderFor(request.MediaType, input.RootFolderId ?? request.RootFolderId);
            if (input.QualityProfileId is { } chosenProfile && !settings.ProfileExists(chosenProfile))
            {
                throw new RequestException("This quality profile does not exist.", RequestError.Invalid, field: "qualityProfileId");
            }

            var profileId = input.QualityProfileId ?? request.QualityProfileId
                ?? (request.MediaType == MediaType.Movie ? settings.Values.DefaultMovieProfileId : settings.Values.DefaultSeriesProfileId);

            request = request with
            {
                Status = RequestStatus.Approved,
                Seasons = seasons,
                QualityProfileId = profileId,
                RootFolderId = rootFolderId,
                DecidedBy = decidedBy,
                Decided = Now,
                Updated = Now,
            };
            requests.Update(request);
        }
        finally
        {
            Gate.Release();
        }

        // The request is "approved" now, so a second approval is refused; the rest runs outside the gate (TMDB, events).
        logger.LogInformation("Request {Id} '{Title}' approved{How}", request.Id, request.Title, automatic ? " automatically" : string.Empty);
        await events.PublishAsync(new RequestChanged(request.Id, request.Status), cancellationToken);
        if (!automatic)
        {
            await events.PublishAsync(new RequestActivity(RequestActivityKind.Approved, request.Id), cancellationToken);
        }

        return await ProcessAsync(request, cancellationToken);
    }

    /// <summary>Adds the title to the library (or uses the one there), monitors it, searches; Processing or Failed.</summary>
    private async Task<MediaRequest> ProcessAsync(MediaRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Two requests for the same title (web and Discord) add it once: the second finds the first one's item.
            var titleGate = TitleGates.GetOrAdd((request.MediaType, request.TmdbId), _ => new SemaphoreSlim(1, 1));
            int itemId;
            await titleGate.WaitAsync(cancellationToken);
            try
            {
                itemId = request.MediaType == MediaType.Movie
                    ? await AddMovieAsync(request, cancellationToken)
                    : await AddSeriesAsync(request, cancellationToken);
            }
            finally
            {
                titleGate.Release();
            }

            request = request with { Status = RequestStatus.Processing, ItemId = itemId, Reason = null, Updated = Now };
            requests.Update(request);
            await events.PublishAsync(new RequestChanged(request.Id, request.Status), cancellationToken);

            // Maybe everything is there already (an existing item with files).
            request = await availability.CheckAsync(request, cancellationToken);
            if (request.Status != RequestStatus.Available)
            {
                QueueSearch(request, itemId);
            }

            return request;
        }
        catch (Exception ex) when (ex is LibraryException or TmdbException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Request {Id} '{Title}' failed: {Error}", request.Id, request.Title, ex.Message);
            request = request with { Status = RequestStatus.Failed, Reason = Clip(ex.Message, MaxReasonLength), Updated = Now };
            requests.Update(request);
            await events.PublishAsync(new RequestChanged(request.Id, request.Status), cancellationToken);
            return request;
        }
    }

    private async Task<int> AddMovieAsync(MediaRequest request, CancellationToken cancellationToken)
    {
        if (movies.FindByTmdbId(request.TmdbId) is { } existing)
        {
            if (!existing.Monitored && existing.File is null)
            {
                await library.SetMovieMonitoredAsync(existing.Id, true);
            }

            return existing.Id;
        }

        var movie = await library.AddMovieAsync(request.TmdbId, request.RootFolderId!.Value, monitored: true, request.QualityProfileId, cancellationToken: cancellationToken);
        return movie.Id;
    }

    private async Task<int> AddSeriesAsync(MediaRequest request, CancellationToken cancellationToken)
    {
        var mode = settings.Values.SeriesMonitor;
        if (series.TmdbIdToId().TryGetValue(request.TmdbId, out var existingId))
        {
            if (series.Find(existingId) is { Monitored: false })
            {
                await library.SetSeriesMonitoredAsync(existingId, true);
            }

            // An existing series keeps what it monitors; the requested seasons are added to that.
            Monitor(existingId, request.Seasons, mode, onlyAdd: true);
            await events.PublishAsync(new LibraryChanged(MediaType.Series, existingId), cancellationToken);
            return existingId;
        }

        var item = await library.AddSeriesAsync(request.TmdbId, request.RootFolderId!.Value, monitored: true, request.QualityProfileId, cancellationToken: cancellationToken);
        try
        {
            await library.RefreshSeriesAsync(item.Id, manual: false, cancellationToken);
            Monitor(item.Id, request.Seasons, mode, onlyAdd: false);
            await events.PublishAsync(new LibraryChanged(MediaType.Series, item.Id), cancellationToken);
        }
        catch (TmdbException ex)
        {
            // The series is in the library; the next refresh loads the episodes.
            logger.LogWarning("Request {Id}: episodes of '{Title}' could not be loaded yet: {Error}", request.Id, item.Title, ex.Message);
        }

        return item.Id;
    }

    /// <summary>
    /// Monitors the requested seasons (null = all) per Settings → Requests "Monitor": requested seasons only, all seasons, or
    /// requested seasons plus everything that has not aired. Specials stay as they are. <paramref name="onlyAdd"/> never
    /// unmonitors (a series that was in the library before).
    /// </summary>
    private void Monitor(int seriesId, IReadOnlyList<int>? seasons, string mode, bool onlyAdd)
    {
        var today = availability.Today;
        bool Aired(Episode e) => e.AirDate is { } date && string.CompareOrdinal(date, today) <= 0;
        bool Want(Episode e) => e.SeasonNumber > 0 && mode switch
        {
            RequestSeriesMonitor.All => true,
            RequestSeriesMonitor.Future => seasons is null || seasons.Contains(e.SeasonNumber) || !Aired(e),
            _ => seasons is null || seasons.Contains(e.SeasonNumber),
        };

        foreach (var episode in series.Episodes(seriesId).Where(e => e.SeasonNumber > 0))
        {
            var want = Want(episode);
            if (episode.Monitored != want && (want || !onlyAdd))
            {
                series.SetEpisodeMonitored(episode.Id, want);
            }
        }
    }

    private void QueueSearch(MediaRequest request, int itemId)
    {
        if (!settings.Values.SearchOnApprove)
        {
            return;
        }

        if (hold.IsOn)
        {
            logger.LogInformation("Request {Id} '{Title}': {Note}", request.Id, request.Title, HoldNote);
            return;
        }

        var seasons = request.Seasons;
        jobs.Enqueue(request.MediaType == MediaType.Movie
            ? new Job($"search-new-movie:{itemId}", $"Search {request.Title}", (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchNewMovieAsync(itemId, ct))
            : new Job($"request-search:{request.Id}", $"Search requested seasons of {request.Title}", async (sp, ct) =>
            {
                var search = sp.GetRequiredService<AutomaticSearchService>();
                if (seasons is null)
                {
                    await search.SearchMissingEpisodesAsync(itemId, ct);
                    return;
                }

                foreach (var season in seasons)
                {
                    await search.SearchSeasonAsync(itemId, season, ct);
                }
            }));
    }

    /// <summary>The seasons to store: the requested ones minus available and already requested ones (null = all).</summary>
    private IReadOnlyList<int>? Remaining(CreateRequestInput input, IReadOnlyList<int> knownSeasons, string title)
    {
        var open = requests.Open(input.MediaType, input.TmdbId);
        if (input.MediaType == MediaType.Movie)
        {
            if (movies.FindByTmdbId(input.TmdbId) is { } movie && Of(movie) == LibraryAvailability.Full)
            {
                throw new RequestException($"{title} is already available.", RequestError.AlreadyAvailable);
            }

            if (open.Count > 0)
            {
                throw new RequestException($"{title} was already requested.", RequestError.AlreadyRequested, open[0]);
            }

            return null;
        }

        if (input.Seasons is null && knownSeasons.Count == 0)
        {
            // Announced, no season listed yet: the whole series.
            return open.Count > 0 ? throw new RequestException($"{title} was already requested.", RequestError.AlreadyRequested, open[0]) : null;
        }

        var wanted = input.Seasons is null ? knownSeasons.ToHashSet() : input.Seasons.ToHashSet();
        var available = series.TmdbIdToId().TryGetValue(input.TmdbId, out var seriesId)
            ? RequestAvailability.AvailableSeasons(series.Episodes(seriesId), availability.Today)
            : new HashSet<int>();
        var requested = open.SelectMany(r => r.Seasons ?? knownSeasons).ToHashSet();
        var remaining = wanted.Where(s => !available.Contains(s) && !requested.Contains(s)).Order().ToList();

        if (remaining.Count == 0)
        {
            // Nothing left: an open request covers it, or it is all there already.
            if (open.FirstOrDefault(r => r.Seasons is null || r.Seasons.Any(wanted.Contains)) is { } existing)
            {
                throw new RequestException($"{title} was already requested.", RequestError.AlreadyRequested, existing);
            }

            throw new RequestException($"{title} is already available.", RequestError.AlreadyAvailable);
        }

        // "All seasons" with nothing taken away stays "all", so seasons that come later are part of it.
        return input.Seasons is null && remaining.Count == wanted.Count && open.Count == 0 ? null : remaining;
    }

    private static LibraryAvailability Of(Movie movie) => RequestAvailability.Of(movie);

    private void CheckLimit(RequestActor actor, UserAccount? user, MediaType mediaType)
    {
        if (user is null || actor.IsGuest)
        {
            return;
        }

        var allowance = UserRequestPolicy.For(user, defaults);
        if (allowance.LimitFor(mediaType) is not { } limit)
        {
            return;
        }

        var since = Now.AddDays(-allowance.LimitDays);
        var used = requests.CountSince(user.Id, mediaType, since);
        if (used < limit)
        {
            return;
        }

        var kind = mediaType == MediaType.Movie ? (limit == 1 ? "movie" : "movies") : "series";
        var window = allowance.LimitDays == 1 ? "day" : $"{allowance.LimitDays.ToString(CultureInfo.InvariantCulture)} days";
        var message = $"You can request {limit.ToString(CultureInfo.InvariantCulture)} {kind} every {window}.";
        var oldest = requests.Query(new RequestQuery(null, user.Id, mediaType, 1, 500)).Records
            .Where(r => r.Created >= since && r.Status != RequestStatus.Declined)
            .Select(r => (DateTime?)r.Created)
            .Min();
        if (limit > 0 && oldest is { } first)
        {
            message += $" Your next request is possible on {first.AddDays(allowance.LimitDays).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC.";
        }

        throw new RequestException(message, RequestError.LimitReached, field: "limit");
    }

    private UserAccount? CurrentUser(RequestActor actor)
    {
        if (actor.UserId is not > 0)
        {
            if (actor.IsGuest && string.IsNullOrWhiteSpace(actor.DiscordUserId))
            {
                throw new RequestException("A Discord user is needed.", RequestError.Invalid);
            }

            return null;
        }

        var user = users.FindAccount(actor.UserId.Value);
        if (user is not { Enabled: true })
        {
            throw new RequestException("This account cannot make requests.", RequestError.Forbidden);
        }

        return user;
    }

    private static string? Validate(CreateRequestInput input)
    {
        if (input.MediaType is not (MediaType.Movie or MediaType.Series))
        {
            throw new RequestException("Use movie or series.", RequestError.Invalid, field: "mediaType");
        }

        if (input.TmdbId <= 0)
        {
            throw new RequestException("A TMDB id is needed.", RequestError.Invalid, field: "tmdbId");
        }

        if (input.MediaType == MediaType.Movie && input.Seasons is not null)
        {
            throw new RequestException("Movies have no seasons.", RequestError.Invalid, field: "seasons");
        }

        if (input.Seasons is not null)
        {
            _ = ValidSeasons(input.Seasons);
        }

        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        if (note?.Length > MaxNoteLength)
        {
            throw new RequestException($"Use at most {MaxNoteLength} characters.", RequestError.Invalid, field: "note");
        }

        return note;
    }

    private static IReadOnlyList<int> ValidSeasons(IReadOnlyList<int> seasons)
    {
        if (seasons.Count == 0)
        {
            throw new RequestException("Choose at least one season.", RequestError.Invalid, field: "seasons");
        }

        if (seasons.Any(s => s is < 1 or > MaxSeasonNumber))
        {
            throw new RequestException("Use season numbers from 1 (specials cannot be requested).", RequestError.Invalid, field: "seasons");
        }

        return [.. seasons.Distinct().Order()];
    }

    private int RootFolderFor(MediaType mediaType, int? requested)
    {
        var id = requested ?? (mediaType == MediaType.Movie ? settings.Values.DefaultMovieRootFolderId : settings.Values.DefaultSeriesRootFolderId);
        if (id is { } chosen)
        {
            var root = rootFolders.Find(chosen);
            if (root is null || root.MediaType != mediaType)
            {
                throw new RequestException("This root folder does not exist or is for the other media type.", RequestError.Invalid, field: "rootFolderId");
            }

            return chosen;
        }

        return rootFolders.All().Where(r => r.MediaType == mediaType).OrderBy(r => r.Id).Select(r => (int?)r.Id).FirstOrDefault()
            ?? throw new RequestException(
                mediaType == MediaType.Movie ? "Add a root folder for movies first (Settings → Media management)." : "Add a root folder for series first (Settings → Media management).",
                RequestError.Invalid,
                field: "rootFolderId");
    }

    private static void RequireAdmin(RequestActor actor)
    {
        if (!actor.IsAdmin)
        {
            throw new RequestException("Only admins can do this.", RequestError.Forbidden);
        }
    }

    private MediaRequest Find(int id) => requests.Find(id) ?? throw new RequestException("Request not found.", RequestError.NotFound);

    private static async Task<T> Lookup<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (TmdbException ex) when (ex.StatusCode == 404)
        {
            throw new RequestException("Not found on TMDB.", RequestError.NotFound, field: "tmdbId");
        }
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
