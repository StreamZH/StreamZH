// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.ImportLists;

/// <summary>"Import list sync": all enabled lists every N hours (Settings → Import lists, default 24), listed in System → Tasks.</summary>
public sealed class ImportListSyncTask(ImportListOptions options) : IScheduledTask
{
    public const string TaskKey = "import-list-sync";

    public string Key => TaskKey;

    public string Name => "Import list sync";

    public TimeSpan? Interval => options.SyncInterval;

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<ImportListSyncService>().SyncAllAsync(cancellationToken);
}

public static class ImportListServiceCollectionExtensions
{
    /// <summary>
    /// Settings → Import lists: the repository, the sources, the TMDB list client, the services and the scheduled sync.
    /// Needs the library services, AddReleaseSearch (search on add) and <see cref="ITmdbSettings"/>.
    /// </summary>
    public static IServiceCollection AddImportListServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ImportListRepository>();
        services.AddSingleton<ImportListOptions>();
        services.AddHttpClient(ImportListHttp.ClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<ITmdbListClient, TmdbClient>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);

            // Only for development and tests: the same TMDB stand-in as the library uses.
            if (sp.GetService<IConfiguration>()?["tmdb-url"] is { Length: > 0 } tmdbUrl)
            {
                client.BaseAddress = new Uri(tmdbUrl.EndsWith('/') ? tmdbUrl : tmdbUrl + "/");
            }
        });

        services.AddScoped<IImportListSource, TmdbListSource>();
        services.AddScoped<IImportListSource, TraktListSource>();
        services.AddScoped<IImportListSource, PlexWatchlistSource>();
        services.AddScoped<IImportListSource, StreamZhListSource>();
        services.AddScoped<IImportListSource, RssListSource>();

        services.AddScoped<ImportListService>();
        services.AddScoped<ImportListSyncService>();
        services.AddSingleton<IScheduledTask, ImportListSyncTask>();
        return services;
    }
}
