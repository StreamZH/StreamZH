// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Import;

/// <summary>
/// Failed download handling (Radarr/Sonarr): a failed download of a movie or episodes puts its release on the blocklist,
/// is removed from the download list ("Remove failed") and, with "Redownload failed", the item is searched again right
/// away; the blocklist keeps the same release from being grabbed again.
/// </summary>
public sealed class FailedDownloadHandler(
    MediaManagementSettings settings,
    BlocklistRepository blocklist,
    ILogger<FailedDownloadHandler> logger,
    DownloadEngine? engine = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Blocklists the release and removes the download; returns true when a new search should follow.</summary>
    public async Task<bool> HandleAsync(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Status != DownloadStatus.Failed || item.Link.MediaType is null)
        {
            return false;
        }

        var options = settings.Current;
        var parsed = ReleaseParser.Parse(item.Title);
        blocklist.Add(new BlocklistItem
        {
            Date = _time.GetUtcNow().UtcDateTime,
            MediaType = item.Link.MediaType,
            MovieId = item.Link.MovieId,
            SeriesId = item.Link.SeriesId,
            EpisodeIds = item.Link.EpisodeIds,
            SourceTitle = item.Title,
            QualityId = parsed?.Quality.Quality.Id ?? 0,
            LanguageIds = [.. (parsed?.Languages ?? []).Select(l => l.Id)],
            Indexer = item.Indexer,
            Size = item.TotalBytes,
            Message = item.Error,
        });
        logger.LogInformation("Download {Id} '{Title}' failed and was added to the blocklist", item.Id, item.Title);

        if (options.RemoveFailedDownloads && engine is not null)
        {
            try
            {
                await engine.DeleteAsync(item.Id, deleteFiles: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not remove the failed download {Id}", item.Id);
            }
        }

        return options.RedownloadFailed;
    }

    /// <summary>
    /// Searches the movie or the episodes of the failed download again (automatic search: the best accepted release is
    /// grabbed). A failed season pack searches the whole season again (packs allowed), single episodes only those episodes.
    /// </summary>
    public static async Task SearchAgainAsync(DownloadItem item, AutomaticSearchService search, SeriesRepository series, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(series);
        if (item.Link is { MediaType: MediaType.Movie, MovieId: { } movieId })
        {
            await search.SearchMoviesAsync([movieId], cancellationToken);
            return;
        }

        if (item.Link is not { MediaType: MediaType.Series, SeriesId: { } seriesId, EpisodeIds.Count: > 0 })
        {
            return;
        }

        var library = series.Episodes(seriesId);
        var linked = item.Link.EpisodeIds.ToHashSet();
        var seasons = library.Where(e => linked.Contains(e.Id)).Select(e => e.SeasonNumber).Distinct().ToList();
        if (seasons.Count == 1 && library.Where(e => e.SeasonNumber == seasons[0]).All(e => linked.Contains(e.Id)) && linked.Count > 1)
        {
            await search.SearchSeasonAsync(seriesId, seasons[0], cancellationToken);
        }
        else
        {
            await search.SearchEpisodesAsync(seriesId, item.Link.EpisodeIds, cancellationToken);
        }
    }
}
