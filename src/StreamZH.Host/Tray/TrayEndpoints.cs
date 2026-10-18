// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Authentication;
using StreamZH.Core;
using StreamZH.Core.Downloads;
using StreamZH.Core.Tray;
using StreamZH.Host.Auth;
using StreamZH.Host.General;
using StreamZH.Host.Service;
using StreamZH.Host.Status;
using StreamZH.Library.Downloads;

namespace StreamZH.Host.Tray;

/// <summary>
/// /api/v1/tray: what the Windows tray icon needs (status, pause/resume downloads, restart, shut down). Open to the tray
/// token from this computer and to owners and administrators; the token opens nothing else.
/// </summary>
public static class TrayEndpoints
{
    public const string Policy = "Tray";

    public static IServiceCollection AddTrayAccess(this IServiceCollection services)
    {
        services.AddSingleton<TrayAccess>();
        services.AddHostedService(sp => sp.GetRequiredService<TrayAccess>());
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TrayAuthenticationHandler>(TrayAuthenticationHandler.SchemeName, null);
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(TrayAuthenticationHandler.SchemeName, AuthSchemes.Smart)
            .RequireRole(TrayAuthenticationHandler.Role, nameof(Core.Users.UserRole.Owner), nameof(Core.Users.UserRole.Admin)));
        return services;
    }

    public static IEndpointRouteBuilder MapTrayEndpoints(this IEndpointRouteBuilder app)
    {
        var tray = app.MapGroup(TrayContract.ApiPath).WithTags("Tray").RequireAuthorization(Policy);

        // run + after: the tray's last known run and finished-download number (notifications only for newer ones).
        tray.MapGet("/status", (string? run, long? after, HttpContext http) => Build(http.RequestServices, run, after));

        tray.MapPost("/pause", async (DownloadEngine engine) =>
        {
            await engine.PauseAllAsync();
            return Results.NoContent();
        });

        tray.MapPost("/resume", async (DownloadEngine engine) =>
        {
            await engine.ResumeAllAsync();
            return Results.NoContent();
        });

        tray.MapPost("/restart", (AppLifecycle lifecycle) => lifecycle.Restart("tray icon") ? Results.Accepted() : Results.Conflict());
        tray.MapPost("/shutdown", (AppLifecycle lifecycle) => lifecycle.Shutdown("tray icon") ? Results.Accepted() : Results.Conflict());

        return app;
    }

    public static TrayStatus Build(IServiceProvider services, string? run, long? after)
    {
        ArgumentNullException.ThrowIfNull(services);
        var access = services.GetRequiredService<TrayAccess>();
        var engine = services.GetRequiredService<DownloadEngine>();
        var queue = engine.Status();
        var items = engine.Active();
        var notices = services.GetRequiredService<OngoingService>().Get().Notices;
        var (finished, last) = access.Finished(run, after);
        return Summarize(
            items,
            queue,
            notices,
            services.GetRequiredService<HostSettings>().InstanceName,
            access.Run,
            finished,
            last);
    }

    /// <summary>The numbers of the tray status; ETA = bytes still to download over the current speed.</summary>
    public static TrayStatus Summarize(
        IReadOnlyList<DownloadItem> items,
        DownloadQueueStatus queue,
        IReadOnlyList<OngoingNotice> notices,
        string instanceName,
        string run,
        IReadOnlyList<TrayFinishedDownload> finished,
        long lastFinished)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(notices);
        var total = items.Sum(i => Math.Max(0, i.TotalBytes));
        var done = items.Sum(i => Math.Clamp(i.DownloadedBytes, 0, Math.Max(0, i.TotalBytes)));
        var remaining = items
            .Where(i => i.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused)
            .Sum(i => Math.Max(0, i.TotalBytes - i.DownloadedBytes));
        double? eta = !queue.Paused && queue.SpeedBps > 0 && remaining > 0 ? (double)remaining / queue.SpeedBps : null;

        return new TrayStatus(
            AppInfo.Version,
            RunningAs.Mode,
            instanceName,
            run,
            queue.Paused,
            queue.Paused ? queue.StatusMessage : null,
            items.Count,
            total,
            done,
            total > 0 ? (double)done / total : 0,
            queue.SpeedBps,
            eta,
            notices.Count(n => n.Level == NoticeLevels.Warn),
            notices.Count(n => n.Level == NoticeLevels.Error),
            finished,
            lastFinished);
    }
}
