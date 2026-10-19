// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Requests;

/// <summary>Library and request state of a title for Discover badges.</summary>
/// <param name="Available">Movie: has a file. Series: every aired episode has a file.</param>
/// <param name="PartiallyAvailable">Series: some aired episodes have files.</param>
/// <param name="ItemId">The movie or series id in the library.</param>
/// <param name="Requested">Status of the oldest open request of the title, if any.</param>
public sealed record TitleState(bool InLibrary, bool Available, bool PartiallyAvailable, int? ItemId, RequestStatus? Requested, int? RequestId)
{
    public static TitleState None { get; } = new(false, false, false, null, null, null);
}

/// <summary>One movie or show of a Discover row or search, with TMDB image paths.</summary>
public sealed record DiscoverEntry(
    MediaType MediaType,
    int TmdbId,
    string Title,
    int? Year,
    string? Overview,
    string? PosterPath,
    string? BackdropPath,
    double? Rating,
    string? ReleaseDate,
    TitleState State);

public sealed record DiscoverResult(int Page, int TotalPages, IReadOnlyList<DiscoverEntry> Results);

/// <summary>A season on the detail page (series only, specials left out).</summary>
/// <param name="Requested">Status of an open request that covers the season.</param>
public sealed record DiscoverSeason(
    int SeasonNumber,
    string? Name,
    int EpisodeCount,
    string? AirDate,
    string? PosterPath,
    bool Available,
    bool PartiallyAvailable,
    RequestStatus? Requested,
    int? RequestId);

/// <summary>Details of a title for the request dialog: the entry, more TMDB data and the seasons with their state.</summary>
public sealed record DiscoverDetails(
    DiscoverEntry Entry,
    string? Tagline,
    IReadOnlyList<string> Genres,
    int? Runtime,
    string? Status,
    string? Certification,
    IReadOnlyList<string> Studios,
    IReadOnlyList<TmdbCastMember> Cast,
    IReadOnlyList<DiscoverSeason> Seasons);

/// <summary>Short-lived cache of TMDB answers for Discover (pages change slowly; TMDB has rate limits).</summary>
public sealed class DiscoverCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxEntries = 500;

    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, object Value)> _entries = new(StringComparer.Ordinal);

    public async Task<T> GetAsync<T>(string key, Func<Task<T>> load)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(load);
        var now = time.GetUtcNow();
        if (_entries.TryGetValue(key, out var entry) && entry.Until > now && entry.Value is T cached)
        {
            return cached;
        }

        var value = await load();
        if (_entries.Count >= MaxEntries)
        {
            foreach (var old in _entries.Where(e => e.Value.Until <= now).Select(e => e.Key).ToList())
            {
                _entries.TryRemove(old, out _);
            }

            if (_entries.Count >= MaxEntries)
            {
                _entries.Clear();
            }
        }

        _entries[key] = (now + Lifetime, value);
        return value;
    }

    public void Clear() => _entries.Clear();
}

/// <summary>
/// Discover (0.10): TMDB charts and search with the library and request state of every title (one batched lookup per
/// page), and the details of a title with its seasons. TMDB answers are cached for ten minutes per language.
/// </summary>
public sealed class DiscoverService(
    ITmdbDiscoverClient tmdb,
    ITmdbSettings tmdbSettings,
    DiscoverCache cache,
    DiscoverLookup lookup,
    SeriesRepository series,
    RequestRepository requests,
    TimeProvider time)
{
    public const int MaxQueryLength = 200;

    private string Today => time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<DiscoverResult> ChartAsync(MediaType mediaType, TmdbChart chart, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, TmdbClient.MaxPage);
        var kind = Kind(mediaType);
        var result = await cache.GetAsync(
            $"{tmdbSettings.Language}|chart|{kind}|{chart}|{page.ToString(CultureInfo.InvariantCulture)}",
            () => tmdb.GetChartAsync(kind, chart, page, cancellationToken));
        return WithState(result);
    }

    /// <param name="mediaType">Null: movies and shows mixed.</param>
    public async Task<DiscoverResult> SearchAsync(string query, int page, MediaType? mediaType = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var text = query.Trim();
        if (text.Length > MaxQueryLength)
        {
            text = text[..MaxQueryLength];
        }

        page = Math.Clamp(page, 1, TmdbClient.MaxPage);
        TmdbMediaKind? kind = mediaType is { } type ? Kind(type) : null;
        var result = await cache.GetAsync(
            $"{tmdbSettings.Language}|search|{kind?.ToString() ?? "multi"}|{page.ToString(CultureInfo.InvariantCulture)}|{text.ToLowerInvariant()}",
            () => tmdb.SearchAsync(kind, text, page, cancellationToken));
        return WithState(result);
    }

    /// <summary>Details of a title; TMDB's "not found" surfaces as <see cref="TmdbException"/> with status 404.</summary>
    public async Task<DiscoverDetails> DetailsAsync(MediaType mediaType, int tmdbId, string certificationCountry, CancellationToken cancellationToken = default)
    {
        if (mediaType == MediaType.Movie)
        {
            var movie = await cache.GetAsync(
                $"{tmdbSettings.Language}|movie|{tmdbId.ToString(CultureInfo.InvariantCulture)}|{certificationCountry}",
                () => tmdb.GetMovieDetailsAsync(tmdbId, certificationCountry, cancellationToken));
            var m = movie.Movie;
            var entry = Entry(new TmdbListEntry(TmdbMediaKind.Movie, m.TmdbId, m.Title, m.Year, movie.ReleaseDate, m.PosterPath, movie.Rating, movie.Votes, m.OriginalLanguage, m.Overview, m.BackdropPath));
            return new DiscoverDetails(
                entry with { State = States([entry]).GetValueOrDefault((MediaType.Movie, m.TmdbId), TitleState.None) },
                movie.Tagline,
                m.Genres,
                m.Runtime > 0 ? m.Runtime : null,
                Blank(m.Status),
                movie.Certification,
                movie.Studios,
                [.. movie.Cast.Take(12)],
                []);
        }

        var show = await cache.GetAsync(
            $"{tmdbSettings.Language}|tv|{tmdbId.ToString(CultureInfo.InvariantCulture)}|{certificationCountry}",
            () => tmdb.GetSeriesDetailsAsync(tmdbId, certificationCountry, cancellationToken));
        var s = show.Series;
        var seriesEntry = Entry(new TmdbListEntry(TmdbMediaKind.Series, s.TmdbId, s.Title, s.Year, show.FirstAirDate, s.PosterPath, show.Rating, show.Votes, s.OriginalLanguage, s.Overview, s.BackdropPath));
        var state = States([seriesEntry]).GetValueOrDefault((MediaType.Series, s.TmdbId), TitleState.None);
        return new DiscoverDetails(
            seriesEntry with { State = state },
            null,
            s.Genres,
            null,
            Blank(s.Status),
            show.Certification,
            show.Studios,
            [.. show.Cast.Take(12)],
            Seasons(s.TmdbId, state.ItemId, show.Seasons));
    }

    /// <summary>Library and request state of titles (three queries for any number).</summary>
    public IReadOnlyDictionary<(MediaType, int), TitleState> States(IReadOnlyCollection<DiscoverEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var movieIds = entries.Where(e => e.MediaType == MediaType.Movie).Select(e => e.TmdbId).Distinct().ToList();
        var seriesIds = entries.Where(e => e.MediaType == MediaType.Series).Select(e => e.TmdbId).Distinct().ToList();
        var movies = lookup.Movies(movieIds);
        var shows = lookup.Series(seriesIds, Today);
        var openMovies = lookup.OpenRequests(MediaType.Movie, movieIds).GroupBy(r => r.TmdbId).ToDictionary(g => g.Key, g => g.First());
        var openSeries = lookup.OpenRequests(MediaType.Series, seriesIds).GroupBy(r => r.TmdbId).ToDictionary(g => g.Key, g => g.First());

        var states = new Dictionary<(MediaType, int), TitleState>();
        foreach (var id in movieIds)
        {
            var movie = movies.GetValueOrDefault(id);
            var open = openMovies.GetValueOrDefault(id);
            states[(MediaType.Movie, id)] = new TitleState(movie is not null, movie?.HasFile == true, false, movie?.Id, open?.Status, open?.Id);
        }

        foreach (var id in seriesIds)
        {
            var show = shows.GetValueOrDefault(id);
            var open = openSeries.GetValueOrDefault(id);
            var full = show is { Aired: > 0 } && show.AiredWithFiles == show.Aired;
            states[(MediaType.Series, id)] = new TitleState(show is not null, full, !full && show?.AiredWithFiles > 0, show?.Id, open?.Status, open?.Id);
        }

        return states;
    }

    private IReadOnlyList<DiscoverSeason> Seasons(int tmdbId, int? seriesId, IReadOnlyList<TmdbSeasonSummary> seasons)
    {
        var episodes = seriesId is { } id ? series.Episodes(id) : [];
        var today = Today;
        var open = requests.Open(MediaType.Series, tmdbId);
        return
        [
            .. seasons.Where(s => s.SeasonNumber > 0).Select(s =>
            {
                var own = episodes.Where(e => e.SeasonNumber == s.SeasonNumber).ToList();
                var availability = RequestAvailability.Of(own, null, today);
                var request = open.FirstOrDefault(r => r.Seasons is null || r.Seasons.Contains(s.SeasonNumber));
                return new DiscoverSeason(
                    s.SeasonNumber,
                    s.Name,
                    s.EpisodeCount > 0 ? s.EpisodeCount : own.Count,
                    s.AirDate,
                    s.PosterPath,
                    availability == LibraryAvailability.Full,
                    availability == LibraryAvailability.Partial,
                    request?.Status,
                    request?.Id);
            }),
        ];
    }

    private DiscoverResult WithState(TmdbListPage page)
    {
        var entries = page.Results.Select(Entry).ToList();
        var states = States(entries);
        return new DiscoverResult(
            page.Page,
            page.TotalPages,
            [.. entries.Select(e => e with { State = states.GetValueOrDefault((e.MediaType, e.TmdbId), TitleState.None) })]);
    }

    private static DiscoverEntry Entry(TmdbListEntry e) => new(
        e.Kind == TmdbMediaKind.Movie ? MediaType.Movie : MediaType.Series,
        e.TmdbId,
        e.Title,
        e.Year > 0 ? e.Year : null,
        e.Overview,
        e.PosterPath,
        e.BackdropPath,
        e.VoteAverage is { } rating ? Math.Round(rating, 1) : null,
        e.ReleaseDate,
        TitleState.None);

    private static TmdbMediaKind Kind(MediaType mediaType) => mediaType == MediaType.Movie ? TmdbMediaKind.Movie : TmdbMediaKind.Series;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
