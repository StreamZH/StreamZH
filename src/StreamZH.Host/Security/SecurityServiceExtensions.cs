// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.Hubs;

namespace StreamZH.Host.Security;

/// <summary>The 0.12 security pieces (security review S1/S3); docs/SECURITY.md describes what they protect against.</summary>
public static class SecurityServiceExtensions
{
    public static IServiceCollection AddStreamZhSecurity(this IServiceCollection services)
    {
        services.AddSingleton<AccessSettings>();
        services.AddSingleton<HostAccess>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<SetupCode>();
        services.AddHostedService(sp => sp.GetRequiredService<SetupCode>());
        services.AddSingleton<HubConnections>();
        services.AddHostedService(sp => sp.GetRequiredService<HubConnections>());
        return services;
    }

    /// <summary>Before rate limits and sign-in: security headers on every response, client address from trusted proxies.</summary>
    public static IApplicationBuilder UseStreamZhSecurity(this IApplicationBuilder app)
    {
        app.UseSecurityHeaders();
        app.UseTrustedProxyForwarding();
        return app;
    }
}
