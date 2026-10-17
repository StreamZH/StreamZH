// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Data;

namespace StreamZH.Library.Search;

/// <summary>
/// Which movies and episodes are already on their way, so automatic searches and RSS do not grab them again (Radarr's
/// "already in the queue"). With the built-in engine that is its queue; with the blackhole folder StreamZH cannot see
/// the downloader, so items grabbed in the last 12 hours count instead.
/// </summary>
public sealed class PendingGrabs(HistoryRepository history, DownloadSettings downloadSettings, IDownloadQueue? queue, TimeProvider time, PendingImports? imports = null)
{
    /// <summary>Blackhole folder: items grabbed this recently are not grabbed again.</summary>
    public static readonly TimeSpan RecentGrabWindow = TimeSpan.FromHours(12);

    public const string SkipInQueue = "inQueue";
    public const string SkipRecentlyGrabbed = "recentlyGrabbed";

    public bool UsesQueue => downloadSettings.Client == DownloadClientKind.BuiltIn && queue is not null;

    /// <summary>"inQueue" or "recentlyGrabbed", depending on the download client.</summary>
    public string SkipReason => UsesQueue ? SkipInQueue : SkipRecentlyGrabbed;

    /// <summary>Null when the movie may be grabbed, otherwise <see cref="SkipReason"/>.</summary>
    public string? MovieSkipReason(int movieId)
    {
        if (UsesQueue)
        {
            // In the queue, or finished and still waiting for the import.
            return queue!.Active().Any(d => d.Link.MovieId == movieId) || imports?.HasMovie(movieId) == true ? SkipInQueue : null;
        }

        return history.MovieHasEventSince(movieId, HistoryEventType.Grabbed, Since()) ? SkipRecentlyGrabbed : null;
    }

    /// <summary>Episodes of the series that are in the queue (built-in engine) or were grabbed recently (blackhole folder).</summary>
    public IReadOnlySet<int> BlockedEpisodes(int seriesId)
    {
        if (UsesQueue)
        {
            var blocked = queue!.Active().Where(d => d.Link.SeriesId == seriesId).SelectMany(d => d.Link.EpisodeIds).ToHashSet();
            if (imports is not null)
            {
                blocked.UnionWith(imports.EpisodeIds(seriesId));
            }

            return blocked;
        }

        return history.EpisodesWithEventSince(seriesId, HistoryEventType.Grabbed, Since());
    }

    private DateTime Since() => (time.GetUtcNow() - RecentGrabWindow).UtcDateTime;
}
