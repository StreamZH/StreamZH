// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StreamZH.Core;
using StreamZH.Core.Downloads;
using StreamZH.Data;

namespace StreamZH.Torrent;

public static class TorrentEngineServiceCollectionExtensions
{
    /// <summary>
    /// The built-in torrent engine (0.11): settings, the engine (state in &lt;data&gt;/torrents), the "StreamZH"
    /// <see cref="ITorrentClient"/> and the hosted service that runs it while such a client is enabled.
    /// </summary>
    public static IServiceCollection AddTorrentEngine(this IServiceCollection services)
    {
        services.TryAddSingleton<DownloadClientRepository>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TorrentEngineSettings>();
        services.TryAddSingleton<ITorrentSavePaths, CategoryTorrentSavePaths>();
        services.AddSingleton(sp => new TorrentEngine(
            sp.GetRequiredService<TorrentEngineSettings>(),
            sp.GetRequiredService<ITorrentSavePaths>(),
            new TorrentStateStore(Path.Combine(sp.GetRequiredService<AppPaths>().DataDirectory, "torrents")),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<TorrentEngine>>()));
        services.AddSingleton<BuiltInTorrentClient>();
        services.AddSingleton<ITorrentClient>(sp => sp.GetRequiredService<BuiltInTorrentClient>());
        services.TryAddSingleton(TorrentEngineHostOptions.Default);
        services.AddSingleton<TorrentEngineHost>();
        services.AddHostedService(sp => sp.GetRequiredService<TorrentEngineHost>());
        return services;
    }
}
