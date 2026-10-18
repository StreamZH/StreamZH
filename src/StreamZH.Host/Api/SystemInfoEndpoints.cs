// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Backups;
using StreamZH.Host.General;
using StreamZH.Host.Service;
using StreamZH.Host.Status;
using StreamZH.Library.Search;

namespace StreamZH.Host.Api;

/// <summary>System → Status (like Radarr): about, health, disk space.</summary>
public static class SystemInfoEndpoints
{
    public const string License = "GPL-3.0-only";

    public static IEndpointRouteBuilder MapSystemInfoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/system/info", (HttpContext http) => Build(http.RequestServices))
            .WithTags("System")
            .RequireAuthorization(AuthSchemes.AdminPolicy);
        return app;
    }

    /// <summary>"docker", "windowsService", "systemd" or "console" (see <see cref="RunningAs"/>).</summary>
    public static string RuntimeMode() => RunningAs.Mode;

    private static SystemInfo Build(IServiceProvider services)
    {
        var paths = services.GetRequiredService<AppPaths>();
        var database = services.GetRequiredService<Database>();
        var startup = services.GetRequiredService<StartupInfo>();
        var running = services.GetRequiredService<RunningHost>();
        var time = services.GetRequiredService<TimeProvider>();

        long databaseSize = 0;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = new FileInfo(paths.DatabaseFile + suffix);
            databaseSize += file.Exists ? file.Length : 0;
        }

        return new SystemInfo(
            AppInfo.Name,
            AppInfo.Version,
            RuntimeInformation.FrameworkDescription,
            SystemEndpoints.OperatingSystemName(),
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            RuntimeMode(),
            paths.DataDirectory,
            paths.DatabaseFile,
            databaseSize,
            database.SchemaVersion(),
            startup.StartedAt,
            Math.Max(0, (time.GetUtcNow() - startup.StartedAt).TotalSeconds),
            GeneralSettingsEndpoints.Listening(services, running),
            running.Values.UrlBase,
            running.Override,
            DiskSpace(services, paths),
            services.GetService<OngoingService>()?.Get().Notices ?? [],
            services.GetRequiredService<StartupRestore>().Outcome,
            License);
    }

    private static List<DiskSpaceEntry> DiskSpace(IServiceProvider services, AppPaths paths)
    {
        var folders = new List<(string Label, string Path)> { ("Data folder", paths.DataDirectory) };
        if (services.GetService<DownloadSettings>() is { } downloads && downloads.Client == DownloadClientKind.BuiltIn)
        {
            folders.Add(("Incomplete downloads", downloads.IncompleteFolder));
            folders.Add(("Complete downloads", downloads.CompleteFolder));
        }
        else if (services.GetService<DownloadSettings>() is { } blackhole)
        {
            folders.Add(("NZB folder", blackhole.NzbFolder));
        }

        foreach (var root in services.GetRequiredService<RootFolderRepository>().All())
        {
            folders.Add(($"Root folder ({(root.MediaType == Core.Media.MediaType.Movie ? "movies" : "series")})", root.Path));
        }

        folders.Add(("Backups", services.GetRequiredService<BackupSettings>().Folder));

        var seen = new HashSet<string>(PathComparer.Instance);
        var entries = new List<DiskSpaceEntry>();
        foreach (var (label, path) in folders)
        {
            if (seen.Add(path))
            {
                entries.Add(Measure(label, path));
            }
        }

        return entries;
    }

    private static DiskSpaceEntry Measure(string label, string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return new DiskSpaceEntry(label, path, null, null, null, "Folder not found");
            }

            var drive = new DriveInfo(path);
            return new DiskSpaceEntry(label, path, drive.RootDirectory.FullName, drive.AvailableFreeSpace, drive.TotalSize, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DiskSpaceEntry(label, path, null, null, null, ex.Message);
        }
    }
}

/// <param name="Drive">The drive or mount point the folder is on.</param>
public sealed record DiskSpaceEntry(string Label, string Path, string? Drive, long? FreeBytes, long? TotalBytes, string? Problem);

/// <param name="RuntimeMode">"docker", "windowsService", "systemd" or "console".</param>
public sealed record SystemInfo(
    string AppName,
    string Version,
    string Runtime,
    string OperatingSystem,
    string Architecture,
    string RuntimeMode,
    string DataDirectory,
    string DatabaseFile,
    long DatabaseSize,
    long DatabaseSchemaVersion,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    IReadOnlyList<string> Listening,
    string UrlBase,
    string? HostOverride,
    IReadOnlyList<DiskSpaceEntry> DiskSpace,
    IReadOnlyList<OngoingNotice> Health,
    RestoreOutcome? Restore,
    string License);
