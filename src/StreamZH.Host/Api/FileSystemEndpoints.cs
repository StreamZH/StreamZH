// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core;
using StreamZH.Host.Auth;

namespace StreamZH.Host.Api;

/// <summary>Folder browser for picking root folders on the machine StreamZH runs on (not the browser's machine).</summary>
public static class FileSystemEndpoints
{
    private static readonly HashSet<string> HiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$recycle.bin", "system volume information", "@eadir", "#recycle", "lost+found", "proc", "sys", "dev", "run",
    };

    public static IEndpointRouteBuilder MapFileSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // 0.12 (SV2 N-03, N-06): admins browse only inside the owner's storage locations (and a typed path outside them
        // is refused before it is opened, so a network path is never contacted); the owner browses everything.
        app.MapGet("/api/v1/filesystem", (string? path, HttpContext http, AppPaths paths) =>
        {
            var owner = GeneralSettingsEndpoints.IsOwner(http.User);
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.Ok(new FolderListing(null, null, owner ? [.. StorageRoots(paths), .. Roots(paths)] : StorageRoots(paths)));
            }

            if (!Path.IsPathFullyQualified(path))
            {
                return Results.Problem("Enter a full path.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!owner && (PathIdentity.SyntaxProblem(path, OperatingSystem.IsWindows()) ?? paths.StorageProblem(path)) is { } outside)
            {
                return Results.Problem(outside, statusCode: StatusCodes.Status403Forbidden, extensions: new Dictionary<string, object?> { ["code"] = AppPaths.OutsideStorageCode });
            }

            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                return Results.Problem("This folder does not exist.", statusCode: StatusCodes.Status404NotFound);
            }

            try
            {
                var entries = directory
                    .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
                    .Where(d => !d.Name.StartsWith('.') && !HiddenNames.Contains(d.Name) && !d.Attributes.HasFlag(FileAttributes.Hidden))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new FolderEntry(d.Name, d.FullName, null))
                    .ToList();

                // Admins cannot go up out of a storage location (they get the list of storage locations instead).
                var up = directory.Parent?.FullName;
                if (!owner && up is not null && paths.StorageProblem(up) is not null)
                {
                    up = null;
                }

                return Results.Ok(new FolderListing(directory.FullName, up, entries));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Problem("StreamZH is not allowed to open this folder.", statusCode: StatusCodes.Status403Forbidden);
            }
        }).RequireAuthorization(AuthSchemes.AdminPolicy).WithTags("File system");

        return app;
    }

    private static List<FolderEntry> StorageRoots(AppPaths paths) =>
        [.. (paths.StorageLocations ?? []).Select(l => new FolderEntry(l, l, "Storage location"))];

    private static List<FolderEntry> Roots(AppPaths paths)
    {
        var roots = new List<FolderEntry>();
        var storage = paths.StorageLocations ?? [];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        void Add(string? path, string? label)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && roots.All(r => !string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))
                && !storage.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(new FolderEntry(label ?? path, path, label is null ? null : path));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Network or DriveType.Removable))
            {
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.Name} {drive.VolumeLabel}";
                roots.Add(new FolderEntry(label, drive.RootDirectory.FullName, drive.DriveType == DriveType.Network ? "Network drive" : null));
            }

            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Videos");
            Add(home, "Home");
        }
        else
        {
            Add("/", "/");
            Add(home, "Home");
            foreach (var mount in new[] { "/mnt", "/media", "/srv", "/data", "/volume1", "/share", "/Volumes" })
            {
                Add(mount, mount);
            }
        }

        return roots;
    }
}

/// <param name="Hint">Extra text shown next to the entry, e.g. "Network drive".</param>
public sealed record FolderEntry(string Name, string Path, string? Hint);

public sealed record FolderListing(string? Path, string? Parent, IReadOnlyList<FolderEntry> Folders);