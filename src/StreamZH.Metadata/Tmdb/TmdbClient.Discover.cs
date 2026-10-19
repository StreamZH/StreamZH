// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace StreamZH.Metadata.Tmdb;

/// <summary>The rows of the Discover page (0.10).</summary>
public enum TmdbChart
{
    /// <summary>Trending this week (trending/movie/week, trending/tv/week).</summary>
    Trending,

    /// <summary>movie/popular, tv/popular.</summary>
    Popular,

    /// <summary>Not released yet: discover with a release (first air) date from today on, most popular first.</summary>
    Upcoming,

    /// <summary>movie/top_rated, tv/top_rated.</summary>
    TopRated,
}

/// <summary>
/// TMDB calls behind Discover and requests (0.10): charts, search over movies and shows with paging, and the details of a
/// title with its seasons. A separate interface so the import-list and NFO clients (and their test fakes) stay as they are.
/// </summary>
public interface ITmdbDiscoverClient
{
    /// <summary>One page (from 1) of a chart; TMDB serves at most 500 pages.</summary>
    Task<TmdbListPage> GetChartAsync(TmdbMediaKind kind, TmdbChart chart, int page, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search by title: movies and shows mixed (search/multi, people are dropped) when <paramref name="kind"/> is null,
    /// else only movies or only shows.
    /// </summary>
    Task<TmdbListPage> SearchAsync(TmdbMediaKind? kind, string query, int page, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ITmdbDetailsClient.GetMovieDetailsAsync"/>
    Task<TmdbMovieDetails> GetMovieDetailsAsync(int tmdbId, string certificationCountry, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ITmdbDetailsClient.GetSeriesDetailsAsync"/>
    Task<TmdbSeriesDetails> GetSeriesDetailsAsync(int tmdbId, string certificationCountry, CancellationToken cancellationToken = default);
}

public sealed partial class TmdbClient : ITmdbDiscoverClient
{
    /// <summary>TMDB refuses pages above 500.</summary>
    public const int MaxPage = 500;

    public async Task<TmdbListPage> GetChartAsync(TmdbMediaKind kind, TmdbChart chart, int page, CancellationToken cancellationToken = default)
    {
        var type = kind == TmdbMediaKind.Movie ? "movie" : "tv";
        var number = Math.Clamp(page, 1, MaxPage).ToString(CultureInfo.InvariantCulture);
        var url = chart switch
        {
            TmdbChart.Trending => $"trending/{type}/week?page={number}",
            TmdbChart.Popular => $"{type}/popular?page={number}",
            TmdbChart.TopRated => $"{type}/top_rated?page={number}",
            TmdbChart.Upcoming => kind == TmdbMediaKind.Movie
                ? $"discover/movie?primary_release_date.gte={Today()}&sort_by=popularity.desc&include_adult=false&page={number}"
                : $"discover/tv?first_air_date.gte={Today()}&sort_by=popularity.desc&include_adult=false&page={number}",
            _ => throw new ArgumentOutOfRangeException(nameof(chart)),
        };
        var dto = await GetAsync<ListPageDto>(url, null, cancellationToken);
        return Page(dto, dto.Results, kind) with { TotalPages = Math.Min(MaxPage, Math.Max(1, dto.TotalPages)) };
    }

    public async Task<TmdbListPage> SearchAsync(TmdbMediaKind? kind, string query, int page, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var type = kind switch
        {
            TmdbMediaKind.Movie => "movie",
            TmdbMediaKind.Series => "tv",
            _ => "multi",
        };
        var number = Math.Clamp(page, 1, MaxPage).ToString(CultureInfo.InvariantCulture);
        var dto = await GetAsync<ListPageDto>($"search/{type}?query={Uri.EscapeDataString(query.Trim())}&include_adult=false&page={number}", null, cancellationToken);
        return Page(dto, dto.Results, kind) with { TotalPages = Math.Min(MaxPage, Math.Max(1, dto.TotalPages)) };
    }

    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
