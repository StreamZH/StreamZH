// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StreamZH.Core;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library;

public sealed record RootFolderInfo(
    int Id, string Path, MediaType MediaType, bool Accessible, bool Writable, string? WriteProblem, long? FreeSpace, long? TotalSpace, int UnmappedFolders);

/// <param name="paths">0.12 (V-02): a root folder may not be inside the data folder (outside downloads/) or contain it.</param>
public sealed class RootFolderService(
    RootFolderRepository rootFolders, MovieRepository movies, SeriesRepository series, ILogger<RootFolderService> logger, AppPaths? paths = null)
{
    // A write probe can trigger security tools (e.g. Windows "Controlled folder access"), so results are cached.
    private static readonly ConcurrentDictionary<string, (bool Writable, string? Problem, DateTime Checked)> WriteChecks = new(PathComparer.Instance);
    private static readonly TimeSpan WriteCheckTtl = TimeSpan.FromMinutes(30);

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
        if (PathIdentity.SyntaxProblem(path, OperatingSystem.IsWindows()) is null && paths?.StorageProblem(full) is { } outside)
        {
            throw new LibraryException(outside, LibraryError.OutsideStorage);
        }

        if (paths?.FolderProblem(full) is { } dataProblem)
        {
            throw new LibraryException(dataProblem, LibraryError.Invalid);
        }


        if (!Directory.Exists(full))
        {
            throw new LibraryException("This folder does not exist or StreamZH cannot see it.", LibraryError.Invalid);
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

        // Read-only folders are allowed: importing and scanning work, moving new downloads in will not.
        WriteChecks.TryRemove(full, out _);
        var id = rootFolders.Add(full, mediaType);
        var info = Describe(rootFolders.Find(id)!);
        if (!info.Writable)
        {
            logger.LogWarning("Root folder {Path} is read-only for StreamZH: {Problem}", full, info.WriteProblem);
        }

        return info;
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

        var (writable, problem) = accessible ? CheckWritable(root.Path) : (false, null);
        var unmapped = accessible ? LibraryImportService.UnmappedFolderNames(root, MappedFolders(root.MediaType)).Count() : 0;
        return new RootFolderInfo(root.Id, root.Path, root.MediaType, accessible, writable, problem, free, total, unmapped);
    }

    internal IReadOnlySet<string> MappedFolders(MediaType mediaType) =>
        mediaType == MediaType.Movie ? movies.FolderPaths() : series.FolderPaths();

    private static bool IsInside(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static (bool Writable, string? Problem) CheckWritable(string folder)
    {
        if (WriteChecks.TryGetValue(folder, out var cached) && DateTime.UtcNow - cached.Checked < WriteCheckTtl)
        {
            return (cached.Writable, cached.Problem);
        }

        var probe = Path.Combine(folder, $".streamzh-write-test-{Guid.NewGuid():N}");
        (bool, string?) result;
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            result = (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = (false, OperatingSystem.IsWindows()
                ? $"{ex.Message} If the folder is under Documents, Videos or Pictures, Windows Security \"Controlled folder access\" may block StreamZH."
                : ex.Message);
        }

        WriteChecks[folder] = (result.Item1, result.Item2, DateTime.UtcNow);
        return result;
    }
}
