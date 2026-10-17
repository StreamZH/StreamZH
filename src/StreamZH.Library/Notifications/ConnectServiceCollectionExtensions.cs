// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Data;

namespace StreamZH.Library.Notifications;

public static class ConnectServiceCollectionExtensions
{
    /// <summary>
    /// Settings → Connect: the connection table, one sender per type, the dispatcher that reacts to the app events and
    /// the "connect" HttpClient. Needs the library repositories and <see cref="StreamZH.Core.Events.IEventBus"/>.
    /// </summary>
    public static IServiceCollection AddConnectNotifications(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(ConnectOptions.Default);
        services.AddSingleton<ConnectionRepository>();
        services.AddHttpClient(ConnectHttp.ClientName, client => client.Timeout = TimeSpan.FromSeconds(30));

        services.AddSingleton<IConnectionSender, PlexSender>();
        services.AddSingleton<IConnectionSender>(sp => new JellyfinEmbySender(ConnectCatalog.Jellyfin, sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton<IConnectionSender>(sp => new JellyfinEmbySender(ConnectCatalog.Emby, sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton<IConnectionSender, KodiSender>();
        services.AddSingleton<IConnectionSender, DiscordSender>();
        services.AddSingleton<IConnectionSender, TelegramSender>();
        services.AddSingleton<IConnectionSender, PushoverSender>();
        services.AddSingleton<IConnectionSender, NtfySender>();
        services.AddSingleton<IConnectionSender, GotifySender>();
        services.AddSingleton<IConnectionSender, EmailSender>();
        services.AddSingleton<IConnectionSender, WebhookSender>();
        services.AddSingleton<IConnectionSender, AppriseSender>();
        services.TryAddSingleton<IEmailTransport, MailKitTransport>();

        services.AddSingleton<ConnectionService>();
        services.AddSingleton<ConnectEventFactory>();
        services.AddSingleton<ConnectDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<ConnectDispatcher>());
        return services;
    }
}
