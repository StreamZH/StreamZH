// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;
using StreamZH.Core.Quality;
using StreamZH.Parsing.Internal;

namespace StreamZH.Parsing;

/// <summary>
/// Reads quality, revision and HDR format from a release or file name.
/// Port of Radarr's QualityParser (with Sonarr's extra WEB and version patterns); works on the name only.
/// </summary>
public static partial class QualityParser
{
    private enum Resolution
    {
        Unknown = 0,
        R360p = 360,
        R480p = 480,
        R540p = 540,
        R576p = 576,
        R720p = 720,
        R1080p = 1080,
        R2160p = 2160,
    }

    public static QualityModel ParseQuality(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return QualityModel.Unknown;
        }

        name = name.Trim();

        var (quality, revision) = ParseQualityName(name);

        // Based on the file extension
        if (quality == Quality.Unknown)
        {
            quality = MediaFileExtensions.GetQualityForExtension(Path.GetExtension(name));
        }

        return new QualityModel(quality, revision);
    }

    /// <summary>HDR format named in the release: Dolby Vision, HDR10+, HDR10, HDR or HLG.</summary>
    public static HdrFormat ParseHdr(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return HdrFormat.None;
        }

        var normalizedName = name.Replace('_', ' ');
        var dolbyVision = DolbyVisionRegex().IsMatch(normalizedName);
        var hdr10Plus = Hdr10PlusRegex().IsMatch(normalizedName);
        var hdr10 = Hdr10Regex().IsMatch(normalizedName);
        var hdr = HdrRegex().IsMatch(normalizedName);

        if (dolbyVision)
        {
            // DV releases that also name HDR10/HDR10+ (or just "HDR") carry an HDR10 fallback layer.
            return hdr10Plus || hdr10 || hdr ? HdrFormat.DolbyVisionHdr10 : HdrFormat.DolbyVision;
        }

        if (hdr10Plus)
        {
            return HdrFormat.Hdr10Plus;
        }

        if (hdr10)
        {
            return HdrFormat.Hdr10;
        }

        if (HlgRegex().IsMatch(normalizedName))
        {
            return HdrFormat.Hlg;
        }

        return hdr ? HdrFormat.Hdr : HdrFormat.None;
    }

    private static (Quality Quality, Revision Revision) ParseQualityName(string name)
    {
        var normalizedName = name.Replace('_', ' ').Trim();
        var revision = ParseRevision(name, normalizedName);

        return (ParseQualityOnly(name, normalizedName), revision);
    }

    private static Quality ParseQualityOnly(string name, string normalizedName)
    {
        var sourceMatch = SourceRegex().Matches(normalizedName).LastOrDefault();
        var resolution = ParseResolution(normalizedName);
        var codecMatch = CodecRegex().Match(normalizedName);
        var remuxMatch = RemuxRegex().IsMatch(normalizedName) || GermanRemuxRegex().IsMatch(normalizedName);
        var brDiskMatch = BrDiskRegex().IsMatch(normalizedName);

        if (RawHdRegex().IsMatch(normalizedName) && !brDiskMatch)
        {
            return Quality.RawHd;
        }

        if (sourceMatch is { Success: true })
        {
            if (sourceMatch.Groups["bluray"].Success)
            {
                if (brDiskMatch)
                {
                    return Quality.BrDisk;
                }

                if (codecMatch.Groups["xvid"].Success || codecMatch.Groups["divx"].Success)
                {
                    return Quality.Bluray480p;
                }

                return resolution switch
                {
                    Resolution.R2160p => remuxMatch ? Quality.Remux2160p : Quality.Bluray2160p,
                    Resolution.R1080p => remuxMatch ? Quality.Remux1080p : Quality.Bluray1080p,
                    Resolution.R720p => Quality.Bluray720p,
                    Resolution.R576p => Quality.Bluray576p,
                    Resolution.R360p or Resolution.R480p or Resolution.R540p => Quality.Bluray480p,

                    // Treat a remux without a resolution as 1080p, not 720p.
                    _ => remuxMatch ? Quality.Remux1080p : Quality.Bluray720p,
                };
            }

            if (sourceMatch.Groups["webdl"].Success)
            {
                return resolution switch
                {
                    Resolution.R2160p => Quality.WebDl2160p,
                    Resolution.R1080p => Quality.WebDl1080p,
                    Resolution.R720p => Quality.WebDl720p,
                    _ when name.Contains("[WEBDL]", StringComparison.Ordinal) => Quality.WebDl720p,
                    _ => Quality.WebDl480p,
                };
            }

            if (sourceMatch.Groups["webrip"].Success)
            {
                return resolution switch
                {
                    Resolution.R2160p => Quality.WebRip2160p,
                    Resolution.R1080p => Quality.WebRip1080p,
                    Resolution.R720p => Quality.WebRip720p,
                    _ => Quality.WebRip480p,
                };
            }

            if (sourceMatch.Groups["scr"].Success)
            {
                return Quality.DvdScr;
            }

            if (sourceMatch.Groups["cam"].Success)
            {
                return Quality.Cam;
            }

            if (sourceMatch.Groups["ts"].Success)
            {
                return Quality.Telesync;
            }

            if (sourceMatch.Groups["tc"].Success)
            {
                return Quality.Telecine;
            }

            if (sourceMatch.Groups["wp"].Success)
            {
                return Quality.Workprint;
            }

            if (sourceMatch.Groups["regional"].Success)
            {
                return Quality.Regional;
            }

            if (sourceMatch.Groups["hdtv"].Success)
            {
                if (Mpeg2Regex().IsMatch(normalizedName))
                {
                    return Quality.RawHd;
                }

                return resolution switch
                {
                    Resolution.R2160p => Quality.Hdtv2160p,
                    Resolution.R1080p => Quality.Hdtv1080p,
                    Resolution.R720p => Quality.Hdtv720p,
                    _ when name.Contains("[HDTV]", StringComparison.Ordinal) => Quality.Hdtv720p,
                    _ => Quality.Sdtv,
                };
            }

            if (sourceMatch.Groups["bdrip"].Success || sourceMatch.Groups["brrip"].Success)
            {
                return resolution switch
                {
                    Resolution.R720p => Quality.Bluray720p,
                    Resolution.R1080p => Quality.Bluray1080p,
                    Resolution.R2160p => Quality.Bluray2160p,
                    Resolution.R576p => Quality.Bluray576p,
                    _ => Quality.Bluray480p,
                };
            }

            if (sourceMatch.Groups["dvdr"].Success)
            {
                return Quality.DvdR;
            }

            if (sourceMatch.Groups["dvd"].Success)
            {
                return Quality.Dvd;
            }

            if (sourceMatch.Groups["pdtv"].Success ||
                sourceMatch.Groups["sdtv"].Success ||
                sourceMatch.Groups["dsr"].Success ||
                sourceMatch.Groups["tvrip"].Success)
            {
                if (resolution == Resolution.R1080p || normalizedName.Contains("1080p", StringComparison.OrdinalIgnoreCase))
                {
                    return Quality.Hdtv1080p;
                }

                if (resolution == Resolution.R720p || normalizedName.Contains("720p", StringComparison.OrdinalIgnoreCase))
                {
                    return Quality.Hdtv720p;
                }

                return HighDefPdtvRegex().IsMatch(normalizedName) ? Quality.Hdtv720p : Quality.Sdtv;
            }
        }

        if (sourceMatch is null && remuxMatch && resolution != Resolution.Unknown)
        {
            switch (resolution)
            {
                case Resolution.R480p:
                    return Quality.Bluray480p;
                case Resolution.R720p:
                    return Quality.Bluray720p;
                case Resolution.R2160p:
                    return Quality.Remux2160p;
                case Resolution.R1080p:
                    return Quality.Remux1080p;
            }
        }

        // Anime Bluray matching
        if (AnimeBlurayRegex().IsMatch(normalizedName))
        {
            if (resolution is Resolution.R360p or Resolution.R480p or Resolution.R540p or Resolution.R576p ||
                normalizedName.Contains("480p", StringComparison.OrdinalIgnoreCase))
            {
                return Quality.Dvd;
            }

            if (resolution == Resolution.R1080p || normalizedName.Contains("1080p", StringComparison.OrdinalIgnoreCase))
            {
                return remuxMatch ? Quality.Remux1080p : Quality.Bluray1080p;
            }

            if (resolution == Resolution.R2160p || normalizedName.Contains("2160p", StringComparison.OrdinalIgnoreCase))
            {
                return remuxMatch ? Quality.Remux2160p : Quality.Bluray2160p;
            }

            // Treat a remux without a resolution as 1080p, not 720p.
            return remuxMatch && resolution != Resolution.R720p ? Quality.Remux1080p : Quality.Bluray720p;
        }

        if (AnimeWebDlRegex().IsMatch(normalizedName))
        {
            if (resolution is Resolution.R360p or Resolution.R480p or Resolution.R540p or Resolution.R576p ||
                normalizedName.Contains("480p", StringComparison.OrdinalIgnoreCase))
            {
                return Quality.WebDl480p;
            }

            if (resolution == Resolution.R1080p || normalizedName.Contains("1080p", StringComparison.OrdinalIgnoreCase))
            {
                return Quality.WebDl1080p;
            }

            if (resolution == Resolution.R2160p || normalizedName.Contains("2160p", StringComparison.OrdinalIgnoreCase))
            {
                return Quality.WebDl2160p;
            }

            return Quality.WebDl720p;
        }

        if (resolution != Resolution.Unknown)
        {
            var source = QualitySource.Unknown;
            var modifier = QualityModifier.None;

            if (remuxMatch)
            {
                source = QualitySource.Bluray;
                modifier = QualityModifier.Remux;
            }
            else
            {
                var extensionQuality = MediaFileExtensions.GetQualityForExtension(MediaFileExtensions.GetPathExtension(name));
                if (extensionQuality != Quality.Unknown)
                {
                    source = extensionQuality.Source;
                }
            }

            switch (resolution)
            {
                case Resolution.R2160p:
                    return source == QualitySource.Unknown ? Quality.Hdtv2160p : Quality.Find(source, 2160, modifier);
                case Resolution.R1080p:
                    return source == QualitySource.Unknown ? Quality.Hdtv1080p : Quality.Find(source, 1080, modifier);
                case Resolution.R720p:
                    return source == QualitySource.Unknown ? Quality.Hdtv720p : Quality.Find(source, 720, modifier);
                default:
                    return source == QualitySource.Unknown ? Quality.Sdtv : Quality.Find(source, 480, modifier);
            }
        }

        if (codecMatch.Groups["x264"].Success)
        {
            return Quality.Sdtv;
        }

        if (normalizedName.Contains("848x480", StringComparison.OrdinalIgnoreCase))
        {
            if (normalizedName.Contains("dvd", StringComparison.Ordinal))
            {
                return Quality.Dvd;
            }

            return normalizedName.Contains("bluray", StringComparison.OrdinalIgnoreCase) ? Quality.Bluray480p : Quality.Sdtv;
        }

        if (normalizedName.Contains("1280x720", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedName.Contains("bluray", StringComparison.OrdinalIgnoreCase) ? Quality.Bluray720p : Quality.Hdtv720p;
        }

        if (normalizedName.Contains("1920x1080", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedName.Contains("bluray", StringComparison.OrdinalIgnoreCase) ? Quality.Bluray1080p : Quality.Hdtv1080p;
        }

        if (normalizedName.Contains("bluray720p", StringComparison.OrdinalIgnoreCase))
        {
            return Quality.Bluray720p;
        }

        if (normalizedName.Contains("bluray1080p", StringComparison.OrdinalIgnoreCase))
        {
            return Quality.Bluray1080p;
        }

        if (normalizedName.Contains("bluray2160p", StringComparison.OrdinalIgnoreCase))
        {
            return Quality.Bluray2160p;
        }

        var otherSourceMatch = OtherSourceRegex().Match(normalizedName);
        if (otherSourceMatch.Success)
        {
            if (otherSourceMatch.Groups["sdtv"].Success)
            {
                return Quality.Sdtv;
            }

            if (otherSourceMatch.Groups["hdtv"].Success)
            {
                return Quality.Hdtv720p;
            }
        }

        return Quality.Unknown;
    }

    private static Resolution ParseResolution(string name)
    {
        var match = ResolutionRegex().Match(name);
        var alternativeMatch = AlternativeResolutionRegex().Match(name);

        if (!match.Success && !alternativeMatch.Success)
        {
            return Resolution.Unknown;
        }

        if (match.Groups["R360p"].Success)
        {
            return Resolution.R360p;
        }

        if (match.Groups["R480p"].Success)
        {
            return Resolution.R480p;
        }

        if (match.Groups["R540p"].Success)
        {
            return Resolution.R540p;
        }

        if (match.Groups["R576p"].Success)
        {
            return Resolution.R576p;
        }

        if (match.Groups["R720p"].Success)
        {
            return Resolution.R720p;
        }

        if (match.Groups["R1080p"].Success)
        {
            return Resolution.R1080p;
        }

        if (match.Groups["R2160p"].Success || alternativeMatch.Groups["R2160p"].Success)
        {
            return Resolution.R2160p;
        }

        return Resolution.Unknown;
    }

    private static Revision ParseRevision(string name, string normalizedName)
    {
        var version = 1;
        var real = 0;
        var isRepack = false;

        var versionMatch = VersionRegex().Match(normalizedName);
        var parsedVersion = versionMatch.Success
            ? int.Parse(versionMatch.Groups["version"].Value, CultureInfo.InvariantCulture)
            : (int?)null;

        if (parsedVersion is not null)
        {
            version = parsedVersion.Value;
        }

        if (ProperRegex().IsMatch(normalizedName))
        {
            version = parsedVersion + 1 ?? 2;
        }

        if (RepackRegex().IsMatch(normalizedName))
        {
            version = parsedVersion + 1 ?? 2;
            isRepack = true;
        }

        // REAL is only counted in upper case; "Real" is usually part of the title.
        var realMatches = RealRegex().Matches(name);
        if (realMatches.Count > 0)
        {
            real = realMatches.Count;
        }

        return new Revision(version, real, isRepack);
    }

    [GeneratedRegex("""
        \b(?:
        (?<bluray>M?Blu[-_. ]?Ray|HD[-_. ]?DVD|BD(?!$)|UHD2?BD|BDISO|BDMux|BD25|BD50|BR[-_. ]?DISK)|
        (?<webdl>WEB[-_. ]?DL(?:mux)?|AmazonHD|AmazonSD|iTunesHD|MaxdomeHD|NetflixU?HD|WebHD|HBOMaxHD|DisneyHD|[. ]WEB[. ](?:[xh][ .]?26[456]|AVC|HEVC|EAC3|DDP?[ .]?5[. ]1)|[. ](?-i:WEB)$|(?:\d{3,4}0p)[-. ](?:Hybrid[-_. ]?)?WEB[-. ]|[-. ]WEB[-. ]\d{3,4}0p|\b\s\/\sWEB\s\/\s\b|(?:AMZN|ATVP|NF|DP)[. -]WEB[. -](?!Rip))|
        (?<webrip>WebRip|Web-Rip|WEBMux)|
        (?<hdtv>HDTV)|
        (?<bdrip>BDRip|BDLight|HD[-_. ]?DVDRip|UHDBDRip)|
        (?<brrip>BRRip)|
        (?<dvdr>\d?x?M?DVD-?[R59])|
        (?<dvd>DVD(?!-R)|DVDRip|xvidvd)|
        (?<dsr>WS[-_. ]DSR|DSR)|
        (?<regional>R[0-9]{1}|REGIONAL)|
        (?<scr>SCR|SCREENER|DVDSCR|DVDSCREENER)|
        (?<ts>TS[-_. ]|TELESYNCH?|HD-TS|HDTS|PDVD|TSRip|HDTSRip)|
        (?<tc>TC|TELECINE|HD-TC|HDTC)|
        (?<cam>CAMRIP|(?:NEW)?CAM|HD-?CAM(?:Rip)?|HQCAM)|
        (?<wp>WORKPRINT|WP)|
        (?<pdtv>PDTV)|
        (?<sdtv>SDTV)|
        (?<tvrip>TVRip)
        )(?:\b|$|[ .])
        """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex SourceRegex();

    [GeneratedRegex(@"\b(?<rawhd>RawHD|Raw[-_. ]HD)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RawHdRegex();

    [GeneratedRegex(@"\b(?<mpeg2>MPEG[-_. ]?2)\b")]
    private static partial Regex Mpeg2Regex();

    [GeneratedRegex(@"^(?!.*\b((?<!HD[._ -]|HD)DVD|BDRip|720p|MKV|XviD|WMV|d3g|(BD)?REMUX|^(?=.*1080p)(?=.*HEVC)|[xh][-_. ]?26[45]|German.*[DM]L|((?<=\d{4}).*German.*([DM]L)?)(?=.*\b(AVC|HEVC|VC[-_. ]?1|MVC|MPEG[-_. ]?2)\b))\b)(((?=.*\b(Blu[-_. ]?ray|BD|HD[-_. ]?DVD)\b)(?=.*\b(AVC|HEVC|VC[-_. ]?1|MVC|MPEG[-_. ]?2|BDMV|ISO)\b))|^((?=.*\b(((?=.*\b((.*_)?COMPLETE.*|Dis[ck])\b)(?=.*(Blu[-_. ]?ray|HD[-_. ]?DVD)))|3D[-_. ]?BD|BR[-_. ]?DISK|Full[-_. ]?Blu[-_. ]?ray|^((?=.*((BD|UHD)[-_. ]?(25|50|66|100|ISO)))))))).*", RegexOptions.IgnoreCase)]
    private static partial Regex BrDiskRegex();

    [GeneratedRegex(@"\b(?<proper>proper)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProperRegex();

    [GeneratedRegex(@"\b(?<repack>repack\d?|rerip\d?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepackRegex();

    [GeneratedRegex(@"\d[-._ ]?v(?<version>\d)[-._ ]|\[v(?<version>\d)\]|repack(?<version>\d)|rerip(?<version>\d)|(?:480|576|720|1080|2160)p[._ ]v(?<version>\d)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\b(?<real>REAL)\b")]
    private static partial Regex RealRegex();

    [GeneratedRegex(@"\b(?:(?<R360p>360p)|(?<R480p>480p|480i|640x480|848x480)|(?<R540p>540p)|(?<R576p>576p)|(?<R720p>720p|1280x720|960p)|(?<R1080p>1080p|1920x1080|1440p|FHD|1080i|4kto1080p)|(?<R2160p>2160p|3840x2160|4k[-_. ](?:UHD|HEVC|BD|H\.?26[56])|(?:UHD|HEVC|BD|H\.?26[56])[-_. ]4k))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();

    // Handle cases where no resolution is in the release name (assume if UHD then 4k) or resolution is non-standard
    [GeneratedRegex(@"\b(?<R2160p>UHD)\b|(?<R2160p>\[4K\])", RegexOptions.IgnoreCase)]
    private static partial Regex AlternativeResolutionRegex();

    [GeneratedRegex(@"\b(?:(?<x264>x264)|(?<h264>h264)|(?<xvidhd>XvidHD)|(?<xvid>X-?vid)|(?<divx>divx))\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodecRegex();

    [GeneratedRegex(@"(?<hdtv>HD[-_. ]TV)|(?<sdtv>SD[-_. ]TV)", RegexOptions.IgnoreCase)]
    private static partial Regex OtherSourceRegex();

    [GeneratedRegex(@"bd(?:720|1080|2160)|(?<=[-_. (\[])bd(?=[-_. )\]])", RegexOptions.IgnoreCase)]
    private static partial Regex AnimeBlurayRegex();

    [GeneratedRegex(@"\[WEB\]|[\[\(]WEB[ .]", RegexOptions.IgnoreCase)]
    private static partial Regex AnimeWebDlRegex();

    [GeneratedRegex(@"hr[-_. ]ws", RegexOptions.IgnoreCase)]
    private static partial Regex HighDefPdtvRegex();

    [GeneratedRegex(@"(?:[_. \[]|\d{4}p-|\bHybrid-)(?<remux>(?:(BD|UHD)[-_. ]?)?Remux)\b|(?<remux>(?:(BD|UHD)[-_. ]?)?Remux[_. ]\d{4}p)", RegexOptions.IgnoreCase)]
    private static partial Regex RemuxRegex();

    [GeneratedRegex(@"((?<=\d{4}).*German.*([DM]L)?)(?=.*\b(AVC|HEVC|VC[_. -]?1|MVC|MPEG[_. -]?2))(?=.*Blu-?ray)", RegexOptions.IgnoreCase)]
    private static partial Regex GermanRemuxRegex();

    // HDR (StreamZH addition, Radarr reads HDR from media info instead of the name)
    [GeneratedRegex(@"\b(?:DV|DoVi|Dolby[-_. ]?Vision)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex DolbyVisionRegex();

    [GeneratedRegex(@"\bHDR10(?:\+|[-_. ]?Plus\b|P\b)", RegexOptions.IgnoreCase)]
    private static partial Regex Hdr10PlusRegex();

    [GeneratedRegex(@"\bHDR10(?![+a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Hdr10Regex();

    [GeneratedRegex(@"\bHDR(?![a-z0-9+])", RegexOptions.IgnoreCase)]
    private static partial Regex HdrRegex();

    [GeneratedRegex(@"\bHLG(?:10)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex HlgRegex();
}
