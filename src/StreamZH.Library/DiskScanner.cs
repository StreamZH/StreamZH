// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Parsing;

namespace StreamZH.Library;

/// <summary>Finds video files in movie and series folders. Read-only, never changes anything on disk.</summary>
public static class DiskScanner
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".divx", ".xvid", ".flv", ".vob",
    };

    private static readonly IReadOnlySet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sample", "samples", "extras", "featurettes", "behind the scenes", "deleted scenes", "interviews", "trailers",
        "@eadir", ".@__thumb", "$recycle.bin", "system volume information", "lost+found",
    };

    private const long SampleMaxBytes = 300L * 1024 * 1024;

    /// <summary>The main movie file: the largest video file that is not a sample.</summary>
    public static MediaFile? ScanMovie(string folder)
    {
        var best = VideoFiles(folder).MaxBy(f => f.Length);
        return best is null ? null : ToMediaFile(folder, best);
    }

    /// <summary>Episode files by (season, episode). Multi-episode files map to every episode they contain.</summary>
    public static IReadOnlyDictionary<(int Season, int Episode), MediaFile> ScanSeries(string folder)
    {
        var result = new Dictionary<(int, int), MediaFile>();

        foreach (var file in VideoFiles(folder))
        {
            if (EpisodeFileParser.Parse(file.Name) is not { } parsed)
            {
                continue;
            }

            var mediaFile = ToMediaFile(folder, file);
            foreach (var episode in parsed.Episodes)
            {
                var key = (parsed.Season, episode);
                if (!result.TryGetValue(key, out var existing) || existing.Size < mediaFile.Size)
                {
                    result[key] = mediaFile;
                }
            }
        }

        return result;
    }

    public static IEnumerable<FileInfo> VideoFiles(string folder)
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
        {
            return [];
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            MaxRecursionDepth = 4,
        };

        return root.EnumerateFiles("*", options)
            .Where(f => VideoExtensions.Contains(f.Extension))
            .Where(f => !IsInSkippedFolder(root, f))
            .Where(f => !IsSample(f));
    }

    private static bool IsInSkippedFolder(DirectoryInfo root, FileInfo file)
    {
        var relative = Path.GetRelativePath(root.FullName, file.DirectoryName ?? root.FullName);
        return relative != "." && relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SkippedFolders.Contains);
    }

    private static bool IsSample(FileInfo file) =>
        file.Length < SampleMaxBytes && file.Name.Contains("sample", StringComparison.OrdinalIgnoreCase);

    private static MediaFile ToMediaFile(string folder, FileInfo file) =>
        new(Path.GetRelativePath(folder, file.FullName), file.Length, file.LastWriteTimeUtc);
}