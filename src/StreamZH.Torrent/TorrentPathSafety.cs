// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using MonoTorrent.Client;
using MtTorrent = MonoTorrent.Torrent;

namespace StreamZH.Torrent;

/// <summary>
/// File names inside a torrent come from strangers (uploaders, the swarm behind a magnet link). MonoTorrent 3.0.2 writes a
/// single-file torrent whose name is an absolute path or starts with "../" outside the save folder, so every torrent is
/// checked before it is added, and the resulting paths again afterwards.
/// </summary>
public static class TorrentPathSafety
{
    private static readonly HashSet<string> WindowsDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Why the torrent's names are unsafe, or null when they are fine.</summary>
    public static string? Problem(MtTorrent torrent)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        if (NameProblem(torrent.Name) is { } nameProblem)
        {
            return $"its name {nameProblem}.";
        }

        // 0.12 (SV2 N-03): the torrent's name becomes a folder or file in the save folder; never a hidden one (".ssh").
        if (torrent.Name is { Length: > 0 } name && name.TrimStart(' ').StartsWith('.'))
        {
            return "its name starts with a dot (a hidden file or folder).";
        }

        foreach (var file in torrent.Files)
        {
            if (NameProblem(file.Path) is { } fileProblem)
            {
                return $"a file name {fileProblem}.";
            }
        }

        return null;
    }

    /// <summary>A path that is not inside <paramref name="folder"/>, or null when all files of the manager stay inside.</summary>
    public static string? OutsideFolder(string folder, TorrentManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (!manager.HasMetadata)
        {
            return null;
        }

        foreach (var file in manager.Files)
        {
            foreach (var path in new[] { file.FullPath, file.DownloadCompleteFullPath, file.DownloadIncompleteFullPath })
            {
                if (!IsInside(folder, path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    public static bool IsInside(string folder, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(root, comparison);
    }

    /// <summary>Checks one name or relative path from the torrent (null = fine).</summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (name.Any(c => c < 32 || c == 127))
        {
            return "contains control characters";
        }

        if (name[0] is '/' or '\\' || Path.IsPathRooted(name) || name.Contains(':', StringComparison.Ordinal))
        {
            return "is an absolute path";
        }

        foreach (var segment in name.Split('/', '\\'))
        {
            if (segment is ".." or ".")
            {
                return "goes up a folder (..)";
            }

            var trimmed = segment.TrimEnd('.', ' ');
            var stem = trimmed.Split('.')[0];
            if (WindowsDevices.Contains(stem))
            {
                return $"uses the reserved name {stem}";
            }
        }

        return null;
    }
}
