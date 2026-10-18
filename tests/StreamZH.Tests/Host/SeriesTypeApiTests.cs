// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StreamZH.Core.Jobs;
using StreamZH.Host.Api;
using StreamZH.Library;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Host;

/// <summary>Series type through the API: lookup suggestion, add, PUT /series/{id}, episode numbers in the detail.</summary>
public sealed class SeriesTypeApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new(new AnimeTmdb());
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-media-").FullName;

    [Fact]
    public async Task Lookup_suggests_anime_and_add_and_put_take_the_series_type()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var lookup = (await client.GetFromJsonAsync<LookupResource[]>("/api/v1/lookup/series?term=breaking"))!;
        Assert.Equal("anime", Assert.Single(lookup).SuggestedSeriesType);

        var path = Directory.CreateDirectory(Path.Combine(_media, "tv")).FullName;
        var root = (await (await client.PostAsJsonAsync("/api/v1/rootfolders", new { path, mediaType = "Series" })).Content.ReadFromJsonAsync<RootFolderInfo>())!;

        var invalid = await client.PostAsJsonAsync("/api/v1/series", new { tmdbId = 1396, rootFolderId = root.Id, seriesType = "weekly" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True((await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("seriesType", out _));

        var response = await client.PostAsJsonAsync("/api/v1/series", new { tmdbId = 1396, rootFolderId = root.Id, seriesType = "daily" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var series = (await response.Content.ReadFromJsonAsync<SeriesResource>())!;
        Assert.Equal("daily", series.SeriesType);

        var changed = await client.PutAsJsonAsync($"/api/v1/series/{series.Id}", new { seriesType = "anime" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("anime", (await changed.Content.ReadFromJsonAsync<SeriesResource>())!.SeriesType);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/v1/series/{series.Id}", new { seriesType = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/v1/series/999", new { seriesType = "anime" })).StatusCode);

        // The refresh after adding counted the absolute numbers; TheXEM (fake) knows the series not.
        await WaitForJobsAsync(client);
        var detail = (await client.GetFromJsonAsync<SeriesDetailResource>($"/api/v1/series/{series.Id}"))!;
        Assert.Equal("anime", detail.Series.SeriesType);
        Assert.Equal(8, detail.Episodes.Single(e => e.SeasonNumber == 2 && e.EpisodeNumber == 1).AbsoluteEpisodeNumber);
        Assert.Null(detail.Episodes.Single(e => e.SeasonNumber == 0).AbsoluteEpisodeNumber);
        Assert.All(detail.Episodes, e => Assert.Null(e.SceneSeasonNumber));
        Assert.Contains(_factory.Xem.Requests, r => r.StartsWith("map/havemap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scene_mappings_task_is_listed()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var tasks = await client.GetStringAsync("/api/v1/system/tasks");

        Assert.Contains("Scene mappings", tasks, StringComparison.Ordinal);
    }

    private static async Task WaitForJobsAsync(HttpClient client)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await client.GetFromJsonAsync<JobStatus>("/api/v1/system/jobs");
            if (status!.Queued == 0 && status.Running is null)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Background jobs did not finish.");
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_media, recursive: true);
    }

    /// <summary>The fake TMDB, with Breaking Bad's search result looking like Japanese animation.</summary>
    private sealed class AnimeTmdb : ITmdbClient
    {
        private readonly FakeTmdbClient _inner = new();

        public bool IsConfigured => true;

        public Task<IReadOnlyList<TmdbSearchResult>> SearchMoviesAsync(string query, int? year, CancellationToken cancellationToken = default) =>
            _inner.SearchMoviesAsync(query, year, cancellationToken);

        public async Task<IReadOnlyList<TmdbSearchResult>> SearchSeriesAsync(string query, int? year, CancellationToken cancellationToken = default) =>
            [.. (await _inner.SearchSeriesAsync(query, year, cancellationToken)).Select(r => r with { GenreIds = [16, 10759], OriginCountries = ["JP"], OriginalLanguage = "ja" })];

        public Task<TmdbMovie> GetMovieAsync(int tmdbId, CancellationToken cancellationToken = default) => _inner.GetMovieAsync(tmdbId, cancellationToken);

        public Task<TmdbSeries> GetSeriesAsync(int tmdbId, CancellationToken cancellationToken = default) => _inner.GetSeriesAsync(tmdbId, cancellationToken);

        public Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default) =>
            _inner.GetSeasonAsync(tmdbId, seasonNumber, cancellationToken);

        public Task TestAsync(string? token = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
