// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Library.Downloads;

public static class DownloadServiceCollectionExtensions
{
    /// <summary>
    /// The built-in download engine (hosted service + <see cref="IDownloadQueue"/>), both download clients and the
    /// client selection used by grabbing. <see cref="IArticleSource"/> and <see cref="IPostProcessor"/> come from the
    /// Usenet and post-processing modules; until those are registered, stand-ins keep the queue waiting
    /// (no connections) and only move files. Registration order does not matter: the stand-ins are added with TryAdd,
    /// and a later AddSingleton of the real implementation wins.
    /// </summary>
    public static IServiceCollection AddDownloadEngine(this IServiceCollection services)
    {
        services.TryAddSingleton<DownloadSettings>();
        services.TryAddSingleton<HistoryRepository>();
        services.TryAddSingleton<DownloadRepository>();
        services.TryAddSingleton<DownloadEngineOptions>();
        services.TryAddSingleton<IFreeSpaceProvider, DriveFreeSpaceProvider>();
        services.TryAddSingleton<IArticleSource, UnconfiguredArticleSource>();
        services.TryAddSingleton<IPostProcessor, MoveOnlyPostProcessor>();

        services.AddSingleton<DownloadEngine>();
        services.AddSingleton<IDownloadQueue>(sp => sp.GetRequiredService<DownloadEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<DownloadEngine>());

        services.AddSingleton<BuiltInDownloadClient>();
        services.AddSingleton<BlackholeDownloadClient>();
        services.AddSingleton<DownloadClientProvider>();
        return services;
    }
}
