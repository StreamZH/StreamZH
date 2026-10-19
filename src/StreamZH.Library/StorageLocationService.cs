// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core;
using StreamZH.Data;

namespace StreamZH.Library;

/// <summary>One storage location as Settings → General → Storage shows it.</summary>
/// <param name="Uses">What is configured inside it ("Root folder D:\Media\Movies"), at most a few.</param>
public sealed record StorageLocationInfo(string Path, bool Exists, IReadOnlyList<string> Uses);

/// <summary>
/// 0.12 (SV2 N-01, N-03, N-05, N-06): the base folders StreamZH may read and write media and downloads in, chosen by the
/// owner (Settings → General → Storage). Every folder an admin sets (root, download, category, watch, NZB backup and
/// recycle bin folders, torrent save paths, file renamer folders, remote path mappings, the folder browser) must be inside
/// one of them (<see cref="AppPaths.StorageProblem"/>), so an admin cannot make StreamZH write into a profile, a start-up
/// folder or the program folder, nor open a network share the owner did not allow. Stored as a JSON list in Config;
/// kept in <see cref="AppPaths.StorageLocations"/> for the checks.
/// </summary>
public sealed class StorageLocationService(
    ConfigRepository config,
    AppPaths paths,
    RootFolderRepository rootFolders,
    DownloadCategoryRepository categories,
    RemotePathMappingRepository mappings,
    MovieRepository? movies = null,
    SeriesRepository? series = null)
{
    private static readonly (string Key, string Label)[] FolderSettings =
    [
        (ConfigKeys.NzbFolder, "NZB folder"),
        (ConfigKeys.IncompleteFolder, "Incomplete folder"),
        (ConfigKeys.CompleteFolder, "Completed folder"),
        (ConfigKeys.DownloadNzbBackupFolder, "NZB backup folder"),
        (ConfigKeys.DownloadWatchFolder, "Watched folder"),
        (ConfigKeys.RecycleBin, "Recycle bin"),
        (ConfigKeys.BackupFolder, "Backup folder"),
    ];

    /// <summary>Reads the stored list into <paramref name="paths"/> (at start). A missing or broken value means no locations: everything is refused until the owner adds one.</summary>
    public static IReadOnlyList<string> Load(ConfigRepository config, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paths);
        var list = Parse(config.Get(ConfigKeys.StorageLocations));
        paths.SetStorageLocations(list);
        return list;
    }

    private static List<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(stored)?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> Locations => Parse(config.Get(ConfigKeys.StorageLocations));

    public IReadOnlyList<StorageLocationInfo> All()
    {
        var uses = Uses();
        return [.. Locations.Select(l => new StorageLocationInfo(
            l,
            Exists(l),
            [.. uses.Where(u => DataFolderGuard.IsSameOrInside(u.Path, l)).Select(u => $"{u.Label}: {u.Path}").Take(5)]))];
    }

    /// <summary>Adds a folder (the owner). A folder inside an existing location is refused; locations inside the new one are merged into it.</summary>
    /// <exception cref="ValidationFailedException">Not a full path, a device or short path, or already covered.</exception>
    public IReadOnlyList<StorageLocationInfo> Add(string? path)
    {
        var full = Check(path);
        var current = Locations.ToList();
        if (current.FirstOrDefault(l => DataFolderGuard.IsSameOrInside(full, PathIdentity.Normalize(l))) is { } covering)
        {
            throw Invalid(string.Equals(PathIdentity.Normalize(covering), full, PathIdentity.Comparison)
                ? "This storage location exists already."
                : $"This folder is inside the storage location {covering} already.");
        }

        if (!Exists(full))
        {
            throw Invalid("This folder does not exist or StreamZH cannot see it.");
        }

        current.RemoveAll(l => DataFolderGuard.IsSameOrInside(PathIdentity.Normalize(l), full));
        current.Add(full);
        Save(current);
        return All();
    }

    /// <summary>Removes a location (the owner); refused while root, download or other folders are configured inside it.</summary>
    /// <exception cref="LibraryException">Unknown, or still in use.</exception>
    public void Remove(string? path)
    {
        var current = Locations.ToList();
        var match = current.FirstOrDefault(l => path is not null && string.Equals(l, path.Trim(), PathIdentity.Comparison))
            ?? throw new LibraryException("This storage location does not exist.", LibraryError.NotFound);
        var rest = current.Where(l => !ReferenceEquals(l, match)).ToList();
        var orphaned = Uses().Where(u => DataFolderGuard.IsSameOrInside(u.Path, match) && !rest.Any(r => DataFolderGuard.IsSameOrInside(u.Path, r))).ToList();
        if (orphaned.Count > 0)
        {
            throw new LibraryException(
                $"Still in use: {string.Join("; ", orphaned.Take(3).Select(u => $"{u.Label} {u.Path}"))}{(orphaned.Count > 3 ? " and more" : string.Empty)}. Change those first.",
                LibraryError.InUse);
        }

        Save(rest);
    }

    private void Save(List<string> locations)
    {
        var sorted = locations.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
        config.Set(ConfigKeys.StorageLocations, JsonSerializer.Serialize(sorted));
        paths.SetStorageLocations(sorted);
    }

    private static string Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            throw Invalid("Enter a full path, e.g. /data/media, D:\\Media or \\\\server\\share.");
        }

        if (PathIdentity.SyntaxProblem(path, OperatingSystem.IsWindows()) is { } syntax)
        {
            throw Invalid(syntax);
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Invalid("This is not a valid path.");
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ValidationFailedException Invalid(string message) => new(new Dictionary<string, string[]> { ["path"] = [message] });

    /// <summary>Every folder the configuration uses (absolute ones), with what it is.</summary>
    public IReadOnlyList<(string Label, string Path)> Uses()
    {
        var uses = new List<(string Label, string Path)>();
        void Add(string label, string? folder)
        {
            if (!string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder.Trim()))
            {
                uses.Add((label, Path.TrimEndingDirectorySeparator(folder.Trim())));
            }
        }

        foreach (var root in rootFolders.All())
        {
            Add("Root folder", root.Path);
        }

        foreach (var (key, label) in FolderSettings)
        {
            Add(label, config.Get(key));
        }

        foreach (var category in categories.All())
        {
            Add($"Category {category.Name}", category.Folder);
        }

        foreach (var mapping in mappings.All())
        {
            Add($"Remote path mapping {mapping.Host}", mapping.LocalPath);
        }

        // SV3 W-04: movie and series folders outside their root folder (taken over from Radarr/Sonarr) keep their location.
        var roots = rootFolders.All().ToDictionary(r => r.Id, r => r.Path);
        bool OutsideRoot(int rootId, string folder) =>
            !roots.TryGetValue(rootId, out var root) || !DataFolderGuard.IsSameOrInside(folder, root);
        foreach (var movie in movies?.All() ?? [])
        {
            if (!string.IsNullOrWhiteSpace(movie.FolderPath) && OutsideRoot(movie.RootFolderId, movie.FolderPath))
            {
                Add($"Movie {movie.Title}", movie.FolderPath);
            }
        }

        foreach (var show in series?.All() ?? [])
        {
            if (!string.IsNullOrWhiteSpace(show.FolderPath) && OutsideRoot(show.RootFolderId, show.FolderPath))
            {
                Add($"Series {show.Title}", show.FolderPath);
            }
        }

        return uses;
    }
}
