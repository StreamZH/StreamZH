// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Library.Migration.Arr.Library;
using StreamZH.Metadata.Tmdb;

// In the migration namespace so MigrationEndpoints.AddMigration needs no extra using.
namespace StreamZH.Library.Migration;

public static class ArrLibraryMigrationServiceCollectionExtensions
{
    /// <summary>
    /// The library sections of the migration (0.9): Radarr's movies, Sonarr's series, and the history and blocklist of
    /// both, plus TMDB's /find for Sonarr series without a TMDB id. Needs the library services and <see cref="ITmdbSettings"/>.
    /// </summary>
    public static IServiceCollection AddArrLibraryMigration(this IServiceCollection services)
    {
        services.AddSingleton<ArrLibraryReader>();
        services.AddSingleton<LibraryMigrationStore>();
        services.AddHttpClient<ITmdbFindClient, TmdbClient>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);

            // Only for development and tests: the same TMDB stand-in as the library uses.
            if (sp.GetService<IConfiguration>()?["tmdb-url"] is { Length: > 0 } tmdbUrl)
            {
                client.BaseAddress = new Uri(tmdbUrl.EndsWith('/') ? tmdbUrl : tmdbUrl + "/");
            }
        });

        services.AddSingleton<IMigrationSection>(sp => new MovieMigrationSection(
            sp.GetRequiredService<ArrLibraryReader>(), sp.GetRequiredService<ILogger<MovieMigrationSection>>()));
        services.AddSingleton<IMigrationSection>(sp => new SeriesMigrationSection(
            sp.GetRequiredService<ArrLibraryReader>(), sp.GetRequiredService<ILogger<SeriesMigrationSection>>()));
        foreach (var source in new[] { MigrationSources.Radarr, MigrationSources.Sonarr })
        {
            services.AddSingleton<IMigrationSection>(sp => new HistoryMigrationSection(source, sp.GetRequiredService<ArrLibraryReader>()));
            services.AddSingleton<IMigrationSection>(sp => new BlocklistMigrationSection(source, sp.GetRequiredService<ArrLibraryReader>()));
        }

        return services;
    }
}
