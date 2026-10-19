// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;
using StreamZH.Tests.Library.Downloads;

namespace StreamZH.Tests.Host;

/// <summary>Settings → Downloads over the API: settings, categories, scheduler, and their use in the download API and the status window.</summary>
public sealed class DownloadSettingsApiTests : IDisposable
{
    private readonly StreamZhFactory _base = new(new FakeTmdbClient());
    private readonly FakeArticleSource _articles = new() { MaxConnections = 2 };
    private readonly WebApplicationFactory<Program> _factory;

    public DownloadSettingsApiTests()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IArticleSource>(_articles);
            services.AddSingleton<IPostProcessor>(new FakePostProcessor());
            services.AddSingleton<IFreeSpaceProvider>(new FakeFreeSpace());
            services.AddSingleton(new DownloadEngineOptions { Tick = TimeSpan.FromMilliseconds(50) });
        }));
    }

    [Fact]
    public async Task Settings_round_trip_and_validation()
    {
        var client = await OwnerClientAsync();
        var defaults = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/downloads");
        Assert.Equal("off", Text(defaults, "duplicateAction"));
        Assert.Equal(string.Empty, Text(defaults, "watchFolder"));
        Assert.Equal(5, defaults.GetProperty("watchIntervalSeconds").GetInt32());
        Assert.True(defaults.GetProperty("onlyVerified").GetBoolean());
        Assert.Equal("normal", Text(defaults, "toolPriority"));
        Assert.Contains(".nfo", Text(defaults, "cleanupList"));

        var watch = Path.Combine(_base.DataDirectory, "downloads", "watch");
        var saved = await client.PutAsJsonAsync("/api/v1/settings/downloads", new
        {
            watchFolder = watch,
            duplicateAction = "tag",
            preCheck = true,
            maxActiveDownloads = 1,
            sevenZipArguments = "-mmt2",
            toolPriority = "belowNormal",
            historyKeepDays = 14,
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(watch, Text(body, "watchFolder"));
        Assert.Equal(("tag", "-mmt2", "belowNormal"), (Text(body, "duplicateAction"), Text(body, "sevenZipArguments"), Text(body, "toolPriority")));
        Assert.True(body.GetProperty("preCheck").GetBoolean());
        Assert.Equal(14, body.GetProperty("historyKeepDays").GetInt32());
        Assert.Equal(0, body.GetProperty("speedLimitKBps").GetInt64()); // untouched

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/downloads", new { duplicateAction = "maybe", sevenZipArguments = "-o/tmp", postProcessWorkers = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("duplicateAction", out _));
        Assert.True(errors.TryGetProperty("sevenZipArguments", out _));
        Assert.True(errors.TryGetProperty("postProcessWorkers", out _));
    }

    [Fact]
    public async Task Categories_over_the_api_and_in_the_download_api()
    {
        var client = await OwnerClientAsync();
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/downloads/categories");
        Assert.Equal(["movies", "series", "other"], list.EnumerateArray().Select(c => Text(c, "name")));
        Assert.All(list.EnumerateArray(), c => Assert.True(c.GetProperty("isDefault").GetBoolean()));
        Assert.EndsWith(Path.Combine("complete", "movies"), Text(list[0], "path"));

        var created = await client.PostAsJsonAsync("/api/v1/settings/downloads/categories", new { name = "anime", folder = "Anime", priority = "high", postProcessing = "unpack" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var anime = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = anime.GetProperty("id").GetInt32();
        Assert.Equal(("high", "unpack", false), (Text(anime, "priority"), Text(anime, "postProcessing"), anime.GetProperty("isDefault").GetBoolean()));

        var invalid = await client.PostAsJsonAsync("/api/v1/settings/downloads/categories", new { name = "x", priority = "urgent", postProcessing = "all" });
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("priority", out _) && errors.TryGetProperty("postProcessing", out _));

        var updated = await client.PutAsJsonAsync($"/api/v1/settings/downloads/categories/{id}", new { name = "anime", folder = string.Empty, priority = "default", postProcessing = "full" });
        Assert.Equal("default", Text(await updated.Content.ReadFromJsonAsync<JsonElement>(), "priority"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/v1/settings/downloads/categories/{list[0].GetProperty("id").GetInt32()}")).StatusCode);

        // Pickers see it; an upload and a queue change can use it.
        var choices = await client.GetFromJsonAsync<JsonElement>("/api/v1/download/categories");
        Assert.Contains(choices.EnumerateArray(), c => Text(c, "name") == "anime" && Text(c, "kind") == "other");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/download/pause-all", null)).StatusCode);
        var release = TestReleases.Build(_articles, "Anime.Upload.2024-GRP", ("a.mkv", 4000, 4000));
        using var form = new MultipartFormDataContent { { new ByteArrayContent(release.Nzb), "file", "Anime.Upload.2024-GRP.nzb" }, { new StringContent("anime"), "category" } };
        var uploaded = await (await client.PostAsync("/api/v1/download", form)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("anime", "other"), (Text(uploaded, "categoryName"), Text(uploaded, "category")));
        var moved = await client.PutAsJsonAsync($"/api/v1/download/{uploaded.GetProperty("id").GetInt32()}", new { category = "series" });
        Assert.Equal("series", Text(await moved.Content.ReadFromJsonAsync<JsonElement>(), "categoryName"));
        var unknown = await client.PutAsJsonAsync($"/api/v1/download/{uploaded.GetProperty("id").GetInt32()}", new { category = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/settings/downloads/categories/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/settings/downloads/categories/{id}")).StatusCode);
    }

    [Fact]
    public async Task Scheduler_rules_over_the_api_can_pause_a_server_and_show_in_the_status_window()
    {
        var client = await OwnerClientAsync();
        var server = await (await client.PostAsJsonAsync("/api/v1/usenet/server", new { name = "Night", host = "news.example.com", username = "u", password = "p" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var serverId = server.GetProperty("id").GetInt32();

        var bad = await client.PostAsJsonAsync("/api/v1/settings/downloads/schedules", new { time = "25:00", days = new[] { "mon", "xyz" }, action = "explode" });
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("time", out _) && errors.TryGetProperty("days", out _) && errors.TryGetProperty("action", out _));

        var created = await client.PostAsJsonAsync(
            "/api/v1/settings/downloads/schedules",
            new { time = "01:00", days = new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" }, action = "pauseServer", argument = serverId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rule = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("01:00 every day: pauseServer " + serverId, Text(rule, "description"));
        await client.PostAsJsonAsync("/api/v1/settings/downloads/schedules", new { time = "01:00", days = new[] { "sun" }, action = "pause" });

        var services = _factory.Services;
        var clock = new ZonedClock(ZonedClock.At(2026, 10, 18, 2, 0)); // a Sunday
        var scheduler = new DownloadScheduler(
            services.GetRequiredService<DownloadScheduleService>(),
            services.GetRequiredService<DownloadEngine>(),
            services.GetRequiredService<DownloadSettings>(),
            services,
            new DownloadEngineOptions { Time = clock },
            NullLogger<DownloadScheduler>.Instance);
        await scheduler.ApplyLatestAsync(clock.GetUtcNow());

        var servers = await client.GetFromJsonAsync<JsonElement>("/api/v1/usenet/server");
        Assert.False(servers.EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == serverId).GetProperty("enabled").GetBoolean());
        var ongoing = await client.GetFromJsonAsync<JsonElement>("/api/v1/status/ongoing");
        Assert.Contains(ongoing.GetProperty("notices").EnumerateArray(), n => Text(n, "key") == "downloads-paused-schedule");

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/downloads/schedules");
        Assert.Equal(2, list.GetArrayLength());
        var ruleId = rule.GetProperty("id").GetInt32();
        var disabled = await client.PutAsJsonAsync($"/api/v1/settings/downloads/schedules/{ruleId}", new { enabled = false, time = "01:30", days = new[] { "sat" }, action = "resumeServer", argument = serverId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var changed = await disabled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("01:30", false), (Text(changed, "time"), changed.GetProperty("enabled").GetBoolean()));
        Assert.Equal(["sat"], changed.GetProperty("days").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/settings/downloads/schedules/{ruleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/settings/downloads/schedules/{ruleId}")).StatusCode);
    }

    private async Task<HttpClient> OwnerClientAsync()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
        return client;
    }

    private static string Text(JsonElement element, string property) => element.GetProperty(property).GetString() ?? string.Empty;

    public void Dispose()
    {
        _factory.Dispose();
        _base.Dispose();
    }
}
