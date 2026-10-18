// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Library.Wanted;

/// <summary>
/// What is on its way right now, for every movie and episode at once (the same rules as <see cref="PendingGrabs"/>):
/// with the built-in engine its queue and finished downloads waiting for the import; with the blackhole folder the
/// grabs of the last 12 hours.
/// </summary>
public sealed class QueueSnapshot
{
    public const string Downloading = "downloading";
    public const string WaitingForImport = "waitingForImport";
    public const string Grabbed = "grabbed";

    public static readonly QueueSnapshot Empty = new(new Dictionary<int, string>(), new Dictionary<int, string>());

    private readonly IReadOnlyDictionary<int, string> _movies;
    private readonly IReadOnlyDictionary<int, string> _episodes;

    public QueueSnapshot(IReadOnlyDictionary<int, string> movies, IReadOnlyDictionary<int, string> episodes)
    {
        _movies = movies;
        _episodes = episodes;
    }

    public string? Movie(int movieId) => _movies.GetValueOrDefault(movieId);

    public string? Episode(int episodeId) => _episodes.GetValueOrDefault(episodeId);
}

public sealed class QueueSnapshotFactory(
    DownloadSettings downloadSettings,
    WantedQueries queries,
    IDownloadQueue? queue = null,
    PendingImports? imports = null,
    TimeProvider? time = null)
{
    public QueueSnapshot Take()
    {
        var movies = new Dictionary<int, string>();
        var episodes = new Dictionary<int, string>();

        if (downloadSettings.Client == DownloadClientKind.BuiltIn && queue is not null)
        {
            foreach (var waiting in imports?.Waiting() ?? [])
            {
                Add(movies, episodes, waiting.MovieId, waiting.EpisodeIds, QueueSnapshot.WaitingForImport);
            }

            // Downloading wins over an older download of the same item that waits for the import.
            foreach (var item in queue.Active())
            {
                Add(movies, episodes, item.Link.MovieId, item.Link.EpisodeIds, QueueSnapshot.Downloading, overwrite: true);
            }
        }
        else
        {
            var since = ((time ?? TimeProvider.System).GetUtcNow() - PendingGrabs.RecentGrabWindow).UtcDateTime;
            foreach (var grab in queries.GrabsSince(since))
            {
                Add(movies, episodes, grab.MovieId, grab.EpisodeIds, QueueSnapshot.Grabbed);
            }
        }

        return new QueueSnapshot(movies, episodes);
    }

    private static void Add(Dictionary<int, string> movies, Dictionary<int, string> episodes, int? movieId, IReadOnlyList<int> episodeIds, string state, bool overwrite = false)
    {
        if (movieId is { } id && (overwrite || !movies.ContainsKey(id)))
        {
            movies[id] = state;
        }

        foreach (var episodeId in episodeIds)
        {
            if (overwrite || !episodes.ContainsKey(episodeId))
            {
                episodes[episodeId] = state;
            }
        }
    }
}
