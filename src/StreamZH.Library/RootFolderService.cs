// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library;

public sealed record RootFolderInfo(int Id, string Path, MediaType MediaType, bool Accessible, long? FreeSpace, long? TotalSpace, int UnmappedFolders);

public sealed class RootFolderService(RootFolderRepository rootFolders, MovieRepository movies, SeriesRepository series)
{
    public IReadOnlyList<RootFolderInfo> All() => [.. rootFolders.All().Select(Describe)];

    public RootFolderInfo Add(string path, MediaType mediaType)
    {
        if (mediaType is not (MediaType.Movie or MediaType.Series))
        {
            throw new LibraryException("Only movies and series are supported in this version.", LibraryError.Invalid);
        }

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            throw new LibraryException("Enter a full path, e.g. /media/movies or D:\\Movies.", LibraryError.Invalid);
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

        if (!Directory.Exists(full))
        {
            throw new LibraryException("This folder does not exist or StreamZH cannot see it.", LibraryError.Invalid);
        }

        if (!IsWritable(full))
        {
            throw new LibraryException("StreamZH cannot write to this folder. Check the permissions.", LibraryError.Invalid);
        }

        foreach (var existing in rootFolders.All())
        {
            if (PathComparer.Instance.Equals(existing.Path, full))
            {
                throw new LibraryException("This root folder already exists.", LibraryError.AlreadyExists);
            }

            if (IsInside(full, existing.Path) || IsInside(existing.Path, full))
            {
                throw new LibraryException($"Root folders cannot be inside each other ({existing.Path}).", LibraryError.Invalid);
            }
        }

        var id = rootFolders.Add(full, mediaType);
        return Describe(rootFolders.Find(id)!);
    }

    public void Delete(int id)
    {
        _ = rootFolders.Find(id) ?? throw new LibraryException("Root folder not found.", LibraryError.NotFound);
        if (rootFolders.InUse(id))
        {
            throw new LibraryException("Remove or move the movies and series in this folder first.", LibraryError.Invalid);
        }

        rootFolders.Delete(id);
    }

    private RootFolderInfo Describe(RootFolder root)
    {
        var accessible = Directory.Exists(root.Path);
        long? free = null, total = null;

        if (accessible)
        {
            try
            {
                var drive = new DriveInfo(root.Path);
                free = drive.AvailableFreeSpace;
                total = drive.TotalSize;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Network shares or unusual mounts may not report space.
            }
        }

        var unmapped = accessible ? LibraryImportService.UnmappedFolderNames(root, MappedFolders(root.MediaType)).Count() : 0;
        return new RootFolderInfo(root.Id, root.Path, root.MediaType, accessible, free, total, unmapped);
    }

    internal IReadOnlySet<string> MappedFolders(MediaType mediaType) =>
        mediaType == MediaType.Movie ? movies.FolderPaths() : series.FolderPaths();

    private static bool IsInside(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool IsWritable(string folder)
    {
        var probe = Path.Combine(folder, $".streamzh-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}