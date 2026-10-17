// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.IO.Compression;
using StreamZH.Core;

namespace StreamZH.Host.Backups;

/// <summary>What happened to a staged restore at this start (shown in the status window and logged).</summary>
public sealed record RestoreOutcome(bool Success, string Message, string? PreviousFolder);

/// <summary>Registered once per start; <see cref="Outcome"/> is null when nothing was restored.</summary>
public sealed record StartupRestore(RestoreOutcome? Outcome);

/// <summary>
/// Applies a staged restore (&lt;data&gt;/restore/pending.zip) at startup, before the database is opened. The current
/// database and keys are moved to &lt;data&gt;/restore/before-restore-&lt;time&gt; (the 3 newest are kept), so nothing
/// is lost; a failed restore puts them back and keeps the zip as failed-&lt;time&gt;.zip. Migrations run afterwards,
/// so a backup of an older StreamZH is upgraded.
/// </summary>
public static class PendingRestore
{
    private const int KeepPrevious = 3;

    public static string Folder(AppPaths paths) => Path.Combine(paths.DataDirectory, "restore");

    public static string ZipPath(AppPaths paths) => Path.Combine(Folder(paths), "pending.zip");

    public static string InfoPath(AppPaths paths) => Path.Combine(Folder(paths), "pending.json");

    public static RestoreOutcome? Apply(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var zip = ZipPath(paths);
        if (!File.Exists(zip))
        {
            return null;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var previous = Path.Combine(Folder(paths), $"before-restore-{stamp}");
        var keys = Path.Combine(paths.DataDirectory, BackupService.KeysFolder);
        var moved = new List<(string From, string To)>();
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var database = archive.GetEntry(BackupService.DatabaseEntry) ?? throw new InvalidDataException("streamzh.db is missing in the backup.");
            Directory.CreateDirectory(previous);

            var extracted = Path.Combine(previous, "restored.db.tmp");
            database.ExtractToFile(extracted, overwrite: true);

            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = paths.DatabaseFile + suffix;
                if (File.Exists(file))
                {
                    var target = Path.Combine(previous, Path.GetFileName(file));
                    File.Move(file, target);
                    moved.Add((file, target));
                }
            }

            var keyEntries = archive.Entries
                .Where(e => e.FullName.StartsWith(BackupService.KeysFolder + "/", StringComparison.Ordinal) && e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (keyEntries.Count > 0 && Directory.Exists(keys))
            {
                var target = Path.Combine(previous, BackupService.KeysFolder);
                Directory.Move(keys, target);
                moved.Add((keys, target));
            }

            File.Move(extracted, paths.DatabaseFile);
            if (keyEntries.Count > 0)
            {
                Directory.CreateDirectory(keys);
                foreach (var entry in keyEntries)
                {
                    entry.ExtractToFile(Path.Combine(keys, entry.Name), overwrite: true);
                }
            }

            archive.Dispose();
            var source = File.Exists(InfoPath(paths)) ? " (" + SourceName(paths) + ")" : string.Empty;
            File.Delete(zip);
            File.Delete(InfoPath(paths));
            RemoveOldPrevious(paths);
            return new RestoreOutcome(true, $"Backup restored{source}. The database before the restore is in {previous}.", previous);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Put back what was moved, keep the zip for a look.
            try
            {
                if (moved.Count > 0 && File.Exists(paths.DatabaseFile) && moved.Any(m => m.From == paths.DatabaseFile))
                {
                    File.Delete(paths.DatabaseFile);
                }

                foreach (var (from, to) in Enumerable.Reverse(moved))
                {
                    if (Directory.Exists(to))
                    {
                        if (Directory.Exists(from))
                        {
                            Directory.Delete(from, recursive: true);
                        }

                        Directory.Move(to, from);
                    }
                    else if (File.Exists(to))
                    {
                        File.Move(to, from, overwrite: true);
                    }
                }

                File.Move(zip, Path.Combine(Folder(paths), $"failed-{stamp}.zip"), overwrite: true);
                File.Delete(InfoPath(paths));
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                return new RestoreOutcome(false, $"Restore failed: {ex.Message} Putting the old database back failed too: {cleanup.Message} Look in {previous}.", previous);
            }

            return new RestoreOutcome(false, $"Restore failed, StreamZH started with the database it had: {ex.Message}", null);
        }
    }

    private static string SourceName(AppPaths paths)
    {
        try
        {
            var json = File.ReadAllText(InfoPath(paths));
            var info = System.Text.Json.JsonSerializer.Deserialize<PendingRestoreInfo>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            return info?.Source ?? "backup";
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return "backup";
        }
    }

    private static void RemoveOldPrevious(AppPaths paths)
    {
        try
        {
            foreach (var old in new DirectoryInfo(Folder(paths)).EnumerateDirectories("before-restore-*").OrderByDescending(d => d.Name).Skip(KeepPrevious))
            {
                old.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not important; they are only copies.
        }
    }
}
