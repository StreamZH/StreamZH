// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Library.PostProcessing;

public enum ArchiveKind
{
    Rar,
    SevenZip,
    Zip,
    Split,
}

/// <summary>One archive, possibly in many volumes.</summary>
/// <param name="FirstVolume">Full path of the volume 7-Zip is started with; null when that volume is missing.</param>
/// <param name="Volumes">All files that belong to the set (deleted after a successful extraction).</param>
public sealed record ArchiveSet(string Name, ArchiveKind Kind, string? FirstVolume, IReadOnlyList<string> Volumes);

/// <summary>
/// Groups archive files into sets: rar5 (.part01.rar), old rar (.rar + .r00 …), .7z / .7z.001, .zip (+ .z01 …) and
/// plain splits (.001). Files without a known extension are recognised by their signature (single volume only).
/// </summary>
public static partial class ArchiveSets
{
    private static readonly byte[] RarSignature = "Rar!\u001A\u0007"u8.ToArray();
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>Extensions that are never probed for an archive signature.</summary>
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".iso", ".img",
        ".srt", ".sub", ".idx", ".ass", ".ssa", ".sup", ".nfo", ".sfv", ".txt", ".jpg", ".jpeg", ".png", ".nzb", ".par2",
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".epub", ".pdf", ".mobi", ".azw3", ".cbr", ".cbz",
    };

    public static IReadOnlyList<ArchiveSet> Detect(IEnumerable<string> files)
    {
        var rar5 = new Dictionary<string, List<(int Number, string Path)>>(StringComparer.OrdinalIgnoreCase);
        var oldRar = new Dictionary<string, (string? First, List<string> Volumes)>(StringComparer.OrdinalIgnoreCase);
        var sevenZip = new Dictionary<string, List<(int Number, string Path)>>(StringComparer.OrdinalIgnoreCase);
        var zip = new Dictionary<string, (string? First, List<string> Volumes)>(StringComparer.OrdinalIgnoreCase);
        var split = new Dictionary<string, List<(int Number, string Path)>>(StringComparer.OrdinalIgnoreCase);
        var sets = new List<ArchiveSet>();

        foreach (var path in files)
        {
            var name = System.IO.Path.GetFileName(path);
            var key = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
            Match match;

            if ((match = Rar5Regex().Match(name)).Success)
            {
                Add(rar5, Path(key, match), (int.Parse(match.Groups["n"].ValueSpan), path));
            }
            else if ((match = RarRegex().Match(name)).Success)
            {
                AddFirst(oldRar, Path(key, match), path);
            }
            else if ((match = OldRarVolumeRegex().Match(name)).Success)
            {
                AddVolume(oldRar, Path(key, match), path);
            }
            else if ((match = SevenZipRegex().Match(name)).Success)
            {
                Add(sevenZip, Path(key, match), (match.Groups["n"].Success ? int.Parse(match.Groups["n"].ValueSpan) : 0, path));
            }
            else if ((match = ZipRegex().Match(name)).Success)
            {
                AddFirst(zip, Path(key, match), path);
            }
            else if ((match = ZipVolumeRegex().Match(name)).Success)
            {
                AddVolume(zip, Path(key, match), path);
            }
            else if ((match = SplitRegex().Match(name)).Success)
            {
                Add(split, Path(key, match), (int.Parse(match.Groups["n"].ValueSpan), path));
            }
            else if (!KnownExtensions.Contains(System.IO.Path.GetExtension(name)) && Sniff(path) is { } kind)
            {
                sets.Add(new ArchiveSet(name, kind, path, [path]));
            }
        }

        // rar5: .part1.rar / .part01.rar / .part001.rar is the first volume.
        sets.AddRange(rar5.Select(g => Numbered(g.Key, ArchiveKind.Rar, g.Value, first: 1)));
        sets.AddRange(oldRar.Select(g => new ArchiveSet(System.IO.Path.GetFileName(g.Key), ArchiveKind.Rar, g.Value.First, Sorted(g.Value))));
        sets.AddRange(sevenZip.Select(g => Numbered(g.Key, ArchiveKind.SevenZip, g.Value, first: g.Value.Any(v => v.Number == 0) ? 0 : 1)));
        sets.AddRange(zip.Select(g => new ArchiveSet(System.IO.Path.GetFileName(g.Key), ArchiveKind.Zip, g.Value.First, Sorted(g.Value))));
        sets.AddRange(split.Select(g => Numbered(g.Key, ArchiveKind.Split, g.Value, first: 1)));

        return sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>RAR, 7z or zip by signature; null for anything else.</summary>
    public static ArchiveKind? Sniff(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            Span<byte> head = stackalloc byte[8];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            head = head[..read];
            return head.StartsWith(RarSignature) ? ArchiveKind.Rar
                : head.StartsWith(SevenZipSignature) ? ArchiveKind.SevenZip
                : head.StartsWith(ZipSignature) ? ArchiveKind.Zip
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Path(string directory, Match match) => System.IO.Path.Combine(directory, match.Groups["base"].Value);

    private static void Add(Dictionary<string, List<(int, string)>> groups, string key, (int, string) entry)
    {
        if (!groups.TryGetValue(key, out var list))
        {
            list = [];
            groups[key] = list;
        }

        list.Add(entry);
    }

    private static void AddFirst(Dictionary<string, (string? First, List<string> Volumes)> groups, string key, string path)
    {
        var entry = groups.GetValueOrDefault(key, (null, []));
        entry.Volumes.Add(path);
        groups[key] = (path, entry.Volumes);
    }

    private static void AddVolume(Dictionary<string, (string? First, List<string> Volumes)> groups, string key, string path)
    {
        var entry = groups.GetValueOrDefault(key, (null, []));
        entry.Volumes.Add(path);
        groups[key] = entry;
    }

    private static ArchiveSet Numbered(string key, ArchiveKind kind, List<(int Number, string Path)> volumes, int first)
    {
        var ordered = volumes.OrderBy(v => v.Number).ToList();
        var firstVolume = ordered.FirstOrDefault(v => v.Number == first).Path;
        return new ArchiveSet(System.IO.Path.GetFileName(key), kind, firstVolume, ordered.Select(v => v.Path).ToList());
    }

    private static List<string> Sorted((string? First, List<string> Volumes) group) =>
        group.Volumes.OrderBy(v => v == group.First ? 0 : 1).ThenBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

    [GeneratedRegex(@"^(?<base>.+)\.part(?<n>\d{1,4})\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex Rar5Regex();

    [GeneratedRegex(@"^(?<base>.+)\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex RarRegex();

    // .r00 … .r99, .r000 …, and .s00 … (volumes after .r99)
    [GeneratedRegex(@"^(?<base>.+)\.[rs]\d{2,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex OldRarVolumeRegex();

    [GeneratedRegex(@"^(?<base>.+)\.7z(?:\.(?<n>\d{3}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex SevenZipRegex();

    [GeneratedRegex(@"^(?<base>.+)\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipRegex();

    [GeneratedRegex(@"^(?<base>.+)\.z\d{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipVolumeRegex();

    [GeneratedRegex(@"^(?<base>.+)\.(?<n>\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitRegex();
}
