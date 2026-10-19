// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Host;

/// <summary>Small fixed TMDB catalogue for API tests.</summary>
public sealed class FakeTmdbClient : ITmdbClient
{
    public static readonly TmdbMovie Dune = new(438631, "tt1160419", "Dune", "Dune", 2021, "Paul Atreides...", 155, "Released", ["Science Fiction"], "/dune.jpg", null);
    public static readonly TmdbMovie Arrival = new(329865, "tt2543164", "Arrival", "Arrival", 2016, "Linguist...", 116, "Released", ["Drama"], "/arrival.jpg", null);
    public static readonly TmdbSeries Show = new(1396, 81189, "tt0903747", "Breaking Bad", "Breaking Bad", 2008, "Walter White...", "Ended", "AMC", ["Drama"], "/bb.jpg", null, [0, 1, 2]);

    public bool IsConfigured => true;

    public Task<IReadOnlyList<TmdbSearchResult>> SearchMoviesAsync(string query, int? year, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TmdbSearchResult>>([.. new[] { Dune, Arrival }
            .Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(m => new TmdbSearchResult(m.TmdbId, m.Title, m.OriginalTitle, m.Year, m.Overview, m.PosterPath))]);

    public Task<IReadOnlyList<TmdbSearchResult>> SearchSeriesAsync(string query, int? year, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TmdbSearchResult>>(
            Show.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? [new TmdbSearchResult(Show.TmdbId, Show.Title, Show.OriginalTitle, Show.Year, Show.Overview, Show.PosterPath)]
                : []);

    /// <summary>When set, every movie lookup waits for this first (a slow TMDB); null answers at once.</summary>
    public Func<int, Task>? BeforeMovie { get; set; }

    public async Task<TmdbMovie> GetMovieAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        if (BeforeMovie is { } wait)
        {
            await wait(tmdbId);
        }

        return new[] { Dune, Arrival }.FirstOrDefault(m => m.TmdbId == tmdbId) is { } movie
            ? movie
            : throw new TmdbException("Not found on TMDB.", 404);
    }

    public Task<TmdbSeries> GetSeriesAsync(int tmdbId, CancellationToken cancellationToken = default) =>
        tmdbId == Show.TmdbId ? Task.FromResult(Show) : throw new TmdbException("Not found on TMDB.", 404);

    public Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TmdbEpisode>>(seasonNumber switch
        {
            0 => [new TmdbEpisode(0, 1, "Special", "2009-02-17", null)],
            1 => [.. Enumerable.Range(1, 7).Select(e => new TmdbEpisode(1, e, $"Episode {e}", $"2008-01-{e + 19:00}", null))],
            _ => [.. Enumerable.Range(1, 13).Select(e => new TmdbEpisode(2, e, $"Episode {e}", "2009-03-08", null))],
        });

    public Task TestAsync(string? token = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}