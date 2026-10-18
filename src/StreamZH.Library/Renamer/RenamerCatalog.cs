// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Renamer;

/// <summary>
/// TMDB answers for the renamer, kept for 30 minutes: the preview runs again after every change in the rules, and a season
/// of 40 files must not cost 40 requests each time. Failures are not kept.
/// </summary>
public sealed class RenamerTmdbCache(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, object? Value)> _entries = new(StringComparer.Ordinal);

    public async Task<T> GetAsync<T>(string key, Func<Task<T>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        var now = _time.GetUtcNow();
        if (_entries.TryGetValue(key, out var entry) && now - entry.At < Lifetime && entry.Value is T cached)
        {
            return cached;
        }

        var value = await load();
        if (_entries.Count > 5000)
        {
            _entries.Clear();
        }

        _entries[key] = (now, value);
        return value;
    }
}

/// <summary>
/// The library and TMDB as the media rule sees them: series with their episodes, movies, and what the import stored about a
/// file. Library lists are read once per preview.
/// </summary>
public sealed class RenamerCatalog(
    MovieRepository movies,
    SeriesRepository series,
    MediaFileDetailsRepository details,
    RenamerTmdbCache cache,
    ITmdbClient? tmdb = null,
    CustomFormatRepository? customFormats = null,
    AlternativeTitleRepository? alternativeTitles = null)
{
    private readonly Dictionary<MediaType, IReadOnlyList<Core.CustomFormats.CustomFormat>> _formats = [];

    /// <summary>The custom formats of a media type, read once per catalog (for the {Custom Formats} token).</summary>
    public IReadOnlyList<Core.CustomFormats.CustomFormat> Formats(MediaType mediaType)
    {
        if (customFormats is null)
        {
            return [];
        }

        lock (_formats)
        {
            if (!_formats.TryGetValue(mediaType, out var list))
            {
                list = customFormats.ForMediaType(mediaType);
                _formats[mediaType] = list;
            }

            return list;
        }
    }

    private IReadOnlyList<Series>? _series;
    private IReadOnlyList<Movie>? _movies;
    private Titles.AlternativeTitleLookup? _alternativeTitles;
    private readonly Dictionary<int, IReadOnlyList<Episode>> _episodes = [];

    public bool TmdbConfigured => tmdb?.IsConfigured == true;

    /// <summary>Reads the library again (before a run: the lists may have changed since the preview).</summary>
    public void Refresh()
    {
        _series = null;
        _movies = null;
        _alternativeTitles = null;
        _episodes.Clear();
    }

    public IReadOnlyList<Series> Series => _series ??= series.All();

    public IReadOnlyList<Movie> Movies => _movies ??= movies.All();

    public Series? FindSeries(int id) => Series.FirstOrDefault(s => s.Id == id);

    public Movie? FindMovie(int id) => Movies.FirstOrDefault(m => m.Id == id);

    public IReadOnlyList<Episode> Episodes(int seriesId)
    {
        if (!_episodes.TryGetValue(seriesId, out var list))
        {
            list = series.Episodes(seriesId);
            _episodes[seriesId] = list;
        }

        return list;
    }

    public MediaFileDetails? Details(string path) => details.Find(path);

    /// <summary>All alternative titles of the library, read once per catalog.</summary>
    private Titles.AlternativeTitleLookup AlternativeTitles => _alternativeTitles ??= alternativeTitles is null
        ? Titles.AlternativeTitleLookup.Empty
        : new([.. alternativeTitles.All(MediaType.Movie), .. alternativeTitles.All(MediaType.Series)]);

    /// <summary>
    /// The library series with this title (and year, when the name has one): title, original title or an alternative title
    /// ("Die Simpsons" finds "The Simpsons").
    /// </summary>
    public Series? MatchSeries(string title, int year, int? seasonNumber = null) => Match(
        Series, title, year, s => Search.ReleaseMatching.SeriesTitles(s, seasonNumber, AlternativeTitles.For(MediaType.Series, s.Id)), s => s.Title, s => s.Year);

    public Movie? MatchMovie(string title, int year) => Match(
        Movies, title, year, m => Search.ReleaseMatching.MovieTitles(m, AlternativeTitles.For(MediaType.Movie, m.Id)), m => m.Title, m => m.Year);

    private static T? Match<T>(IEnumerable<T> items, string title, int year, Func<T, IEnumerable<string>> names, Func<T, string> name, Func<T, int> itemYear)
        where T : class
    {
        var clean = TitleNormalizer.Clean(title);
        if (clean.Length == 0)
        {
            return null;
        }

        var candidates = items
            .Where(i => names(i).Where(n => !string.IsNullOrWhiteSpace(n)).SelectMany(TitleNormalizer.CleanVariants).Contains(clean)
                || TitleNormalizer.Clean($"{name(i)} {itemYear(i)}") == clean)
            .ToList();
        if (year > 0)
        {
            return candidates.FirstOrDefault(c => itemYear(c) == year) ?? candidates.FirstOrDefault(c => Math.Abs(itemYear(c) - year) <= 1);
        }

        return candidates.FirstOrDefault();
    }

    // ------------------------------------------------------------------ TMDB

    public async Task<Series?> TmdbSeriesAsync(int tmdbId, CancellationToken cancellationToken)
    {
        if (tmdb is not { IsConfigured: true } client)
        {
            return null;
        }

        var found = await cache.GetAsync($"series:{tmdbId}", () => client.GetSeriesAsync(tmdbId, cancellationToken));
        return ToSeries(found);
    }

    /// <summary>The best TMDB result for the title: same clean title (and year), otherwise the first.</summary>
    public async Task<Series?> SearchTmdbSeriesAsync(string title, int year, CancellationToken cancellationToken)
    {
        if (tmdb is not { IsConfigured: true } client || title.Trim().Length == 0)
        {
            return null;
        }

        var results = await cache.GetAsync(
            $"series-search:{title.Trim().ToLowerInvariant()}:{year}",
            () => client.SearchSeriesAsync(title.Trim(), year > 0 ? year : null, cancellationToken));
        if (results.Count == 0 && year > 0)
        {
            results = await cache.GetAsync(
                $"series-search:{title.Trim().ToLowerInvariant()}:0",
                () => client.SearchSeriesAsync(title.Trim(), null, cancellationToken));
        }

        var best = Best(results, title, year);
        return best is null ? null : await TmdbSeriesAsync(best.TmdbId, cancellationToken);
    }

    public async Task<IReadOnlyList<Episode>> TmdbSeasonAsync(int tmdbId, int season, CancellationToken cancellationToken)
    {
        if (tmdb is not { IsConfigured: true } client)
        {
            return [];
        }

        var episodes = await cache.GetAsync($"season:{tmdbId}:{season}", () => client.GetSeasonAsync(tmdbId, season, cancellationToken));
        return [.. episodes.Select(e => new Episode { SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber, Title = e.Title, AirDate = e.AirDate })];
    }

    /// <summary>All seasons (for air dates and absolute numbers).</summary>
    public async Task<IReadOnlyList<Episode>> TmdbAllEpisodesAsync(int tmdbId, CancellationToken cancellationToken)
    {
        if (tmdb is not { IsConfigured: true } client)
        {
            return [];
        }

        var show = await cache.GetAsync($"series:{tmdbId}", () => client.GetSeriesAsync(tmdbId, cancellationToken));
        var all = new List<Episode>();
        foreach (var season in show.SeasonNumbers.Order().Take(100))
        {
            all.AddRange(await TmdbSeasonAsync(tmdbId, season, cancellationToken));
        }

        return all;
    }

    public async Task<Movie?> TmdbMovieAsync(int tmdbId, CancellationToken cancellationToken)
    {
        if (tmdb is not { IsConfigured: true } client)
        {
            return null;
        }

        var m = await cache.GetAsync($"movie:{tmdbId}", () => client.GetMovieAsync(tmdbId, cancellationToken));
        return new Movie
        {
            TmdbId = m.TmdbId,
            ImdbId = m.ImdbId,
            Title = m.Title,
            OriginalTitle = m.OriginalTitle,
            Year = m.Year,
            Genres = m.Genres,
            OriginalLanguageId = Language.FromIsoCode(m.OriginalLanguage).Id,
        };
    }

    public async Task<Movie?> SearchTmdbMovieAsync(string title, int year, CancellationToken cancellationToken)
    {
        if (tmdb is not { IsConfigured: true } client || title.Trim().Length == 0)
        {
            return null;
        }

        var results = await cache.GetAsync(
            $"movie-search:{title.Trim().ToLowerInvariant()}:{year}",
            () => client.SearchMoviesAsync(title.Trim(), year > 0 ? year : null, cancellationToken));
        var best = Best(results, title, year);
        return best is null ? null : await TmdbMovieAsync(best.TmdbId, cancellationToken);
    }

    private static TmdbSearchResult? Best(IReadOnlyList<TmdbSearchResult> results, string title, int year)
    {
        var clean = TitleNormalizer.Clean(title);
        bool Same(TmdbSearchResult r) => TitleNormalizer.Clean(r.Title) == clean || TitleNormalizer.Clean(r.OriginalTitle) == clean;
        return results.FirstOrDefault(r => Same(r) && (year == 0 || r.Year == year))
            ?? results.FirstOrDefault(Same)
            ?? results.FirstOrDefault(r => year > 0 && r.Year == year)
            ?? results.FirstOrDefault();
    }

    private static Series ToSeries(TmdbSeries s) => new()
    {
        TmdbId = s.TmdbId,
        TvdbId = s.TvdbId,
        ImdbId = s.ImdbId,
        Title = s.Title,
        OriginalTitle = s.OriginalTitle,
        Year = s.Year,
        Genres = s.Genres,
        Network = s.Network,
        OriginalLanguageId = Language.FromIsoCode(s.OriginalLanguage).Id,
    };
}
