// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only
// Portions based on Prowlarr (https://github.com/Prowlarr/Prowlarr), Copyright (c) Prowlarr contributors, GPL-3.0.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Indexers;
using StreamZH.Data;

namespace StreamZH.Library.Indexers;

/// <summary>
/// Wraps the protocol client: records every call of a saved indexer in the indexer history and enforces its query and
/// grab limits over a rolling window (Prowlarr's IndexerLimitService). At the limit no request is sent and an
/// <see cref="IndexerException"/> with <see cref="IndexerException.IsLimitReached"/> is thrown; that is not a failure
/// of the indexer, so it does not start a back-off.
/// The source of a call comes from <see cref="IndexerCallContext"/>.
/// </summary>
public sealed class TrackedIndexerClient(
    IIndexerClient inner,
    IndexerHistoryRepository history,
    TimeProvider time,
    ILogger<TrackedIndexerClient> logger,
    IndexerService? notifier = null) : IIndexerClient
{
    private const int MaxTextLength = 500;

    public Task<IndexerCapabilities> GetCapabilitiesAsync(IndexerDefinition indexer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        return TrackAsync(
            indexer,
            IndexerHistoryEventType.Caps,
            IndexerCallSource.Test,
            query: null,
            title: null,
            LimitKind.None,
            () => inner.GetCapabilitiesAsync(indexer, cancellationToken),
            _ => null);
    }

    public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(IndexerDefinition indexer, SearchCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(criteria);
        return TrackAsync(
            indexer,
            IndexerHistoryEventType.Query,
            IndexerCallSource.Interactive,
            Describe(criteria),
            title: null,
            LimitKind.Query,
            () => inner.SearchAsync(indexer, criteria, cancellationToken),
            r => r.Count);
    }

    public Task<IReadOnlyList<ReleaseInfo>> FetchRecentAsync(IndexerDefinition indexer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        return TrackAsync(
            indexer,
            IndexerHistoryEventType.Rss,
            IndexerCallSource.Rss,
            query: null,
            title: null,
            LimitKind.Query,
            () => inner.FetchRecentAsync(indexer, cancellationToken),
            r => r.Count);
    }

    public Task<byte[]> DownloadNzbAsync(IndexerDefinition indexer, ReleaseInfo release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(release);
        return TrackAsync(
            indexer,
            IndexerHistoryEventType.Grab,
            IndexerCallSource.Interactive,
            query: null,
            release.Title,
            LimitKind.Grab,
            () => inner.DownloadNzbAsync(indexer, release, cancellationToken),
            _ => null);
    }

    /// <summary>"Dune: Part Two (2024) tmdbid=693134", "The Office S02E05", "The Office S02", "Daily Show 2024-03-01".</summary>
    public static string Describe(SearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var text = new StringBuilder(criteria.Titles.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim() ?? string.Empty);
        void Add(string part)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(part);
        }

        switch (criteria)
        {
            case MovieSearchCriteria movie:
                if (movie.Year > 0)
                {
                    Add(string.Create(CultureInfo.InvariantCulture, $"({movie.Year})"));
                }

                if (movie.TmdbId > 0)
                {
                    Add(string.Create(CultureInfo.InvariantCulture, $"tmdbid={movie.TmdbId}"));
                }

                if (!string.IsNullOrWhiteSpace(movie.ImdbId))
                {
                    Add($"imdbid={movie.ImdbId.Trim()}");
                }

                break;

            case EpisodeSearchCriteria episode:
                if (episode.AirDate is { } airDate)
                {
                    Add(airDate);
                }
                else
                {
                    var numbers = string.Concat(episode.EpisodeNumbers.Order().Select(e => string.Create(CultureInfo.InvariantCulture, $"E{e:00}")));
                    Add(string.Create(CultureInfo.InvariantCulture, $"S{episode.SeasonNumber:00}{numbers}"));
                }

                if (episode.TvdbId > 0)
                {
                    Add(string.Create(CultureInfo.InvariantCulture, $"tvdbid={episode.TvdbId}"));
                }

                break;
        }

        return text.ToString();
    }

    /// <summary>Throws when the limit is reached; returns the calls already made in the window (null: no limit).</summary>
    private int? CheckLimit(IndexerDefinition indexer, bool grab)
    {
        var limit = grab ? indexer.GrabLimit : indexer.QueryLimit;
        if (indexer.Id <= 0 || limit is not > 0)
        {
            return null;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var window = indexer.LimitsWindow;
        var since = now - window;
        var used = grab ? history.CountGrabsSince(indexer.Id, since) : history.CountQueriesSince(indexer.Id, since);
        if (used < limit)
        {
            return used;
        }

        TimeSpan? retryAfter = history.OldestSince(indexer.Id, since, grab) is { } oldest && oldest + window > now ? oldest + window - now : null;
        var unit = indexer.LimitsUnit == IndexerLimitsUnit.Hour ? "hour" : "day";
        var message = string.Create(CultureInfo.InvariantCulture, $"{(grab ? "Grab" : "Query")} limit reached ({limit} per {unit})");
        logger.LogInformation("Indexer '{Name}': {Message}, {Used} used in the last {Hours} hours", indexer.Name, message, used, window.TotalHours);
        throw new IndexerException(message, retryAfter: retryAfter) { IsLimitReached = true };
    }

    private async Task<T> TrackAsync<T>(
        IndexerDefinition indexer,
        IndexerHistoryEventType eventType,
        IndexerCallSource defaultSource,
        string? query,
        string? title,
        LimitKind limitKind,
        Func<Task<T>> call,
        Func<T, int?> results)
    {
        if (indexer.Id <= 0)
        {
            return await call();
        }

        var limit = limitKind == LimitKind.None ? null : CheckLimit(indexer, limitKind == LimitKind.Grab);
        var source = IndexerCallContext.Current ?? defaultSource;
        var started = time.GetTimestamp();
        try
        {
            var result = await call();
            Record(indexer, eventType, source, started, query, results(result), title, null, limit);
            return result;
        }
        catch (Exception ex)
        {
            var error = ex is OperationCanceledException ? "The request was canceled or timed out." : ex.Message;
            Record(indexer, eventType, source, started, query, null, title, error, limit);
            throw;
        }
    }

    private void Record(
        IndexerDefinition indexer, IndexerHistoryEventType eventType, IndexerCallSource source, long started,
        string? query, int? results, string? title, string? error, int? usedBefore)
    {
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            history.Add(new IndexerHistoryEntry
            {
                Date = now,
                IndexerId = indexer.Id,
                EventType = eventType,
                Successful = error is null,
                ElapsedMs = (int)Math.Min(int.MaxValue, Math.Max(0, time.GetElapsedTime(started).TotalMilliseconds)),
                Source = source,
                Query = Truncate(query),
                Results = results,
                Title = Truncate(title),
                Error = Truncate(error),
            });
            history.PruneIfDue(now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not record the {EventType} call to indexer '{Name}': {Message}", eventType, indexer.Name, ex.Message);
            return;
        }

        // This call used up the limit: the status in the UI changes.
        var limit = eventType == IndexerHistoryEventType.Grab ? indexer.GrabLimit : indexer.QueryLimit;
        if (usedBefore is { } used && limit is > 0 && used + 1 >= limit)
        {
            notifier?.NotifyChanged();
        }
    }

    private static string? Truncate(string? text) =>
        text is { Length: > MaxTextLength } ? text[..MaxTextLength] : text;

    private enum LimitKind
    {
        None,
        Query,
        Grab,
    }
}
