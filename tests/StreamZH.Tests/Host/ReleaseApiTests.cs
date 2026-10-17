// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Indexers;
using StreamZH.Core.Jobs;
using StreamZH.Host.Api;
using StreamZH.Library;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Host;

public sealed class ReleaseApiTests : IDisposable
{
    private readonly StreamZhFactory _base = new(new FakeTmdbClient());
    private readonly FakeIndexerProvider _indexers = new();
    private readonly FakeIndexerClient _client = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-media-").FullName;

    public ReleaseApiTests()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IIndexerProvider>(_indexers);
            services.AddSingleton<IIndexerClient>(_client);
        }));
        _indexers.Indexers.Add(FakeIndexerProvider.Definition(1, "Alpha"));
    }

    [Fact]
    public async Task Interactive_search_grab_and_history()
    {
        var client = await OwnerClientAsync();
        var movie = await AddMovieAsync(client);
        _client.Returns(1,
            Release("Dune.2021.German.DL.1080p.WEB.H264-GRP"),
            Release("Dune.2021.720p.WEB.H264-ENG"));

        var indexers = (await client.GetFromJsonAsync<ActiveIndexersResource>("/api/v1/release/indexers"))!;
        Assert.Equal(new ActiveIndexersResource(1, 1), indexers);

        var search = (await client.GetFromJsonAsync<ReleaseSearchResource>($"/api/v1/release?movieId={movie.Id}"))!;
        Assert.Equal(1, search.Indexers);
        Assert.Empty(search.FailedIndexers);
        Assert.Equal(2, search.Releases.Count);

        var best = search.Releases[0];
        Assert.True(best.Accepted);
        Assert.Equal((1, "Alpha", "WEBDL-1080p", "GRP", 1), (best.IndexerId, best.Indexer, best.Quality, best.ReleaseGroup, best.Rank));
        Assert.Contains("German", best.Languages);
        Assert.Equal(2, best.Age);
        Assert.Equal(10, best.Grabs);
        Assert.NotNull(best.InfoUrl);
        Assert.False(search.Releases[1].Accepted);
        Assert.Contains(search.Releases[1].Rejections, r => r.Code == "WantedLanguage");

        var grab = await client.PostAsJsonAsync("/api/v1/release", new { indexerId = best.IndexerId, guid = best.Guid });
        Assert.Equal(HttpStatusCode.OK, grab.StatusCode);
        var grabbed = (await grab.Content.ReadFromJsonAsync<GrabResource>())!;
        Assert.Equal("Dune.2021.German.DL.1080p.WEB.H264-GRP.nzb", grabbed.FileName);
        Assert.True(File.Exists(Path.Combine(_base.DataDirectory, "downloads", "nzb", grabbed.FileName)));

        var history = (await client.GetFromJsonAsync<HistoryPage>($"/api/v1/history?movieId={movie.Id}"))!;
        var entry = Assert.Single(history.Records);
        Assert.Equal(1, history.TotalRecords);
        Assert.Equal(("Dune.2021.German.DL.1080p.WEB.H264-GRP", "WEBDL-1080p", "Alpha"), (entry.SourceTitle, entry.Quality, entry.Indexer));
        Assert.Empty((await client.GetFromJsonAsync<HistoryPage>($"/api/v1/history?movieId={movie.Id + 1}"))!.Records);

        var raw = await client.GetStringAsync("/api/v1/history");
        Assert.Contains("\"eventType\":\"Grabbed\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"mediaType\":\"Movie\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grab_errors_are_readable()
    {
        var client = await OwnerClientAsync();
        var movie = await AddMovieAsync(client);
        _client.Returns(1, Release("Dune.2021.German.DL.1080p.WEB.H264-GRP"));
        var search = (await client.GetFromJsonAsync<ReleaseSearchResource>($"/api/v1/release?movieId={movie.Id}"))!;

        _client.Nzb = (_, _) => throw new IndexerException("Download limit reached", 501);
        var failed = await client.PostAsJsonAsync("/api/v1/release", new { indexerId = 1, guid = search.Releases[0].Guid });
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var problem = await failed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Alpha did not deliver the NZB: Download limit reached", problem.GetProperty("detail").GetString());

        var unknown = await client.PostAsJsonAsync("/api/v1/release", new { indexerId = 1, guid = "unknown" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var missing = await client.GetAsync("/api/v1/release");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Series_search_by_season_and_by_episode()
    {
        var client = await OwnerClientAsync();
        var series = await AddSeriesAsync(client);
        _client.Returns(1,
            Release("Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP"),
            Release("Breaking.Bad.S01.German.DL.1080p.BluRay.x264-GRP"));

        var season = (await client.GetFromJsonAsync<ReleaseSearchResource>($"/api/v1/release?seriesId={series.Series.Id}&seasonNumber=1"))!;
        Assert.All(season.Releases, r => Assert.True(r.Accepted));
        Assert.True(season.Releases[0].FullSeason);
        Assert.Equal(7, season.Releases[0].EpisodeIds.Count);

        var episode = series.Episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 2);
        var single = (await client.GetFromJsonAsync<ReleaseSearchResource>($"/api/v1/release?episodeId={episode.Id}"))!;
        var accepted = Assert.Single(single.Releases, r => r.Accepted);
        Assert.Equal(1, accepted.SeasonNumber);
        Assert.Equal([2], accepted.EpisodeNumbers);
        Assert.Equal("FullSeason", Assert.Single(single.Releases.Single(r => !r.Accepted).Rejections).Code);
    }

    [Fact]
    public async Task Commands_queue_automatic_searches()
    {
        var client = await OwnerClientAsync();
        var movie = await AddMovieAsync(client);
        var series = await AddSeriesAsync(client);
        _client.Returns(1, Release("Dune.2021.German.DL.1080p.WEB.H264-GRP"));

        var movies = await client.PostAsJsonAsync("/api/v1/command", new { name = "MoviesSearch", movieIds = new[] { movie.Id } });
        Assert.Equal(HttpStatusCode.Accepted, movies.StatusCode);
        Assert.Equal("MoviesSearch", (await movies.Content.ReadFromJsonAsync<CommandResponse>())!.Name);
        await WaitForJobsAsync(client);
        Assert.Single((await client.GetFromJsonAsync<HistoryPage>($"/api/v1/history?movieId={movie.Id}"))!.Records);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/command", new { name = "missingMoviesSearch" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/api/v1/command", new { name = "SeasonSearch", seriesId = series.Series.Id, seasonNumber = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/api/v1/command", new { name = "EpisodeSearch", episodeIds = new[] { series.Episodes[1].Id } })).StatusCode);
        await WaitForJobsAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/command", new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/command", new { name = "MoviesSearch" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/command", new { name = "SeasonSearch", seriesId = 999, seasonNumber = 1 })).StatusCode);
    }

    [Fact]
    public async Task Release_test_page_shows_only_the_mismatch_for_another_movie()
    {
        var client = await OwnerClientAsync();
        var movie = await AddMovieAsync(client);

        var other = (await client.GetFromJsonAsync<ParseResult>(
            $"/api/v1/parse?title=Arrival.2016.CAM.x264-GRP&movieId={movie.Id}"))!;
        Assert.False(other.Decision!.Accepted);
        Assert.Equal("WrongMedia", Assert.Single(other.Decision.Rejections).Code);

        var match = (await client.GetFromJsonAsync<ParseResult>(
            $"/api/v1/parse?title=Dune.2021.German.DL.1080p.WEB.H264-GRP&movieId={movie.Id}&sizeMb=6000"))!;
        Assert.True(match.Decision!.Accepted);
        Assert.Equal("Deutsch 1080p", match.Decision.Profile);
    }

    [Fact]
    public async Task Download_settings_validate_the_nzb_folder()
    {
        var client = await OwnerClientAsync();

        var settings = (await client.GetFromJsonAsync<DownloadSettingsResource>("/api/v1/settings/downloads"))!;
        Assert.Equal(Path.Combine(_base.DataDirectory, "downloads", "nzb"), settings.NzbFolder);
        Assert.True(settings.IsDefault);
        Assert.Null(settings.Problem);

        var relative = await client.PutAsJsonAsync("/api/v1/settings/downloads", new { nzbFolder = "nzb" });
        Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);
        var problem = await relative.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("nzbFolder", out _));

        var watch = Path.Combine(_media, "watch");
        var saved = await client.PutAsJsonAsync("/api/v1/settings/downloads", new { nzbFolder = watch });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(watch, (await saved.Content.ReadFromJsonAsync<DownloadSettingsResource>())!.NzbFolder);
        Assert.True(Directory.Exists(watch));
    }

    private ReleaseInfo Release(string title) => new()
    {
        Guid = $"guid-{title}",
        Title = title,
        DownloadUrl = $"https://alpha.example/get/{title}",
        InfoUrl = $"https://alpha.example/details/{title}",
        Size = 4L * 1024 * 1024 * 1024,
        PublishDate = DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(-5),
        Grabs = 10,
    };

    private async Task<HttpClient> OwnerClientAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<MovieResource> AddMovieAsync(HttpClient client)
    {
        var root = await AddRootFolderAsync(client, "movies", "Movie");
        var response = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MovieResource>())!;
    }

    private async Task<SeriesDetailResource> AddSeriesAsync(HttpClient client)
    {
        var root = await AddRootFolderAsync(client, "tv", "Series");
        var response = await client.PostAsJsonAsync("/api/v1/series", new { tmdbId = 1396, rootFolderId = root.Id });
        var series = (await response.Content.ReadFromJsonAsync<SeriesResource>())!;
        await WaitForJobsAsync(client);
        return (await client.GetFromJsonAsync<SeriesDetailResource>($"/api/v1/series/{series.Id}"))!;
    }

    private async Task<RootFolderInfo> AddRootFolderAsync(HttpClient client, string name, string mediaType)
    {
        var path = Directory.CreateDirectory(Path.Combine(_media, name)).FullName;
        var response = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path, mediaType });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RootFolderInfo>())!;
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
        _base.Dispose();
        Directory.Delete(_media, recursive: true);
    }
}
