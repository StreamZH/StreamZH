// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Library.Import;

namespace StreamZH.Tests.Host;

public sealed class MediaManagementApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-mm-").FullName;

    [Fact]
    public async Task Settings_read_validate_save_and_preview()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var settings = (await client.GetFromJsonAsync<JsonObject>("/api/v1/settings/media-management"))!;
        Assert.True(settings["renameMovies"]!.GetValue<bool>());
        Assert.Equal("smart", settings["colonReplacement"]!.GetValue<string>());
        Assert.Equal("preferAndUpgrade", settings["propersAndRepacks"]!.GetValue<string>());
        Assert.Equal("move", settings["importMode"]!.GetValue<string>());
        Assert.Equal(OperatingSystem.IsWindows(), settings["isWindows"]!.GetValue<bool>());

        settings["standardMovieFormat"] = "{Release Year}";
        settings["minimumFreeSpaceMb"] = -5;
        var invalid = await client.PutAsJsonAsync("/api/v1/settings/media-management", settings);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var problem = (await invalid.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotNull(problem["errors"]!["standardMovieFormat"]);
        Assert.NotNull(problem["errors"]!["minimumFreeSpaceMb"]);

        settings["standardMovieFormat"] = "{Movie.Title}.{Release.Year}.{Quality.Full}";
        settings["minimumFreeSpaceMb"] = 500;
        settings["colonReplacement"] = "dash";
        settings["multiEpisodeStyle"] = "range";
        var saved = await client.PutAsJsonAsync("/api/v1/settings/media-management", settings);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var reloaded = (await client.GetFromJsonAsync<JsonObject>("/api/v1/settings/media-management"))!;
        Assert.Equal("{Movie.Title}.{Release.Year}.{Quality.Full}", reloaded["standardMovieFormat"]!.GetValue<string>());
        Assert.Equal(500, reloaded["minimumFreeSpaceMb"]!.GetValue<int>());
        Assert.Equal("range", reloaded["multiEpisodeStyle"]!.GetValue<string>());

        reloaded["seasonFolderFormat"] = "Staffel {season:00}";
        reloaded["standardEpisodeFormat"] = "{Series Title}";
        var preview = (await (await client.PostAsJsonAsync("/api/v1/settings/media-management/preview", reloaded)).Content.ReadFromJsonAsync<NamingPreview>())!;
        Assert.Equal("The.Movie.Title-.Part.Two.2010.Bluray-1080p.Proper.mkv", preview.MovieFile);
        Assert.Equal("Staffel 01", preview.SeasonFolder);
        Assert.Equal(string.Empty, preview.StandardEpisode);
        Assert.Contains("standardEpisodeFormat", preview.Errors.Keys);
        Assert.Equal("The Series Title's! - 2013-10-30 - Episode Title (1) WEBDL-1080p.mkv", preview.DailyEpisode);

        // {Custom Formats} with Radarr's sample formats.
        reloaded["standardMovieFormat"] = "{Movie Title} {[Custom Formats]} {Custom Formats:-x264}";
        var formats = (await (await client.PostAsJsonAsync("/api/v1/settings/media-management/preview", reloaded)).Content.ReadFromJsonAsync<NamingPreview>())!;
        Assert.EndsWith(" Part Two [Surround Sound x264] Surround Sound.mkv", formats.MovieFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_need_an_admin()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/settings/media-management")).StatusCode);
    }

    [Fact]
    public async Task A_completed_download_is_imported_through_the_api()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await RootFolderAsync(client, "movies", "Movie");
        var movie = (await (await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root })).Content.ReadFromJsonAsync<MovieResource>())!;
        Assert.False(movie.HasFile);

        var output = Path.Combine(_media, "complete", "Dune.2021.German.DL.1080p.BluRay.x264-DETAiLS");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "dune.mkv"), "video");
        var id = AddFinished("Dune.2021.German.DL.1080p.BluRay.x264-DETAiLS", DownloadStatus.Completed, output, MediaType.Movie, movie.Id);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/import/downloads/{id}", null)).StatusCode);
        await WaitForJobsAsync(client);

        var states = (await client.GetFromJsonAsync<ImportStateResource[]>($"/api/v1/import/downloads?ids={id},999"))!;
        var state = Assert.Single(states);
        Assert.Equal("imported", state.State);
        var file = Path.Combine(movie.FolderPath, "Dune (2021) Bluray-1080p.mkv");
        Assert.Equal([file], state.Files);
        Assert.True((await client.GetFromJsonAsync<MovieResource>($"/api/v1/movies/{movie.Id}"))!.HasFile);
        Assert.Empty((await client.GetFromJsonAsync<PendingImportResource[]>("/api/v1/import/pending"))!);

        var history = (await client.GetFromJsonAsync<HistoryPage>($"/api/v1/history?movieId={movie.Id}"))!;
        Assert.Contains(history.Records, r => r.EventType == HistoryEventType.Imported);

        // Rename after the format changed.
        var settings = (await client.GetFromJsonAsync<JsonObject>("/api/v1/settings/media-management"))!;
        settings["standardMovieFormat"] = "{Movie Title} [{Quality Title}]";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/settings/media-management", settings)).StatusCode);
        var preview = (await client.GetFromJsonAsync<RenamePreview[]>($"/api/v1/rename/movie/{movie.Id}"))!;
        Assert.Equal("Dune [Bluray-1080p].mkv", Assert.Single(preview).NewPath);
        var renamed = (await (await client.PostAsync($"/api/v1/rename/movie/{movie.Id}", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(1, renamed["renamed"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(movie.FolderPath, "Dune [Bluray-1080p].mkv")));
    }

    [Fact]
    public async Task Failed_imports_show_in_pending_and_as_a_notice_until_dismissed()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await RootFolderAsync(client, "movies", "Movie");
        var movie = (await (await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root })).Content.ReadFromJsonAsync<MovieResource>())!;
        var output = Path.Combine(_media, "complete", "Dune.2021.1080p-EMPTY");
        Directory.CreateDirectory(output);
        var id = AddFinished("Dune.2021.1080p-EMPTY", DownloadStatus.Completed, output, MediaType.Movie, movie.Id);

        await client.PostAsync($"/api/v1/import/downloads/{id}", null);
        await WaitForJobsAsync(client);

        var pending = Assert.Single((await client.GetFromJsonAsync<PendingImportResource[]>("/api/v1/import/pending"))!);
        Assert.Equal("failed", pending.Import.State);
        Assert.Contains("No video file found", pending.Import.Message, StringComparison.Ordinal);
        Assert.Equal(id, pending.Download.Id);

        var ongoing = (await client.GetFromJsonAsync<JsonObject>("/api/v1/status/ongoing"))!;
        Assert.Contains(ongoing["notices"]!.AsArray(), n => n!["key"]!.GetValue<string>().StartsWith($"import-failed-{id}-", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/import/downloads/{id}/dismiss", null)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PendingImportResource[]>("/api/v1/import/pending"))!);
        ongoing = (await client.GetFromJsonAsync<JsonObject>("/api/v1/status/ongoing"))!;
        Assert.DoesNotContain(ongoing["notices"]!.AsArray(), n => n!["key"]!.GetValue<string>().StartsWith("import-failed-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Manual_import_checks_the_choice_and_imports()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await RootFolderAsync(client, "movies", "Movie");
        var movie = (await (await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root })).Content.ReadFromJsonAsync<MovieResource>())!;
        var output = Path.Combine(_media, "complete", "upload");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "Dune.2021.720p.WEB.mkv"), "video");
        var id = AddFinished("upload", DownloadStatus.Completed, output, null, null);

        var noLink = await client.PostAsync($"/api/v1/import/downloads/{id}", null);
        Assert.Equal(HttpStatusCode.BadRequest, noLink.StatusCode);

        var files = (await client.GetFromJsonAsync<ImportFileCandidate[]>($"/api/v1/import/downloads/{id}/files"))!;
        Assert.Equal("Dune.2021.720p.WEB.mkv", Assert.Single(files).RelativePath);
        Assert.Equal("WEBDL-720p", files[0].Quality);

        var wrong = await client.PostAsJsonAsync($"/api/v1/import/downloads/{id}/manual", new { mediaType = "Movie", movieId = 9999, files = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await client.PostAsJsonAsync($"/api/v1/import/downloads/{id}/manual", new { mediaType = "Movie", movieId = movie.Id, files = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        await WaitForJobsAsync(client);

        var state = Assert.Single((await client.GetFromJsonAsync<ImportStateResource[]>($"/api/v1/import/downloads?ids={id}"))!);
        Assert.Equal("imported", state.State);
        Assert.True(state.Manual);
        Assert.Equal(404, (int)(await client.PostAsync("/api/v1/import/downloads/9999", null)).StatusCode);
    }

    [Fact]
    public async Task Blocklist_can_be_listed_and_cleared()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var repo = _factory.Services.GetRequiredService<BlocklistRepository>();
        var first = repo.Add(new BlocklistItem { Date = DateTime.UtcNow.AddHours(-1), SourceTitle = "Dune.2021.1080p-BAD", MediaType = MediaType.Movie, MovieId = 1, QualityId = 7, Message = "Missing articles" });
        repo.Add(new BlocklistItem { Date = DateTime.UtcNow, SourceTitle = "Arrival.2016.1080p-BAD", MediaType = MediaType.Movie, MovieId = 2 });

        var page = (await client.GetFromJsonAsync<BlocklistPage>("/api/v1/blocklist"))!;
        Assert.Equal(2, page.TotalRecords);
        Assert.Equal(["Arrival.2016.1080p-BAD", "Dune.2021.1080p-BAD"], page.Records.Select(r => r.SourceTitle));
        Assert.Equal("Bluray-1080p", page.Records[1].Quality);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/blocklist/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/blocklist/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/v1/blocklist")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<BlocklistPage>("/api/v1/blocklist"))!.TotalRecords);
    }

    [Fact]
    public async Task New_movies_get_folders_from_the_folder_format_and_optionally_right_away()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await RootFolderAsync(client, "movies", "Movie");
        var settings = (await client.GetFromJsonAsync<JsonObject>("/api/v1/settings/media-management"))!;
        settings["movieFolderFormat"] = "{Movie Title} ({Release Year}) {tmdb-{TmdbId}}";
        settings["createEmptyFolders"] = true;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/settings/media-management", settings)).StatusCode);

        var movie = (await (await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 329865, rootFolderId = root })).Content.ReadFromJsonAsync<MovieResource>())!;

        Assert.Equal("Arrival (2016) {tmdb-329865}", Path.GetFileName(movie.FolderPath));
        Assert.True(Directory.Exists(movie.FolderPath));
    }

    [Fact]
    public async Task Rescan_unmonitors_movies_whose_file_was_deleted_when_wanted()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await RootFolderAsync(client, "movies", "Movie");
        var folder = Path.Combine(_media, "movies", "Dune (2021)");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Dune.2021.1080p.mkv"), "x");
        var movie = (await (await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root })).Content.ReadFromJsonAsync<MovieResource>())!;
        Assert.True(movie.HasFile);

        var settings = (await client.GetFromJsonAsync<JsonObject>("/api/v1/settings/media-management"))!;
        settings["unmonitorDeletedFiles"] = true;
        settings["deleteEmptyFolders"] = true;
        await client.PutAsJsonAsync("/api/v1/settings/media-management", settings);
        File.Delete(Path.Combine(folder, "Dune.2021.1080p.mkv"));

        await client.PostAsync("/api/v1/library/rescan", null);
        await WaitForJobsAsync(client);

        movie = (await client.GetFromJsonAsync<MovieResource>($"/api/v1/movies/{movie.Id}"))!;
        Assert.False(movie.HasFile);
        Assert.False(movie.Monitored);
        Assert.False(Directory.Exists(folder));
    }

    private int AddFinished(string title, DownloadStatus status, string output, MediaType? mediaType, int? movieId)
    {
        var repository = _factory.Services.GetRequiredService<DownloadRepository>();
        return repository.Add(
            new DownloadRecord
            {
                Title = title,
                Category = mediaType == MediaType.Movie ? DownloadCategory.Movie : DownloadCategory.Other,
                Status = status,
                MediaType = mediaType,
                MovieId = movieId,
                NzbPath = Path.Combine(_media, "x.nzb"),
                WorkFolder = Path.Combine(_media, "work"),
                OutputPath = output,
                Added = DateTime.UtcNow,
                Completed = DateTime.UtcNow,
            },
            []).Id;
    }

    private async Task<int> RootFolderAsync(HttpClient client, string name, string mediaType)
    {
        var path = Directory.CreateDirectory(Path.Combine(_media, name)).FullName;
        var response = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path, mediaType });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>();
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

        throw new TimeoutException("Jobs did not finish.");
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
