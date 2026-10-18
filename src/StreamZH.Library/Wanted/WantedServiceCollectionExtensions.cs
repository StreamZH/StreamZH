// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Data;

namespace StreamZH.Library.Wanted;

public static class WantedServiceCollectionExtensions
{
    /// <summary>Wanted (missing, cutoff unmet), Calendar and the "last searched" times (needs AddReleaseSearch).</summary>
    public static IServiceCollection AddWanted(this IServiceCollection services)
    {
        services.AddSingleton<LastSearchRepository>();
        services.AddSingleton<WantedQueries>();
        services.AddScoped<QueueSnapshotFactory>();
        services.AddScoped<WantedService>();
        services.AddScoped<WantedSearch>();
        services.AddScoped<CalendarService>();
        return services;
    }
}
