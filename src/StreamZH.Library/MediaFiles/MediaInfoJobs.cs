// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.MediaFiles;

/// <summary>"Refresh media info" (System → Tasks): analyses new, changed and outdated files every N days (default 7, 0 = off).</summary>
public sealed class MediaInfoRefreshTask(MediaInfoSettings settings) : IScheduledTask
{
    public const string TaskKey = "refresh-media-info";

    public string Key => TaskKey;

    public string Name => "Refresh media info";

    public TimeSpan? Interval => settings.RefreshInterval;

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<MediaInfoService>().AnalyzeLibraryAsync(force: false, cancellationToken);
}

/// <summary>
/// After a library scan or refresh (<see cref="LibraryChanged"/>), new and changed files of that item are analysed in a
/// background job; nothing is queued when every file already has a current analysis.
/// </summary>
public sealed class MediaInfoScanHandler(IEventBus events, JobQueue jobs, MediaInfoService mediaInfo, MovieRepository movies, SeriesRepository series)
    : IHostedService
{
    private IDisposable? _subscription;

    public static Job MovieJob(int movieId, string title, bool force) =>
        new($"mediainfo-movie:{movieId}{(force ? ":force" : string.Empty)}", $"Analyse the media file of {title}",
            (sp, ct) => sp.GetRequiredService<MediaInfoService>().AnalyzeMovieAsync(movieId, force, ct));

    public static Job SeriesJob(int seriesId, string title, bool force) =>
        new($"mediainfo-series:{seriesId}{(force ? ":force" : string.Empty)}", $"Analyse the media files of {title}",
            (sp, ct) => sp.GetRequiredService<MediaInfoService>().AnalyzeSeriesAsync(seriesId, force, ct));

    public static Job LibraryJob() =>
        new("mediainfo-library", "Analyse new media files", (sp, ct) => sp.GetRequiredService<MediaInfoService>().AnalyzeLibraryAsync(force: false, ct));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = events.Subscribe<LibraryChanged>((e, _) =>
        {
            try
            {
                Queue(e);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read is analysed at the next scan.
            }

            return ValueTask.CompletedTask;
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private void Queue(LibraryChanged e)
    {
        if (!mediaInfo.Enabled)
        {
            return;
        }

        if (e.Id is not { } id)
        {
            jobs.Enqueue(LibraryJob());
            return;
        }

        if (e.MediaType == MediaType.Movie && movies.Find(id) is { File: { } file } movie && mediaInfo.HasWork(movie.FolderPath, [file]))
        {
            jobs.Enqueue(MovieJob(id, movie.Title, force: false));
        }
        else if (e.MediaType == MediaType.Series && series.Find(id) is { } show
                 && mediaInfo.HasWork(show.FolderPath, [.. series.Episodes(id).Select(x => x.File).OfType<MediaFile>().DistinctBy(f => f.RelativePath)]))
        {
            jobs.Enqueue(SeriesJob(id, show.Title, force: false));
        }
    }
}
