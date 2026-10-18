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
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".divx", ".xvid", ".flv", ".vob", ".iso",
    };

    private static readonly IReadOnlySet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sample", "samples", "extras", "featurettes", "behind the scenes", "deleted scenes", "interviews", "trailers",
        "@eadir", ".@__thumb", "$recycle.bin", "system volume information", "lost+found",
    };

    private const long SampleMaxBytes = 300L * 1024 * 1024;

    /// <summary>Folder names of unpacked discs: Blu-ray (BDMV) and DVD (VIDEO_TS).</summary>
    private static readonly string[] DiscFolders = ["BDMV", "VIDEO_TS"];

    /// <summary>
    /// The main movie file: an unpacked Blu-ray/DVD folder counts as one file, otherwise the largest
    /// video file that is not a sample.
    /// </summary>
    public static MediaFile? ScanMovie(string folder)
    {
        if (FindDisc(folder) is { } disc)
        {
            return disc;
        }

        var best = VideoFiles(folder).MaxBy(f => f.Length);
        return best is null ? null : ToMediaFile(folder, best);
    }

    /// <summary>Number of media files and their size for the import list (a disc folder counts as one).</summary>
    public static (int Files, long Size) SummarizeMovieFolder(string folder)
    {
        if (FindDisc(folder) is { } disc)
        {
            return (1, disc.Size);
        }

        var files = VideoFiles(folder).ToList();
        return (files.Count, files.Sum(f => f.Length));
    }

    private static MediaFile? FindDisc(string folder)
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
        {
            return null;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var disc = DiscFolders.SelectMany(name => root.EnumerateDirectories(name, options)).FirstOrDefault();
        if (disc is null)
        {
            return null;
        }

        var files = disc.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        if (files.Count == 0)
        {
            return null;
        }

        return new MediaFile(
            Path.GetRelativePath(folder, disc.FullName),
            files.Sum(f => f.Length),
            files.Max(f => f.LastWriteTimeUtc));
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

    /// <summary>
    /// Like <see cref="ScanSeries(string)"/>, plus files named by air date ("Show - 2026-10-18 - Title.mkv", daily series) and,
    /// for anime series, by absolute number ("Show - 012.mkv"), mapped to the episodes of the library (TMDB numbering).
    /// </summary>
    public static IReadOnlyDictionary<(int Season, int Episode), MediaFile> ScanSeries(string folder, Series series, IReadOnlyList<Episode> library)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(library);
        var result = new Dictionary<(int, int), MediaFile>();

        void Add((int, int) key, MediaFile mediaFile)
        {
            if (!result.TryGetValue(key, out var existing) || existing.Size < mediaFile.Size)
            {
                result[key] = mediaFile;
            }
        }

        foreach (var file in VideoFiles(folder))
        {
            var mediaFile = ToMediaFile(folder, file);
            if (EpisodeFileParser.Parse(file.Name) is { } simple)
            {
                foreach (var episode in simple.Episodes)
                {
                    Add((simple.Season, episode), mediaFile);
                }

                continue;
            }

            var parsed = Parsing.Releases.ReleaseParser.ParseEpisode(Path.GetFileNameWithoutExtension(file.Name));
            if (parsed is null || (parsed.AirDate is null && (series.SeriesType != SeriesType.Anime || parsed.AbsoluteEpisodeNumbers.Count == 0)))
            {
                continue;
            }

            foreach (var episode in Search.ReleaseMatching.MapEpisodes(parsed, library, series, sceneSource: false).Episodes)
            {
                Add((episode.SeasonNumber, episode.EpisodeNumber), mediaFile);
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