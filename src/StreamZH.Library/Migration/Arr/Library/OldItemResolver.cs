// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Data;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>
/// Finds the StreamZH movie, series or episode of an old app's id for the history and blocklist sections: the mapping
/// the movies/series section of the same run recorded, else the TMDB id (Radarr, Sonarr v4), TVDB or IMDb id; episodes
/// are matched like the series section does (read from Sonarr once per series, only when needed).
/// </summary>
public sealed class OldItemResolver
{
    private readonly MigrationContext _context;
    private readonly ArrLibraryReader _reader;
    private readonly SeriesRepository _series;
    private readonly Dictionary<int, ArrMovie> _radarrMovies;
    private readonly Dictionary<int, ArrSeries> _sonarrSeries;
    private readonly IReadOnlyDictionary<int, int> _moviesByTmdb;
    private readonly Dictionary<int, int> _seriesByTmdb;
    private readonly Dictionary<int, int> _seriesByTvdb;
    private readonly Dictionary<string, int> _seriesByImdb;
    private readonly HashSet<int> _matchedSeries = [];

    private OldItemResolver(
        MigrationContext context, ArrLibraryReader reader, MovieRepository movies, SeriesRepository series, IReadOnlyList<ArrMovie> radarrMovies, IReadOnlyList<ArrSeries> sonarrSeries)
    {
        _context = context;
        _reader = reader;
        _series = series;
        _radarrMovies = radarrMovies.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        _sonarrSeries = sonarrSeries.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        _moviesByTmdb = movies.TmdbIdToId();
        var all = series.All();
        _seriesByTmdb = all.GroupBy(s => s.TmdbId).ToDictionary(g => g.Key, g => g.First().Id);
        _seriesByTvdb = all.Where(s => s.TvdbId is > 0).GroupBy(s => s.TvdbId!.Value).ToDictionary(g => g.Key, g => g.First().Id);
        _seriesByImdb = all.Where(s => !string.IsNullOrEmpty(s.ImdbId)).GroupBy(s => s.ImdbId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<OldItemResolver> CreateAsync(MigrationContext context, ArrLibraryReader reader, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reader);
        var radarr = context.Source == MigrationSources.Radarr ? await reader.MoviesAsync(context, cancellationToken) : [];
        var sonarr = context.Source == MigrationSources.Sonarr ? await reader.SeriesAsync(context, cancellationToken) : [];
        return new OldItemResolver(context, reader, services.GetRequiredService<MovieRepository>(), services.GetRequiredService<SeriesRepository>(), radarr, sonarr);
    }

    public string? MovieTitle(int radarrMovieId) => _radarrMovies.TryGetValue(radarrMovieId, out var m) ? Title(m.Title, m.Year) : null;

    public string? SeriesTitle(int sonarrSeriesId) => _sonarrSeries.TryGetValue(sonarrSeriesId, out var s) ? Title(s.Title, s.Year) : null;

    /// <summary>The StreamZH movie of a Radarr movie id, or null.</summary>
    public int? Movie(int radarrMovieId)
    {
        if (_context.Ids.Get("movie", radarrMovieId) is { } mapped)
        {
            return mapped;
        }

        return _radarrMovies.TryGetValue(radarrMovieId, out var movie) && movie.TmdbId > 0 && _moviesByTmdb.TryGetValue(movie.TmdbId, out var id) ? id : null;
    }

    /// <summary>True when the movies section can take the Radarr movie over (it has a TMDB id).</summary>
    public bool MovieCanBeTakenOver(int radarrMovieId) => _radarrMovies.TryGetValue(radarrMovieId, out var movie) && movie.TmdbId > 0;

    /// <summary>The StreamZH series of a Sonarr series id, or null.</summary>
    public int? Series(int sonarrSeriesId)
    {
        if (_context.Ids.Get("series", sonarrSeriesId) is { } mapped)
        {
            return mapped;
        }

        if (!_sonarrSeries.TryGetValue(sonarrSeriesId, out var series))
        {
            return null;
        }

        if (series.TmdbId > 0 && _seriesByTmdb.TryGetValue(series.TmdbId, out var byTmdb))
        {
            return byTmdb;
        }

        if (series.TvdbId > 0 && _seriesByTvdb.TryGetValue(series.TvdbId, out var byTvdb))
        {
            return byTvdb;
        }

        if (series.TvdbId > 0 && _reader.TmdbIdsByTvdbId.TryGetValue(series.TvdbId, out var found) && found is { } tmdb && _seriesByTmdb.TryGetValue(tmdb, out var byFound))
        {
            return byFound;
        }

        return !string.IsNullOrEmpty(series.ImdbId) && _seriesByImdb.TryGetValue(series.ImdbId, out var byImdb) ? byImdb : null;
    }

    /// <summary>True when the Sonarr series is known (the series section can take it over when TMDB has it).</summary>
    public bool SeriesKnown(int sonarrSeriesId) => _sonarrSeries.ContainsKey(sonarrSeriesId);

    /// <summary>The StreamZH episode of a Sonarr episode, matched once per series; null when it has none.</summary>
    public async Task<int?> EpisodeAsync(int sonarrSeriesId, int sonarrEpisodeId, CancellationToken cancellationToken)
    {
        if (_context.Ids.Get("episode", sonarrEpisodeId) is { } mapped)
        {
            return mapped;
        }

        if (!_matchedSeries.Add(sonarrSeriesId) || Series(sonarrSeriesId) is not { } seriesId || _series.Find(seriesId) is not { } series)
        {
            return null;
        }

        var sonarr = await _reader.EpisodesAsync(_context, sonarrSeriesId, cancellationToken);
        foreach (var (oldId, episode) in EpisodeMatcher.Match(series.SeriesType, sonarr, _series.Episodes(seriesId)))
        {
            _context.Ids.Set("episode", oldId, episode.Id);
        }

        return _context.Ids.Get("episode", sonarrEpisodeId);
    }

    private static string Title(string? title, int year) =>
        year > 0 ? $"{title} ({year.ToString(System.Globalization.CultureInfo.InvariantCulture)})" : title ?? string.Empty;
}
