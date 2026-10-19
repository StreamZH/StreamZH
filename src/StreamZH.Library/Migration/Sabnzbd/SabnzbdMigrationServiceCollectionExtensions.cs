// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Net;
using StreamZH.Library.Migration.Indexers;

namespace StreamZH.Library.Migration.Sabnzbd;

public static class SabnzbdMigrationServiceCollectionExtensions
{
    /// <summary>
    /// Migration from SABnzbd (probe, servers, categories, download settings) and the indexer parts of Prowlarr, Radarr
    /// and Sonarr (indexers, download clients for information).
    /// </summary>
    public static IServiceCollection AddSabnzbdMigration(this IServiceCollection services)
    {
        services.AddHttpClient<SabnzbdApiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.MaxResponseContentBufferSize = ResponseLimits.SabnzbdMigration;
        });
        services.AddSingleton<IMigrationProbe, SabnzbdMigrationProbe>();
        services.AddSingleton<IMigrationSection, SabnzbdServersSection>();
        services.AddSingleton<IMigrationSection, SabnzbdCategoriesSection>();
        services.AddSingleton<IMigrationSection, SabnzbdDownloadsSection>();

        services.AddSingleton<IMigrationSection, ProwlarrIndexersSection>();
        foreach (var source in new[] { MigrationSources.Radarr, MigrationSources.Sonarr })
        {
            services.AddSingleton<IMigrationSection>(_ => new ArrIndexersSection(source));
            services.AddSingleton<IMigrationSection>(_ => new ArrDownloadClientsSection(source));
        }

        return services;
    }
}
