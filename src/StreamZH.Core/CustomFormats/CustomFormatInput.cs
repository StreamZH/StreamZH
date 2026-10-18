// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;

namespace StreamZH.Core.CustomFormats;

/// <summary>
/// What custom format conditions look at, like Radarr's and Sonarr's CustomFormatInput: a release from an indexer or a
/// file on disk. <see cref="Languages"/> are the effective languages (no tag = the original language, "German.DL" =
/// German plus the original language), as the decision engine sees them.
/// </summary>
public sealed record CustomFormatInput
{
    public required MediaType MediaType { get; init; }

    /// <summary>Release name (for a file: the release it came from, else its file name).</summary>
    public required string ReleaseTitle { get; init; }

    /// <summary>File name on disk (files only); release title conditions also match it, like Radarr.</summary>
    public string? FileName { get; init; }

    public QualityModel Quality { get; init; } = QualityModel.Unknown;

    public IReadOnlyList<Language> Languages { get; init; } = [];

    /// <summary>Original language of the movie or series; language conditions with -2 compare against it.</summary>
    public Language OriginalLanguage { get; init; } = Language.Unknown;

    public string? ReleaseGroup { get; init; }

    public string? Edition { get; init; }

    /// <summary>Year from the release name, else the movie or series year (Radarr's year condition); 0 when unknown.</summary>
    public int Year { get; init; }

    public CustomFormatReleaseType ReleaseType { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Indexer flags as bit flags of the media type's enum; Usenet indexers have none yet (0).</summary>
    public int IndexerFlags { get; init; }

    /// <summary>
    /// A release from an indexer. <paramref name="targetYear"/> is the movie or series year, used when the name has none.
    /// </summary>
    public static CustomFormatInput ForRelease(
        ParsedRelease release, long sizeBytes, MediaType mediaType, Language originalLanguage, int targetYear = 0, int indexerFlags = 0)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(originalLanguage);
        return new CustomFormatInput
        {
            MediaType = mediaType,
            ReleaseTitle = release.ReleaseTitle,
            Quality = release.Quality,
            Languages = LanguageRules.EffectiveLanguages(release, originalLanguage),
            OriginalLanguage = originalLanguage,
            ReleaseGroup = release.ReleaseGroup,
            Edition = release.Edition,
            Year = release.Year > 0 ? release.Year : targetYear,
            ReleaseType = CustomFormatValues.ReleaseType(release),
            SizeBytes = sizeBytes,
            IndexerFlags = indexerFlags,
        };
    }

    /// <summary>
    /// A file on disk (Radarr's "file custom formats"): <paramref name="sceneName"/> is the release it came from when known,
    /// the file's quality, languages, release group and edition are what the import stored or what its name says.
    /// </summary>
    public static CustomFormatInput ForFile(
        MediaType mediaType,
        string fileName,
        string? sceneName,
        QualityModel quality,
        IReadOnlyList<Language> languages,
        Language originalLanguage,
        string? releaseGroup,
        string? edition,
        int year,
        long sizeBytes,
        CustomFormatReleaseType releaseType = CustomFormatReleaseType.Unknown,
        int indexerFlags = 0)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(originalLanguage);
        return new CustomFormatInput
        {
            MediaType = mediaType,
            ReleaseTitle = string.IsNullOrWhiteSpace(sceneName) ? fileName : sceneName,
            FileName = fileName,
            Quality = quality ?? QualityModel.Unknown,
            Languages = LanguageRules.Normalize(languages, originalLanguage),
            OriginalLanguage = originalLanguage,
            ReleaseGroup = releaseGroup,
            Edition = edition,
            Year = year,
            ReleaseType = releaseType,
            SizeBytes = sizeBytes,
            IndexerFlags = indexerFlags,
        };
    }
}
