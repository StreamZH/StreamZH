// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Decisions;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.Search;

/// <summary>
/// A release an automatic flow would have grabbed, held back by the protocol delay (Radarr's pending release).
/// <see cref="EpisodeIds"/> is empty for movies.
/// </summary>
public sealed record PendingRelease(
    string Id,
    MediaType MediaType,
    int? MovieId,
    int? SeriesId,
    IReadOnlyList<int> EpisodeIds,
    string Subject,
    ReleaseInfo Release,
    DateTimeOffset Added,
    string Reason)
{
    public bool IsFor(int movieId) => MediaType == MediaType.Movie && MovieId == movieId;

    public bool Overlaps(int seriesId, IEnumerable<int> episodeIds) =>
        MediaType == MediaType.Series && SeriesId == seriesId && EpisodeIds.Any(episodeIds.Contains);
}

/// <summary>Pending releases changed (hub message, so Activity can refresh).</summary>
public sealed record PendingReleasesChanged : IAppEvent;

/// <summary>
/// Releases waiting for their protocol delay (Settings → Indexers), like Radarr's PendingReleaseService, simplified: one
/// list kept in the config table (JSON), so it survives a restart without a table of its own. RSS sync and the
/// "Process pending releases" task decide them again together with new releases and grab the best one once the delay
/// has passed; grabbing anything for the item (or the item no longer wanting it) clears its entries. Entries expire
/// after <see cref="MaxAge"/>.
/// </summary>
public sealed class PendingReleaseService(ConfigRepository config, TimeProvider time, ILogger<PendingReleaseService> logger, IEventBus? events = null)
{
    /// <summary>Longer than the largest delay (one week), so nothing expires while it still waits.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    public const int MaxPerItem = 20;
    public const int MaxTotal = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _lock = new();
    private List<PendingRelease>? _items;

    /// <summary>All pending releases, oldest first (expired ones are dropped).</summary>
    public IReadOnlyList<PendingRelease> All()
    {
        lock (_lock)
        {
            return [.. Load()];
        }
    }

    public bool HasAny
    {
        get
        {
            lock (_lock)
            {
                return Load().Count > 0;
            }
        }
    }

    public IReadOnlyList<PendingRelease> ForMovie(int movieId)
    {
        lock (_lock)
        {
            return [.. Load().Where(p => p.IsFor(movieId))];
        }
    }

    public IReadOnlyList<PendingRelease> ForSeries(int seriesId)
    {
        lock (_lock)
        {
            return [.. Load().Where(p => p.MediaType == MediaType.Series && p.SeriesId == seriesId)];
        }
    }

    /// <summary>Publish date of the oldest release waiting for the movie (Radarr's OldestPendingRelease); null when none waits.</summary>
    public DateTimeOffset? OldestForMovie(int movieId) => Oldest(ForMovie(movieId));

    /// <summary>Publish date of the oldest release waiting for any of the episodes.</summary>
    public DateTimeOffset? OldestForEpisodes(int seriesId, IReadOnlyCollection<int> episodeIds)
    {
        lock (_lock)
        {
            return Oldest(Load().Where(p => p.Overlaps(seriesId, episodeIds)));
        }
    }

    /// <summary>Replaces what waits for the movie with <paramref name="delayed"/> (the decisions held back by the delay only).</summary>
    public void ReplaceMovie(int movieId, IEnumerable<ReleaseDecision> delayed)
    {
        ArgumentNullException.ThrowIfNull(delayed);
        Change(items => items.RemoveAll(p => p.IsFor(movieId)), delayed);
    }

    /// <summary>Replaces what waits for any of the episodes with <paramref name="delayed"/>.</summary>
    public void ReplaceEpisodes(int seriesId, IReadOnlyCollection<int> episodeIds, IEnumerable<ReleaseDecision> delayed)
    {
        ArgumentNullException.ThrowIfNull(episodeIds);
        ArgumentNullException.ThrowIfNull(delayed);
        Change(items => items.RemoveAll(p => p.Overlaps(seriesId, episodeIds)), delayed);
    }

    /// <summary>Adds decisions held back by the delay (an automatic search); entries already waiting stay.</summary>
    public void Add(IEnumerable<ReleaseDecision> delayed)
    {
        ArgumentNullException.ThrowIfNull(delayed);
        Change(_ => 0, delayed);
    }

    public void RemoveMovie(int movieId) => ReplaceMovie(movieId, []);

    public void RemoveEpisodes(int seriesId, IReadOnlyCollection<int> episodeIds) => ReplaceEpisodes(seriesId, episodeIds, []);

    /// <summary>Removes one entry ("Remove" in Activity); false when it is not there.</summary>
    public bool Remove(string id)
    {
        var removed = 0;
        Change(items => removed = items.RemoveAll(p => p.Id == id), []);
        return removed > 0;
    }

    /// <summary>
    /// The decisions an automatic flow should keep as pending: accepted except for the protocol delay, which is a
    /// temporary rejection; ranked as given, at most <see cref="MaxPerItem"/>.
    /// </summary>
    public static IReadOnlyList<ReleaseDecision> DelayedOnly(IEnumerable<ReleaseDecision> decisions) =>
        [.. decisions.Where(d => d.Rejections.Count > 0 && d.Rejections.All(r => r.Code == RejectionCodes.MinimumAgeDelay)).Take(MaxPerItem)];

    private void Change(Func<List<PendingRelease>, int> remove, IEnumerable<ReleaseDecision> add)
    {
        var changed = false;
        lock (_lock)
        {
            var items = Load();
            changed |= remove(items) > 0;
            var now = time.GetUtcNow();
            foreach (var decision in add.Take(MaxPerItem))
            {
                var release = decision.Release;
                items.RemoveAll(p => p.Release.IndexerId == release.IndexerId && p.Release.Guid == release.Guid);
                var subject = decision.Subject;
                items.Add(new PendingRelease(
                    System.Guid.NewGuid().ToString("N"),
                    subject.MediaType ?? MediaType.Movie,
                    subject.MovieId,
                    subject.SeriesId,
                    decision.EpisodeIds,
                    subject.Title,
                    release,
                    now,
                    decision.Rejections.FirstOrDefault()?.Message ?? string.Empty));
                changed = true;
            }

            if (items.Count > MaxTotal)
            {
                items.RemoveRange(0, items.Count - MaxTotal);
            }

            if (changed)
            {
                Save(items);
            }
        }

        if (changed)
        {
            logger.LogDebug("Pending releases changed");
            if (events is not null)
            {
                _ = PublishAsync(events);
            }
        }
    }

    private async Task PublishAsync(IEventBus bus)
    {
        try
        {
            await bus.PublishAsync(new PendingReleasesChanged());
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not publish the pending release change: {Message}", ex.Message);
        }
    }

    private static DateTimeOffset? Oldest(IEnumerable<PendingRelease> items)
    {
        var dates = items.Select(p => p.Release.PublishDate).Where(d => d != default).ToList();
        return dates.Count == 0 ? null : dates.Min();
    }

    private List<PendingRelease> Load()
    {
        if (_items is null)
        {
            _items = [];
            var json = config.Get(ConfigKeys.PendingReleases);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    _items = JsonSerializer.Deserialize<List<PendingRelease>>(json, Json) ?? [];
                }
                catch (JsonException ex)
                {
                    logger.LogWarning("Stored pending releases are unreadable and were dropped: {Message}", ex.Message);
                }
            }
        }

        var cutoff = time.GetUtcNow() - MaxAge;
        if (_items.RemoveAll(p => p.Added < cutoff) > 0)
        {
            Save(_items);
        }

        _items.Sort((a, b) => a.Added.CompareTo(b.Added));
        return _items;
    }

    private void Save(List<PendingRelease> items) =>
        config.Set(ConfigKeys.PendingReleases, items.Count == 0 ? string.Empty : JsonSerializer.Serialize(items, Json));
}
