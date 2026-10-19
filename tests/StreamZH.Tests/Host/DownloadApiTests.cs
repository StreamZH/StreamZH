// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;
using StreamZH.Tests.Library.Downloads;
using StreamZH.Usenet.Pool;

namespace StreamZH.Tests.Host;

public sealed class DownloadApiTests : IDisposable
{
    private readonly StreamZhFactory _base = new(new FakeTmdbClient());
    private readonly FakeArticleSource _articles = new() { MaxConnections = 2 };
    private readonly FakePostProcessor _postProcessor = new();
    private readonly WebApplicationFactory<Program> _factory;

    public DownloadApiTests()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IArticleSource>(_articles);
            services.AddSingleton<IPostProcessor>(_postProcessor);
            services.AddSingleton<IFreeSpaceProvider>(new FakeFreeSpace());
            services.AddSingleton(new DownloadEngineOptions { Tick = TimeSpan.FromMilliseconds(50) });
        }));
    }

    [Fact]
    public async Task Upload_queue_commands_history_and_delete()
    {
        var client = await OwnerClientAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/download/pause-all", null)).StatusCode);

        var release = TestReleases.Build(_articles, "Upload.Test.2024.1080p.WEB-GRP", ("data.mkv", 20_000, 4000), ("data.nfo", 100, 4000));
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(release.Nzb), "file", "Upload.Test.2024.1080p.WEB-GRP{{pw}}.nzb" },
            { new StringContent("series"), "category" },
        };
        var created = await client.PostAsync("/api/v1/download", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = item.GetProperty("id").GetInt32();
        Assert.Equal($"/api/v1/download/{id}", created.Headers.Location?.ToString());
        Assert.Equal(("Upload.Test.2024.1080p.WEB-GRP", "series", "queued"), (Text(item, "title"), Text(item, "category"), Text(item, "status")));
        Assert.False(item.TryGetProperty("files", out _));

        var queue = await client.GetFromJsonAsync<JsonElement>("/api/v1/download");
        Assert.True(queue.GetProperty("paused").GetBoolean());
        Assert.Equal(0, queue.GetProperty("speedLimitKBps").GetInt64());
        Assert.True(queue.GetProperty("freeSpaceBytes").GetInt64() > 0);
        var listed = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal(id, listed.GetProperty("id").GetInt32());
        Assert.Equal(0, listed.GetProperty("progress").GetDouble());

        var updated = await client.PutAsJsonAsync($"/api/v1/download/{id}", new { priority = 1, category = "movie" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var changed = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((1, "movie"), (changed.GetProperty("priority").GetInt32(), Text(changed, "category")));

        var invalid = await client.PutAsJsonAsync($"/api/v1/download/{id}", new { priority = 5, category = "music" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("priority", out _));
        Assert.True(errors.TryGetProperty("category", out _));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/download/{id}/pause", null)).StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/download/{id}");
        Assert.Equal("paused", Text(detail, "status"));
        var files = detail.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(["data.mkv", "data.nfo"], files.Select(f => Text(f, "name")));
        Assert.Equal(["other", "nfo"], files.Select(f => Text(f, "kind")));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/download/{id}/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/download/{id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/download/999/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/download/resume-all", null)).StatusCode);

        await DownloadTestBed.WaitUntilAsync(() => client.GetFromJsonAsync<JsonElement>($"/api/v1/download/{id}").Result.GetProperty("status").GetString() == "completed");
        Assert.Equal("pw", Assert.Single(_postProcessor.Jobs).Password);

        var history = await client.GetFromJsonAsync<JsonElement>("/api/v1/download/history?page=1&pageSize=10");
        Assert.Equal(1, history.GetProperty("totalRecords").GetInt32());
        var record = Assert.Single(history.GetProperty("records").EnumerateArray());
        Assert.Equal(("completed", 1.0), (Text(record, "status"), record.GetProperty("progress").GetDouble()));
        Assert.EndsWith(Path.Combine("movies", "Upload.Test.2024.1080p.WEB-GRP"), Text(record, "outputPath"), StringComparison.Ordinal);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/v1/download")).GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/download/{id}?deleteFiles=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/download/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/download/{id}")).StatusCode);
    }

    [Fact]
    public async Task Upload_rejects_missing_and_invalid_files()
    {
        var client = await OwnerClientAsync();

        var json = await client.PostAsJsonAsync("/api/v1/download", new { file = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);

        using var noFile = new MultipartFormDataContent { { new StringContent("Some title"), "title" } };
        var missing = await client.PostAsync("/api/v1/download", noFile);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.True((await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("file", out _));

        using var html = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("<html>login</html>")), "file", "x.nzb" } };
        var notNzb = await client.PostAsync("/api/v1/download", html);
        Assert.Equal(HttpStatusCode.BadRequest, notNzb.StatusCode);
        Assert.StartsWith("This is not a valid NZB file", Text(await notNzb.Content.ReadFromJsonAsync<JsonElement>(), "detail"), StringComparison.Ordinal);

        var release = TestReleases.Build(_articles, "Category.Test", ("a.bin", 100, 4000));
        using var badCategory = new MultipartFormDataContent
        {
            { new ByteArrayContent(release.Nzb), "file", "a.nzb" },
            { new StringContent("music"), "category" },
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/download", badCategory)).StatusCode);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/download")).StatusCode);
    }

    [Fact]
    public async Task Download_settings_choose_the_client_folders_and_speed_limit()
    {
        var client = await OwnerClientAsync();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/downloads");
        Assert.Equal("builtin", Text(settings, "client"));
        Assert.Equal(Path.Combine(_base.DataDirectory, "downloads", "incomplete"), Text(settings, "incompleteFolder"));
        Assert.Equal(Path.Combine(_base.DataDirectory, "downloads", "complete"), Text(settings, "completeFolder"));
        Assert.Equal(Text(settings, "completeFolder"), Text(settings, "defaultCompleteFolder"));
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("incompleteProblem").ValueKind);
        Assert.Equal(0, settings.GetProperty("speedLimitKBps").GetInt64());

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/downloads", new { client = "sabnzbd", completeFolder = "relative", speedLimitKBps = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(["client", "completeFolder", "speedLimitKBps"], errors.EnumerateObject().Select(p => p.Name).Order());

        var complete = Path.Combine(_base.DataDirectory, "downloads", "finished");
        var saved = await client.PutAsJsonAsync("/api/v1/settings/downloads", new { client = "blackhole", completeFolder = complete, speedLimitKBps = 2048 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var result = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("blackhole", complete, 2048L), (Text(result, "client"), Text(result, "completeFolder"), result.GetProperty("speedLimitKBps").GetInt64()));
        Assert.Equal(2048, (await client.GetFromJsonAsync<JsonElement>("/api/v1/download")).GetProperty("speedLimitKBps").GetInt64());
    }

    [Fact]
    public async Task Download_expert_settings_keep_todays_behaviour_by_default_and_reach_the_engine()
    {
        var client = await OwnerClientAsync();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/downloads");
        Assert.Equal(1, settings.GetProperty("minFreeSpaceGb").GetDouble());
        Assert.False(settings.GetProperty("keepFiles").GetBoolean());
        Assert.True(settings.GetProperty("abortHopeless").GetBoolean());
        Assert.Equal(3, settings.GetProperty("articleTries").GetInt32());
        Assert.Equal(3, _factory.Services.GetRequiredService<UsenetPoolOptions>().Attempts);

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/downloads", new { minFreeSpaceGb = -1, articleTries = 11, keepFiles = true });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(["articleTries", "minFreeSpaceGb"], errors.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(_factory.Services.GetRequiredService<DownloadSettings>().KeepFiles); // nothing saved

        var saved = await client.PutAsJsonAsync("/api/v1/settings/downloads", new { minFreeSpaceGb = 2.5, keepFiles = true, abortHopeless = false, articleTries = 5 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var result = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2.5, result.GetProperty("minFreeSpaceGb").GetDouble());
        Assert.True(result.GetProperty("keepFiles").GetBoolean());
        Assert.False(result.GetProperty("abortHopeless").GetBoolean());
        Assert.Equal(5, result.GetProperty("articleTries").GetInt32());

        var downloads = _factory.Services.GetRequiredService<DownloadSettings>();
        Assert.Equal((long)(2.5 * 1024 * 1024 * 1024), downloads.MinFreeSpaceBytes);
        Assert.Equal(5, _factory.Services.GetRequiredService<UsenetPoolOptions>().Attempts);
    }

    private static string? Text(JsonElement element, string property) => element.GetProperty(property).GetString();

    private async Task<HttpClient> OwnerClientAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public void Dispose()
    {
        _articles.Release();
        _factory.Dispose();
        _base.Dispose();
    }
}
