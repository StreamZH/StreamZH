// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Data;
using System.Text.Json;
using Dapper;
using FluentMigrator;
using Microsoft.Data.Sqlite;
using StreamZH.Core;

namespace StreamZH.Data.Migrations;

/// <summary>
/// 0.12 (SV2 N-03): the owner's storage locations, the base folders every admin-set folder must be inside. An existing
/// installation gets every folder it uses today, so nothing stops working: root folders (and movie or series folders
/// outside them), the download folders (NZB,
/// incomplete, complete, NZB backup, watched), absolute category folders, the recycle bin, the local side of remote path
/// mappings, a backup folder outside the data folder, and the default download folder (&lt;data&gt;/downloads). A new
/// installation starts with the default download folder only. Folders inside another one are left out.
/// </summary>
[Migration(29, "Storage locations")]
public sealed class M029_StorageLocations : Migration
{
    private static readonly string[] FolderKeys =
    [
        ConfigKeys.NzbFolder, ConfigKeys.IncompleteFolder, ConfigKeys.CompleteFolder, ConfigKeys.DownloadNzbBackupFolder,
        ConfigKeys.DownloadWatchFolder, ConfigKeys.RecycleBin, ConfigKeys.BackupFolder,
    ];

    public override void Up() => Execute.WithConnection((connection, transaction) =>
    {
        var file = new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource;
        var data = file is { Length: > 0 } && file != ":memory:" && Path.IsPathFullyQualified(file) ? Path.GetDirectoryName(file) : null;
        Seed(connection, transaction, data);
    });

    public override void Down() => Execute.Sql($"DELETE FROM Config WHERE Key = '{ConfigKeys.StorageLocations}'");

    /// <summary>Writes the storage locations unless they are set already; returns them.</summary>
    public static IReadOnlyList<string> Seed(IDbConnection connection, IDbTransaction? transaction, string? dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var existing = connection.QuerySingleOrDefault<string>(
            "SELECT Value FROM Config WHERE Key = @key", new { key = ConfigKeys.StorageLocations }, transaction);
        if (existing is not null)
        {
            return JsonSerializer.Deserialize<List<string>>(existing) ?? [];
        }

        var folders = new List<string>();
        if (dataDirectory is not null)
        {
            folders.Add(Path.Combine(dataDirectory, "downloads"));
        }

        folders.AddRange(connection.Query<string>("SELECT Path FROM RootFolders", transaction: transaction));

        // Movie and series folders outside their root folder (possible after a migration from Radarr/Sonarr).
        folders.AddRange(connection.Query<string>("SELECT FolderPath FROM Movies UNION SELECT FolderPath FROM Series", transaction: transaction));
        folders.AddRange(connection.Query<string>(
            $"SELECT Value FROM Config WHERE Key IN ({string.Join(", ", FolderKeys.Select(k => $"'{k}'"))})", transaction: transaction));
        folders.AddRange(connection.Query<string>("SELECT Folder FROM DownloadCategories", transaction: transaction));
        folders.AddRange(connection.Query<string>("SELECT LocalPath FROM RemotePathMappings", transaction: transaction));

        var locations = Reduce(folders, dataDirectory);
        connection.Execute(
            "INSERT INTO Config (Key, Value) VALUES (@key, @value)",
            new { key = ConfigKeys.StorageLocations, value = JsonSerializer.Serialize(locations) },
            transaction);
        return locations;
    }

    /// <summary>
    /// Full paths only, without trailing separators and duplicates; a folder inside another one is left out, and so is
    /// a folder inside the data folder other than its downloads folder (V-02 refuses those anyway).
    /// </summary>
    public static List<string> Reduce(IEnumerable<string?> folders, string? dataDirectory)
    {
        var full = new List<string>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder.Trim()))
            {
                continue;
            }

            string path;
            try
            {
                path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (dataDirectory is not null
                && DataFolderGuard.IsSameOrInside(path, dataDirectory)
                && !DataFolderGuard.IsSameOrInside(path, Path.Combine(dataDirectory, "downloads")))
            {
                continue;
            }

            full.Add(path);
        }

        return [.. full
            .Distinct(StringComparer.FromComparison(PathIdentity.Comparison))
            .Where(p => !full.Any(other => !string.Equals(other, p, PathIdentity.Comparison) && DataFolderGuard.IsSameOrInside(p, other)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }
}
