// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Host.Auth;

namespace StreamZH.Host.Status;

/// <summary>The status window at the bottom right: GET /api/v1/status/ongoing (administrators only).</summary>
public static class StatusEndpoints
{
    public static IServiceCollection AddOngoingStatus(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IStatusProbe, StatusProbe>();
        services.AddSingleton<OngoingService>();
        services.AddHostedService(sp => sp.GetRequiredService<OngoingService>());
        return services;
    }

    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/status/ongoing", (OngoingService ongoing) => ongoing.Get())
            .WithTags("Status")
            .RequireAuthorization(AuthSchemes.AdminPolicy);
        return app;
    }
}
