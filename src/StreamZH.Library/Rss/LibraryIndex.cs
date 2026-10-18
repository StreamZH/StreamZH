// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.Search;
using StreamZH.Library.Titles;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Rss;

/// <summary>
/// Finds the library movie or series an RSS release belongs to without comparing it with every item: lookup by the ids
/// the indexer sends (TMDB, TVDB, IMDb) and by clean title (title, original title and alternative titles), then the same
/// check as a search (<see cref="ReleaseMatching"/>).
/// </summary>
public sealed class LibraryIndex
{
    private readonly Dictionary<string, List<Movie>> _moviesByTitle = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Movie> _moviesByTmdb = [];
    private readonly Dictionary<long, Movie> _moviesByImdb = [];
    private readonly Dictionary<string, List<Series>> _seriesByTitle = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Series> _seriesByTvdb = [];
    private readonly Dictionary<int, Series> _seriesByTmdb = [];
    private readonly Dictionary<long, Series> _seriesByImdb = [];
    private readonly AlternativeTitleLookup _alternativeTitles;

    public LibraryIndex(IEnumerable<Movie> movies, IEnumerable<Series> series, AlternativeTitleLookup? alternativeTitles = null)
    {
        ArgumentNullException.ThrowIfNull(movies);
        ArgumentNullException.ThrowIfNull(series);
        _alternativeTitles = alternativeTitles ?? AlternativeTitleLookup.Empty;

        foreach (var movie in movies)
        {
            foreach (var title in ReleaseMatching.MovieTitles(movie, _alternativeTitles.For(MediaType.Movie, movie.Id)).Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                foreach (var clean in TitleNormalizer.CleanVariants(title))
                {
                    Add(_moviesByTitle, clean, movie);
                }
            }

            _moviesByTmdb.TryAdd(movie.TmdbId, movie);
            if (ImdbNumber(movie.ImdbId) is > 0 and var imdb)
            {
                _moviesByImdb.TryAdd(imdb, movie);
            }
        }

        foreach (var show in series)
        {
            // Every title regardless of its season: the season check happens in ReleaseMatching.
            foreach (var title in ReleaseMatching.SeriesTitles(show, null, _alternativeTitles.For(MediaType.Series, show.Id)).Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                foreach (var clean in TitleNormalizer.CleanVariants(title))
                {
                    Add(_seriesByTitle, clean, show);
                    if (show.Year > 0)
                    {
                        Add(_seriesByTitle, clean + show.Year.ToString(CultureInfo.InvariantCulture), show);
                    }
                }
            }

            if (show.TvdbId is > 0 and var tvdb)
            {
                _seriesByTvdb.TryAdd(tvdb, show);
            }

            _seriesByTmdb.TryAdd(show.TmdbId, show);
            if (ImdbNumber(show.ImdbId) is > 0 and var imdb)
            {
                _seriesByImdb.TryAdd(imdb, show);
            }
        }
    }

    /// <summary>The movie the release is for, or null.</summary>
    public Movie? FindMovie(ParsedRelease parsed, ReleaseInfo? info = null)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var candidates = new List<Movie>();
        AddFound(candidates, _moviesByTmdb, info?.TmdbId ?? 0);
        AddFound(candidates, _moviesByTmdb, parsed.TmdbId ?? 0);
        AddFound(candidates, _moviesByImdb, ImdbNumber(info?.ImdbId));
        AddFound(candidates, _moviesByImdb, ImdbNumber(parsed.ImdbId));
        if (_moviesByTitle.TryGetValue(parsed.CleanTitle, out var byTitle))
        {
            candidates.AddRange(byTitle);
        }

        return candidates
            .Distinct()
            .Where(m => ReleaseMatching.MatchMovie(parsed, m, info, _alternativeTitles.For(MediaType.Movie, m.Id)) == ReleaseMatch.Match)
            .OrderBy(m => parsed.Year > 0 && m.Year > 0 ? Math.Abs(parsed.Year - m.Year) : 0)
            .FirstOrDefault();
    }

    /// <summary>The series the release is for, or null.</summary>
    public Series? FindSeries(ParsedRelease parsed, ReleaseInfo? info = null)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var candidates = new List<Series>();
        AddFound(candidates, _seriesByTvdb, info?.TvdbId ?? 0);
        AddFound(candidates, _seriesByTmdb, info?.TmdbId ?? 0);
        AddFound(candidates, _seriesByImdb, ImdbNumber(info?.ImdbId));
        if (_seriesByTitle.TryGetValue(parsed.CleanTitle, out var byTitle))
        {
            candidates.AddRange(byTitle);
        }

        return candidates.Distinct().FirstOrDefault(s => ReleaseMatching.MatchSeries(parsed, s, info, _alternativeTitles.For(MediaType.Series, s.Id)) == ReleaseMatch.Match);
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T item)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        if (!list.Contains(item))
        {
            list.Add(item);
        }
    }

    private static void AddFound<TKey, T>(List<T> found, Dictionary<TKey, T> map, TKey key)
        where TKey : notnull
    {
        if (map.TryGetValue(key, out var item))
        {
            found.Add(item);
        }
    }

    /// <summary>"tt0111161" and "111161" are the same IMDb id; 0 when there is none.</summary>
    private static long ImdbNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var digits = value.Trim();
        if (digits.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }
}
