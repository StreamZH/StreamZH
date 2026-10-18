// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Quality;

namespace StreamZH.Core.CustomFormats;

/// <summary>Sonarr's release type, read from the episode numbers of a release or file (values as in Sonarr).</summary>
public enum CustomFormatReleaseType
{
    Unknown = 0,
    SingleEpisode = 1,
    MultiEpisode = 2,
    SeasonPack = 3,
}

/// <summary>One choice of a select field (value as stored in the format, label for the UI).</summary>
public sealed record CustomFormatOption(int Value, string Label);

/// <summary>
/// The enum values custom format conditions use, per media type: Radarr's for movie formats, Sonarr's for series formats
/// (so Radarr/Sonarr exports and the TRaSH Guides JSON work unchanged), and how StreamZH's own values map onto them.
/// </summary>
public static class CustomFormatValues
{
    /// <summary>Radarr's Source enum (same numbers as <see cref="QualitySource"/>).</summary>
    public static IReadOnlyList<CustomFormatOption> MovieSources { get; } =
    [
        new(0, "Unknown"), new(1, "CAM"), new(2, "TELESYNC"), new(3, "TELECINE"), new(4, "WORKPRINT"),
        new(5, "DVD"), new(6, "TV"), new(7, "WEBDL"), new(8, "WEBRIP"), new(9, "BLURAY"),
    ];

    /// <summary>Sonarr's QualitySource enum.</summary>
    public static IReadOnlyList<CustomFormatOption> SeriesSources { get; } =
    [
        new(0, "Unknown"), new(1, "Television"), new(2, "Television Raw"), new(3, "Web"), new(4, "Web Rip"),
        new(5, "DVD"), new(6, "Bluray"), new(7, "Bluray Raw"),
    ];

    /// <summary>Radarr's and Sonarr's Resolution enum (the number is the line count).</summary>
    public static IReadOnlyList<CustomFormatOption> Resolutions { get; } =
    [
        new(0, "Unknown"), new(360, "360p"), new(480, "480p"), new(540, "540p"), new(576, "576p"),
        new(720, "720p"), new(1080, "1080p"), new(2160, "2160p"),
    ];

    /// <summary>Radarr's Modifier enum (same numbers as <see cref="QualityModifier"/>).</summary>
    public static IReadOnlyList<CustomFormatOption> Modifiers { get; } =
    [
        new(0, "None"), new(1, "Regional"), new(2, "Screener"), new(3, "Raw-HD"), new(4, "BR-DISK"), new(5, "Remux"),
    ];

    /// <summary>Sonarr's ReleaseType enum.</summary>
    public static IReadOnlyList<CustomFormatOption> ReleaseTypes { get; } =
    [
        new(0, "Unknown"), new(1, "Single Episode"), new(2, "Multi-Episode"), new(3, "Season Pack"),
    ];

    /// <summary>Radarr's IndexerFlags (bit flags).</summary>
    public static IReadOnlyList<CustomFormatOption> MovieIndexerFlags { get; } =
    [
        new(1, "Freeleech"), new(2, "Halfleech"), new(4, "Double Upload"), new(8, "Golden"), new(16, "Approved"),
        new(32, "Internal"), new(64, "Internal (AHD)"), new(128, "Scene"), new(256, "Freeleech 75%"), new(512, "Freeleech 25%"),
        new(1024, "User Release (AHD)"), new(2048, "Nuked"),
    ];

    /// <summary>Sonarr's IndexerFlags (bit flags).</summary>
    public static IReadOnlyList<CustomFormatOption> SeriesIndexerFlags { get; } =
    [
        new(1, "Freeleech"), new(2, "Halfleech"), new(4, "Double Upload"), new(8, "Internal"), new(16, "Scene"),
        new(32, "Freeleech 75%"), new(64, "Freeleech 25%"), new(128, "Nuked"), new(256, "Subtitles"),
    ];

    /// <summary>Sonarr's language ids (they differ from Radarr's above 25); names match <see cref="Language"/>.</summary>
    private static readonly (int Id, string Name)[] SonarrLanguages =
    [
        (0, "Unknown"), (1, "English"), (2, "French"), (3, "Spanish"), (4, "German"), (5, "Italian"), (6, "Danish"), (7, "Dutch"),
        (8, "Japanese"), (9, "Icelandic"), (10, "Chinese"), (11, "Russian"), (12, "Polish"), (13, "Vietnamese"), (14, "Swedish"),
        (15, "Norwegian"), (16, "Finnish"), (17, "Turkish"), (18, "Portuguese"), (19, "Flemish"), (20, "Greek"), (21, "Korean"),
        (22, "Hungarian"), (23, "Hebrew"), (24, "Lithuanian"), (25, "Czech"), (26, "Arabic"), (27, "Hindi"), (28, "Bulgarian"),
        (29, "Malayalam"), (30, "Ukrainian"), (31, "Slovak"), (32, "Thai"), (33, "Portuguese (Brazil)"), (34, "Spanish (Latino)"),
        (35, "Romanian"), (36, "Latvian"), (37, "Persian"), (38, "Catalan"), (39, "Croatian"), (40, "Serbian"), (41, "Bosnian"),
        (42, "Estonian"), (43, "Tamil"), (44, "Indonesian"), (45, "Macedonian"), (46, "Slovenian"), (47, "Azerbaijani"),
        (48, "Uzbek"), (49, "Malay"), (50, "Urdu"), (51, "Romansh"), (52, "Georgian"), (53, "Irish"),
    ];

    private static readonly Dictionary<int, int> SonarrToStreamZh = SonarrLanguages
        .Select(l => (l.Id, Language: Language.All.FirstOrDefault(x => x.Name == l.Name)))
        .Where(x => x.Language is not null)
        .ToDictionary(x => x.Id, x => x.Language!.Id);

    /// <summary>Language choices for a media type: Original and Unknown first, then the languages by name (like Radarr's list).</summary>
    public static IReadOnlyList<CustomFormatOption> Languages(MediaType mediaType)
    {
        if (mediaType == MediaType.Series)
        {
            return
            [
                new(Language.Original.Id, Language.Original.Name),
                new(0, "Unknown"),
                .. SonarrLanguages.Where(l => l.Id > 0).OrderBy(l => l.Name, StringComparer.Ordinal).Select(l => new CustomFormatOption(l.Id, l.Name)),
            ];
        }

        return
        [
            new(Language.Any.Id, Language.Any.Name),
            new(Language.Original.Id, Language.Original.Name),
            new(0, "Unknown"),
            .. Language.All.Select(l => new CustomFormatOption(l.Id, l.Name)),
        ];
    }

    public static IReadOnlyList<CustomFormatOption> Sources(MediaType mediaType) => mediaType == MediaType.Series ? SeriesSources : MovieSources;

    public static IReadOnlyList<CustomFormatOption> IndexerFlags(MediaType mediaType) =>
        mediaType == MediaType.Series ? SeriesIndexerFlags : MovieIndexerFlags;

    /// <summary>
    /// The StreamZH language a condition value means: Radarr ids are StreamZH's own; Sonarr ids are mapped by name.
    /// -1 (Any) and -2 (Original) stay as they are; a Sonarr language StreamZH does not know gives null (never matches).
    /// </summary>
    public static int? LanguageId(MediaType mediaType, int value)
    {
        if (value < 0 || mediaType != MediaType.Series)
        {
            return value;
        }

        return value == 0 ? 0 : SonarrToStreamZh.TryGetValue(value, out var id) ? id : null;
    }

    /// <summary>The source value of a quality for the media type (Radarr's or Sonarr's enum).</summary>
    public static int Source(MediaType mediaType, Quality.Quality quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        if (mediaType != MediaType.Series)
        {
            return (int)quality.Source;
        }

        // Sonarr: 0 Unknown, 1 Television, 2 TelevisionRaw, 3 Web, 4 WebRip, 5 DVD, 6 Bluray, 7 BlurayRaw.
        return quality.Source switch
        {
            QualitySource.Tv => quality.Modifier == QualityModifier.RawHd ? 2 : 1,
            QualitySource.WebDl => 3,
            QualitySource.WebRip => 4,
            QualitySource.Dvd => 5,
            QualitySource.Bluray => quality.Modifier is QualityModifier.Remux or QualityModifier.BrDisk ? 7 : 6,
            _ => 0,
        };
    }

    /// <summary>The resolution value of a quality; Sonarr gives DVD 480 lines, Radarr 0.</summary>
    public static int Resolution(MediaType mediaType, Quality.Quality quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        return mediaType == MediaType.Series && quality == Quality.Quality.Dvd ? 480 : quality.Resolution;
    }

    /// <summary>Radarr's modifier value of a quality.</summary>
    public static int Modifier(Quality.Quality quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        return (int)quality.Modifier;
    }

    /// <summary>Sonarr's release type: several episode numbers, one, or a season pack (like Sonarr's ParsedEpisodeInfo.ReleaseType).</summary>
    public static CustomFormatReleaseType ReleaseType(ParsedRelease? release)
    {
        if (release is null)
        {
            return CustomFormatReleaseType.Unknown;
        }

        if (release.EpisodeNumbers.Count > 1 || release.AbsoluteEpisodeNumbers.Count > 1)
        {
            return CustomFormatReleaseType.MultiEpisode;
        }

        if (release.EpisodeNumbers.Count == 1 || release.AbsoluteEpisodeNumbers.Count == 1)
        {
            return CustomFormatReleaseType.SingleEpisode;
        }

        return release.FullSeason ? CustomFormatReleaseType.SeasonPack : CustomFormatReleaseType.Unknown;
    }
}
