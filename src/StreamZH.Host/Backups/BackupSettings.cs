// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Host.General;

namespace StreamZH.Host.Backups;

/// <summary>Settings → General → Backups (like Radarr): folder, interval and retention of the scheduled backups.</summary>
public sealed class BackupSettings(SettingsCache cache, AppPaths paths)
{
    public const int DefaultIntervalDays = 7;
    public const int MaxIntervalDays = 30;
    public const int DefaultRetentionDays = 28;
    public const int MaxRetentionDays = 365;

    public string DefaultFolder => Path.Combine(paths.DataDirectory, "Backups");

    /// <summary>
    /// Where backups go; relative paths are inside the data folder. A folder in the data folder outside Backups/ (or one
    /// that contains the data folder, e.g. from an older version or a restored database) is not used (0.12, V-02).
    /// </summary>
    public string Folder => cache.Get(ConfigKeys.BackupFolder) is { } folder
        && Path.GetFullPath(folder, paths.DataDirectory) is var full
        && paths.FolderProblem(full, DataFolderUse.Backups) is null
            ? full
            : DefaultFolder;

    /// <summary>Days between scheduled backups; 0 = no scheduled backups.</summary>
    public int IntervalDays => Math.Clamp(cache.GetInt(ConfigKeys.BackupIntervalDays, DefaultIntervalDays), 0, MaxIntervalDays);

    /// <summary>Scheduled backups older than this are deleted. Manual backups stay until deleted by hand.</summary>
    public int RetentionDays => Math.Clamp(cache.GetInt(ConfigKeys.BackupRetentionDays, DefaultRetentionDays), 1, MaxRetentionDays);

    public bool IsDefaultFolder => cache.Get(ConfigKeys.BackupFolder) is null;

    public void Update(string? folder, int? intervalDays, int? retentionDays)
    {
        if (folder is not null)
        {
            var trimmed = folder.Trim();
            cache.Set(ConfigKeys.BackupFolder, trimmed.Length == 0 || PathComparer.Instance.Equals(Path.GetFullPath(trimmed, paths.DataDirectory), DefaultFolder) ? null : trimmed);
        }

        if (intervalDays is { } interval)
        {
            cache.SetInt(ConfigKeys.BackupIntervalDays, interval);
        }

        if (retentionDays is { } retention)
        {
            cache.SetInt(ConfigKeys.BackupRetentionDays, retention);
        }
    }
}
