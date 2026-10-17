// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Library;

public static class FolderNames
{
    private static readonly char[] Invalid = ['<', '>', '"', '/', '\\', '|', '?', '*'];

    /// <summary>"Title (Year)", safe on Windows, Linux and macOS. Colons become " - " like Radarr and Sonarr.</summary>
    public static string For(string title, int year)
    {
        var name = title.Replace(": ", " - ", StringComparison.Ordinal).Replace(':', '-');
        name = new string([.. name.Where(c => !Invalid.Contains(c) && !char.IsControl(c))]).Trim();
        if (name.Length == 0)
        {
            name = "Unknown";
        }

        // Windows does not allow a trailing dot or space at the end of a folder name.
        return year > 0 ? $"{name} ({year})" : name.TrimEnd('.', ' ');
    }
}