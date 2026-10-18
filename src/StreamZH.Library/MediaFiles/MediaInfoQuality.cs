// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;

namespace StreamZH.Library.MediaFiles;

/// <summary>
/// Radarr's AugmentQualityFromMediaInfo: when neither the release nor the file name says the resolution, the video's
/// width and height decide it (the source, e.g. WEBDL or Bluray, still comes from the name).
/// </summary>
public static partial class MediaInfoQuality
{
    /// <summary>True when a name states a resolution (720p, 1080p, 2160p, 4K UHD …), so media info must not change it.</summary>
    public static bool NameHasResolution(string? name) => !string.IsNullOrWhiteSpace(name) && ResolutionRegex().IsMatch(name);

    /// <summary>
    /// The quality with the resolution of the video when none of <paramref name="names"/> states one; otherwise (or without
    /// a usable video size, or for qualities without a resolution step such as BR-DISK, DVD or CAM) unchanged.
    /// </summary>
    public static QualityModel Augment(QualityModel quality, MediaInfo? mediaInfo, params string?[] names)
    {
        ArgumentNullException.ThrowIfNull(quality);
        if (mediaInfo?.Video is not { Resolution: > 0 } video || names.Any(NameHasResolution))
        {
            return quality;
        }

        var augmented = WithResolution(quality.Quality, video.Resolution);
        return augmented == quality.Quality ? quality : quality with { Quality = augmented };
    }

    /// <summary>The same source with another resolution (WEBDL-480p + 1080 → WEBDL-1080p); unknown sources count as HDTV like Radarr.</summary>
    public static Quality WithResolution(Quality quality, int resolution)
    {
        ArgumentNullException.ThrowIfNull(quality);
        if (quality.Modifier is QualityModifier.BrDisk or QualityModifier.RawHd or QualityModifier.Regional or QualityModifier.Screener)
        {
            return quality;
        }

        return quality.Source switch
        {
            QualitySource.WebDl => resolution switch
            {
                2160 => Quality.WebDl2160p,
                1080 => Quality.WebDl1080p,
                720 => Quality.WebDl720p,
                _ => Quality.WebDl480p,
            },
            QualitySource.WebRip => resolution switch
            {
                2160 => Quality.WebRip2160p,
                1080 => Quality.WebRip1080p,
                720 => Quality.WebRip720p,
                _ => Quality.WebRip480p,
            },
            QualitySource.Bluray => resolution switch
            {
                2160 => quality.Modifier == QualityModifier.Remux ? Quality.Remux2160p : Quality.Bluray2160p,
                1080 => quality.Modifier == QualityModifier.Remux ? Quality.Remux1080p : Quality.Bluray1080p,
                720 => Quality.Bluray720p,
                576 => Quality.Bluray576p,
                _ => Quality.Bluray480p,
            },
            QualitySource.Tv or QualitySource.Unknown => resolution switch
            {
                2160 => Quality.Hdtv2160p,
                1080 => Quality.Hdtv1080p,
                720 => Quality.Hdtv720p,
                _ => Quality.Sdtv,
            },
            _ => quality,
        };
    }

    // The resolutions QualityParser reads from names (plus UHD and [4K]).
    [GeneratedRegex(@"\b(?:360p|480p|480i|640x480|848x480|540p|576p|576i|720p|1280x720|960p|1080p|1920x1080|1440p|FHD|1080i|4kto1080p|2160p|3840x2160|4k[-_. ](?:UHD|HEVC|BD|H\.?26[56])|(?:UHD|HEVC|BD|H\.?26[56])[-_. ]4k|UHD)\b|\[4K\]", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();
}
