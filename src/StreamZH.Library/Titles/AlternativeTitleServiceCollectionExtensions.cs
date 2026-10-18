// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Titles;

public static class AlternativeTitleServiceCollectionExtensions
{
    /// <summary>
    /// Alternative titles: the TMDB titles client and <see cref="AlternativeTitleService"/>. Needs the
    /// <see cref="Data.AlternativeTitleRepository"/> and <see cref="ITmdbSettings"/>.
    /// </summary>
    public static IServiceCollection AddAlternativeTitles(this IServiceCollection services)
    {
        services.AddHttpClient<ITmdbTitlesClient, TmdbClient>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);

            // Only for development and tests: the same TMDB stand-in as the library uses.
            if (sp.GetService<IConfiguration>()?["tmdb-url"] is { Length: > 0 } tmdbUrl)
            {
                client.BaseAddress = new Uri(tmdbUrl.EndsWith('/') ? tmdbUrl : tmdbUrl + "/");
            }
        });
        services.AddScoped<AlternativeTitleService>();
        return services;
    }
}
