// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Data;

namespace StreamZH.Library.Search;

/// <summary>
/// Finished downloads whose files are still waiting in the download folder for the import: not imported yet, queued,
/// waiting, or failed and not dismissed. Like Radarr's queue, such a download still counts as "on its way", so a
/// search does not grab the same movie again (a 29 GB release must not be downloaded twice).
/// </summary>
public sealed class PendingImports(DownloadRepository downloads, DownloadImportRepository imports)
{
    private static readonly ImportState[] Open = [ImportState.Queued, ImportState.Importing, ImportState.Waiting, ImportState.Failed];

    /// <summary>Completed downloads with a library link whose files are still there and not imported.</summary>
    public IReadOnlyList<DownloadRecord> Waiting()
    {
        var completed = downloads.CompletedWithLink();
        if (completed.Count == 0)
        {
            return [];
        }

        var states = imports.ForDownloads(completed.Select(d => d.Id)).ToDictionary(i => i.DownloadId);
        return [.. completed.Where(d =>
        {
            if (states.TryGetValue(d.Id, out var import) && (!Open.Contains(import.State) || import.Dismissed))
            {
                return false;
            }

            return !string.IsNullOrEmpty(d.OutputPath) && Directory.Exists(d.OutputPath);
        })];
    }

    public bool HasMovie(int movieId) => Waiting().Any(d => d.MovieId == movieId);

    public IReadOnlySet<int> EpisodeIds(int seriesId) =>
        Waiting().Where(d => d.SeriesId == seriesId).SelectMany(d => d.EpisodeIds).ToHashSet();
}
