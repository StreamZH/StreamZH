// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>Turns Radarr's and Sonarr's qualities, languages, media info and files into StreamZH's.</summary>
public static class ArrLibraryMapping
{
    /// <summary>Sonarr names some qualities differently than Radarr (StreamZH uses Radarr's names and ids).</summary>
    private static readonly Dictionary<string, Quality> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bluray-1080p Remux"] = Quality.Remux1080p,
        ["Bluray-2160p Remux"] = Quality.Remux2160p,
        ["Remux-1080p"] = Quality.Remux1080p,
        ["Remux-2160p"] = Quality.Remux2160p,
        ["HDTV-480p"] = Quality.Sdtv,
        ["WEB 480p"] = Quality.WebDl480p,
        ["WEB 720p"] = Quality.WebDl720p,
        ["WEB 1080p"] = Quality.WebDl1080p,
        ["WEB 2160p"] = Quality.WebDl2160p,
    };

    private static readonly Dictionary<string, Quality> ByName =
        Quality.All.ToDictionary(q => q.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// StreamZH's quality for an old app's quality: by name (Sonarr's ids differ from Radarr's, names mostly match), then
    /// for Radarr by id (StreamZH uses Radarr's ids); Unknown when nothing fits.
    /// </summary>
    public static Quality QualityOf(string source, ArrQuality? quality)
    {
        if (quality is null)
        {
            return Quality.Unknown;
        }

        if (quality.Name is { Length: > 0 } name)
        {
            if (ByName.TryGetValue(name.Trim(), out var byName))
            {
                return byName;
            }

            if (Aliases.TryGetValue(name.Trim(), out var alias))
            {
                return alias;
            }
        }

        if (source == MigrationSources.Radarr && Quality.FindById(quality.Id) is { } byId && byId != Quality.Unknown)
        {
            return byId;
        }

        return BySourceAndResolution(quality);
    }

    public static QualityModel QualityModelOf(string source, ArrQualityModel? model) =>
        new(QualityOf(source, model?.Quality), new Revision(Math.Max(1, model?.Revision?.Version ?? 1), Math.Max(0, model?.Revision?.Real ?? 0), model?.Revision?.IsRepack ?? false));

    /// <summary>StreamZH's languages for the old app's (by name; Sonarr's and Radarr's ids differ above 25).</summary>
    public static IReadOnlyList<Language> LanguagesOf(IEnumerable<ArrLanguage>? languages) =>
        [.. (languages ?? [])
            .Select(l => LanguageByName(l.Name))
            .Where(l => l.Id > 0)
            .Distinct()];

    public static Language LanguageByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Language.Unknown;
        }

        var trimmed = name.Trim();
        return Language.All.FirstOrDefault(l => l.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            ?? (trimmed.Equals("Portuguese (Brazil)", StringComparison.OrdinalIgnoreCase) ? Language.PortugueseBrazil : Language.Unknown);
    }

    /// <summary>
    /// The old app's media info summary as StreamZH media info, marked with revision 0 so StreamZH analyses the file again
    /// with ffprobe when it can; until then names, filters and the file details show what Radarr or Sonarr found.
    /// </summary>
    public static MediaInfo? MediaInfoOf(ArrMediaInfo? info, FileInfo? file)
    {
        if (info is null)
        {
            return null;
        }

        var (width, height) = ParseResolution(info.Resolution);
        MediaVideoInfo? video = string.IsNullOrWhiteSpace(info.VideoCodec) && width == 0
            ? null
            : new MediaVideoInfo(
                VideoCodecName(info.VideoCodec),
                info.VideoCodec?.Trim() ?? string.Empty,
                null,
                info.VideoBitDepth,
                width,
                height,
                MediaVideoInfo.ResolutionOf(width, height),
                info.VideoFps > 0 ? (double)info.VideoFps : null,
                string.IsNullOrWhiteSpace(info.ScanType) ? null : info.ScanType.Trim(),
                HdrOf(info.VideoDynamicRange, info.VideoDynamicRangeType),
                null,
                null,
                info.VideoBitrate > 0 ? info.VideoBitrate : null,
                false);

        var audioCodes = Codes(info.AudioLanguages);
        var audioCount = Math.Max(Math.Max(audioCodes.Count, info.AudioStreamCount), string.IsNullOrWhiteSpace(info.AudioCodec) ? 0 : 1);
        var audio = new List<MediaAudioTrack>();
        for (var i = 0; i < audioCount; i++)
        {
            var code = i < audioCodes.Count ? audioCodes[i] : null;
            var language = MediaLanguages.FromCode(code);
            var codec = info.AudioCodec?.Trim() ?? string.Empty;
            audio.Add(new MediaAudioTrack(
                codec.ToLowerInvariant(),
                codec,
                null,
                i == 0 ? info.AudioChannels : 0,
                null,
                MediaLanguages.IsUndefined(code) ? null : code,
                language.Id,
                null,
                i == 0,
                false,
                i == 0 && info.AudioBitrate > 0 ? info.AudioBitrate : null));
        }

        var subtitles = Codes(info.Subtitles)
            .Select(code => new MediaSubtitleTrack(string.Empty, string.Empty, MediaLanguages.IsUndefined(code) ? null : code, MediaLanguages.FromCode(code).Id, null, false, false, false))
            .ToList();

        return new MediaInfo
        {
            Revision = 0,
            RuntimeSeconds = ParseRuntime(info.RunTime),
            FileSize = file?.Length,
            FileModified = file?.LastWriteTimeUtc,
            Video = video,
            AudioTracks = audio,
            Subtitles = subtitles,
        };
    }

    /// <summary>
    /// The StreamZH file details of a file Radarr or Sonarr imported: their quality (with revision), languages, release
    /// group, the original release name (for custom formats) and edition. Codecs come from the release name like an
    /// import would store them.
    /// </summary>
    public static MediaFileDetails DetailsOf(string source, ArrFileBase file, string localPath, string? edition, FileInfo? onDisk)
    {
        ArgumentNullException.ThrowIfNull(file);
        var quality = QualityModelOf(source, file.Quality);
        var name = file.SceneName is { Length: > 0 } scene ? scene : Path.GetFileNameWithoutExtension(localPath);
        var parsed = ReleaseParser.ParseEpisode(name) ?? ReleaseParser.Parse(name);
        return new MediaFileDetails(
            localPath,
            quality.Quality.Id,
            quality.Revision.Version,
            quality.Revision.Real,
            quality.Revision.IsRepack,
            [.. LanguagesOf(file.AllLanguages).Select(l => l.Id)],
            Blank(file.ReleaseGroup) ?? parsed?.ReleaseGroup,
            Blank(file.SceneName),
            Blank(edition) ?? parsed?.Edition,
            parsed?.VideoCodec,
            parsed?.AudioCodec,
            file.DateAdded == default ? DateTime.UtcNow : DateTime.SpecifyKind(file.DateAdded, DateTimeKind.Utc),
            MediaInfoOf(file.MediaInfo, onDisk),
            null);
    }

    /// <summary>"12.3 GB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };

    private static Quality BySourceAndResolution(ArrQuality quality)
    {
        var modifier = quality.Modifier?.ToLowerInvariant() switch
        {
            "remux" => QualityModifier.Remux,
            "brdisk" => QualityModifier.BrDisk,
            "rawhd" => QualityModifier.RawHd,
            "regional" => QualityModifier.Regional,
            "screener" => QualityModifier.Screener,
            _ => QualityModifier.None,
        };
        var source = quality.Source?.ToLowerInvariant() switch
        {
            "webdl" or "web" => QualitySource.WebDl,
            "webrip" => QualitySource.WebRip,
            "tv" or "television" => QualitySource.Tv,
            "televisionraw" => QualitySource.Tv,
            "bluray" => QualitySource.Bluray,
            "blurayraw" => QualitySource.Bluray,
            "dvd" => QualitySource.Dvd,
            "cam" => QualitySource.Cam,
            "telesync" => QualitySource.Telesync,
            "telecine" => QualitySource.Telecine,
            "workprint" => QualitySource.Workprint,
            _ => QualitySource.Unknown,
        };
        if (quality.Source?.Equals("blurayRaw", StringComparison.OrdinalIgnoreCase) == true)
        {
            modifier = QualityModifier.Remux;
        }

        if (quality.Source?.Equals("televisionRaw", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Quality.RawHd;
        }

        return Quality.Find(source, quality.Resolution, modifier);
    }

    private static (int Width, int Height) ParseResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return (0, 0);
        }

        var parts = resolution.Split('x', StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            ? (w, h)
            : (0, 0);
    }

    private static double? ParseRuntime(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime))
        {
            return null;
        }

        var parts = runtime.Split(':');
        var seconds = 0d;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            seconds = (seconds * 60) + value;
        }

        return seconds > 0 ? seconds : null;
    }

    private static List<string> Codes(string? joined) =>
        string.IsNullOrWhiteSpace(joined) ? [] : [.. joined.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string VideoCodecName(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        "x264" or "h264" or "avc" => "h264",
        "x265" or "h265" or "hevc" => "hevc",
        "av1" => "av1",
        "vc1" => "vc1",
        "mpeg2" => "mpeg2video",
        "xvid" or "divx" or "mpeg4" => "mpeg4",
        "vp9" => "vp9",
        null or "" => string.Empty,
        var other => other,
    };

    private static MediaHdrType HdrOf(string? range, string? type) => type?.Trim() switch
    {
        "DV" => MediaHdrType.DolbyVision,
        "DV HDR10" => MediaHdrType.DolbyVisionHdr10,
        "DV HDR10Plus" => MediaHdrType.DolbyVisionHdr10Plus,
        "DV HLG" => MediaHdrType.DolbyVisionHlg,
        "DV SDR" => MediaHdrType.DolbyVisionSdr,
        "HDR10" => MediaHdrType.Hdr10,
        "HDR10Plus" => MediaHdrType.Hdr10Plus,
        "HLG" => MediaHdrType.Hlg,
        "PQ" => MediaHdrType.Pq,
        _ => string.Equals(range?.Trim(), "HDR", StringComparison.OrdinalIgnoreCase) ? MediaHdrType.Pq : MediaHdrType.None,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
