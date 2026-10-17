// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Metadata;

/// <summary>
/// Writes NFO files and artwork after an import or rename, as a background job per movie or series (shown in the
/// status window; the import never waits for it). Nothing happens while the Kodi metadata writer is off.
/// </summary>
public sealed class MetadataEventHandler(IEventBus events, JobQueue jobs, MetadataConfig settings, MovieRepository movies, SeriesRepository series)
    : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    public static Job MovieJob(int movieId, string title) =>
        new($"metadata-movie:{movieId}", $"Write metadata for {title}", (sp, ct) => sp.GetRequiredService<KodiMetadataWriter>().WriteMovieAsync(movieId, ct));

    public static Job SeriesJob(int seriesId, string title) =>
        new($"metadata-series:{seriesId}", $"Write metadata for {title}", (sp, ct) => sp.GetRequiredService<KodiMetadataWriter>().WriteSeriesAsync(seriesId, ct));

    public static Job LibraryJob() =>
        new("metadata-library", "Write metadata for the library", (sp, ct) => sp.GetRequiredService<KodiMetadataWriter>().WriteAllAsync(ct));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(events.Subscribe<MediaImported>((e, _) =>
        {
            Queue(e.MediaType, e.MovieId, e.SeriesId, e.Title);
            return ValueTask.CompletedTask;
        }));
        _subscriptions.Add(events.Subscribe<MediaRenamed>((e, _) =>
        {
            Queue(e.MediaType, e.MovieId, e.SeriesId, null);
            return ValueTask.CompletedTask;
        }));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    private void Queue(MediaType type, int? movieId, int? seriesId, string? title)
    {
        if (!settings.Kodi.Enabled)
        {
            return;
        }

        if (type == MediaType.Movie && movieId is { } m)
        {
            jobs.Enqueue(MovieJob(m, title ?? movies.Find(m)?.Title ?? $"movie {m}"));
        }
        else if (type == MediaType.Series && seriesId is { } s)
        {
            jobs.Enqueue(SeriesJob(s, series.Find(s)?.Title ?? title ?? $"series {s}"));
        }
    }
}

public static class MetadataServiceCollectionExtensions
{
    /// <summary>
    /// The metadata settings, the TMDB detail and image clients, the image cache and the Kodi metadata writer with its
    /// event handler. Needs <see cref="ITmdbSettings"/>, the repositories and the job queue.
    /// </summary>
    public static IServiceCollection AddMetadataWriters(this IServiceCollection services)
    {
        services.TryAddSingleton<MetadataConfig>();
        services.AddHttpClient<ITmdbDetailsClient, TmdbClient>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);

            // Only for development and tests: the same TMDB stand-in as the library uses.
            if (sp.GetService<IConfiguration>()?["tmdb-url"] is { Length: > 0 } tmdbUrl)
            {
                client.BaseAddress = new Uri(tmdbUrl.EndsWith('/') ? tmdbUrl : tmdbUrl + "/");
            }
        });
        services.AddHttpClient<ITmdbImageClient, TmdbImageClient>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.BaseAddress = sp.GetService<IConfiguration>()?["tmdb-image-url"] is { Length: > 0 } imageUrl
                ? new Uri(imageUrl.EndsWith('/') ? imageUrl : imageUrl + "/")
                : TmdbImages.BaseAddress;
        });
        services.TryAddScoped<MetadataImageStore>();
        services.TryAddScoped<KodiMetadataWriter>();
        services.AddSingleton<MetadataEventHandler>();
        services.AddHostedService(sp => sp.GetRequiredService<MetadataEventHandler>());
        return services;
    }
}
