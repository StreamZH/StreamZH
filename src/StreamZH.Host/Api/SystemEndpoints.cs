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
            OperatingSystem: OperatingSystemName(),
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

    /// <summary>"Windows 11 25H2" instead of "Microsoft Windows 10.0.26200".</summary>
    public static string OperatingSystemName()
    {
        if (OperatingSystem.IsWindows())
        {
            var build = Environment.OSVersion.Version.Build;
            var name = build >= 22000 ? "Windows 11" : "Windows 10";
            try
            {
                var display = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
                return string.IsNullOrWhiteSpace(display) ? $"{name} (build {build})" : $"{name} {display}";
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return $"{name} (build {build})";
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"macOS {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";
        }

        return RuntimeInformation.OSDescription;
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