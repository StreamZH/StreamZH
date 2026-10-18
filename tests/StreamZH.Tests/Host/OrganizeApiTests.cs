// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Api;

namespace StreamZH.Tests.Host;

public sealed class OrganizeApiTests : IDisposable
{
    private const string Release = "Dune.2021.German.DL.1080p.BluRay.x264-GRP";
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-organize-").FullName;

    [Fact]
    public async Task Organize_previews_applies_as_a_job_and_undoes()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var movieId = await ReleaseMovieAsync(client);
        var oldFolder = Path.Combine(_media, "movies", Release);

        var preview = (await (await client.PostAsJsonAsync("/api/v1/organize/preview", new { mediaType = "Movie", ids = new[] { movieId } }))
            .Content.ReadFromJsonAsync<JsonArray>())!;
        var item = Assert.Single(preview)!;
        Assert.True(item["folderChanged"]!.GetValue<bool>());
        Assert.True(item["canApply"]!.GetValue<bool>());
        Assert.Equal(Path.Combine(_media, "movies", "Dune (2021)"), item["newFolder"]!.GetValue<string>());
        Assert.Equal("video", item["files"]![0]!["kind"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/organize/apply", new { mediaType = "Movie", ids = new[] { movieId } })).StatusCode);
        var status = await WaitForRunAsync(client);
        Assert.Equal(1, status["last"]!["done"]!.GetValue<int>());
        Assert.Equal("organize", status["last"]!["kind"]!.GetValue<string>());
        Assert.NotNull(status["undo"]);
        var movie = (await client.GetFromJsonAsync<MovieResource>($"/api/v1/movies/{movieId}"))!;
        Assert.Equal(Path.Combine(_media, "movies", "Dune (2021)"), movie.FolderPath);
        Assert.Equal("Dune (2021) Bluray-1080p.mkv", movie.File!.RelativePath);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/organize/undo", new { runId = 999 })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/organize/undo", new { runId = status["undo"]!["runId"]!.GetValue<int>() })).StatusCode);
        status = await WaitForRunAsync(client);
        Assert.Equal("undo", status["last"]!["kind"]!.GetValue<string>());
        Assert.Null(status["undo"]);
        Assert.True(File.Exists(Path.Combine(oldFolder, $"{Release}.mkv")));
        Assert.Equal(oldFolder, (await client.GetFromJsonAsync<MovieResource>($"/api/v1/movies/{movieId}"))!.FolderPath);
    }

    [Fact]
    public async Task Requests_are_validated_and_need_an_admin()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/organize/preview", new { mediaType = "Movie", ids = new[] { 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/v1/movie/editor", new { movieIds = new[] { 1 }, monitored = false })).StatusCode);

        var client = await _factory.CreateOwnerClientAsync();
        var empty = await client.PostAsJsonAsync("/api/v1/organize/apply", new { mediaType = "Movie", ids = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.NotNull((await empty.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!["ids"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/organize/preview", new { mediaType = "Music", ids = new[] { 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/series/editor", new { seriesIds = Array.Empty<int>() })).StatusCode);

        var status = (await client.GetFromJsonAsync<JsonObject>("/api/v1/organize/status"))!;
        Assert.Null(status["running"]);
        Assert.Null(status["undo"]);
        Assert.Null(status["recycleBin"]);
    }

    [Fact]
    public async Task Mass_editor_changes_monitored_and_moves_to_another_root_folder()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var movieId = await ReleaseMovieAsync(client);
        var second = await RootFolderAsync(client, "movies2", "Movie");

        var edit = await client.PutAsJsonAsync("/api/v1/movie/editor", new { movieIds = new[] { movieId }, monitored = false, rootFolderId = second, moveFiles = true });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var result = (await edit.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(1, result["updated"]!.GetValue<int>());
        Assert.Equal(1, result["moving"]!.GetValue<int>());

        var status = await WaitForRunAsync(client);
        Assert.Equal("move", status["last"]!["kind"]!.GetValue<string>());
        var movie = (await client.GetFromJsonAsync<MovieResource>($"/api/v1/movies/{movieId}"))!;
        Assert.False(movie.Monitored);
        Assert.Equal(second, movie.RootFolderId);
        Assert.Equal(Path.Combine(_media, "movies2", Release), movie.FolderPath);
        Assert.True(File.Exists(Path.Combine(movie.FolderPath, $"{Release}.mkv")));

        var seriesRoot = await RootFolderAsync(client, "tv", "Series");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/movie/editor", new { movieIds = new[] { movieId }, rootFolderId = seriesRoot })).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_from_the_library_and_deletes_files_only_when_asked()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var movieId = await ReleaseMovieAsync(client);
        var folder = Path.Combine(_media, "movies", Release);

        var keep = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/movie/editor")
        {
            Content = JsonContent.Create(new { movieIds = new[] { movieId }, deleteFiles = false }),
        });
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/movies/{movieId}")).StatusCode);
        Assert.True(Directory.Exists(folder));

        movieId = await ReleaseMovieAsync(client);
        var delete = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/movie/editor")
        {
            Content = JsonContent.Create(new { movieIds = new[] { movieId }, deleteFiles = true }),
        });
        var removed = Assert.Single((await delete.Content.ReadFromJsonAsync<JsonObject>())!["removed"]!.AsArray())!;
        Assert.True(removed["filesDeleted"]!.GetValue<bool>());
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>Dune, added through the API, sitting in a release-named folder like an import of StreamZH 0.2.</summary>
    private async Task<int> ReleaseMovieAsync(HttpClient client)
    {
        var root = Path.Combine(_media, "movies");
        var rootId = Directory.Exists(root)
            ? _factory.Services.GetRequiredService<RootFolderRepository>().All().Single(r => r.Path == root).Id
            : await RootFolderAsync(client, "movies", "Movie");
        var added = (await (await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = rootId })).Content.ReadFromJsonAsync<MovieResource>())!;
        var folder = Path.Combine(root, Release);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, $"{Release}.mkv"), "video");
        var movies = _factory.Services.GetRequiredService<MovieRepository>();
        var movie = movies.Find(added.Id)!;
        movie.FolderPath = folder;
        movie.File = new MediaFile($"{Release}.mkv", 5, DateTime.UtcNow);
        movies.Update(movie);
        return movie.Id;
    }

    private async Task<int> RootFolderAsync(HttpClient client, string name, string mediaType)
    {
        var path = Directory.CreateDirectory(Path.Combine(_media, name)).FullName;
        var response = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path, mediaType });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>();
    }

    private static async Task<JsonObject> WaitForRunAsync(HttpClient client)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = (await client.GetFromJsonAsync<JsonObject>("/api/v1/organize/status"))!;
            var jobs = (await client.GetFromJsonAsync<JobStatus>("/api/v1/system/jobs"))!;
            if (status["running"] is null && jobs.Queued == 0 && jobs.Running is null)
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The run did not finish.");
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(_media, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
