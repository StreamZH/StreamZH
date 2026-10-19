// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Host.Security;

namespace StreamZH.Host.Backups;

public enum BackupType
{
    Scheduled,
    Manual,
}

/// <param name="Type">"scheduled" or "manual" (also the sub-folder).</param>
public sealed record BackupInfo(string Name, string Type, long Size, DateTimeOffset Time);

/// <summary>backup.json in every backup zip.</summary>
public sealed record BackupManifest(string App, string Version, long SchemaVersion, DateTimeOffset Created, string Type);

/// <summary>A backup waiting to be restored at the next start.</summary>
public sealed record PendingRestoreInfo(string Source, DateTimeOffset Staged, string? Version);

/// <summary>
/// Backups like Radarr's: a zip with a consistent copy of the database (SQLite "VACUUM INTO", safe while StreamZH runs),
/// the data protection keys (so sign-ins survive a restore) and backup.json. Scheduled ones go to "scheduled" and are
/// deleted after the retention time, manual ones to "manual" and stay. Restore checks the zip and stages it; it is
/// applied at the next start, before the database is opened (<see cref="PendingRestore"/>).
/// </summary>
public sealed partial class BackupService(BackupSettings settings, AppPaths paths, Database database, TimeProvider time, ILogger<BackupService> logger)
{
    public const string DatabaseEntry = "streamzh.db";
    public const string ManifestEntry = "backup.json";
    public const string KeysFolder = "keys";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _lock = new();

    /// <summary>The error of the last scheduled backup, for the status window; null after a successful one.</summary>
    public string? LastScheduledError { get; private set; }

    public static string TypeName(BackupType type) => type == BackupType.Scheduled ? "scheduled" : "manual";

    public static BackupType? ParseType(string? type) => type?.ToLowerInvariant() switch
    {
        "scheduled" => BackupType.Scheduled,
        "manual" => BackupType.Manual,
        _ => null,
    };

    public BackupInfo Create(BackupType type)
    {
        lock (_lock)
        {
            try
            {
                var info = CreateLocked(type);
                if (type == BackupType.Scheduled)
                {
                    LastScheduledError = null;
                }

                return info;
            }
            catch (Exception ex) when (type == BackupType.Scheduled)
            {
                LastScheduledError = ex.Message;
                throw;
            }
        }
    }

    public IReadOnlyList<BackupInfo> List()
    {
        var all = new List<BackupInfo>();
        foreach (var type in Enum.GetValues<BackupType>())
        {
            var folder = Path.Combine(settings.Folder, TypeName(type));
            if (!Directory.Exists(folder))
            {
                continue;
            }

            all.AddRange(new DirectoryInfo(folder).EnumerateFiles("*.zip")
                .Where(f => IsBackupName(f.Name))
                .Select(f => new BackupInfo(f.Name, TypeName(type), f.Length, f.LastWriteTimeUtc)));
        }

        return [.. all.OrderByDescending(b => b.Time)];
    }

    /// <summary>Full path of a backup, or null (unknown type, a name that is not a backup file, missing).</summary>
    public string? PathOf(string type, string name)
    {
        if (ParseType(type) is not { } parsed || !IsBackupName(name))
        {
            return null;
        }

        var path = Path.Combine(settings.Folder, TypeName(parsed), name);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string type, string name)
    {
        if (PathOf(type, name) is not { } path)
        {
            return false;
        }

        File.Delete(path);
        logger.LogInformation("Backup {Name} deleted", name);
        return true;
    }

    /// <summary>Deletes scheduled backups older than the retention time. Returns how many.</summary>
    public int Cleanup()
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(settings.RetentionDays);
        var deleted = 0;
        foreach (var backup in List().Where(b => b.Type == TypeName(BackupType.Scheduled) && b.Time < cutoff))
        {
            try
            {
                File.Delete(Path.Combine(settings.Folder, backup.Type, backup.Name));
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Old backup {Name} could not be deleted: {Message}", backup.Name, ex.Message);
            }
        }

        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} scheduled backups older than {Days} days", deleted, settings.RetentionDays);
        }

        return deleted;
    }

    /// <summary>
    /// Checks a backup zip: contains a StreamZH database that is intact and not from a newer StreamZH. Null when it can
    /// be restored, else the reason.
    /// </summary>
    public string? Validate(string zipPath, out BackupManifest? manifest)
    {
        manifest = null;
        var work = WorkFolder();
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.GetEntry(DatabaseEntry) is not { } entry)
            {
                return "This is not a StreamZH backup: streamzh.db is missing in the zip file.";
            }

            if (archive.GetEntry(ManifestEntry) is { } manifestEntry)
            {
                using var stream = manifestEntry.Open();
                manifest = JsonSerializer.Deserialize<BackupManifest>(stream, Json);
            }

            var extracted = Path.Combine(work, DatabaseEntry);
            entry.ExtractToFile(extracted);
            return CheckDatabase(extracted);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        {
            return $"The file cannot be read as a backup: {ex.Message}";
        }
        finally
        {
            DeleteFolder(work);
        }
    }

    /// <summary>Checks the zip and stages it for the next start. Null when staged, else the reason.</summary>
    public string? StageRestore(string zipPath, string source)
    {
        if (Validate(zipPath, out var manifest) is { } problem)
        {
            return problem;
        }

        Directory.CreateDirectory(PendingRestore.Folder(paths));
        File.Copy(zipPath, PendingRestore.ZipPath(paths), overwrite: true);
        var info = new PendingRestoreInfo(source, time.GetUtcNow(), manifest?.Version);
        File.WriteAllText(PendingRestore.InfoPath(paths), JsonSerializer.Serialize(info, Json));
        logger.LogWarning("Backup {Source} will be restored at the next start", source);
        return null;
    }

    public PendingRestoreInfo? Pending()
    {
        if (!File.Exists(PendingRestore.ZipPath(paths)))
        {
            return null;
        }

        try
        {
            return File.Exists(PendingRestore.InfoPath(paths))
                ? JsonSerializer.Deserialize<PendingRestoreInfo>(File.ReadAllText(PendingRestore.InfoPath(paths)), Json)
                : new PendingRestoreInfo("backup", File.GetLastWriteTimeUtc(PendingRestore.ZipPath(paths)), null);
        }
        catch (JsonException)
        {
            return new PendingRestoreInfo("backup", File.GetLastWriteTimeUtc(PendingRestore.ZipPath(paths)), null);
        }
    }

    public bool CancelRestore()
    {
        var existed = File.Exists(PendingRestore.ZipPath(paths));
        File.Delete(PendingRestore.ZipPath(paths));
        File.Delete(PendingRestore.InfoPath(paths));
        if (existed)
        {
            logger.LogInformation("Staged restore cancelled");
        }

        return existed;
    }

    /// <summary>A file in the data folder for an upload, deleted by the caller.</summary>
    public string UploadPath()
    {
        Directory.CreateDirectory(PendingRestore.Folder(paths));
        return Path.Combine(PendingRestore.Folder(paths), $"upload-{Guid.NewGuid():N}.zip");
    }

    public static bool IsBackupName(string? name) => name is not null && BackupNameRegex().IsMatch(name);

    private BackupInfo CreateLocked(BackupType type)
    {
        var folder = Path.Combine(settings.Folder, TypeName(type));
        Directory.CreateDirectory(folder);
        var now = time.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, time.LocalTimeZone);
        var baseName = $"streamzh_backup_v{AppInfo.Version}_{local.ToString("yyyy.MM.dd_HH.mm.ss", CultureInfo.InvariantCulture)}";
        var name = baseName + ".zip";
        for (var i = 2; File.Exists(Path.Combine(folder, name)); i++)
        {
            name = $"{baseName}_{i}.zip";
        }

        var work = WorkFolder();
        var target = Path.Combine(folder, name);
        var partial = target + ".partial";
        try
        {
            // A consistent copy while StreamZH keeps writing (WAL mode): VACUUM INTO.
            var copy = Path.Combine(work, DatabaseEntry);
            using (var connection = database.Open())
            {
                connection.Execute("VACUUM INTO @copy", new { copy });
            }

            var manifest = new BackupManifest(AppInfo.Name, AppInfo.Version, database.SchemaVersion(), now, TypeName(type));
            using (var archive = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(copy, DatabaseEntry, CompressionLevel.Optimal);
                var keys = Path.Combine(paths.DataDirectory, KeysFolder);
                if (Directory.Exists(keys))
                {
                    foreach (var file in Directory.EnumerateFiles(keys, "*.xml"))
                    {
                        archive.CreateEntryFromFile(file, $"{KeysFolder}/{Path.GetFileName(file)}", CompressionLevel.Optimal);
                    }
                }

                var entry = archive.CreateEntry(ManifestEntry);
                using var stream = entry.Open();
                JsonSerializer.Serialize(stream, manifest, Json);
            }

            File.Move(partial, target);

            // The database and the sign-in keys are secrets: owner only, also in a backup folder outside the data folder.
            DataFolderPermissions.RestrictFile(target);
            var info = new FileInfo(target);
            logger.LogInformation("Backup {Name} created ({Type}, {Size} bytes)", name, TypeName(type), info.Length);
            return new BackupInfo(name, TypeName(type), info.Length, info.LastWriteTimeUtc);
        }
        finally
        {
            File.Delete(partial);
            DeleteFolder(work);
        }
    }

    private string? CheckDatabase(string file)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            if (connection.ExecuteScalar<string>("PRAGMA integrity_check") != "ok")
            {
                return "The database in the backup is damaged.";
            }

            var tables = connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table'").ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!tables.Contains("Config") || !tables.Contains("VersionInfo"))
            {
                return "This is not a StreamZH database.";
            }

            var version = connection.ExecuteScalar<long>("SELECT IFNULL(MAX(Version), 0) FROM VersionInfo");
            var current = database.SchemaVersion();
            return version > current
                ? $"The backup is from a newer StreamZH (database version {version}, this one knows up to {current}). Update StreamZH first."
                : null;
        }
        catch (SqliteException ex)
        {
            return $"The database in the backup cannot be opened: {ex.Message}";
        }
    }

    private string WorkFolder()
    {
        var work = Path.Combine(paths.DataDirectory, "restore", "work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next cleanup; never fail a backup because of the temp folder.
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\.zip$")]
    private static partial Regex BackupNameRegex();
}
