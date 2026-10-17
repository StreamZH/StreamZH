// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Parsing;

namespace StreamZH.Library;

/// <summary>A folder in a root folder that is not in the library yet.</summary>
public sealed record ImportCandidate(string FolderName, string Path, string Title, int? Year, int? TmdbId, int VideoFiles, long Size);

public sealed record ImportItem(string FolderName, int TmdbId);

/// <summary>Finds existing media folders and adds them to the library without moving or renaming anything.</summary>
public sealed class LibraryImportService(RootFolderRepository rootFolders, MovieRepository movies, SeriesRepository series)
{
    private static readonly IReadOnlySet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "@eadir", "#recycle", "$recycle.bin", "system volume information", "lost+found", ".trash", ".grab", "@recycle",
    };

    public IReadOnlyList<ImportCandidate> Candidates(int rootFolderId)
    {
        var root = rootFolders.Find(rootFolderId) ?? throw new LibraryException("Root folder not found.", LibraryError.NotFound);
        var mapped = root.MediaType == MediaType.Movie ? movies.FolderPaths() : series.FolderPaths();

        return [.. UnmappedFolderNames(root, mapped)
            .Select(name =>
            {
                var path = Path.Combine(root.Path, name);
                var parsed = FolderNameParser.Parse(name);
                var files = DiskScanner.VideoFiles(path).ToList();
                return new ImportCandidate(name, path, parsed.Title, parsed.Year, parsed.TmdbId, files.Count, files.Sum(f => f.Length));
            })
            .OrderBy(c => c.FolderName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Validates the selection and returns the folder path for each item.</summary>
    public IReadOnlyList<(ImportItem Item, string FolderPath)> Prepare(int rootFolderId, IReadOnlyList<ImportItem> items)
    {
        var root = rootFolders.Find(rootFolderId) ?? throw new LibraryException("Root folder not found.", LibraryError.NotFound);
        var available = UnmappedFolderNames(root, root.MediaType == MediaType.Movie ? movies.FolderPaths() : series.FolderPaths())
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<(ImportItem, string)>();
        foreach (var item in items.DistinctBy(i => i.FolderName))
        {
            if (!available.Contains(item.FolderName) || item.FolderName.Contains("..", StringComparison.Ordinal))
            {
                throw new LibraryException($"'{item.FolderName}' is not an unmapped folder in {root.Path}.", LibraryError.Invalid);
            }

            result.Add((item, Path.Combine(root.Path, item.FolderName)));
        }

        return result;
    }

    internal static IEnumerable<string> UnmappedFolderNames(RootFolder root, IReadOnlySet<string> mapped)
    {
        var directory = new DirectoryInfo(root.Path);
        if (!directory.Exists)
        {
            return [];
        }

        return directory.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
            .Where(d => !d.Name.StartsWith('.') && !Ignored.Contains(d.Name))
            .Where(d => !mapped.Contains(d.FullName))
            .Select(d => d.Name);
    }
}