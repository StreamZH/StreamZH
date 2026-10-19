// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Data;

namespace StreamZH.Library.Search;

public static class SearchServiceCollectionExtensions
{
    /// <summary>
    /// Release search, grabbing and history. Needs <see cref="IIndexerProvider"/> (AddIndexerManagement) and
    /// <see cref="IIndexerClient"/> (AddNewznabIndexers).
    /// Search and grab are scoped, so a typed HttpClient behind <see cref="IIndexerClient"/> is not held forever; the cache is shared.
    /// </summary>
    public static IServiceCollection AddReleaseSearch(this IServiceCollection services)
    {
        services.AddSingleton<HistoryRepository>();
        services.AddSingleton<EpisodeLookup>();
        services.AddSingleton<DecisionTargetFactory>();
        services.AddSingleton<ReleaseCache>();
        services.AddSingleton<DownloadSettings>();
        services.AddSingleton<ReleaseProfileRepository>();
        services.AddSingleton<IndexerOptions>();
        services.AddSingleton<AutomationHold>();
        services.AddScoped<ReleaseSearchService>();
        services.AddScoped<GrabService>();
        services.AddScoped<AutomaticSearchService>();
        services.AddScoped<FreeSearchService>();
        return services;
    }
}
