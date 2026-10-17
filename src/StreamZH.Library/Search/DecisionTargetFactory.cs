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

/// <summary>Builds the <see cref="DecisionTarget"/> for a movie or a set of episodes from the library.</summary>
public sealed class DecisionTargetFactory(QualityProfileRepository profiles)
{
    private static readonly IReadOnlySet<string> NoBlocklist = new HashSet<string>();

    public DecisionTarget ForMovie(Movie movie)
    {
        ArgumentNullException.ThrowIfNull(movie);
        var profile = Profile(movie.QualityProfileId);
        var (quality, languages) = Existing(movie.File, movie.FolderPath);

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
            NoBlocklist)
        {
            ExistingFileName = movie.File is null ? null : Path.GetFileName(movie.File.RelativePath),
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
                .Select(e => (Existing: Existing(e.File, series.FolderPath), Name: Path.GetFileName(e.File!.RelativePath)))
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
            NoBlocklist)
        {
            ExistingFileName = fileName,
        };
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
