// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using StreamZH.Core.Quality;

namespace StreamZH.Parsing.Internal;

/// <summary>Known media file extensions and the fallback quality each one implies.</summary>
internal static partial class MediaFileExtensions
{
    private static readonly Dictionary<string, Quality> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Unknown
        { ".webm", Quality.Unknown },

        // SDTV
        { ".m4v", Quality.Sdtv },
        { ".3gp", Quality.Sdtv },
        { ".nsv", Quality.Sdtv },
        { ".ty", Quality.Sdtv },
        { ".strm", Quality.Sdtv },
        { ".rm", Quality.Sdtv },
        { ".rmvb", Quality.Sdtv },
        { ".m3u", Quality.Sdtv },
        { ".ifo", Quality.Sdtv },
        { ".mov", Quality.Sdtv },
        { ".qt", Quality.Sdtv },
        { ".divx", Quality.Sdtv },
        { ".xvid", Quality.Sdtv },
        { ".bivx", Quality.Sdtv },
        { ".nrg", Quality.Sdtv },
        { ".pva", Quality.Sdtv },
        { ".wmv", Quality.Sdtv },
        { ".asf", Quality.Sdtv },
        { ".asx", Quality.Sdtv },
        { ".ogm", Quality.Sdtv },
        { ".ogv", Quality.Sdtv },
        { ".m2v", Quality.Sdtv },
        { ".avi", Quality.Sdtv },
        { ".bin", Quality.Sdtv },
        { ".dat", Quality.Sdtv },
        { ".dvr-ms", Quality.Sdtv },
        { ".mpg", Quality.Sdtv },
        { ".mpeg", Quality.Sdtv },
        { ".mp4", Quality.Sdtv },
        { ".avc", Quality.Sdtv },
        { ".vp3", Quality.Sdtv },
        { ".svq3", Quality.Sdtv },
        { ".nuv", Quality.Sdtv },
        { ".viv", Quality.Sdtv },
        { ".dv", Quality.Sdtv },
        { ".fli", Quality.Sdtv },
        { ".flv", Quality.Sdtv },
        { ".wpl", Quality.Sdtv },

        // DVD
        { ".img", Quality.Dvd },
        { ".iso", Quality.Dvd },
        { ".vob", Quality.Dvd },

        // HD
        { ".mkv", Quality.WebDl720p },
        { ".mk3d", Quality.WebDl720p },
        { ".ts", Quality.Sdtv },
        { ".wtv", Quality.Sdtv },

        // Bluray
        { ".m2ts", Quality.Bluray720p },
    };

    private static readonly HashSet<string> UsenetExtensions = new(StringComparer.OrdinalIgnoreCase) { ".par2", ".nzb" };

    /// <summary>Quality implied by a file extension (with leading dot), Unknown if the extension is not a media extension.</summary>
    public static Quality GetQualityForExtension(string extension) =>
        FileExtensions.TryGetValue(extension, out var quality) ? quality : Quality.Unknown;

    /// <summary>Extension after the last dot, including the dot; empty if there is none.</summary>
    public static string GetPathExtension(string path)
    {
        var idx = path.LastIndexOf('.');
        return idx == -1 || idx == path.Length - 1 ? string.Empty : path[idx..];
    }

    /// <summary>Removes a trailing media or usenet file extension (".mkv", ".nzb", ...); other "extensions" stay.</summary>
    public static string RemoveFileExtension(string title) =>
        FileExtensionRegex().Replace(title, m =>
            FileExtensions.ContainsKey(m.Value) || UsenetExtensions.Contains(m.Value) ? string.Empty : m.Value);

    [GeneratedRegex(@"\.[a-z0-9]{2,4}$", RegexOptions.IgnoreCase)]
    private static partial Regex FileExtensionRegex();
}
