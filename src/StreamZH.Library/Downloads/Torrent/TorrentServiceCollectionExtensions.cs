// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core;
using StreamZH.Core.Downloads;
using StreamZH.Data;

namespace StreamZH.Library.Downloads.Torrent;

public static class TorrentServiceCollectionExtensions
{
    /// <summary>
    /// 0.11 torrents: the qBittorrent, Transmission and Deluge clients (<see cref="ITorrentClient"/>; the built-in engine adds
    /// its own), Settings → Download clients and remote path mappings, the torrent
    /// tracker (hosted) and the combined <see cref="IDownloadQueue"/> (Usenet engine + torrents). Call after AddDownloadEngine:
    /// the combined queue replaces the engine as <see cref="IDownloadQueue"/>.
    /// </summary>
    public static IServiceCollection AddTorrentClients(this IServiceCollection services)
    {
        services.TryAddSingleton<DownloadClientRepository>();
        services.TryAddSingleton<RemotePathMappingRepository>();
        services.TryAddSingleton<DownloadImportRepository>();
        services.TryAddSingleton<TorrentTrackerOptions>();

        services.AddHttpClient(TorrentClientHttp.HttpClientName, client =>
            {
                // Each call has its own timeout (TorrentClientHttp.Timeout).
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"{AppInfo.Name}/{AppInfo.Version}");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            });

        services.AddSingleton<ITorrentClient, QBittorrentClient>();
        services.AddSingleton<ITorrentClient, TransmissionClient>();
        services.AddSingleton<ITorrentClient, DelugeClient>();
        services.AddSingleton<DownloadClientService>();
        services.AddSingleton<RemotePathMappingService>();
        services.AddSingleton<TorrentTracker>();
        services.AddHostedService(sp => sp.GetRequiredService<TorrentTracker>());
        services.AddSingleton<IDownloadQueue, CombinedDownloadQueue>();
        return services;
    }
}
