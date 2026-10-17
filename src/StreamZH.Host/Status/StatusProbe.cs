// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using StreamZH.Data;

namespace StreamZH.Host.Status;

/// <summary>Free space of the drive a folder is on. <see cref="Drive"/> identifies the drive, so two folders on it are reported once.</summary>
public sealed record DiskSpace(string Drive, long FreeBytes);

/// <summary>Whether StreamZH can see and write a folder.</summary>
public sealed record FolderAccess(bool Exists, bool Writable, string? Problem);

/// <summary>File system checks for the status window; replaced in tests.</summary>
public interface IStatusProbe
{
    /// <summary>Null when the folder does not exist or the drive does not report its space (some network shares).</summary>
    DiskSpace? FreeSpace(string folder);

    FolderAccess Access(string folder);
}

/// <summary>
/// Real checks. The write probe creates and deletes a small file, which security tools may notice, so results are cached
/// for a few minutes: the status window asks every 15 to 60 seconds.
/// </summary>
public sealed class StatusProbe(TimeProvider time) : IStatusProbe
{
    private static readonly TimeSpan AccessTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (FolderAccess Access, DateTimeOffset Checked)> _access = new(PathComparer.Instance);

    public DiskSpace? FreeSpace(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return null;
            }

            var drive = new DriveInfo(folder);
            return new DiskSpace(drive.RootDirectory.FullName, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public FolderAccess Access(string folder)
    {
        var now = time.GetUtcNow();
        if (_access.TryGetValue(folder, out var cached) && now - cached.Checked < AccessTtl)
        {
            return cached.Access;
        }

        var access = Check(folder);
        _access[folder] = (access, now);
        return access;
    }

    private static FolderAccess Check(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return new FolderAccess(false, false, "The folder does not exist or StreamZH cannot see it.");
        }

        var probe = Path.Combine(folder, $".streamzh-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return new FolderAccess(true, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderAccess(true, false, ex.Message);
        }
    }
}
