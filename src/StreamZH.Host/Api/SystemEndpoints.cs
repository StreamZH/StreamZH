// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Logging;

namespace StreamZH.Host.Api;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/system").WithTags("System").RequireAuthorization();

        group.MapGet("/status", (Database database, StartupInfo startup) => new SystemStatus(
            AppName: AppInfo.Name,
            Version: AppInfo.Version,
            Runtime: RuntimeInformation.FrameworkDescription,
            OperatingSystem: RuntimeInformation.OSDescription,
            IsDocker: Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
            StartedAt: startup.StartedAt,
            DatabaseSchemaVersion: database.SchemaVersion()));

        group.MapGet("/logs", (FileLoggerProvider files, int? lines) =>
            new LogTail(files.Tail(Math.Clamp(lines ?? 200, 1, 2000))))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        // Anonymous, for Docker health checks and uptime monitors.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("System").AllowAnonymous();

        return app;
    }
}

public sealed record SystemStatus(
    string AppName,
    string Version,
    string Runtime,
    string OperatingSystem,
    bool IsDocker,
    DateTimeOffset StartedAt,
    long DatabaseSchemaVersion);

public sealed record LogTail(IReadOnlyList<string> Lines);