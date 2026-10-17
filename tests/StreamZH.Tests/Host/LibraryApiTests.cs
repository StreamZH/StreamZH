// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StreamZH.Core.Jobs;
using StreamZH.Host.Api;
using StreamZH.Library;

namespace StreamZH.Tests.Host;

public sealed class LibraryApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-media-").FullName;

    [Fact]
    public async Task Adds_movie_finds_existing_file_and_blocks_duplicates()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await AddRootFolderAsync(client, "movies", "Movie");
        Touch(Path.Combine(root.Path, "Dune (2021)", "Dune.2021.2160p.mkv"));

        var response = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var movie = (await response.Content.ReadFromJsonAsync<MovieResource>())!;

        Assert.Equal("Dune", movie.Title);
        Assert.True(movie.HasFile);
        Assert.Equal(Path.Combine(root.Path, "Dune (2021)"), movie.FolderPath);

        var duplicate = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root.Id });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var lookup = await client.GetFromJsonAsync<LookupResource[]>("/api/v1/lookup/movies?term=dune");
        Assert.Equal(movie.Id, Assert.Single(lookup!).LibraryId);

        // Removing from the library never deletes files.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/movies/{movie.Id}")).StatusCode);
        Assert.True(File.Exists(Path.Combine(root.Path, "Dune (2021)", "Dune.2021.2160p.mkv")));
    }

    [Fact]
    public async Task Adds_series_and_loads_episodes_in_the_background()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await AddRootFolderAsync(client, "tv", "Series");
        Touch(Path.Combine(root.Path, "Breaking Bad (2008)", "Season 01", "Breaking.Bad.S01E01.mkv"));
        Touch(Path.Combine(root.Path, "Breaking Bad (2008)", "Season 01", "Breaking.Bad.S01E02E03.mkv"));

        var response = await client.PostAsJsonAsync("/api/v1/series", new { tmdbId = 1396, rootFolderId = root.Id });
        var series = (await response.Content.ReadFromJsonAsync<SeriesResource>())!;

        await WaitForJobsAsync(client);
        var detail = (await client.GetFromJsonAsync<SeriesDetailResource>($"/api/v1/series/{series.Id}"))!;

        Assert.Equal(20, detail.Series.EpisodeCount);
        Assert.Equal(3, detail.Series.FileCount);
        Assert.Equal([2, 1, 0], detail.Seasons.Select(s => s.SeasonNumber));
        Assert.False(detail.Episodes.First(e => e.SeasonNumber == 0).Monitored);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/v1/series/{series.Id}/seasons/2/monitor", new { monitored = false })).StatusCode);
        detail = (await client.GetFromJsonAsync<SeriesDetailResource>($"/api/v1/series/{series.Id}"))!;
        Assert.False(detail.Seasons.Single(s => s.SeasonNumber == 2).Monitored);
    }

    [Fact]
    public async Task Imports_existing_folders()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await AddRootFolderAsync(client, "movies", "Movie");
        Touch(Path.Combine(root.Path, "Arrival.2016.1080p", "arrival.mkv"));
        Touch(Path.Combine(root.Path, "Dune (2021) {tmdb-438631}", "dune.mkv"));
        Directory.CreateDirectory(Path.Combine(root.Path, "@eaDir"));

        var candidates = (await client.GetFromJsonAsync<ImportCandidate[]>($"/api/v1/library-import/{root.Id}"))!;
        Assert.Equal(["Arrival.2016.1080p", "Dune (2021) {tmdb-438631}"], candidates.Select(c => c.FolderName));
        Assert.Equal(438631, candidates[1].TmdbId);
        Assert.Equal(("Arrival", 2016), (candidates[0].Title, candidates[0].Year));

        var import = await client.PostAsJsonAsync("/api/v1/library-import", new
        {
            rootFolderId = root.Id,
            monitored = true,
            items = new[] { new { folderName = "Arrival.2016.1080p", tmdbId = 329865 }, new { folderName = "Dune (2021) {tmdb-438631}", tmdbId = 438631 } },
        });
        Assert.Equal(HttpStatusCode.Accepted, import.StatusCode);

        await WaitForJobsAsync(client);
        var movies = (await client.GetFromJsonAsync<MovieResource[]>("/api/v1/movies"))!;
        Assert.Equal(["Arrival", "Dune"], movies.Select(m => m.Title));
        Assert.All(movies, m => Assert.True(m.HasFile));
        Assert.Equal(Path.Combine(root.Path, "Arrival.2016.1080p"), movies[0].FolderPath);

        Assert.Empty((await client.GetFromJsonAsync<ImportCandidate[]>($"/api/v1/library-import/{root.Id}"))!);

        var stats = (await client.GetFromJsonAsync<LibraryStats>("/api/v1/library/stats"))!;
        Assert.Equal((2, 2), (stats.Movies, stats.MoviesWithFile));

        // A folder still in use cannot be removed.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/v1/rootfolders/{root.Id}")).StatusCode);
    }

    [Fact]
    public async Task Validates_root_folders()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await AddRootFolderAsync(client, "movies", "Movie");

        var missing = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path = Path.Combine(_media, "nope"), mediaType = "Movie" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var relative = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path = "movies", mediaType = "Movie" });
        Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);

        Directory.CreateDirectory(Path.Combine(root.Path, "nested"));
        var nested = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path = Path.Combine(root.Path, "nested"), mediaType = "Series" });
        Assert.Equal(HttpStatusCode.BadRequest, nested.StatusCode);

        var all = (await client.GetFromJsonAsync<RootFolderInfo[]>("/api/v1/rootfolders"))!;
        Assert.True(Assert.Single(all).Accessible);
        Assert.True(all[0].Writable);
        Assert.Equal(1, all[0].UnmappedFolders);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/rootfolders/{root.Id}")).StatusCode);
    }

    [Fact]
    public async Task Folder_browser_lists_subfolders_and_roots()
    {
        var client = await _factory.CreateOwnerClientAsync();
        Directory.CreateDirectory(Path.Combine(_media, "Movies"));
        Directory.CreateDirectory(Path.Combine(_media, "TV Shows"));
        Directory.CreateDirectory(Path.Combine(_media, ".hidden"));

        var listing = (await client.GetFromJsonAsync<FolderListing>($"/api/v1/filesystem?path={Uri.EscapeDataString(_media)}"))!;
        Assert.Equal(["Movies", "TV Shows"], listing.Folders.Select(f => f.Name));
        Assert.Equal(Path.GetDirectoryName(_media), listing.Parent);

        var roots = (await client.GetFromJsonAsync<FolderListing>("/api/v1/filesystem"))!;
        Assert.Null(roots.Path);
        Assert.NotEmpty(roots.Folders);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(Path.Combine(_media, "nope"))}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/filesystem")).StatusCode);
    }

    [Fact]
    public async Task Without_tmdb_key_lookup_explains_what_is_missing()
    {
        using var plain = new StreamZhFactory();
        var client = await plain.CreateOwnerClientAsync();

        var response = await client.GetAsync("/api/v1/lookup/movies?term=dune");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tmdb-not-configured", problem.GetProperty("code").GetString());

        var saved = await client.PutAsJsonAsync("/api/v1/settings/metadata", new { tmdbToken = "abcdefgh12345678", language = "de-CH" });
        var settings = await saved.Content.ReadFromJsonAsync<MetadataSettings>();
        Assert.True(settings!.TmdbConfigured);
        Assert.Equal("…5678", settings.TmdbTokenHint);
        Assert.Equal("de-CH", settings.Language);
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
        for (var i = 0; i < 100; i++)
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

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(500L * 1024 * 1024);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_media, recursive: true);
    }
}