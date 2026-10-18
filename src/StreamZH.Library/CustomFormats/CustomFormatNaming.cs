// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.Import;

namespace StreamZH.Library.CustomFormats;

/// <summary>Adds the custom formats a file matches to its naming info, for the {Custom Formats} token (Radarr's file custom formats).</summary>
public static class CustomFormatNaming
{
    /// <summary>
    /// The file's formats from what the naming knows (release name, quality, languages, group, edition) plus its name,
    /// size and the movie or series year. Unchanged when there are no formats.
    /// </summary>
    public static NamingFileInfo WithCustomFormats(
        this NamingFileInfo info,
        IReadOnlyList<CustomFormat> formats,
        MediaType mediaType,
        Language originalLanguage,
        int year,
        string fileName,
        long sizeBytes,
        CustomFormatReleaseType releaseType = CustomFormatReleaseType.Unknown)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(formats);
        if (formats.Count == 0)
        {
            return info;
        }

        var input = CustomFormatInput.ForFile(
            mediaType, fileName, info.SceneName, info.Quality, info.Languages, originalLanguage ?? Language.Unknown,
            info.ReleaseGroup, info.Edition, year, sizeBytes, releaseType);
        return info with { CustomFormats = CustomFormatCalculator.Matching(formats, input) };
    }

    /// <summary>Single or multi-episode file (Sonarr's release type of a file).</summary>
    public static CustomFormatReleaseType EpisodeFileType(int episodeCount) =>
        episodeCount > 1 ? CustomFormatReleaseType.MultiEpisode : CustomFormatReleaseType.SingleEpisode;

    /// <summary>Size of a file, 0 when it cannot be read.</summary>
    public static long SizeOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
