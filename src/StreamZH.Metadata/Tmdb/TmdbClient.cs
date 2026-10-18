// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamZH.Metadata.Tmdb;

public interface ITmdbClient
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<TmdbSearchResult>> SearchMoviesAsync(string query, int? year, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TmdbSearchResult>> SearchSeriesAsync(string query, int? year, CancellationToken cancellationToken = default);

    Task<TmdbMovie> GetMovieAsync(int tmdbId, CancellationToken cancellationToken = default);

    Task<TmdbSeries> GetSeriesAsync(int tmdbId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default);

    /// <summary>Checks a token (or the stored one) against TMDB.</summary>
    Task TestAsync(string? token = null, CancellationToken cancellationToken = default);
}

/// <summary>Minimal TMDB v3 client. Data source: The Movie Database (TMDB). Not endorsed or certified by TMDB.</summary>
public sealed partial class TmdbClient(HttpClient http, ITmdbSettings settings) : ITmdbClient
{
    public static readonly Uri BaseAddress = new("https://api.themoviedb.org/3/");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Token);

    public async Task<IReadOnlyList<TmdbSearchResult>> SearchMoviesAsync(string query, int? year, CancellationToken cancellationToken = default)
    {
        var url = $"search/movie?query={Uri.EscapeDataString(query)}&include_adult=false{(year is { } y ? $"&year={y}" : string.Empty)}";
        var page = await GetAsync<SearchPage<MovieDto>>(url, null, cancellationToken);
        return [.. page.Results.Select(m => new TmdbSearchResult(
            m.Id, m.Title ?? string.Empty, m.OriginalTitle ?? m.Title ?? string.Empty, YearOf(m.ReleaseDate), Blank(m.Overview), m.PosterPath))];
    }

    public async Task<IReadOnlyList<TmdbSearchResult>> SearchSeriesAsync(string query, int? year, CancellationToken cancellationToken = default)
    {
        var url = $"search/tv?query={Uri.EscapeDataString(query)}&include_adult=false{(year is { } y ? $"&first_air_date_year={y}" : string.Empty)}";
        var page = await GetAsync<SearchPage<TvDto>>(url, null, cancellationToken);
        return [.. page.Results.Select(t => new TmdbSearchResult(
            t.Id, t.Name ?? string.Empty, t.OriginalName ?? t.Name ?? string.Empty, YearOf(t.FirstAirDate), Blank(t.Overview), t.PosterPath))];
    }

    public async Task<TmdbMovie> GetMovieAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        var m = await GetAsync<MovieDto>($"movie/{tmdbId}?append_to_response=release_dates", null, cancellationToken);
        return new TmdbMovie(
            m.Id,
            Blank(m.ImdbId),
            m.Title ?? string.Empty,
            m.OriginalTitle ?? m.Title ?? string.Empty,
            YearOf(m.ReleaseDate),
            Blank(m.Overview),
            m.Runtime ?? 0,
            m.Status ?? string.Empty,
            [.. (m.Genres ?? []).Select(g => g.Name).OfType<string>()],
            m.PosterPath,
            m.BackdropPath,
            m.OriginalLanguage,
            Blank(m.ReleaseDate),
            EarliestRelease(m.ReleaseDates, DigitalReleaseType),
            EarliestRelease(m.ReleaseDates, PhysicalReleaseType));
    }

    public async Task<TmdbSeries> GetSeriesAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        var t = await GetAsync<TvDto>($"tv/{tmdbId}?append_to_response=external_ids", null, cancellationToken);
        return new TmdbSeries(
            t.Id,
            t.ExternalIds?.TvdbId,
            Blank(t.ExternalIds?.ImdbId),
            t.Name ?? string.Empty,
            t.OriginalName ?? t.Name ?? string.Empty,
            YearOf(t.FirstAirDate),
            Blank(t.Overview),
            t.Status ?? string.Empty,
            t.Networks?.FirstOrDefault()?.Name,
            [.. (t.Genres ?? []).Select(g => g.Name).OfType<string>()],
            t.PosterPath,
            t.BackdropPath,
            [.. (t.Seasons ?? []).Select(s => s.SeasonNumber).Order()],
            t.OriginalLanguage);
    }

    public async Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default)
    {
        var season = await GetAsync<SeasonDto>($"tv/{tmdbId}/season/{seasonNumber}", null, cancellationToken);
        return [.. (season.Episodes ?? []).Select(e => new TmdbEpisode(
            e.SeasonNumber, e.EpisodeNumber, string.IsNullOrWhiteSpace(e.Name) ? $"Episode {e.EpisodeNumber}" : e.Name, Blank(e.AirDate), Blank(e.Overview)))];
    }

    public async Task TestAsync(string? token = null, CancellationToken cancellationToken = default) =>
        await GetAsync<JsonElement>("configuration", token, cancellationToken);

    private async Task<T> GetAsync<T>(string relativeUrl, string? tokenOverride, CancellationToken cancellationToken)
    {
        var token = (tokenOverride ?? settings.Token)?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new TmdbException("TMDB is not set up. Add your TMDB API key under Settings.");
        }

        var separator = relativeUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{relativeUrl}{separator}language={Uri.EscapeDataString(settings.Language)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(http.BaseAddress ?? BaseAddress, url));
        if (ApiKeyRegex().IsMatch(token))
        {
            request.RequestUri = new Uri(request.RequestUri + $"&api_key={token}");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TmdbException($"TMDB is not reachable: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new TmdbException("TMDB rejected the API key.", 401);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new TmdbException("Not found on TMDB.", 404);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new TmdbException("TMDB rate limit reached, try again in a moment.", 429);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TmdbException($"TMDB answered {(int)response.StatusCode}.", (int)response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw new TmdbException("TMDB returned an empty answer.");
        }
    }

    private static int YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : 0;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ApiKeyRegex();
}