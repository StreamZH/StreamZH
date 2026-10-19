// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Indexers;

/// <summary>
/// The bits of <see cref="ReleaseInfo.IndexerFlags"/>: Radarr's IndexerFlags enum. Sonarr numbers the same flags
/// differently; <see cref="ToSonarr"/> maps them for series custom formats (their "Indexer Flag" condition uses Sonarr's bits).
/// </summary>
public static class IndexerFlagBits
{
    public const int Freeleech = 1;
    public const int Halfleech = 2;
    public const int DoubleUpload = 4;
    public const int PtpGolden = 8;
    public const int PtpApproved = 16;
    public const int Internal = 32;
    public const int Scene = 128;
    public const int Freeleech75 = 256;
    public const int Freeleech25 = 512;
    public const int Nuked = 2048;

    // Radarr bit → Sonarr bit (Sonarr: Freeleech 1, Halfleech 2, DoubleUpload 4, Internal 8, Scene 16, Freeleech75 32, Freeleech25 64, Nuked 128).
    private static readonly (int Radarr, int Sonarr)[] SonarrMap =
    [
        (Freeleech, 1), (Halfleech, 2), (DoubleUpload, 4), (Internal, 8), (Scene, 16), (Freeleech75, 32), (Freeleech25, 64), (Nuked, 128),
    ];

    // Short names for the API (and the UI's badges), in display order.
    private static readonly (int Bit, string Name)[] Names =
    [
        (Freeleech, "freeleech"), (Freeleech75, "freeleech75"), (Freeleech25, "freeleech25"), (Halfleech, "halfleech"),
        (DoubleUpload, "doubleUpload"), (Internal, "internal"), (Scene, "scene"), (PtpGolden, "golden"), (PtpApproved, "approved"),
        (Nuked, "nuked"),
    ];

    /// <summary>Radarr bits → Sonarr bits; flags Sonarr does not know (Golden, Approved) are dropped.</summary>
    public static int ToSonarr(int radarrFlags)
    {
        var result = 0;
        foreach (var (radarr, sonarr) in SonarrMap)
        {
            if ((radarrFlags & radarr) != 0)
            {
                result |= sonarr;
            }
        }

        return result;
    }

    /// <summary>"freeleech", "internal", … for the set bits.</summary>
    public static IReadOnlyList<string> ToNames(int flags) =>
        flags == 0 ? [] : [.. Names.Where(n => (flags & n.Bit) != 0).Select(n => n.Name)];

    /// <summary>
    /// Radarr's TorznabRssParser.GetFlags: download volume factor 0 → freeleech, 0.25 → 75 % freeleech, 0.5 → halfleech,
    /// 0.75 → 25 % freeleech; upload factor 2 → double upload; tags "internal"/"scene" (plus Prowlarr's "freeleech",
    /// "halfleech", "doubleupload" and "nuked" tags).
    /// </summary>
    public static int FromTorznab(double? downloadVolumeFactor, double? uploadVolumeFactor, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var flags = downloadVolumeFactor switch
        {
            0d => Freeleech,
            0.25 => Freeleech75,
            0.5 => Halfleech,
            0.75 => Freeleech25,
            _ => 0,
        };

        if (uploadVolumeFactor == 2d)
        {
            flags |= DoubleUpload;
        }

        foreach (var tag in tags)
        {
            flags |= tag.Trim().ToLowerInvariant() switch
            {
                "internal" => Internal,
                "scene" => Scene,
                "freeleech" => Freeleech,
                "halfleech" => Halfleech,
                "doubleupload" => DoubleUpload,
                "nuked" => Nuked,
                _ => 0,
            };
        }

        return flags;
    }
}
