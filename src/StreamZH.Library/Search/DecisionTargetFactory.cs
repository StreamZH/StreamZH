// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Parsing;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Search;

/// <summary>
/// Builds the <see cref="DecisionTarget"/> for a movie or a set of episodes from the library: the file on disk (quality as the
/// import stored it, else read from the name, and the custom formats it matches), the custom formats of the media type,
/// the blocklisted releases and the "Propers and repacks" setting.
/// </summary>
public sealed class DecisionTargetFactory(
    QualityProfileRepository profiles,
    BlocklistRepository? blocklist = null,
    MediaFileDetailsRepository? fileDetails = null,
    Import.MediaManagementSettings? mediaManagement = null,
    CustomFormatRepository? customFormats = null)
{
    private static readonly IReadOnlySet<string> NoBlocklist = new HashSet<string>();

    public DecisionTarget ForMovie(Movie movie)
    {
        ArgumentNullException.ThrowIfNull(movie);
        var profile = Profile(movie.QualityProfileId);
        var original = Language.FindById(movie.OriginalLanguageId);
        var formats = Formats(MediaType.Movie);
        var existing = movie.File is null ? null : Facts(movie.File, movie.FolderPath);

        return new DecisionTarget(
            MediaType.Movie,
            movie.Title,
            movie.Year,
            movie.Runtime,
            EpisodeCount: 1,
            profile,
            original,
            existing?.Quality,
            existing?.Languages ?? [],
            movie.Monitored,
            blocklist?.TitlesForMovie(movie.Id) ?? NoBlocklist)
        {
            ExistingFileName = movie.File is null ? null : Path.GetFileName(movie.File.RelativePath),
            Propers = Propers,
            CustomFormats = formats,
            ExistingCustomFormats = existing is null ? [] : FileFormats(MediaType.Movie, existing, original, movie.Year, formats),
        };
    }

    /// <summary>
    /// Target for a release that covers <paramref name="episodes"/> (one, several or a whole season).
    /// The existing quality is the lowest one on disk (by quality rank, revision, then custom format score), but only when
    /// every episode has a file: a release that fills a gap is always wanted. Episode runtime is not stored yet, so the size
    /// check is skipped for series.
    /// </summary>
    /// <param name="episodeCount">Overrides the episode count when the episodes are not in the library (release test page).</param>
    public DecisionTarget ForEpisodes(Series series, IReadOnlyList<Episode> episodes, int? episodeCount = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(episodes);
        var profile = Profile(series.QualityProfileId);
        var original = Language.FindById(series.OriginalLanguageId);
        var formats = Formats(MediaType.Series);

        QualityModel? quality = null;
        IReadOnlyList<Language> languages = [];
        IReadOnlyList<CustomFormat> existingFormats = [];
        string? fileName = null;
        if (episodes.Count > 0 && episodes.All(e => e.File is not null))
        {
            var lowest = episodes
                .Select(e => e.File!)
                .DistinctBy(f => f.RelativePath)
                .Select(f =>
                {
                    var facts = Facts(f, series.FolderPath);
                    var matched = FileFormats(MediaType.Series, facts, original, series.Year, formats);
                    return (Facts: facts, Formats: matched, Score: CustomFormatCalculator.Score(profile, matched));
                })
                .MinBy(x => (profile.RankOf(x.Facts.Quality.Quality.Id), x.Facts.Quality.Revision, x.Score));
            quality = lowest.Facts.Quality;
            languages = lowest.Facts.Languages;
            existingFormats = lowest.Formats;
            fileName = Path.GetFileName(lowest.Facts.File.RelativePath);
        }

        return new DecisionTarget(
            MediaType.Series,
            series.Title,
            series.Year,
            RuntimeMinutes: 0,
            episodeCount ?? Math.Max(1, episodes.Count),
            profile,
            original,
            quality,
            languages,
            series.Monitored && (episodes.Count == 0 || episodes.Any(e => e.Monitored)),
            series.Id > 0 ? blocklist?.TitlesForSeries(series.Id) ?? NoBlocklist : NoBlocklist)
        {
            ExistingFileName = fileName,
            Propers = Propers,
            CustomFormats = formats,
            ExistingCustomFormats = existingFormats,
        };
    }

    private ProperHandling Propers => mediaManagement?.Current.PropersAndRepacks ?? ProperHandling.PreferAndUpgrade;

    /// <summary>The custom formats of a media type (none when custom formats are not available, e.g. in some tests).</summary>
    public IReadOnlyList<CustomFormat> Formats(MediaType mediaType) => customFormats?.ForMediaType(mediaType) ?? [];

    /// <summary>
    /// The custom formats a file on disk matches, like Radarr's file custom formats: the release it came from (else its
    /// name), its stored quality, languages, release group and edition (else read from the name), its size and the
    /// movie or series year. <paramref name="formats"/> defaults to all formats of the media type.
    /// </summary>
    public IReadOnlyList<CustomFormat> FileFormats(
        MediaType mediaType, MediaFile? file, string folderPath, Language originalLanguage, int year, IReadOnlyList<CustomFormat>? formats = null)
    {
        ArgumentNullException.ThrowIfNull(originalLanguage);
        if (file is null)
        {
            return [];
        }

        var all = formats ?? Formats(mediaType);
        return all.Count == 0 ? [] : FileFormats(mediaType, Facts(file, folderPath), originalLanguage, year, all);
    }

    /// <summary>The stored details of an imported file (quality survives renaming), else <see cref="Existing"/>.</summary>
    public (QualityModel? Quality, IReadOnlyList<Language> Languages) ExistingFile(MediaFile? file, string folderPath)
    {
        if (file is null)
        {
            return (null, []);
        }

        var facts = Facts(file, folderPath);
        return (facts.Quality, facts.Languages);
    }

    /// <summary>Quality and languages of a file on disk, read from the file name and the item folder name.</summary>
    public static (QualityModel? Quality, IReadOnlyList<Language> Languages) Existing(MediaFile? file, string folderPath)
    {
        if (file is null)
        {
            return (null, []);
        }

        var fileName = Path.GetFileName(file.RelativePath);
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath ?? string.Empty));
        var quality = QualityParser.ParseQuality($"{fileName} {folderName}");
        var languages = LanguageParser.ParseLanguages(fileName)
            .Concat(string.IsNullOrEmpty(folderName) ? [] : LanguageParser.ParseLanguages(folderName))
            .Where(l => l != Language.Unknown)
            .Distinct()
            .ToList();

        return (quality, languages);
    }

    private sealed record FileFacts(MediaFile File, QualityModel Quality, IReadOnlyList<Language> Languages, MediaFileDetails? Stored);

    private FileFacts Facts(MediaFile file, string folderPath)
    {
        if (fileDetails?.Find(Path.Combine(folderPath, file.RelativePath)) is { } stored)
        {
            return new FileFacts(
                file,
                new QualityModel(Quality.FindById(stored.QualityId), new Revision(stored.RevisionVersion, stored.RevisionReal, stored.IsRepack)),
                [.. stored.LanguageIds.Select(Language.FindById).Where(l => l != Language.Unknown)],
                stored);
        }

        var (quality, languages) = Existing(file, folderPath);
        return new FileFacts(file, quality ?? QualityModel.Unknown, languages, null);
    }

    private static IReadOnlyList<CustomFormat> FileFormats(
        MediaType mediaType, FileFacts facts, Language originalLanguage, int year, IReadOnlyList<CustomFormat> formats)
    {
        if (formats.Count == 0)
        {
            return [];
        }

        var fileName = Path.GetFileName(facts.File.RelativePath);
        var parsed = mediaType == MediaType.Series
            ? ReleaseParser.ParseEpisode(fileName) ?? ReleaseParser.Parse(fileName)
            : ReleaseParser.Parse(fileName);
        var input = CustomFormatInput.ForFile(
            mediaType,
            fileName,
            facts.Stored?.SceneName,
            facts.Quality,
            facts.Languages,
            originalLanguage,
            facts.Stored?.ReleaseGroup ?? parsed?.ReleaseGroup,
            facts.Stored?.Edition ?? parsed?.Edition,
            year,
            facts.File.Size,
            mediaType == MediaType.Series ? CustomFormatValues.ReleaseType(parsed) : CustomFormatReleaseType.Unknown);
        return CustomFormatCalculator.Matching(formats, input);
    }

    private QualityProfile Profile(int id) =>
        profiles.Find(id) ?? throw new LibraryException("The quality profile of this item no longer exists.", LibraryError.Invalid);
}
