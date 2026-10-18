// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json.Serialization;
using StreamZH.Core.Quality;

namespace StreamZH.Core.Media;

/// <summary>Dynamic range of the video, like Radarr's HdrFormat (read from the color transfer, primaries and side data).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MediaHdrType>))]
public enum MediaHdrType
{
    None = 0,

    /// <summary>PQ transfer without HDR10 metadata.</summary>
    Pq,
    Hdr10,
    Hdr10Plus,
    Hlg,
    DolbyVision,
    DolbyVisionHdr10,
    DolbyVisionHdr10Plus,
    DolbyVisionHlg,
    DolbyVisionSdr,
}

/// <summary>
/// What ffprobe found in a media file (Radarr's and Sonarr's MediaInfoModel), stored as JSON with the file details.
/// Codecs are normalised like Radarr's MediaInfoFormatter (x264, x265, AV1 …; AAC, EAC3 Atmos, DTS-HD MA …).
/// </summary>
public sealed record MediaInfo
{
    /// <summary>Raised when the analysis learns something new, so older results are analysed again.</summary>
    public const int CurrentRevision = 1;

    public int Revision { get; init; } = CurrentRevision;

    /// <summary>"Matroska", "MP4", "AVI", "MPEG-TS" …</summary>
    public string? Container { get; init; }

    public double? RuntimeSeconds { get; init; }

    /// <summary>Bits per second of the whole file.</summary>
    public long? OverallBitrate { get; init; }

    /// <summary>Size of the file when it was analysed (a different size means it changed).</summary>
    public long? FileSize { get; init; }

    /// <summary>Last write time (UTC) of the file when it was analysed (a different time means it changed).</summary>
    public DateTime? FileModified { get; init; }

    public MediaVideoInfo? Video { get; init; }

    public IReadOnlyList<MediaAudioTrack> AudioTracks { get; init; } = [];

    public IReadOnlyList<MediaSubtitleTrack> Subtitles { get; init; } = [];

    /// <summary>The audio track players start with: the default track, else the first that is no commentary.</summary>
    [JsonIgnore]
    public MediaAudioTrack? PrimaryAudio =>
        AudioTracks.FirstOrDefault(a => a.Default && !a.Commentary) ?? AudioTracks.FirstOrDefault(a => !a.Commentary) ?? AudioTracks.FirstOrDefault();

    /// <summary>The known audio languages, in track order, without commentary tracks and without duplicates.</summary>
    [JsonIgnore]
    public IReadOnlyList<Language> AudioLanguages =>
        [.. AudioTracks.Where(a => !a.Commentary).Select(a => Language.FindById(a.LanguageId)).Where(l => l.Id > 0).Distinct()];

    /// <summary>The known subtitle languages, in track order, without duplicates.</summary>
    [JsonIgnore]
    public IReadOnlyList<Language> SubtitleLanguages =>
        [.. Subtitles.Select(s => Language.FindById(s.LanguageId)).Where(l => l.Id > 0).Distinct()];
}

/// <param name="CodecName">ffprobe's codec name (h264, hevc, av1 …).</param>
/// <param name="Codec">Normalised like Radarr: x264, h264, x265, h265, AV1, VC1, MPEG2, XviD …</param>
/// <param name="Resolution">2160, 1080, 720, 576 or 480, from width and height like Radarr (0 = unknown).</param>
/// <param name="ScanType">"Progressive" or "Interlaced"; null when unknown.</param>
public sealed record MediaVideoInfo(
    string CodecName,
    string Codec,
    string? Profile,
    int BitDepth,
    int Width,
    int Height,
    int Resolution,
    double? FrameRate,
    string? ScanType,
    MediaHdrType Hdr,
    string? ColorPrimaries,
    string? ColorTransfer,
    long? Bitrate,
    bool Is3D,
    int? DolbyVisionProfile = null)
{
    /// <summary>Radarr's {MediaInfo VideoDynamicRange}: "HDR" for any HDR type.</summary>
    [JsonIgnore]
    public string DynamicRange => Hdr == MediaHdrType.None ? string.Empty : "HDR";

    /// <summary>Radarr's {MediaInfo VideoDynamicRangeType}: DV, DV HDR10, DV HDR10Plus, HDR10, HDR10Plus, HLG, PQ …</summary>
    [JsonIgnore]
    public string DynamicRangeType => Hdr switch
    {
        MediaHdrType.DolbyVision => "DV",
        MediaHdrType.DolbyVisionHdr10 => "DV HDR10",
        MediaHdrType.DolbyVisionHdr10Plus => "DV HDR10Plus",
        MediaHdrType.DolbyVisionHlg => "DV HLG",
        MediaHdrType.DolbyVisionSdr => "DV SDR",
        MediaHdrType.Hdr10 => "HDR10",
        MediaHdrType.Hdr10Plus => "HDR10Plus",
        MediaHdrType.Hlg => "HLG",
        MediaHdrType.Pq => "PQ",
        _ => string.Empty,
    };

    /// <summary>Radarr's resolution steps from width and height: 2160, 1080, 720, 576, 480 (0 without a size).</summary>
    public static int ResolutionOf(int width, int height) => (width, height) switch
    {
        _ when width >= 3200 || height >= 2100 => 2160,
        _ when width >= 1800 || height >= 1000 => 1080,
        _ when width >= 1200 || height >= 700 => 720,
        _ when width >= 1000 || height >= 560 => 576,
        _ when width > 0 && height > 0 => 480,
        _ => 0,
    };
}

/// <param name="CodecName">ffprobe's codec name (truehd, eac3, dts …).</param>
/// <param name="Codec">Normalised like Radarr: AAC, AC3, EAC3, EAC3 Atmos, TrueHD, TrueHD Atmos, DTS, DTS-HD MA, DTS-X, FLAC, Opus, MP3, PCM …</param>
/// <param name="Channels">Channels as Radarr writes them: 1.0, 2.0, 5.1, 7.1 …</param>
/// <param name="LanguageCode">The language tag of the track (usually ISO 639-2, e.g. "ger"); null when missing or "und".</param>
/// <param name="LanguageId">The StreamZH language of <paramref name="LanguageCode"/>, 0 when unknown.</param>
public sealed record MediaAudioTrack(
    string CodecName,
    string Codec,
    string? Profile,
    decimal Channels,
    string? ChannelLayout,
    string? LanguageCode,
    int LanguageId,
    string? Title,
    bool Default,
    bool Commentary,
    long? Bitrate)
{
    /// <summary>"5.1", "2.0".</summary>
    [JsonIgnore]
    public string ChannelsText => Channels > 0 ? Channels.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty;
}

/// <param name="CodecName">ffprobe's codec name (subrip, ass, hdmv_pgs_subtitle …).</param>
/// <param name="Format">SRT, ASS, PGS, VobSub …</param>
public sealed record MediaSubtitleTrack(
    string CodecName,
    string Format,
    string? LanguageCode,
    int LanguageId,
    string? Title,
    bool Default,
    bool Forced,
    bool HearingImpaired);

/// <summary>ISO 639-2 codes (both the bibliographic and terminology forms) of the StreamZH languages, as ffprobe reports them.</summary>
public static class MediaLanguages
{
    private static readonly Dictionary<string, Language> ByCode = Build();

    /// <summary>The language of a track tag ("ger", "deu", "de", "pt-BR"); Unknown for "und", "mul", empty or unmapped codes.</summary>
    public static Language FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Language.Unknown;
        }

        var text = code.Trim().Replace('_', '-').ToLowerInvariant();
        if (text is "pt-br" or "pob")
        {
            return Language.PortugueseBrazil;
        }

        if (text is "es-419" or "es-mx" or "es-la")
        {
            return Language.SpanishLatino;
        }

        var primary = text.Split('-')[0];
        if (ByCode.TryGetValue(primary, out var language))
        {
            return language;
        }

        return primary.Length == 2 ? Language.FromIsoCode(primary) : Language.Unknown;
    }

    /// <summary>The code as written in names: ISO 639-1 upper case for known languages ("DE"), else the tag itself in upper case.</summary>
    public static string? NameCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || IsUndefined(code))
        {
            return null;
        }

        var language = FromCode(code);
        return language.Id > 0 && IsoOne.TryGetValue(language.Id, out var iso) ? iso : code.Trim().ToUpperInvariant();
    }

    /// <summary>"und" (undetermined), "mul" (multiple), "zxx" (no linguistic content), "mis".</summary>
    public static bool IsUndefined(string? code) =>
        string.IsNullOrWhiteSpace(code) || code.Trim().ToLowerInvariant() is "und" or "mul" or "zxx" or "mis" or "unknown";

    private static readonly Dictionary<int, string> IsoOne = new()
    {
        [Language.English.Id] = "EN", [Language.French.Id] = "FR", [Language.Spanish.Id] = "ES", [Language.German.Id] = "DE",
        [Language.Italian.Id] = "IT", [Language.Danish.Id] = "DA", [Language.Dutch.Id] = "NL", [Language.Japanese.Id] = "JA",
        [Language.Icelandic.Id] = "IS", [Language.Chinese.Id] = "ZH", [Language.Russian.Id] = "RU", [Language.Polish.Id] = "PL",
        [Language.Vietnamese.Id] = "VI", [Language.Swedish.Id] = "SV", [Language.Norwegian.Id] = "NO", [Language.Finnish.Id] = "FI",
        [Language.Turkish.Id] = "TR", [Language.Portuguese.Id] = "PT", [Language.Greek.Id] = "EL", [Language.Korean.Id] = "KO",
        [Language.Hungarian.Id] = "HU", [Language.Hebrew.Id] = "HE", [Language.Lithuanian.Id] = "LT", [Language.Czech.Id] = "CS",
        [Language.Hindi.Id] = "HI", [Language.Romanian.Id] = "RO", [Language.Thai.Id] = "TH", [Language.Bulgarian.Id] = "BG",
        [Language.PortugueseBrazil.Id] = "PT", [Language.Arabic.Id] = "AR", [Language.Ukrainian.Id] = "UK", [Language.Persian.Id] = "FA",
        [Language.Bengali.Id] = "BN", [Language.Slovak.Id] = "SK", [Language.Latvian.Id] = "LV", [Language.SpanishLatino.Id] = "ES",
        [Language.Catalan.Id] = "CA", [Language.Croatian.Id] = "HR", [Language.Serbian.Id] = "SR", [Language.Bosnian.Id] = "BS",
        [Language.Estonian.Id] = "ET", [Language.Tamil.Id] = "TA", [Language.Indonesian.Id] = "ID", [Language.Telugu.Id] = "TE",
        [Language.Macedonian.Id] = "MK", [Language.Slovenian.Id] = "SL", [Language.Malayalam.Id] = "ML", [Language.Kannada.Id] = "KN",
        [Language.Albanian.Id] = "SQ", [Language.Afrikaans.Id] = "AF", [Language.Marathi.Id] = "MR", [Language.Tagalog.Id] = "TL",
        [Language.Urdu.Id] = "UR", [Language.Romansh.Id] = "RM", [Language.Mongolian.Id] = "MN", [Language.Georgian.Id] = "KA",
        [Language.Flemish.Id] = "NL",
    };

    /// <summary>ISO 639-1 code of a language for names ("DE"), null when it has none.</summary>
    public static string? IsoOneOf(Language language) =>
        language is not null && IsoOne.TryGetValue(language.Id, out var iso) ? iso : null;

    private static Dictionary<string, Language> Build()
    {
        var map = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        void Add(Language language, params string[] codes)
        {
            foreach (var code in codes)
            {
                map[code] = language;
            }
        }

        Add(Language.English, "eng");
        Add(Language.French, "fre", "fra");
        Add(Language.Spanish, "spa");
        Add(Language.German, "ger", "deu", "gsw");
        Add(Language.Italian, "ita");
        Add(Language.Danish, "dan");
        Add(Language.Dutch, "dut", "nld");
        Add(Language.Japanese, "jpn");
        Add(Language.Icelandic, "ice", "isl");
        Add(Language.Chinese, "chi", "zho", "cmn", "yue");
        Add(Language.Russian, "rus");
        Add(Language.Polish, "pol");
        Add(Language.Vietnamese, "vie");
        Add(Language.Swedish, "swe");
        Add(Language.Norwegian, "nor", "nob", "nno");
        Add(Language.Finnish, "fin");
        Add(Language.Turkish, "tur");
        Add(Language.Portuguese, "por");
        Add(Language.Flemish, "vls");
        Add(Language.Greek, "gre", "ell");
        Add(Language.Korean, "kor");
        Add(Language.Hungarian, "hun");
        Add(Language.Hebrew, "heb");
        Add(Language.Lithuanian, "lit");
        Add(Language.Czech, "cze", "ces");
        Add(Language.Hindi, "hin");
        Add(Language.Romanian, "rum", "ron");
        Add(Language.Thai, "tha");
        Add(Language.Bulgarian, "bul");
        Add(Language.Arabic, "ara");
        Add(Language.Ukrainian, "ukr");
        Add(Language.Persian, "per", "fas");
        Add(Language.Bengali, "ben");
        Add(Language.Slovak, "slo", "slk");
        Add(Language.Latvian, "lav");
        Add(Language.Catalan, "cat");
        Add(Language.Croatian, "hrv", "scr");
        Add(Language.Serbian, "srp", "scc");
        Add(Language.Bosnian, "bos");
        Add(Language.Estonian, "est");
        Add(Language.Tamil, "tam");
        Add(Language.Indonesian, "ind");
        Add(Language.Telugu, "tel");
        Add(Language.Macedonian, "mac", "mkd");
        Add(Language.Slovenian, "slv");
        Add(Language.Malayalam, "mal");
        Add(Language.Kannada, "kan");
        Add(Language.Albanian, "alb", "sqi");
        Add(Language.Afrikaans, "afr");
        Add(Language.Marathi, "mar");
        Add(Language.Tagalog, "tgl", "fil");
        Add(Language.Urdu, "urd");
        Add(Language.Romansh, "roh");
        Add(Language.Mongolian, "mon");
        Add(Language.Georgian, "geo", "kat");
        return map;
    }
}
