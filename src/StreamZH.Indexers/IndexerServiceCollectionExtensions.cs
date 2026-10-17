// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core;
using StreamZH.Core.Indexers;
using StreamZH.Indexers.Newznab;

namespace StreamZH.Indexers;

public static class IndexerServiceCollectionExtensions
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Registers the Newznab client as <see cref="IIndexerClient"/> (typed HttpClient, 60 s timeout, "StreamZH/{version}" User-Agent)
    /// and the shared caps cache. <paramref name="configure"/> runs last and can override the defaults.
    /// </summary>
    public static IServiceCollection AddNewznabIndexers(this IServiceCollection services, Action<HttpClient>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => new NewznabCapabilitiesCache(sp.GetService<TimeProvider>()));
        services.AddHttpClient<IIndexerClient, NewznabClient>(client =>
            {
                client.Timeout = Timeout;
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"{AppInfo.Name}/{AppInfo.Version}");
                configure?.Invoke(client);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });

        return services;
    }
}
