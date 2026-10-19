// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Jobs;
using StreamZH.Core.Requests;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Requests;

public static class RequestServiceCollectionExtensions
{
    /// <summary>
    /// Discover and requests (0.10): Settings → Requests (also the <see cref="IRequestDefaults"/>: added with
    /// AddSingleton, so it wins over the users module's TryAdd default in any order), the discover service with its TMDB
    /// client and cache, the request lifecycle, the availability check (after imports and library changes, and every 15
    /// minutes) and the Connect notifications.
    /// Needs the library services, AddReleaseSearch (search, side-by-side mode) and AddConnectNotifications.
    /// </summary>
    public static IServiceCollection AddRequests(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RequestRepository>();
        services.AddSingleton<DiscoverLookup>();
        services.AddSingleton<RequestSettings>();
        services.AddSingleton<IRequestDefaults>(sp => sp.GetRequiredService<RequestSettings>());

        services.AddHttpClient<ITmdbDiscoverClient, TmdbClient>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);

            // Only for development and tests: the same TMDB stand-in as the library uses.
            if (sp.GetService<IConfiguration>()?["tmdb-url"] is { Length: > 0 } tmdbUrl)
            {
                client.BaseAddress = new Uri(tmdbUrl.EndsWith('/') ? tmdbUrl : tmdbUrl + "/");
            }
        });
        services.AddSingleton<DiscoverCache>();
        services.AddScoped<DiscoverService>();

        services.AddScoped<RequestService>();
        services.AddSingleton<RequestAvailability>();
        services.AddHostedService<RequestAvailabilityWatcher>();
        services.AddSingleton<IScheduledTask, RequestAvailabilityTask>();
        services.AddHostedService<RequestNotifier>();

        // The Discord bot's gateway; replaces the bot's placeholder (registered with TryAdd) in any order.
        services.AddSingleton<Discord.IRequestGateway, RequestGateway>();
        return services;
    }
}
