// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using StreamZH.Core.Decisions;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;

namespace StreamZH.Library.Search;

/// <summary>What a search was for: a movie, or episodes of a series. <see cref="MediaType"/> is null for a free text search (Search page).</summary>
public sealed record SearchSubject(MediaType? MediaType, int? MovieId, int? SeriesId, string Title);

/// <summary>One search result with its verdict. <see cref="Parsed"/> is null when the name could not be read.</summary>
/// <param name="EpisodeIds">Series: the library episodes the release covers (empty when unknown).</param>
public sealed record ReleaseDecision(ReleaseInfo Release, ParsedRelease? Parsed, Decision Decision, SearchSubject Subject, IReadOnlyList<int> EpisodeIds)
{
    public bool Accepted => Decision.Accepted;

    public IReadOnlyList<Rejection> Rejections => Decision.Rejections;
}

public sealed record IndexerSearchFailure(int IndexerId, string Indexer, string Message);

/// <summary>Ranked decisions (accepted first, best first), how many indexers were asked and which of them failed.</summary>
public sealed record ReleaseSearchResult(IReadOnlyList<ReleaseDecision> Decisions, int IndexerCount, IReadOnlyList<IndexerSearchFailure> Failures)
{
    public ReleaseDecision? Best => Decisions.FirstOrDefault(d => d.Accepted);
}

public sealed record GrabResult(string Title, string Indexer, string NzbPath);

/// <summary>A release was grabbed (manually, by an automatic search, or from the Search page with <see cref="MediaType"/> null).</summary>
public sealed record ReleaseGrabbed(MediaType? MediaType, int? MovieId, int? SeriesId, string Title, string Indexer) : IAppEvent;

/// <summary>An automatic search job finished. <see cref="Id"/> is the movie or series id, null for "all missing movies".</summary>
public sealed record AutomaticSearchFinished(MediaType MediaType, int? Id, int Searched, int Grabbed) : IAppEvent;

/// <summary>
/// The latest search results for 30 minutes, so the UI can grab a release by (indexer id, guid)
/// without sending the whole release back (like Radarr's release cache).
/// </summary>
public sealed class ReleaseCache(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(int IndexerId, string Guid), (ReleaseDecision Decision, DateTimeOffset Stored)> _items = new();

    public void Store(IEnumerable<ReleaseDecision> decisions)
    {
        var now = _time.GetUtcNow();
        foreach (var (key, value) in _items)
        {
            if (now - value.Stored > Lifetime)
            {
                _items.TryRemove(key, out _);
            }
        }

        foreach (var decision in decisions)
        {
            _items[(decision.Release.IndexerId, decision.Release.Guid)] = (decision, now);
        }
    }

    public ReleaseDecision? Find(int indexerId, string guid) =>
        _items.TryGetValue((indexerId, guid), out var entry) && _time.GetUtcNow() - entry.Stored <= Lifetime ? entry.Decision : null;
}
