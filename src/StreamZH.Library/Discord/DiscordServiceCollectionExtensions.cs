// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Library.Discord;

public static class DiscordServiceCollectionExtensions
{
    /// <summary>
    /// Settings → Discord and the built-in bot: settings, REST and gateway clients, the bot service, request
    /// notifications and the settings test. <see cref="IRequestGateway"/> defaults to <see cref="RequestsNotReadyGateway"/>
    /// until the requests module registers its adapter (register it before or after this call; the last one wins).
    /// The "discord" HttpClient has no request logging: interaction tokens are part of its URLs.
    /// </summary>
    /// <param name="apiBaseAddress">Development and tests only: a stand-in for Discord's REST API.</param>
    public static IServiceCollection AddDiscordBot(this IServiceCollection services, Uri? apiBaseAddress = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DiscordBotSettings>();
        services.AddSingleton<DiscordBotStatusHolder>();
        services.TryAddSingleton(new DiscordBotOptions());
        services.TryAddSingleton(apiBaseAddress is null ? DiscordApiOptions.Default : new DiscordApiOptions(apiBaseAddress));
        services.TryAddSingleton<IRequestGateway, RequestsNotReadyGateway>();
        services.TryAddSingleton<IDiscordLinkCodes, DiscordLinkCodes>();
        services.TryAddSingleton<DiscordLinkRepository>();
        services.TryAddSingleton<RequestRepository>();

        services.AddHttpClient(DiscordRestClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20)).RemoveAllLoggers();
        services.TryAddSingleton<IDiscordApi>(sp => new DiscordRestClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(DiscordRestClient.HttpClientName),
            sp.GetRequiredService<DiscordApiOptions>(),
            sp.GetRequiredService<TimeProvider>()));

        // The websocket goes through the proxy of Settings → General like every other outgoing connection.
        services.TryAddSingleton(sp => new DiscordGatewayOptions
        {
            Connect = DiscordGatewayOptions.DefaultConnect(() => sp.GetService<IWebProxy>()),
        });
        services.TryAddSingleton<IDiscordGateway>(sp => new DiscordGatewayClient(
            sp.GetRequiredService<IDiscordApi>(),
            sp.GetRequiredService<DiscordGatewayOptions>(),
            sp.GetRequiredService<ILogger<DiscordGatewayClient>>()));

        services.AddSingleton<DiscordInteractionHandler>();
        services.AddSingleton<DiscordBotTester>();
        services.AddSingleton<DiscordBotService>();
        services.AddHostedService(sp => sp.GetRequiredService<DiscordBotService>());
        services.AddSingleton<DiscordNotifier>();
        services.AddHostedService(sp => sp.GetRequiredService<DiscordNotifier>());
        return services;
    }
}
