// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Parsing.Releases;

/// <summary>
/// Video and audio codec from the release info part of a name, normalised to one spelling
/// ("H.264", "h264", "AVC" → "x264"; "DDP5.1", "DD+", "E-AC-3" → "EAC3").
/// </summary>
public static partial class CodecParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly (Regex Regex, string Name)[] VideoCodecs =
    [
        (X265Regex(), "x265"),
        (X264Regex(), "x264"),
        (Av1Regex(), "AV1"),
        (Vp9Regex(), "VP9"),
        (Vc1Regex(), "VC-1"),
        (Mpeg2Regex(), "MPEG2"),
        (XvidRegex(), "XviD"),
        (DivxRegex(), "DivX"),
    ];

    // Ordered from best to worst: a dual-language release lists several tracks, the best one wins.
    private static readonly (Regex Regex, string Name)[] AudioCodecs =
    [
        (TrueHdRegex(), "TrueHD"),
        (DtsXRegex(), "DTS-X"),
        (DtsHdMaRegex(), "DTS-HD MA"),
        (DtsHdRegex(), "DTS-HD"),
        (DtsEsRegex(), "DTS-ES"),
        (DtsRegex(), "DTS"),
        (Eac3Regex(), "EAC3"),
        (Ac3Regex(), "AC3"),
        (FlacRegex(), "FLAC"),
        (PcmRegex(), "PCM"),
        (OpusRegex(), "Opus"),
        (AacRegex(), "AAC"),
        (Mp3Regex(), "MP3"),
    ];

    /// <summary>"x264", "x265", "AV1", "VP9", "VC-1", "MPEG2", "XviD", "DivX" or null.</summary>
    public static string? ParseVideoCodec(string releaseInfo) => FirstMatch(VideoCodecs, releaseInfo);

    /// <summary>"TrueHD", "DTS-X", "DTS-HD MA", "DTS-HD", "DTS-ES", "DTS", "EAC3", "AC3", "FLAC", "PCM", "Opus", "AAC", "MP3" or null; " Atmos" is appended when present.</summary>
    public static string? ParseAudioCodec(string releaseInfo)
    {
        var codec = FirstMatch(AudioCodecs, releaseInfo);
        bool atmos;
        try
        {
            atmos = AtmosRegex().IsMatch(releaseInfo);
        }
        catch (RegexMatchTimeoutException)
        {
            atmos = false;
        }

        if (!atmos)
        {
            return codec;
        }

        return codec is null ? "Atmos" : codec + " Atmos";
    }

    private static string? FirstMatch((Regex Regex, string Name)[] codecs, string input)
    {
        foreach (var (regex, name) in codecs)
        {
            try
            {
                if (regex.IsMatch(input))
                {
                    return name;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Treat as no match.
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<![a-z])[xh][\W_]?265(?![0-9])|(?<![a-z0-9])HEVC(?![a-z0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex X265Regex();

    [GeneratedRegex(@"(?<![a-z])[xh][\W_]?264(?![0-9])|(?<![a-z0-9])AVC(?![a-z0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex X264Regex();

    [GeneratedRegex(@"(?<![a-z0-9])AV1(?![a-z0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex Av1Regex();

    [GeneratedRegex(@"(?<![a-z0-9])VP9(?![a-z0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex Vp9Regex();

    [GeneratedRegex(@"(?<![a-z0-9])VC-?1(?![a-z0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex Vc1Regex();

    [GeneratedRegex(@"(?<![a-z0-9])MPEG-?2(?![a-z0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex Mpeg2Regex();

    [GeneratedRegex(@"(?<![a-z0-9])XviD(?![a-z0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex XvidRegex();

    [GeneratedRegex(@"(?<![a-z0-9])DivX(?![a-z0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex DivxRegex();

    [GeneratedRegex(@"(?<![a-z0-9])True-?HD(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex TrueHdRegex();

    [GeneratedRegex(@"(?<![a-z0-9])DTS[-. ]?X(?![a-z0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex DtsXRegex();

    [GeneratedRegex(@"(?<![a-z0-9])DTS[-. ]?HD[-. ]?MA(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex DtsHdMaRegex();

    [GeneratedRegex(@"(?<![a-z0-9])DTS[-. ]?HD(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex DtsHdRegex();

    [GeneratedRegex(@"(?<![a-z0-9])DTS[-. ]?ES(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex DtsEsRegex();

    [GeneratedRegex(@"(?<![a-z0-9])DTS(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex DtsRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:E-?AC-?3|DDP|DD\+|DD[\W_]?Plus)(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex Eac3Regex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:AC-?3(?:D|MD|LD)?|DD(?=[\W_]?\d)|Dolby[\W_]?Digital)(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex Ac3Regex();

    [GeneratedRegex(@"(?<![a-z0-9])FLAC(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex FlacRegex();

    [GeneratedRegex(@"(?<![a-z0-9])L?PCM(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex PcmRegex();

    [GeneratedRegex(@"(?<![a-z0-9])Opus(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex OpusRegex();

    [GeneratedRegex(@"(?<![a-z0-9])AAC(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex AacRegex();

    [GeneratedRegex(@"(?<![a-z0-9])MP3(?![a-z0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex Mp3Regex();

    [GeneratedRegex(@"(?<![a-z0-9])Atmos(?![a-z])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex AtmosRegex();
}
