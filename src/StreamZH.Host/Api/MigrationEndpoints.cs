// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.Auth;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Arr.Settings;
using StreamZH.Library.Migration.Sabnzbd;

namespace StreamZH.Host.Api;

/// <summary>
/// The migration wizard (0.9): take over settings and libraries from Radarr, Sonarr, Prowlarr and SABnzbd. Admins only.
/// API keys and secrets in the requests are used for that request/run and never stored.
/// </summary>
public static class MigrationEndpoints
{
    public static IServiceCollection AddMigration(this IServiceCollection services)
    {
        services.AddMigrationCore();
        services.AddArrSettingsMigration();
        services.AddArrLibraryMigration();
        services.AddSabnzbdMigration();
        return services;
    }

    public static IEndpointRouteBuilder MapMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/migration").RequireAuthorization(AuthSchemes.AdminPolicy).WithTags("Migration");

        group.MapGet("/sources", (MigrationService migration) => migration.Sources());

        group.MapPost("/{source}/test", async (string source, MigrationRequest request, MigrationService migration, CancellationToken ct) =>
            MigrationSources.IsKnown(source) ? Results.Ok(await migration.TestAsync(source, request, ct)) : UnknownSource(source));

        group.MapPost("/{source}/preview", async (string source, MigrationRequest request, MigrationService migration, CancellationToken ct) =>
            MigrationSources.IsKnown(source) ? Results.Ok(await migration.PreviewAsync(source, request, ct)) : UnknownSource(source));

        group.MapPost("/{source}/run", async (string source, MigrationRequest request, MigrationService migration, HttpContext http, CancellationToken ct) =>
        {
            if (!MigrationSources.IsKnown(source))
            {
                return UnknownSource(source);
            }

            var run = await migration.StartAsync(source, request, http.User.Identity?.Name, ct);
            return Results.Accepted($"/api/v1/migration/runs/{run.Id}", run);
        });

        group.MapGet("/runs", (MigrationService migration) => migration.Runs());

        group.MapGet("/runs/{id:int}", (int id, MigrationService migration) =>
            migration.Find(id) is { } run ? Results.Ok(run) : Results.NotFound());

        group.MapPost("/runs/{id:int}/cancel", (int id, MigrationService migration) =>
            migration.Cancel(id) ? Results.NoContent() : Results.NotFound());

        return app;
    }

    private static IResult UnknownSource(string source) =>
        Results.NotFound(new { title = $"Unknown source '{source}'. Use one of: {string.Join(", ", MigrationSources.All)}." });
}
