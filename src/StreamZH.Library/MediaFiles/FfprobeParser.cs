// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamZH.Core.Media;

namespace StreamZH.Library.MediaFiles;

/// <summary>The output could not be read as ffprobe JSON.</summary>
public sealed class MediaInfoException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Turns the JSON of <c>ffprobe -print_format json -show_format -show_streams</c> (plus, for HDR video, the side data of
/// the first frame) into a <see cref="MediaInfo"/>, with Radarr's rules: codecs from codec name, profile and tags (and the
/// scene name for x264/x265), the HDR type from transfer, primaries and side data, channels from the channel layout.
/// </summary>
public static partial class FfprobeParser
{
    private const string DoviRecord = "DOVI configuration record";
    private const string MasteringDisplay = "Mastering display metadata";
    private const string ContentLightLevel = "Content light level metadata";
    private const string Hdr10Plus = "HDR Dynamic Metadata SMPTE2094-40 (HDR10+)";
    private const string Stereo3D = "Stereo 3D";

    /// <summary>True when the video needs the frame side data to tell HDR10, HDR10+ and PQ apart.</summary>
    public static bool NeedsFrameSideData(string streamsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(streamsJson);
            var video = Streams(document.RootElement).FirstOrDefault(s => Str(s, "codec_type") == "video" && !IsAttachedPicture(s));
            return video.ValueKind == JsonValueKind.Object
                   && (Str(video, "color_transfer") is "smpte2084" || HasSideData(video, DoviRecord));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <param name="streamsJson">Output of <c>-show_format -show_streams</c>.</param>
    /// <param name="framesJson">Output of <c>-show_frames</c> for the first video frame (side data), or null.</param>
    /// <param name="sceneName">Release or file name: tells x264 from h264 and x265 from h265 like Radarr.</param>
    /// <param name="fileSize">Size of the file, kept to notice when it changes.</param>
    public static MediaInfo Parse(string streamsJson, string? framesJson = null, string? sceneName = null, long? fileSize = null)
    {
        ArgumentNullException.ThrowIfNull(streamsJson);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(streamsJson);
        }
        catch (JsonException e)
        {
            throw new MediaInfoException("ffprobe did not return JSON.", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("streams", out _))
            {
                throw new MediaInfoException("ffprobe did not list the streams of the file.");
            }

            var streams = Streams(root).ToList();
            var format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
            var frameSideData = FrameSideData(framesJson);

            var videoStream = streams.FirstOrDefault(s => Str(s, "codec_type") == "video" && !IsAttachedPicture(s));
            var video = videoStream.ValueKind == JsonValueKind.Object ? Video(videoStream, frameSideData, sceneName) : null;
            var audio = streams.Where(s => Str(s, "codec_type") == "audio").Select(Audio).ToList();
            var subtitles = streams.Where(s => Str(s, "codec_type") == "subtitle").Select(Subtitle).ToList();

            return new MediaInfo
            {
                Container = format.ValueKind == JsonValueKind.Object ? ContainerName(Str(format, "format_name")) : null,
                RuntimeSeconds = Seconds(format) ?? (videoStream.ValueKind == JsonValueKind.Object ? Seconds(videoStream) : null),
                OverallBitrate = format.ValueKind == JsonValueKind.Object ? Long(format, "bit_rate") : null,
                FileSize = fileSize ?? (format.ValueKind == JsonValueKind.Object ? Long(format, "size") : null),
                Video = video,
                AudioTracks = audio,
                Subtitles = subtitles,
            };
        }
    }

    // ------------------------------------------------------------------ video

    private static MediaVideoInfo Video(JsonElement stream, IReadOnlyList<JsonElement> frameSideData, string? sceneName)
    {
        var codecName = Str(stream, "codec_name") ?? string.Empty;
        var width = Int(stream, "width") ?? 0;
        var height = Int(stream, "height") ?? 0;
        var bitDepth = BitDepth(stream);
        var sideData = SideData(stream).Concat(frameSideData).ToList();
        var primaries = Str(stream, "color_primaries");
        var transfer = Str(stream, "color_transfer");
        var dovi = sideData.FirstOrDefault(d => Str(d, "side_data_type") == DoviRecord);

        return new MediaVideoInfo(
            codecName,
            VideoCodec(codecName, Str(stream, "codec_tag_string"), Tag(stream, "ENCODER"), sceneName),
            Str(stream, "profile"),
            bitDepth,
            width,
            height,
            MediaVideoInfo.ResolutionOf(width, height),
            FrameRate(Str(stream, "avg_frame_rate")) ?? FrameRate(Str(stream, "r_frame_rate")),
            ScanType(Str(stream, "field_order")),
            HdrType(bitDepth, primaries, transfer, sideData),
            primaries,
            transfer,
            Bitrate(stream),
            sideData.Any(d => Str(d, "side_data_type") == Stereo3D) || Tag(stream, "stereo_mode") is { } mode && !mode.Equals("mono", StringComparison.OrdinalIgnoreCase),
            dovi.ValueKind == JsonValueKind.Object ? Int(dovi, "dv_profile") : null);
    }

    /// <summary>Radarr's FormatVideoCodec: x264/x265 when the encoder or the scene name says so, else h264/h265 (AVC/HEVC from the name).</summary>
    public static string VideoCodec(string codecName, string? codecTag, string? encoder, string? sceneName)
    {
        var tag = (codecTag ?? string.Empty).ToUpperInvariant();
        switch (codecName.ToLowerInvariant())
        {
            case "h264":
                return Encoded(encoder, "x264") ? "x264" : SceneMatch(sceneName, ["AVC", "x264", "h264"]);
            case "hevc":
                return Encoded(encoder, "x265") ? "x265" : SceneMatch(sceneName, ["HEVC", "x265", "h265"]);
            case "av1":
                return "AV1";
            case "vc1":
                return "VC1";
            case "mpeg2video":
                return "MPEG2";
            case "mpeg1video":
                return "MPEG";
            case "mpeg4":
                return tag switch
                {
                    "XVID" => "XviD",
                    "DIVX" or "DX50" or "DIV3" => "DivX",
                    _ => SceneMatch(sceneName, ["XviD", "DivX"], "MPEG4"),
                };
            case "msmpeg4v3" or "msmpeg4v2":
                return "DivX";
            case "vp6" or "vp6f":
                return "VP6";
            case "vp7":
                return "VP7";
            case "vp8":
                return "VP8";
            case "vp9":
                return "VP9";
            case "wmv1" or "wmv2" or "wmv3":
                return "WMV";
            case "h263":
                return "h263";
            case "":
                return string.Empty;
            default:
                return codecName.ToUpperInvariant();
        }

        static bool Encoded(string? encoder, string library) => encoder?.Contains(library, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>The first token the scene name contains (as a word), else the last one (Radarr's GetSceneNameMatch).</summary>
    private static string SceneMatch(string? sceneName, string[] tokens, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(sceneName))
        {
            var name = VideoExtensionRegex().Replace(sceneName.Trim(), string.Empty).Replace("H.264", "h264", StringComparison.OrdinalIgnoreCase)
                .Replace("H.265", "h265", StringComparison.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                if (Regex.IsMatch(name, $@"(?<![a-z0-9]){Regex.Escape(token)}(?![a-z0-9])", RegexOptions.IgnoreCase))
                {
                    return token;
                }
            }
        }

        return fallback ?? tokens[^1];
    }

    /// <summary>Bit depth from bits_per_raw_sample, else the pixel format ("yuv420p10le" → 10), else 8.</summary>
    private static int BitDepth(JsonElement stream)
    {
        if (Int(stream, "bits_per_raw_sample") is > 0 and var bits)
        {
            return bits;
        }

        var pixelFormat = Str(stream, "pix_fmt") ?? string.Empty;
        if (pixelFormat.StartsWith("p010", StringComparison.Ordinal))
        {
            return 10;
        }

        var match = PixelFormatDepthRegex().Match(pixelFormat);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 8;
    }

    /// <summary>
    /// Radarr's GetHdrFormat: Dolby Vision from the configuration record (with HDR10, HDR10+, HLG or SDR base layer by its
    /// compatibility id), otherwise BT.2020 with PQ (HDR10+ with dynamic metadata, HDR10 with mastering display or light
    /// level data, else plain PQ) or HLG.
    /// </summary>
    public static MediaHdrType HdrType(int bitDepth, string? primaries, string? transfer, IReadOnlyList<JsonElement> sideData)
    {
        ArgumentNullException.ThrowIfNull(sideData);
        bool Has(string type) => sideData.Any(d => Str(d, "side_data_type") == type);

        var dovi = sideData.FirstOrDefault(d => Str(d, "side_data_type") == DoviRecord);
        if (dovi.ValueKind == JsonValueKind.Object)
        {
            return Int(dovi, "dv_bl_signal_compatibility_id") switch
            {
                1 or 6 => Has(Hdr10Plus) ? MediaHdrType.DolbyVisionHdr10Plus : MediaHdrType.DolbyVisionHdr10,
                2 => MediaHdrType.DolbyVisionSdr,
                4 => MediaHdrType.DolbyVisionHlg,
                _ => MediaHdrType.DolbyVision,
            };
        }

        if (bitDepth < 10 || primaries != "bt2020")
        {
            return MediaHdrType.None;
        }

        return transfer switch
        {
            "arib-std-b67" => MediaHdrType.Hlg,
            "smpte2084" when Has(Hdr10Plus) => MediaHdrType.Hdr10Plus,
            "smpte2084" when Has(MasteringDisplay) || Has(ContentLightLevel) => MediaHdrType.Hdr10,
            "smpte2084" => MediaHdrType.Pq,
            _ => MediaHdrType.None,
        };
    }

    private static string? ScanType(string? fieldOrder) => fieldOrder switch
    {
        "progressive" => "Progressive",
        "tt" or "bb" or "tb" or "bt" => "Interlaced",
        _ => null,
    };

    private static double? FrameRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate))
        {
            return null;
        }

        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0 && numerator > 0)
        {
            return Math.Round(numerator / denominator, 3);
        }

        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? Math.Round(value, 3) : null;
    }

    // ------------------------------------------------------------------ audio

    private static MediaAudioTrack Audio(JsonElement stream)
    {
        var codecName = Str(stream, "codec_name") ?? string.Empty;
        var profile = Str(stream, "profile");
        var title = Tag(stream, "title");
        var language = Tag(stream, "language");
        var known = MediaLanguages.IsUndefined(language) ? null : language!.Trim();
        return new MediaAudioTrack(
            codecName,
            AudioCodec(codecName, Str(stream, "codec_tag_string"), profile),
            profile,
            Channels(Str(stream, "channel_layout"), Int(stream, "channels") ?? 0),
            Str(stream, "channel_layout"),
            known,
            MediaLanguages.FromCode(known).Id,
            title,
            Disposition(stream, "default"),
            Disposition(stream, "comment") || title?.Contains("commentary", StringComparison.OrdinalIgnoreCase) == true || title?.Contains("kommentar", StringComparison.OrdinalIgnoreCase) == true,
            Bitrate(stream));
    }

    /// <summary>Radarr's FormatAudioCodec for ffprobe's codec names and profiles.</summary>
    public static string AudioCodec(string codecName, string? codecTag, string? profile)
    {
        var tag = (codecTag ?? string.Empty).ToLowerInvariant();
        var p = profile ?? string.Empty;
        var atmos = p.Contains("Atmos", StringComparison.OrdinalIgnoreCase);
        switch (codecName.ToLowerInvariant())
        {
            case "truehd":
                return atmos || tag == "thd+" ? "TrueHD Atmos" : "TrueHD";
            case "eac3":
                return atmos || tag == "ec+3" ? "EAC3 Atmos" : "EAC3";
            case "ac3":
                return "AC3";
            case "dts":
                return p switch
                {
                    _ when p.Contains("DTS:X", StringComparison.OrdinalIgnoreCase) => "DTS-X",
                    _ when p.Contains("DTS-HD MA", StringComparison.OrdinalIgnoreCase) => "DTS-HD MA",
                    _ when p.Contains("DTS-HD HRA", StringComparison.OrdinalIgnoreCase) => "DTS-HD HRA",
                    _ when p.Contains("DTS-ES", StringComparison.OrdinalIgnoreCase) => "DTS-ES",
                    _ when p.Contains("DTS Express", StringComparison.OrdinalIgnoreCase) => "DTS Express",
                    _ when p.Contains("96/24", StringComparison.Ordinal) => "DTS 96/24",
                    _ => "DTS",
                };
            case "aac":
                return p.Contains("HE", StringComparison.OrdinalIgnoreCase) ? "HE-AAC" : "AAC";
            case "flac":
                return "FLAC";
            case "opus":
                return "Opus";
            case "mp3":
                return "MP3";
            case "mp2":
                return "MP2";
            case "vorbis":
                return "Vorbis";
            case "alac":
                return "ALAC";
            case "wmav1" or "wmav2" or "wmapro":
                return "WMA";
            case var name when name.StartsWith("pcm_", StringComparison.Ordinal) || name.StartsWith("adpcm_", StringComparison.Ordinal):
                return "PCM";
            case "":
                return string.Empty;
            default:
                return codecName.ToUpperInvariant();
        }
    }

    /// <summary>Radarr's FormatAudioChannels: the layout ("5.1(side)" → 5.1, "stereo" → 2.0), else the channel count (6 → 5.1, 8 → 7.1).</summary>
    public static decimal Channels(string? layout, int channels)
    {
        var text = (layout ?? string.Empty).Trim().ToLowerInvariant();
        switch (text)
        {
            case "mono":
                return 1.0m;
            case "stereo" or "downmix":
                return 2.0m;
            case "quad" or "quad(side)":
                return 4.0m;
            case "hexagonal":
                return 6.0m;
            case "octagonal" or "hexadecagonal":
                return channels > 0 ? channels : 8.0m;
        }

        var match = ChannelLayoutRegex().Match(text);
        if (match.Success)
        {
            return decimal.Parse(match.Groups[1].Value + "." + match.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        return channels switch
        {
            <= 0 => 0,
            1 => 1.0m,
            2 => 2.0m,
            3 => 2.1m,
            6 => 5.1m,
            7 => 6.1m,
            8 => 7.1m,
            _ => channels,
        };
    }

    // ------------------------------------------------------------------ subtitles

    private static MediaSubtitleTrack Subtitle(JsonElement stream)
    {
        var codecName = Str(stream, "codec_name") ?? string.Empty;
        var language = Tag(stream, "language");
        var known = MediaLanguages.IsUndefined(language) ? null : language!.Trim();
        var title = Tag(stream, "title");
        return new MediaSubtitleTrack(
            codecName,
            SubtitleFormat(codecName),
            known,
            MediaLanguages.FromCode(known).Id,
            title,
            Disposition(stream, "default"),
            Disposition(stream, "forced") || title?.Contains("forced", StringComparison.OrdinalIgnoreCase) == true,
            Disposition(stream, "hearing_impaired") || title?.Contains("SDH", StringComparison.Ordinal) == true);
    }

    private static string SubtitleFormat(string codecName) => codecName.ToLowerInvariant() switch
    {
        "subrip" or "srt" => "SRT",
        "ass" or "ssa" => "ASS",
        "hdmv_pgs_subtitle" => "PGS",
        "dvd_subtitle" => "VobSub",
        "dvb_subtitle" => "DVB",
        "mov_text" => "TX3G",
        "webvtt" => "WebVTT",
        "" => string.Empty,
        var other => other.ToUpperInvariant(),
    };

    // ------------------------------------------------------------------ container

    private static string? ContainerName(string? formatName)
    {
        if (string.IsNullOrEmpty(formatName))
        {
            return null;
        }

        var names = formatName.Split(',');
        return names[0] switch
        {
            "matroska" => "Matroska",
            "webm" => "WebM",
            "mov" when names.Contains("mp4") => "MP4",
            "mov" => "QuickTime",
            "avi" => "AVI",
            "mpegts" => "MPEG-TS",
            "mpeg" => "MPEG-PS",
            "asf" => "ASF",
            "flv" => "FLV",
            "ogg" => "Ogg",
            _ => names[0].ToUpperInvariant(),
        };
    }

    // ------------------------------------------------------------------ JSON helpers

    private static IEnumerable<JsonElement> Streams(JsonElement root) =>
        root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array ? streams.EnumerateArray() : [];

    private static bool IsAttachedPicture(JsonElement stream) => Disposition(stream, "attached_pic");

    private static IEnumerable<JsonElement> SideData(JsonElement element) =>
        element.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    private static bool HasSideData(JsonElement element, string type) => SideData(element).Any(d => Str(d, "side_data_type") == type);

    private static List<JsonElement> FrameSideData(string? framesJson)
    {
        if (string.IsNullOrWhiteSpace(framesJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(framesJson);
            if (!document.RootElement.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. frames.EnumerateArray().SelectMany(SideData).Select(d => d.Clone())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static int? Int(JsonElement element, string name) =>
        Str(element, name) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? Long(JsonElement element, string name) =>
        Str(element, name) is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static double? Seconds(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && Str(element, "duration") is { } text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? Math.Round(value, 3)
            : null;

    /// <summary>Stream bit rate, else the BPS tag mkvmerge writes ("BPS" or "BPS-eng").</summary>
    private static long? Bitrate(JsonElement stream)
    {
        if (Long(stream, "bit_rate") is { } rate)
        {
            return rate;
        }

        return (Tag(stream, "BPS") ?? Tag(stream, "BPS-eng")) is { } bps
               && long.TryParse(bps, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
    }

    private static string? Tag(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var tag in tags.EnumerateObject())
        {
            if (tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && tag.Value.ValueKind == JsonValueKind.String)
            {
                return tag.Value.GetString();
            }
        }

        return null;
    }

    private static bool Disposition(JsonElement stream, string name) =>
        stream.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object
        && disposition.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == 1;

    [GeneratedRegex(@"p(\d{2})(?:le|be)?$")]
    private static partial Regex PixelFormatDepthRegex();

    [GeneratedRegex(@"^(\d+)\.(\d)")]
    private static partial Regex ChannelLayoutRegex();

    [GeneratedRegex(@"\.(?:mkv|mp4|m4v|avi|ts|m2ts|mts|wmv|mov|webm|mpg|mpeg)$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoExtensionRegex();
}
