// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging;
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
    IEventBus events,
    ILogger<AutomaticSearchService> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>
    /// Items grabbed this recently are not searched again. A placeholder until the download queue (0.5)
    /// can tell whether a grab is still downloading.
    /// </summary>
    public static readonly TimeSpan RecentGrabWindow = TimeSpan.FromHours(12);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Searches the movies and grabs the best release for each. Returns how many were grabbed.</summary>
    public async Task<int> SearchMoviesAsync(IReadOnlyCollection<int> movieIds, CancellationToken cancellationToken = default)
    {
        var grabbed = 0;
        foreach (var id in movieIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            grabbed += await SearchMovieCoreAsync(id, cancellationToken) ? 1 : 0;
        }

        await events.PublishAsync(new AutomaticSearchFinished(MediaType.Movie, movieIds.Count == 1 ? movieIds.First() : null, movieIds.Count, grabbed), cancellationToken);
        return grabbed;
    }

    /// <summary>Monitored movies without a file that are released (or have no known status).</summary>
    public async Task<int> SearchMissingMoviesAsync(CancellationToken cancellationToken = default)
    {
        var thisYear = _time.GetUtcNow().Year;
        var missing = movies.All()
            .Where(m => m.Monitored && m.File is null && (m.Status is "" or "Released" || (m.Year > 0 && m.Year < thisYear)))
            .Select(m => m.Id)
            .ToList();

        logger.LogInformation("Searching {Count} missing movies", missing.Count);
        var grabbed = 0;
        foreach (var id in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            grabbed += await SearchMovieCoreAsync(id, cancellationToken) ? 1 : 0;
        }

        await events.PublishAsync(new AutomaticSearchFinished(MediaType.Movie, null, missing.Count, grabbed), cancellationToken);
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
        await events.PublishAsync(new AutomaticSearchFinished(MediaType.Series, seriesId, wanted.Count, grabbed), cancellationToken);
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

        await events.PublishAsync(new AutomaticSearchFinished(MediaType.Series, seriesId, episodes.Count, grabbed), cancellationToken);
        return grabbed;
    }

    private async Task<bool> SearchMovieCoreAsync(int movieId, CancellationToken cancellationToken)
    {
        if (history.MovieHasEventSince(movieId, HistoryEventType.Grabbed, Since()))
        {
            logger.LogInformation("Movie {Id} was grabbed in the last {Hours} hours, skipping the search", movieId, RecentGrabWindow.TotalHours);
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
        var done = history.EpisodesWithEventSince(seriesId, HistoryEventType.Grabbed, Since());
        var open = wanted.Where(id => !done.Contains(id)).ToHashSet();
        if (open.Count == 0)
        {
            logger.LogInformation("Nothing to search in season {Season} of series {Id} (no wanted episodes or all grabbed recently)", seasonNumber, seriesId);
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

    private DateTime Since() => (_time.GetUtcNow() - RecentGrabWindow).UtcDateTime;
}
