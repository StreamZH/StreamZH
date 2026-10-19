// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core;
using StreamZH.Core.Indexers;
using StreamZH.Core.Net;
using StreamZH.Indexers.Newznab;

namespace StreamZH.Indexers;

public static class IndexerServiceCollectionExtensions
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Registers the Newznab client (typed HttpClient, 60 s timeout, "StreamZH/{version}" User-Agent) as <see cref="NewznabClient"/>,
    /// as keyed <see cref="IIndexerClient"/> under <see cref="IndexerClientKeys.Inner"/> (wrapped by the tracking client of
    /// AddIndexerManagement) and, only when nothing else is registered, as plain <see cref="IIndexerClient"/>.
    /// Also registers the shared caps cache. <paramref name="configure"/> runs last and can override the defaults.
    /// </summary>
    public static IServiceCollection AddNewznabIndexers(this IServiceCollection services, Action<HttpClient>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => new NewznabCapabilitiesCache(sp.GetService<TimeProvider>()));
        services.AddKeyedTransient<IIndexerClient>(IndexerClientKeys.Inner, (sp, _) => sp.GetRequiredService<NewznabClient>());
        services.TryAddTransient<IIndexerClient>(sp => sp.GetRequiredService<NewznabClient>());
        // Named "IIndexerClient" as in 0.4.0, so configuration by name keeps working.
        services.AddHttpClient<NewznabClient>(nameof(IIndexerClient), client =>
            {
                client.Timeout = Timeout;
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"{AppInfo.Name}/{AppInfo.Version}");
                configure?.Invoke(client);
            })
            // Redirects are followed by the client itself, so a redirect to a magnet link (Torznab) can be recognised.
            // The connect callback keeps feed links and redirects to other hosts away from loopback and metadata addresses.
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<OutboundAddressGuard>().Apply(
                new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false }));

        // 0.12 (V-10): one guard for the check before sending and the connect callback; it knows whether the outgoing
        // proxy (Settings → General, or HTTP(S)_PROXY) carries a request, so a name only the proxy could resolve is refused.
        services.TryAddSingleton(sp => new OutboundAddressGuard(usesProxy: uri => OutboundAddressGuard.ThroughProxy(sp.GetService<IWebProxy>() ?? HttpClient.DefaultProxy, uri)));

        return services;
    }
}
