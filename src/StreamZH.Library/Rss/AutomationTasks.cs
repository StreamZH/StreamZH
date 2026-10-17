// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Library.Rss;

/// <summary>RSS sync every N minutes (Settings → Indexers, default 60, 0 = off), listed in System → Tasks.</summary>
public sealed class RssSyncTask(IndexerOptions options) : IScheduledTask
{
    public const string TaskKey = "rss-sync";

    public string Key => TaskKey;

    public string Name => "RSS sync";

    public TimeSpan? Interval => options.RssSyncInterval;

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<RssSyncService>().SyncAsync(cancellationToken);
}

/// <summary>
/// Expert option: search for all missing movies and episodes every N hours (off by default; Radarr and Sonarr only
/// do this on demand). Uses the automatic search, so the queue and minimum availability are respected.
/// </summary>
public sealed class MissingSearchTask(IndexerOptions options) : IScheduledTask
{
    public const string TaskKey = "missing-search";

    public string Key => TaskKey;

    public string Name => "Search for missing";

    public TimeSpan? Interval => options.MissingSearchInterval;

    public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var search = services.GetRequiredService<AutomaticSearchService>();
        var grabbed = await search.SearchMissingMoviesAsync(cancellationToken);
        foreach (var show in services.GetRequiredService<SeriesRepository>().All().Where(s => s.Monitored))
        {
            cancellationToken.ThrowIfCancellationRequested();
            grabbed += await search.SearchMissingEpisodesAsync(show.Id, cancellationToken);
        }

        services.GetRequiredService<ILogger<MissingSearchTask>>().LogInformation("Search for missing finished, {Grabbed} releases grabbed", grabbed);
    }
}

public static class RssServiceCollectionExtensions
{
    /// <summary>RSS sync and the scheduled missing search (needs AddReleaseSearch).</summary>
    public static IServiceCollection AddRssSync(this IServiceCollection services)
    {
        services.AddScoped<RssSyncService>();
        services.AddSingleton<IScheduledTask, RssSyncTask>();
        services.AddSingleton<IScheduledTask, MissingSearchTask>();
        return services;
    }
}
