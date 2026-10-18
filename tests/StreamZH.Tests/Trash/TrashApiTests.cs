// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Host.Status;
using StreamZH.Library.Trash;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Trash;

/// <summary>The TRaSH endpoints over HTTP, with the fixture folder as local repository (no network).</summary>
public sealed class TrashApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> ReadyAsync()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var settings = await client.PutAsJsonAsync("/api/v1/settings/trash", new
        {
            enabled = true,
            repository = TrashTestBed.GuidesFolder,
            branch = "master",
            syncIntervalHours = 12,
            autoUpdate = true,
            keepScoreOverrides = true,
        });
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        return client;
    }

    [Fact]
    public async Task Settings_have_defaults_and_validate()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var defaults = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/trash");
        Assert.True(defaults.GetProperty("enabled").GetBoolean());
        Assert.Equal("https://github.com/TRaSH-Guides/Guides", defaults.GetProperty("repository").GetString());
        Assert.Equal("master", defaults.GetProperty("branch").GetString());
        Assert.Equal(24, defaults.GetProperty("syncIntervalHours").GetInt32());
        Assert.True(defaults.GetProperty("autoUpdate").GetBoolean());
        Assert.True(defaults.GetProperty("keepScoreOverrides").GetBoolean());

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/trash", new { repository = "not a url", branch = "", syncIntervalHours = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("repository", out _));
        Assert.True(errors.TryGetProperty("branch", out _));
        Assert.True(errors.TryGetProperty("syncIntervalHours", out _));

        var saved = await client.PutAsJsonAsync("/api/v1/settings/trash", new { repository = "https://github.com/me/Guides.git", syncIntervalHours = 6, autoUpdate = false });
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://github.com/me/Guides", body.GetProperty("repository").GetString());
        Assert.False(body.GetProperty("autoUpdate").GetBoolean());

        var task = _factory.Services.GetServices<StreamZH.Core.Jobs.IScheduledTask>().Single(t => t.Key == TrashSyncTask.TaskKey);
        Assert.Equal(TimeSpan.FromHours(6), task.Interval);
    }

    [Fact]
    public async Task Status_before_and_after_the_sync()
    {
        var client = await ReadyAsync();

        var before = await client.GetFromJsonAsync<JsonElement>("/api/v1/trash/status");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("commit").ValueKind);
        Assert.Equal(0, before.GetProperty("counts").GetProperty("movieFormats").GetInt32());
        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/v1/trash/profiles") ?? []);

        var sync = await client.PostAsync("/api/v1/trash/sync", null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var status = await sync.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, status.GetProperty("error").ValueKind);
        Assert.Equal(2, status.GetProperty("counts").GetProperty("movieProfiles").GetInt32());
        Assert.Equal(2, status.GetProperty("counts").GetProperty("seriesProfiles").GetInt32());
        Assert.True(status.GetProperty("counts").GetProperty("seriesFormats").GetInt32() > 50);
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("lastSync").ValueKind);
    }

    [Fact]
    public async Task Lists_of_profiles_formats_and_groups()
    {
        var client = await ReadyAsync();
        await client.PostAsync("/api/v1/trash/sync", null);

        var movies = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/trash/profiles?mediaType=movie");
        Assert.NotNull(movies);
        var german = movies.Single(p => p.GetProperty("trashId").GetString() == TrashTestBed.MovieGerman);
        Assert.Equal("German", german.GetProperty("group").GetString());
        Assert.Equal("german-hd-bluray-web", german.GetProperty("slug").GetString());
        Assert.Equal("movie", german.GetProperty("mediaType").GetString());
        Assert.Equal(4, (await client.GetFromJsonAsync<JsonElement[]>("/api/v1/trash/profiles"))!.Length);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/trash/profiles/{TrashTestBed.SeriesGerman}?mediaType=series");
        Assert.Equal("german", detail.GetProperty("scoreSet").GetString());
        Assert.Equal("Merged QPs", detail.GetProperty("cutoff").GetString());
        var merged = detail.GetProperty("items")[0];
        Assert.Equal("Merged QPs", merged.GetProperty("name").GetString());
        Assert.Equal(6, merged.GetProperty("items").GetArrayLength());
        Assert.Equal(6, merged.GetProperty("qualityIds").GetArrayLength());
        var formats = detail.GetProperty("formats").EnumerateArray().ToList();
        Assert.Contains(formats, f => f.GetProperty("name").GetString() == "German DL" && f.GetProperty("score").GetInt32() == 11000);
        Assert.Contains(formats, f => f.GetProperty("source").GetString() == "group");
        Assert.Contains(detail.GetProperty("groups").EnumerateArray(), g => g.GetProperty("default").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/trash/profiles/nope")).StatusCode);

        var cfs = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/trash/formats?mediaType=movie");
        Assert.NotNull(cfs);
        var dl = cfs.Single(f => f.GetProperty("name").GetString() == "German DL");
        Assert.Equal(-10000, dl.GetProperty("defaultScore").GetInt32());
        Assert.Equal(11000, dl.GetProperty("scores").GetProperty("german").GetInt32());
        Assert.False(dl.GetProperty("installed").GetBoolean());
        var repack = cfs.Single(f => f.GetProperty("name").GetString() == "Repack/Proper");
        Assert.Equal("Repacks/Proper (Required)", repack.GetProperty("category").GetString());

        var groups = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/trash/groups?mediaType=series");
        var unwanted = groups!.Single(g => g.GetProperty("trashId").GetString() == "6f0872eebfc95b1f93474b7ac866ced0");
        Assert.True(unwanted.GetProperty("default").GetBoolean());
        Assert.Contains(unwanted.GetProperty("profiles").EnumerateArray(), p => p.GetString() == TrashTestBed.SeriesGerman);
        Assert.Contains(unwanted.GetProperty("formats").EnumerateArray(), f => f.GetProperty("default").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/trash/formats?mediaType=music")).StatusCode);
    }

    [Fact]
    public async Task Apply_creates_a_profile_and_downloads_the_guides_first()
    {
        var client = await ReadyAsync();

        var response = await client.PostAsJsonAsync("/api/v1/trash/apply", new
        {
            profileId = (int?)null,
            name = "Deutsch HD",
            movieTemplate = TrashTestBed.MovieGerman,
            seriesTemplate = TrashTestBed.SeriesGerman,
            extraGroups = new[] { "bc85e56ee3bd0f01467866d5f1261543" },
            applyQualitySizes = true,
            naming = new { movieFolder = "plex-tmdb" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("created").GetInt32() > 50);
        Assert.Equal(0, body.GetProperty("updated").GetInt32());
        var profile = body.GetProperty("profile");
        Assert.Equal("Deutsch HD", profile.GetProperty("name").GetString());
        var id = profile.GetProperty("id").GetInt32();

        var formats = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/trash/formats?mediaType=movie");
        Assert.True(formats!.Single(f => f.GetProperty("name").GetString() == "German DL").GetProperty("installed").GetBoolean());

        var again = await client.PostAsJsonAsync("/api/v1/trash/apply", new { profileId = id, movieTemplate = TrashTestBed.MovieGerman });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetInt32());

        var invalid = await client.PostAsJsonAsync("/api/v1/trash/apply", new { movieTemplate = "nope", naming = new { seriesFile = "nope" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("naming.seriesFile", out _));
    }

    [Fact]
    public async Task Recyclarr_preview_and_apply()
    {
        var client = await ReadyAsync();

        var preview = await client.PostAsJsonAsync("/api/v1/trash/recyclarr", new { yaml = RecyclarrImporterTests.Sample, apply = false });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var plan = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, plan.GetProperty("profiles").GetArrayLength());
        Assert.Equal("[German] HD Bluray + WEB", plan.GetProperty("profiles")[0].GetProperty("template").GetString());
        Assert.True(plan.GetProperty("warnings").GetArrayLength() > 0);

        var applied = await client.PostAsJsonAsync("/api/v1/trash/recyclarr", new { yaml = RecyclarrImporterTests.Sample, apply = true });
        Assert.True((await applied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetBoolean());
        var profiles = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/qualityprofiles");
        Assert.Contains(profiles!, p => p.GetProperty("name").GetString() == "Kids");

        var broken = await client.PostAsJsonAsync("/api/v1/trash/recyclarr", new { yaml = "radarr: [", apply = false });
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
    }

    [Fact]
    public async Task Writes_need_an_admin_and_a_failed_sync_shows_a_notice()
    {
        var client = await _factory.CreateOwnerClientAsync();
        await client.PutAsJsonAsync("/api/v1/settings/trash", new { repository = "https://example.invalid/Guides" });

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/trash/sync", null)).StatusCode);

        // Downloads go through the test host's HTTP client factory; the fake host answers nothing useful.
        var store = _factory.Services.GetRequiredService<TrashGuideStore>();
        store.SetError("The TRaSH Guides could not be downloaded: test");
        var ongoing = _factory.Services.GetRequiredService<OngoingService>().Get();
        Assert.Contains(ongoing.Notices, n => n.Key == TrashNotices.Key);
    }
}
