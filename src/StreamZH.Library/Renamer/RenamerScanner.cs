// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Library.Renamer;

/// <summary>
/// Which folders the renamer may work in: the folders the folder browser shows. Hidden and system folders (names starting
/// with a dot, $Recycle.Bin, @eaDir, proc …), drive roots, operating system folders and the StreamZH data folder are refused.
/// </summary>
public static class RenamerPaths
{
    /// <summary>The names the folder browser hides (Settings → root folders), at any depth.</summary>
    public static readonly IReadOnlySet<string> HiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "$recycle.bin", "system volume information", "@eadir", "#recycle", "lost+found", "proc", "sys", "dev", "run",
    };

    private static readonly string[] UnixSystemFolders = ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/run", "/sbin", "/sys", "/usr", "/System", "/Library", "/private"];

    /// <summary>The normalized folder, or the reason it cannot be used.</summary>
    public static (string? Folder, string? Error) Check(string? path, string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, "Choose a folder.");
        }

        var trimmed = path.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            return (null, "Enter a full path.");
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "This is not a valid path.");
        }

        if (Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase)
            || full.Length <= 3 && full.EndsWith(':'))
        {
            return (null, "Choose a folder, not a whole drive.");
        }

        if (IsHidden(full))
        {
            return (null, "Hidden and system folders cannot be renamed in.");
        }

        if (IsSystemFolder(full))
        {
            return (null, "Folders of the operating system cannot be renamed in.");
        }

        if (dataDirectory is { Length: > 0 } && IsInside(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory))))
        {
            return (null, "StreamZH's own data folder cannot be renamed in.");
        }

        // 0.12 (V-02): nor a folder around it (a rename could move something into it).
        if (dataDirectory is { Length: > 0 } && StreamZH.Core.DataFolderGuard.Problem(dataDirectory, full) is { } dataProblem)
        {
            return (null, dataProblem);
        }

        if (!Directory.Exists(full))
        {
            return (null, "This folder does not exist.");
        }

        return (full, null);
    }

    /// <summary>A path segment is hidden (".git", "@eaDir") or a system name.</summary>
    public static bool IsHidden(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var segments = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s.StartsWith('.') || HiddenNames.Contains(s));
    }

    private static bool IsSystemFolder(string full)
    {
        if (OperatingSystem.IsWindows())
        {
            var system = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            };
            return system.Where(s => s.Length > 0).Any(s => IsInside(full, Path.TrimEndingDirectorySeparator(s)));
        }

        return UnixSystemFolders.Any(s => IsInside(full, s));
    }

    public static bool IsInside(string path, string folder)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(path, folder, comparison)
            || path.StartsWith(folder + Path.DirectorySeparatorChar, comparison)
            || path.StartsWith(folder + Path.AltDirectorySeparatorChar, comparison);
    }

    /// <summary>"Season 1/a.mkv" with forward slashes.</summary>
    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>Lists the files (and folders) of the source that pass the filter, sorted like a file manager.</summary>
public static partial class RenamerScanner
{
    public const int MaxEntries = 10_000;

    public static readonly IReadOnlySet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup", ".smi", ".txt",
    };

    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".avif",
    };

    /// <summary>Null when the filter can be used; otherwise the field (camelCase) and the message.</summary>
    public static (string Field, string Message)? Check(RenamerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (NamePattern.TryParse(filter.Include).Error is { } include)
        {
            return ("include", include);
        }

        if (NamePattern.TryParse(filter.Exclude).Error is { } exclude)
        {
            return ("exclude", exclude);
        }

        if (filter.MinSizeMb is < 0 || filter.MaxSizeMb is < 0)
        {
            return ("minSizeMb", "Sizes cannot be negative.");
        }

        if (filter.MinSizeMb is { } min && filter.MaxSizeMb is { } max && max < min)
        {
            return ("maxSizeMb", "The maximum is smaller than the minimum.");
        }

        return filter.Extensions == ExtensionPreset.Custom && Extensions(filter).Count == 0 ? ("customExtensions", "Enter at least one extension, e.g. mkv, srt.") : null;
    }

    /// <summary>The extensions the filter lets through (with dot, lower case); empty = all.</summary>
    public static IReadOnlySet<string> Extensions(RenamerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Extensions switch
        {
            ExtensionPreset.Video => DiskScanner.VideoExtensions,
            ExtensionPreset.Subtitles => SubtitleExtensions,
            ExtensionPreset.VideoAndSubtitles => new HashSet<string>(DiskScanner.VideoExtensions.Concat(SubtitleExtensions), StringComparer.OrdinalIgnoreCase),
            ExtensionPreset.Images => ImageExtensions,
            ExtensionPreset.Custom => filter.CustomExtensions
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => "." + e.TrimStart('*').TrimStart('.').ToLowerInvariant())
                .Where(e => e.Length > 1)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(),
        };
    }

    /// <summary>Everything under <paramref name="root"/> that matches, and how many entries were looked at.</summary>
    public static (IReadOnlyList<ScanEntry> Entries, int Total, bool Truncated) Scan(string root, RenamerSource source, RenamerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filter);
        var include = NamePattern.TryParse(filter.Include).Pattern ?? throw new ArgumentException("Invalid include pattern.", nameof(filter));
        var exclude = NamePattern.TryParse(filter.Exclude).Pattern ?? throw new ArgumentException("Invalid exclude pattern.", nameof(filter));
        var extensions = Extensions(filter);
        var files = source.Target != RenameTarget.Folders;
        var folders = source.Target != RenameTarget.Files;
        var maxDepth = source.Subfolders ? Math.Clamp(source.Depth, 1, RenamerSource.MaxDepth) : 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden, RecurseSubdirectories = false };
        var minBytes = filter.MinSizeMb is { } min ? (long)(min * 1024 * 1024) : (long?)null;
        var maxBytes = filter.MaxSizeMb is { } max ? (long)(max * 1024 * 1024) : (long?)null;

        var result = new List<ScanEntry>();
        var total = 0;
        var truncated = false;

        void Walk(DirectoryInfo directory, int depth)
        {
            var relativeFolder = depth == 0 ? string.Empty : RenamerPaths.Relative(root, directory.FullName);
            var subfolders = new List<DirectoryInfo>();
            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
                {
                    if (entry.Name.StartsWith('.') || RenamerPaths.HiddenNames.Contains(entry.Name))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo sub)
                    {
                        if (filter.HideSamples && IsSample(sub.Name))
                        {
                            continue;
                        }

                        subfolders.Add(sub);
                        // Folders in every folder that is looked at (with subfolders off: the folders in the source).
                        if (folders)
                        {
                            total++;
                            if (Passes(sub.Name) && !Add(new ScanEntry(RenamerPaths.Relative(root, sub.FullName), relativeFolder, sub.Name, true, 0, sub.LastWriteTimeUtc)))
                            {
                                return;
                            }
                        }

                        continue;
                    }

                    if (!files || entry is not FileInfo file)
                    {
                        continue;
                    }

                    total++;
                    if (extensions.Count > 0 && !extensions.Contains(file.Extension))
                    {
                        continue;
                    }

                    if (filter.HideSamples && IsSample(Path.GetFileNameWithoutExtension(file.Name)))
                    {
                        continue;
                    }

                    if (minBytes is { } low && file.Length < low || maxBytes is { } high && file.Length > high || !Passes(file.Name))
                    {
                        continue;
                    }

                    if (!Add(new ScanEntry(RenamerPaths.Relative(root, file.FullName), relativeFolder, file.Name, false, file.Length, file.LastWriteTimeUtc)))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return;
            }

            if (depth >= maxDepth)
            {
                return;
            }

            foreach (var sub in subfolders)
            {
                if (truncated)
                {
                    return;
                }

                Walk(sub, depth + 1);
            }
        }

        bool Passes(string name) => (include.IsEmpty || include.Matches(name)) && (exclude.IsEmpty || !exclude.Matches(name));

        bool Add(ScanEntry entry)
        {
            if (result.Count >= MaxEntries)
            {
                truncated = true;
                return false;
            }

            result.Add(entry);
            return true;
        }

        Walk(new DirectoryInfo(root), 0);
        result.Sort(Compare);
        return (result, total, truncated);
    }

    /// <summary>Folder by folder (natural order), in each folder the folders first, then the files.</summary>
    public static int Compare(ScanEntry a, ScanEntry b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var byFolder = NaturalComparer.Instance.Compare(a.Folder, b.Folder);
        if (byFolder != 0)
        {
            return byFolder;
        }

        return a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : NaturalComparer.Instance.Compare(a.Name, b.Name);
    }

    public static bool IsSample(string name) => SampleRegex().IsMatch(name);

    [GeneratedRegex(@"(?:^|[ ._\-\[(])samples?(?:[ ._\-\])]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SampleRegex();
}
