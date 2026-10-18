// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Library.Wanted;

/// <summary>
/// Wanted → Missing and Cutoff unmet, like Radarr's and Sonarr's Wanted pages. Missing: movies without a file (available
/// by the minimum availability of Settings → Indexers) and aired episodes without a file. Cutoff unmet: files the decision
/// engine would still upgrade (<see cref="CutoffRules"/>, with the quality the import stored for the file). The lists are
/// built in memory: a home library is a few thousand items at most.
/// </summary>
public sealed class WantedService(
    MovieRepository movies,
    SeriesRepository series,
    QualityProfileRepository profiles,
    WantedQueries queries,
    LastSearchRepository lastSearches,
    DecisionTargetFactory targets,
    QueueSnapshotFactory queue,
    IEventBus events,
    IndexerOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public WantedPage Missing(WantedQuery query) => PageOf(query, MissingMovies, MissingEpisodes);

    public WantedPage Cutoff(WantedQuery query) => PageOf(query, CutoffMovies, CutoffEpisodes);

    public WantedSummary Summary()
    {
        var defaults = new WantedQuery(WantedItemType.Movies);
        var context = Context();
        return new WantedSummary(
            MissingMovies(defaults, context).Count,
            MissingEpisodes(defaults, context).Count,
            CutoffMovies(defaults, context).Count,
            CutoffEpisodes(defaults, context).Count);
    }

    /// <summary>Ids for "Search all": the default filters (monitored, available), like Radarr/Sonarr.</summary>
    public IReadOnlyList<WantedRecord> AllMissing(WantedItemType type)
    {
        var query = new WantedQuery(type);
        return type == WantedItemType.Movies ? MissingMovies(query, Context()) : MissingEpisodes(query, Context());
    }

    public IReadOnlyList<WantedRecord> AllCutoffUnmet(WantedItemType type)
    {
        var query = new WantedQuery(type);
        return type == WantedItemType.Movies ? CutoffMovies(query, Context()) : CutoffEpisodes(query, Context());
    }

    /// <summary>"Unmonitor selected" (or monitor again). Unknown ids are ignored; returns how many items changed.</summary>
    public async Task<int> SetMonitoredAsync(IReadOnlyCollection<int> movieIds, IReadOnlyCollection<int> episodeIds, bool monitored, CancellationToken cancellationToken = default)
    {
        var changed = 0;
        foreach (var id in movieIds.Distinct())
        {
            if (movies.Find(id) is { } movie && movie.Monitored != monitored)
            {
                movie.Monitored = monitored;
                movies.Update(movie);
                changed++;
                await events.PublishAsync(new LibraryChanged(MediaType.Movie, id), cancellationToken);
            }
        }

        var wanted = episodeIds.ToHashSet();
        if (wanted.Count > 0)
        {
            var touched = new HashSet<int>();
            foreach (var episode in queries.AllEpisodes().Where(e => wanted.Contains(e.Id) && e.Monitored != monitored))
            {
                series.SetEpisodeMonitored(episode.Id, monitored);
                touched.Add(episode.SeriesId);
                changed++;
            }

            foreach (var seriesId in touched)
            {
                await events.PublishAsync(new LibraryChanged(MediaType.Series, seriesId), cancellationToken);
            }
        }

        return changed;
    }

    private sealed record ListContext(
        DateTimeOffset Now,
        string Today,
        IReadOnlyDictionary<int, QualityProfile> Profiles,
        QueueSnapshot Queue,
        IReadOnlyDictionary<int, DateTime> MovieSearches,
        IReadOnlyDictionary<int, DateTime> EpisodeSearches,
        IReadOnlyList<CustomFormat> MovieFormats,
        IReadOnlyList<CustomFormat> SeriesFormats);

    /// <summary>
    /// The file's custom format score, or null when the profile has no custom format cutoff to reach (no score, or no
    /// formats at all), so the formats are only matched where they can matter.
    /// </summary>
    private static int? FormatScore(QualityProfile profile, IReadOnlyList<CustomFormat> formats, Func<IReadOnlyList<CustomFormat>> fileFormats)
    {
        if (!profile.UpgradeAllowed || (formats.Count == 0 && profile.CutoffFormatScore <= 0))
        {
            return null;
        }

        return CustomFormatCalculator.Score(profile, formats.Count == 0 ? [] : fileFormats());
    }

    private ListContext Context()
    {
        var now = _time.GetUtcNow();
        return new ListContext(
            now,
            now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            profiles.All().ToDictionary(p => p.Id),
            queue.Take(),
            lastSearches.Movies(),
            lastSearches.Episodes(),
            targets.Formats(MediaType.Movie),
            targets.Formats(MediaType.Series));
    }

    private WantedPage PageOf(
        WantedQuery query,
        Func<WantedQuery, ListContext, IReadOnlyList<WantedRecord>> movieList,
        Func<WantedQuery, ListContext, IReadOnlyList<WantedRecord>> episodeList)
    {
        var context = Context();
        var movieRecords = movieList(query, context);
        var episodeRecords = episodeList(query, context);
        var records = query.Type == WantedItemType.Movies ? movieRecords : episodeRecords;

        var pageSize = Math.Clamp(query.PageSize, 1, WantedQuery.MaxPageSize);
        var pages = Math.Max(1, (int)Math.Ceiling(records.Count / (double)pageSize));
        var page = Math.Clamp(query.Page, 1, pages);
        var sorted = Sort(records, query.Sort, query.Descending);

        return new WantedPage(
            page,
            pageSize,
            records.Count,
            query.Sort,
            query.Descending ? "descending" : "ascending",
            new WantedCounts(movieRecords.Count, episodeRecords.Count),
            [.. sorted.Skip((page - 1) * pageSize).Take(pageSize)]);
    }

    private static IEnumerable<WantedRecord> Sort(IReadOnlyList<WantedRecord> records, WantedSortKey key, bool descending)
    {
        // Title, then season and episode, as the tie-breaker for every column.
        IOrderedEnumerable<WantedRecord> ordered = key switch
        {
            WantedSortKey.Date => Order(records, r => r.Date ?? string.Empty, descending),
            WantedSortKey.LastSearched => Order(records, r => r.LastSearched ?? DateTime.MinValue, descending),
            WantedSortKey.Status => Order(records, r => $"{r.Queue ?? "~"}|{r.Status}", descending),
            WantedSortKey.Profile => Order(records, r => r.Profile, descending, StringComparer.OrdinalIgnoreCase),
            WantedSortKey.Quality => Order(records, r => r.QualityRank, descending),
            _ => Order(records, r => r.SortTitle, descending, StringComparer.Ordinal),
        };

        return key == WantedSortKey.Title
            ? ordered.ThenBy(r => r.SeasonNumber ?? 0).ThenBy(r => r.EpisodeNumber ?? 0)
            : ordered.ThenBy(r => r.SortTitle, StringComparer.Ordinal).ThenBy(r => r.SeasonNumber ?? 0).ThenBy(r => r.EpisodeNumber ?? 0);
    }

    private static IOrderedEnumerable<WantedRecord> Order<T>(IEnumerable<WantedRecord> records, Func<WantedRecord, T> key, bool descending, IComparer<T>? comparer = null) =>
        descending ? records.OrderByDescending(key, comparer) : records.OrderBy(key, comparer);

    private IReadOnlyList<WantedRecord> MissingMovies(WantedQuery query, ListContext context)
    {
        var list = new List<WantedRecord>();
        foreach (var movie in movies.All())
        {
            if (movie.File is not null || (query.MonitoredOnly && !movie.Monitored) || !context.Profiles.TryGetValue(movie.QualityProfileId, out var profile))
            {
                continue;
            }

            var availableFrom = AvailableFrom(movie, context.Now);
            if (query.AvailableOnly && availableFrom is not null)
            {
                continue;
            }

            list.Add(MovieRecord(movie, profile, context, availableFrom is null ? WantedStatus.Missing : WantedStatus.NotAvailable, availableFrom, null, [], []));
        }

        return list;
    }

    private IReadOnlyList<WantedRecord> CutoffMovies(WantedQuery query, ListContext context)
    {
        var list = new List<WantedRecord>();
        foreach (var movie in movies.All())
        {
            if (movie.File is null || (query.MonitoredOnly && !movie.Monitored) || !context.Profiles.TryGetValue(movie.QualityProfileId, out var profile))
            {
                continue;
            }

            var (quality, languages) = targets.ExistingFile(movie.File, movie.FolderPath);
            quality ??= QualityModel.Unknown;
            var original = Language.FindById(movie.OriginalLanguageId);
            var score = FormatScore(profile, context.MovieFormats, () => targets.FileFormats(MediaType.Movie, movie.File, movie.FolderPath, original, movie.Year, context.MovieFormats));
            var reasons = CutoffRules.Unmet(profile, quality, languages, original, score);
            if (reasons.Count > 0)
            {
                list.Add(MovieRecord(movie, profile, context, WantedStatus.CutoffUnmet, null, quality, languages, reasons));
            }
        }

        return list;
    }

    private IReadOnlyList<WantedRecord> MissingEpisodes(WantedQuery query, ListContext context)
    {
        var shows = series.All().ToDictionary(s => s.Id);
        var list = new List<WantedRecord>();
        foreach (var episode in queries.AllEpisodes())
        {
            if (episode.File is not null || !shows.TryGetValue(episode.SeriesId, out var show) || !context.Profiles.TryGetValue(show.QualityProfileId, out var profile))
            {
                continue;
            }

            // Specials only when they are monitored on their own (StreamZH adds them unmonitored, like Sonarr).
            var monitored = show.Monitored && episode.Monitored;
            if ((query.MonitoredOnly || episode.SeasonNumber == 0) && !monitored)
            {
                continue;
            }

            var aired = episode.AirDate is { } date && string.CompareOrdinal(date, context.Today) <= 0;
            if (query.AvailableOnly && !aired)
            {
                continue;
            }

            list.Add(EpisodeRecord(show, episode, profile, context, aired ? WantedStatus.Missing : WantedStatus.Unaired, null, [], []));
        }

        return list;
    }

    private IReadOnlyList<WantedRecord> CutoffEpisodes(WantedQuery query, ListContext context)
    {
        var shows = series.All().ToDictionary(s => s.Id);
        var list = new List<WantedRecord>();
        foreach (var episode in queries.AllEpisodes())
        {
            if (episode.File is null || !shows.TryGetValue(episode.SeriesId, out var show) || !context.Profiles.TryGetValue(show.QualityProfileId, out var profile)
                || (query.MonitoredOnly && !(show.Monitored && episode.Monitored)))
            {
                continue;
            }

            var (quality, languages) = targets.ExistingFile(episode.File, show.FolderPath);
            quality ??= QualityModel.Unknown;
            var original = Language.FindById(show.OriginalLanguageId);
            var score = FormatScore(profile, context.SeriesFormats, () => targets.FileFormats(MediaType.Series, episode.File, show.FolderPath, original, show.Year, context.SeriesFormats));
            var reasons = CutoffRules.Unmet(profile, quality, languages, original, score);
            if (reasons.Count > 0)
            {
                list.Add(EpisodeRecord(show, episode, profile, context, WantedStatus.CutoffUnmet, quality, languages, reasons));
            }
        }

        return list;
    }

    /// <summary>Null = available now; otherwise the day it becomes available (null date when not known).</summary>
    private string? AvailableFrom(Movie movie, DateTimeOffset now)
    {
        // Null means "available", so no ?? here.
        var from = options is not null
            ? options.AvailableFrom(movie, now)
            : IndexerOptions.AvailableFrom(movie, IndexerOptions.Defaults.MinimumAvailability, 0, now);
        return from switch
        {
            null => null,
            { } d when d == DateTimeOffset.MaxValue => string.Empty,
            { } d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    private static WantedRecord MovieRecord(
        Movie movie, QualityProfile profile, ListContext context, string status, string? availableFrom,
        QualityModel? quality, IReadOnlyList<Language> languages, IReadOnlyList<string> reasons) =>
        new(
            "movie",
            movie.Id,
            movie.Id,
            null,
            null,
            movie.Title,
            movie.Year,
            null,
            null,
            null,
            movie.InCinemas,
            movie.InCinemas,
            movie.DigitalRelease,
            movie.PhysicalRelease,
            availableFrom is { Length: > 0 } ? availableFrom : null,
            movie.Monitored,
            status,
            context.Queue.Movie(movie.Id),
            profile.Id,
            profile.Name,
            quality?.ToString(),
            CutoffRules.CutoffQuality(profile).Name,
            WantedLanguage(profile),
            FileLanguages(quality, languages, movie.OriginalLanguageId),
            reasons,
            context.MovieSearches.TryGetValue(movie.Id, out var searched) ? searched : null,
            movie.PosterPath)
        {
            SortTitle = string.IsNullOrEmpty(movie.SortTitle) ? SortTitles.For(movie.Title) : movie.SortTitle,
            QualityRank = quality is null ? -1 : profile.RankOf(quality.Quality.Id),
        };

    private static WantedRecord EpisodeRecord(
        Series show, Episode episode, QualityProfile profile, ListContext context, string status,
        QualityModel? quality, IReadOnlyList<Language> languages, IReadOnlyList<string> reasons) =>
        new(
            "episode",
            episode.Id,
            null,
            show.Id,
            episode.Id,
            show.Title,
            show.Year,
            episode.SeasonNumber,
            episode.EpisodeNumber,
            episode.Title,
            episode.AirDate,
            null,
            null,
            null,
            null,
            show.Monitored && episode.Monitored,
            status,
            context.Queue.Episode(episode.Id),
            profile.Id,
            profile.Name,
            quality?.ToString(),
            CutoffRules.CutoffQuality(profile).Name,
            WantedLanguage(profile),
            FileLanguages(quality, languages, show.OriginalLanguageId),
            reasons,
            context.EpisodeSearches.TryGetValue(episode.Id, out var searched) ? searched : null,
            show.PosterPath)
        {
            SortTitle = string.IsNullOrEmpty(show.SortTitle) ? SortTitles.For(show.Title) : show.SortTitle,
            QualityRank = quality is null ? -1 : profile.RankOf(quality.Quality.Id),
        };

    /// <summary>Languages of the file as the decision engine sees them ("German.DL" = German + the original language).</summary>
    private static IReadOnlyList<string> FileLanguages(QualityModel? quality, IReadOnlyList<Language> languages, int originalLanguageId) =>
        quality is null ? [] : [.. Core.Decisions.LanguageRules.Normalize(languages, Language.FindById(originalLanguageId)).Select(l => l.Name)];

    private static string? WantedLanguage(QualityProfile profile)
    {
        var language = Language.FindById(profile.LanguageId);
        return Core.Decisions.LanguageRules.IsReal(language) ? language.Name : null;
    }
}
