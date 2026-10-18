// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Parsing;

namespace StreamZH.Library.Search;

/// <summary>
/// Builds the <see cref="DecisionTarget"/> for a movie or a set of episodes from the library: the file on disk (quality as the
/// import stored it, else read from the name), the blocklisted releases and the "Propers and repacks" setting.
/// </summary>
public sealed class DecisionTargetFactory(
    QualityProfileRepository profiles,
    BlocklistRepository? blocklist = null,
    MediaFileDetailsRepository? fileDetails = null,
    Import.MediaManagementSettings? mediaManagement = null)
{
    private static readonly IReadOnlySet<string> NoBlocklist = new HashSet<string>();

    public DecisionTarget ForMovie(Movie movie)
    {
        ArgumentNullException.ThrowIfNull(movie);
        var profile = Profile(movie.QualityProfileId);
        var (quality, languages) = ExistingFile(movie.File, movie.FolderPath);

        return new DecisionTarget(
            MediaType.Movie,
            movie.Title,
            movie.Year,
            movie.Runtime,
            EpisodeCount: 1,
            profile,
            Language.FindById(movie.OriginalLanguageId),
            quality,
            languages,
            movie.Monitored,
            blocklist?.TitlesForMovie(movie.Id) ?? NoBlocklist)
        {
            ExistingFileName = movie.File is null ? null : Path.GetFileName(movie.File.RelativePath),
            Propers = Propers,
        };
    }

    /// <summary>
    /// Target for a release that covers <paramref name="episodes"/> (one, several or a whole season).
    /// The existing quality is the lowest one on disk, but only when every episode has a file: a release that fills
    /// a gap is always wanted. Episode runtime is not stored yet, so the size check is skipped for series.
    /// </summary>
    /// <param name="episodeCount">Overrides the episode count when the episodes are not in the library (release test page).</param>
    public DecisionTarget ForEpisodes(Series series, IReadOnlyList<Episode> episodes, int? episodeCount = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(episodes);
        var profile = Profile(series.QualityProfileId);

        QualityModel? quality = null;
        IReadOnlyList<Language> languages = [];
        string? fileName = null;
        if (episodes.Count > 0 && episodes.All(e => e.File is not null))
        {
            (quality, languages, fileName) = episodes
                .Select(e => (Existing: ExistingFile(e.File, series.FolderPath), Name: Path.GetFileName(e.File!.RelativePath)))
                .Select(x => (x.Existing.Quality, x.Existing.Languages, x.Name))
                .MinBy(x => (profile.RankOf(x.Quality!.Quality.Id), x.Quality.Revision));
        }

        return new DecisionTarget(
            MediaType.Series,
            series.Title,
            series.Year,
            RuntimeMinutes: 0,
            episodeCount ?? Math.Max(1, episodes.Count),
            profile,
            Language.FindById(series.OriginalLanguageId),
            quality,
            languages,
            series.Monitored && (episodes.Count == 0 || episodes.Any(e => e.Monitored)),
            series.Id > 0 ? blocklist?.TitlesForSeries(series.Id) ?? NoBlocklist : NoBlocklist)
        {
            ExistingFileName = fileName,
            Propers = Propers,
        };
    }

    private ProperHandling Propers => mediaManagement?.Current.PropersAndRepacks ?? ProperHandling.PreferAndUpgrade;

    /// <summary>The stored details of an imported file (quality survives renaming), else <see cref="Existing"/>.</summary>
    public (QualityModel? Quality, IReadOnlyList<Language> Languages) ExistingFile(MediaFile? file, string folderPath)
    {
        if (file is not null && fileDetails?.Find(Path.Combine(folderPath, file.RelativePath)) is { } stored)
        {
            return (
                new QualityModel(Quality.FindById(stored.QualityId), new Revision(stored.RevisionVersion, stored.RevisionReal, stored.IsRepack)),
                [.. stored.LanguageIds.Select(Language.FindById).Where(l => l != Language.Unknown)]);
        }

        return Existing(file, folderPath);
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

    private QualityProfile Profile(int id) =>
        profiles.Find(id) ?? throw new LibraryException("The quality profile of this item no longer exists.", LibraryError.Invalid);
}
