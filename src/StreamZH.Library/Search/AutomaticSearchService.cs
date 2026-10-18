// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.Search;

/// <summary>
/// Searches and grabs the best accepted release without asking, like Radarr/Sonarr's automatic search
/// (ProcessDownloadDecisions): the best release per item wins; for episodes, releases that overlap with
/// one already grabbed in this run are skipped. If an indexer fails to deliver the NZB, the next release
/// from another indexer is tried.
/// </summary>
public sealed class AutomaticSearchService(
    ReleaseSearchService search,
    GrabService grab,
    MovieRepository movies,
    SeriesRepository series,
    HistoryRepository history,
    DownloadSettings downloadSettings,
    IEventBus events,
    ILogger<AutomaticSearchService> logger,
    IDownloadQueue? queue = null,
    TimeProvider? timeProvider = null,
    IndexerOptions? options = null,
    PendingImports? pendingImports = null,
    LastSearchRepository? lastSearches = null)
{
    /// <summary>
    /// With the blackhole folder StreamZH cannot see whether a grab is still downloading: items grabbed this recently are
    /// not searched again. With the built-in engine only items that are in its queue are skipped (like Radarr's "already
    /// in the queue"), so a grab that went to the folder, failed or was removed does not block a new search.
    /// </summary>
    public static readonly TimeSpan RecentGrabWindow = PendingGrabs.RecentGrabWindow;

    public const string SkipInQueue = PendingGrabs.SkipInQueue;
    public const string SkipRecentlyGrabbed = PendingGrabs.SkipRecentlyGrabbed;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly PendingGrabs _pending = new(history, downloadSettings, queue, timeProvider ?? TimeProvider.System, pendingImports);

    // Items of the current run that were skipped, and why (for the "Search" button's note).
    private int _skipped;
    private string? _skipReason;

    private bool UsesQueue => _pending.UsesQueue;

    private string? MovieSkipReason(int movieId) => _pending.MovieSkipReason(movieId);

    /// <summary>Episodes of the series that are not searched now: in the queue (built-in engine) or grabbed recently (folder).</summary>
    private IReadOnlySet<int> BlockedEpisodes(int seriesId) => _pending.BlockedEpisodes(seriesId);

    /// <summary>Minimum availability from Settings → Indexers (In cinemas with the default settings).</summary>
    private bool IsAvailable(Movie movie, DateTimeOffset now) =>
        options?.IsAvailable(movie, now)
        ?? IndexerOptions.AvailableFrom(movie, IndexerOptions.Defaults.MinimumAvailability, 0, now) is null;

    private void Skip(string reason, int count = 1)
    {
        _skipped += count;
        _skipReason = reason;
    }

    private AutomaticSearchFinished Finished(MediaType type, int? id, int searched, int grabbed)
    {
        var finished = new AutomaticSearchFinished(type, id, searched, grabbed, _skipped, _skipReason);
        _skipped = 0;
        _skipReason = null;
        return finished;
    }

    /// <summary>Searches the movies and grabs the best release for each. Returns how many were grabbed.</summary>
    public async Task<int> SearchMoviesAsync(IReadOnlyCollection<int> movieIds, CancellationToken cancellationToken = default)
    {
        var grabbed = 0;
        foreach (var id in movieIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            grabbed += await SearchMovieCoreAsync(id, cancellationToken) ? 1 : 0;
        }

        await events.PublishAsync(Finished(MediaType.Movie, movieIds.Count == 1 ? movieIds.First() : null, movieIds.Count, grabbed), cancellationToken);
        return grabbed;
    }

    /// <summary>Monitored movies without a file that are available (minimum availability, Settings → Indexers).</summary>
    public async Task<int> SearchMissingMoviesAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var missing = movies.All()
            .Where(m => m.Monitored && m.File is null && IsAvailable(m, now))
            .Select(m => m.Id)
            .ToList();

        logger.LogInformation("Searching {Count} missing movies", missing.Count);
        var grabbed = 0;
        foreach (var id in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            grabbed += await SearchMovieCoreAsync(id, cancellationToken) ? 1 : 0;
        }

        await events.PublishAsync(Finished(MediaType.Movie, null, missing.Count, grabbed), cancellationToken);
        return grabbed;
    }

    /// <summary>Monitored, aired episodes of one season: season packs and single episodes.</summary>
    public async Task<int> SearchSeasonAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken = default)
    {
        var today = _time.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var wanted = series.Episodes(seriesId)
            .Where(e => e.SeasonNumber == seasonNumber && e.Monitored && e.AirDate is { } date && string.CompareOrdinal(date, today) <= 0)
            .Select(e => e.Id)
            .ToHashSet();

        var grabbed = await SearchEpisodesCoreAsync(seriesId, seasonNumber, null, wanted, cancellationToken);
        await events.PublishAsync(Finished(MediaType.Series, seriesId, wanted.Count, grabbed), cancellationToken);
        return grabbed;
    }

    /// <summary>
    /// A newly added movie ("Start search for missing movie"): searched only when it has no file and is available
    /// (minimum availability), otherwise RSS picks it up later. Returns how many were grabbed (0 or 1).
    /// </summary>
    public async Task<int> SearchNewMovieAsync(int movieId, CancellationToken cancellationToken = default)
    {
        if (movies.Find(movieId) is not { } movie || movie.File is not null || !movie.Monitored)
        {
            return 0;
        }

        if (!IsAvailable(movie, _time.GetUtcNow()))
        {
            logger.LogInformation("'{Title}' is not available yet (minimum availability), RSS sync will grab it later", movie.Title);
            return 0;
        }

        return await SearchMoviesAsync([movieId], cancellationToken);
    }

    /// <summary>
    /// Monitored, aired episodes without a file of one series (Sonarr's "Search for missing episodes"; used after adding a
    /// series and by the scheduled missing search). A season with several missing episodes is searched as a season (packs
    /// too), a single missing episode on its own.
    /// </summary>
    public async Task<int> SearchMissingEpisodesAsync(int seriesId, CancellationToken cancellationToken = default)
    {
        var grabbed = 0;
        var searched = 0;
        if (series.Find(seriesId) is { Monitored: true })
        {
            var today = _time.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var missing = series.Episodes(seriesId)
                .Where(e => e.Monitored && e.File is null && e.SeasonNumber > 0 && e.AirDate is { } date && string.CompareOrdinal(date, today) <= 0)
                .GroupBy(e => e.SeasonNumber)
                .OrderBy(g => g.Key);

            foreach (var season in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wanted = season.Select(e => e.Id).ToHashSet();
                searched += wanted.Count;
                grabbed += await SearchEpisodesCoreAsync(seriesId, season.Key, wanted.Count == 1 ? [.. wanted] : null, wanted, cancellationToken);
            }
        }

        await events.PublishAsync(Finished(MediaType.Series, seriesId, searched, grabbed), cancellationToken);
        return grabbed;
    }

    /// <summary>Specific episodes (any seasons of one series); monitored or not, the user asked for them.</summary>
    public async Task<int> SearchEpisodesAsync(int seriesId, IReadOnlyCollection<int> episodeIds, CancellationToken cancellationToken = default)
    {
        var grabbed = 0;
        var episodes = series.Episodes(seriesId).Where(e => episodeIds.Contains(e.Id)).ToList();
        foreach (var season in episodes.GroupBy(e => e.SeasonNumber))
        {
            var ids = season.Select(e => e.Id).ToList();
            grabbed += await SearchEpisodesCoreAsync(seriesId, season.Key, ids, ids.ToHashSet(), cancellationToken);
        }

        await events.PublishAsync(Finished(MediaType.Series, seriesId, episodes.Count, grabbed), cancellationToken);
        return grabbed;
    }

    private async Task<bool> SearchMovieCoreAsync(int movieId, CancellationToken cancellationToken)
    {
        if (MovieSkipReason(movieId) is { } reason)
        {
            if (reason == SkipInQueue)
            {
                logger.LogInformation("Movie {Id} is already in the download queue, skipping the search", movieId);
            }
            else
            {
                logger.LogInformation("Movie {Id} was grabbed in the last {Hours} hours, skipping the search", movieId, RecentGrabWindow.TotalHours);
            }

            Skip(reason);
            return false;
        }

        ReleaseSearchResult result;
        try
        {
            result = await search.SearchMovieAsync(movieId, IndexerUse.AutomaticSearch, cancellationToken);
        }
        catch (LibraryException ex)
        {
            logger.LogWarning("Search for movie {Id} skipped: {Message}", movieId, ex.Message);
            return false;
        }

        lastSearches?.MarkMovies([movieId], _time.GetUtcNow().UtcDateTime);

        var accepted = result.Decisions.Where(d => d.Accepted).ToList();
        if (accepted.Count == 0)
        {
            logger.LogInformation("No acceptable release for movie {Id} ({Count} results)", movieId, result.Decisions.Count);
            return false;
        }

        return await GrabFirstAsync(accepted, cancellationToken) is not null;
    }

    private async Task<int> SearchEpisodesCoreAsync(
        int seriesId, int seasonNumber, IReadOnlyCollection<int>? episodeIds, IReadOnlySet<int> wanted, CancellationToken cancellationToken)
    {
        var done = BlockedEpisodes(seriesId);
        var open = wanted.Where(id => !done.Contains(id)).ToHashSet();
        var blocked = wanted.Count - open.Count;
        if (blocked > 0)
        {
            Skip(UsesQueue ? SkipInQueue : SkipRecentlyGrabbed, blocked);
        }

        if (open.Count == 0)
        {
            logger.LogInformation(
                "Nothing to search in season {Season} of series {Id} (no wanted episodes, or all {Reason})",
                seasonNumber, seriesId, UsesQueue ? "in the download queue" : "grabbed recently");
            return 0;
        }

        ReleaseSearchResult result;
        try
        {
            result = await search.SearchEpisodesAsync(seriesId, seasonNumber, episodeIds, IndexerUse.AutomaticSearch, cancellationToken);
        }
        catch (LibraryException ex)
        {
            logger.LogWarning("Search for season {Season} of series {Id} skipped: {Message}", seasonNumber, seriesId, ex.Message);
            return 0;
        }

        lastSearches?.MarkEpisodes(open, _time.GetUtcNow().UtcDateTime);

        // Best first; a release only counts if it brings a wanted episode and touches none already taken.
        var taken = new HashSet<int>(done);
        var grabbed = 0;
        var failedIndexers = new HashSet<int>();
        foreach (var decision in result.Decisions.Where(d => d.Accepted))
        {
            if (decision.EpisodeIds.Count == 0 || decision.EpisodeIds.Any(taken.Contains) || !decision.EpisodeIds.Any(open.Contains)
                || failedIndexers.Contains(decision.Release.IndexerId))
            {
                continue;
            }

            if (await TryGrabAsync(decision, cancellationToken))
            {
                grabbed++;
                taken.UnionWith(decision.EpisodeIds);
            }
            else
            {
                failedIndexers.Add(decision.Release.IndexerId);
            }

            if (open.All(taken.Contains))
            {
                break;
            }
        }

        if (grabbed == 0)
        {
            logger.LogInformation("No acceptable release for season {Season} of series {Id} ({Count} results)", seasonNumber, seriesId, result.Decisions.Count);
        }

        return grabbed;
    }

    /// <summary>Grabs the first release that works; after a failed NZB download the rest of that indexer is skipped.</summary>
    private async Task<ReleaseDecision?> GrabFirstAsync(IEnumerable<ReleaseDecision> ranked, CancellationToken cancellationToken)
    {
        var failedIndexers = new HashSet<int>();
        foreach (var decision in ranked)
        {
            if (failedIndexers.Contains(decision.Release.IndexerId))
            {
                continue;
            }

            if (await TryGrabAsync(decision, cancellationToken))
            {
                return decision;
            }

            failedIndexers.Add(decision.Release.IndexerId);
        }

        return null;
    }

    private async Task<bool> TryGrabAsync(ReleaseDecision decision, CancellationToken cancellationToken)
    {
        using var source = IndexerCallContext.Begin(IndexerCallSource.Automatic);
        try
        {
            await grab.GrabAsync(decision, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IndexerException or LibraryException)
        {
            logger.LogWarning("Grabbing '{Title}' failed: {Message}", decision.Release.Title, ex.Message);
            return false;
        }
    }

}
