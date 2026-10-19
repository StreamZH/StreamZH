// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Jobs;
using StreamZH.Core.Profiles;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;
using StreamZH.Library;

namespace StreamZH.Tests.Host;

public sealed class ProfileApiTests : IDisposable
{
    private const int DeutschHd = 4;
    private const int HdOnly = 2;

    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-media-").FullName;

    [Fact]
    public async Task Lists_qualities_languages_and_seeded_profiles()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var qualities = (await client.GetFromJsonAsync<JsonElement[]>("/api/v1/qualities"))!;
        Assert.Equal(30, qualities.Length);
        var remux = qualities.Single(q => q.GetProperty("name").GetString() == "Remux-2160p");
        Assert.Equal(31, remux.GetProperty("id").GetInt32());
        Assert.Equal("Bluray", remux.GetProperty("source").GetString());
        Assert.Equal("Remux", remux.GetProperty("modifier").GetString());
        Assert.Equal(2160, remux.GetProperty("resolution").GetInt32());

        var languages = (await client.GetFromJsonAsync<LanguageResource[]>("/api/v1/languages"))!;
        Assert.Equal(["Any", "Original"], languages.Take(2).Select(l => l.Name));
        Assert.Contains(languages, l => l.Name == "German" && l.Id == 4);
        Assert.DoesNotContain(languages, l => l.Name == "Unknown");

        var profiles = (await client.GetFromJsonAsync<QualityProfileResource[]>("/api/v1/qualityprofiles"))!;
        Assert.Equal(5, profiles.Length);
        var german = (await client.GetFromJsonAsync<QualityProfileResource>($"/api/v1/qualityprofiles/{DeutschHd}"))!;
        Assert.Equal("Deutsch 1080p", german.Name);
        Assert.Equal("Bluray-1080p", german.Items.Single(i => i.QualityId == german.CutoffQualityId).Name);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/qualityprofiles/999")).StatusCode);

        // 1.0: a new installation starts with HD-1080p (any language) as default.
        var settings = (await client.GetFromJsonAsync<ProfileSettings>("/api/v1/settings/profiles"))!;
        Assert.Equal(HdOnly, settings.DefaultQualityProfileId);

        var schema = (await client.GetFromJsonAsync<QualityProfileResource>("/api/v1/qualityprofiles/schema"))!;
        Assert.Equal(0, schema.Id);
        Assert.Equal(30, schema.Items.Count);
        Assert.All(schema.Items, i => Assert.False(i.Allowed));

        var definitions = (await client.GetFromJsonAsync<QualityDefinition[]>("/api/v1/qualitydefinitions"))!;
        Assert.Equal(30, definitions.Length);
        Assert.Equal((0d, (double?)100, (double?)95), definitions.Where(d => d.Title == "WEBDL-720p").Select(d => (d.MinSize, d.MaxSize, d.PreferredSize)).Single());
    }

    [Fact]
    public async Task Creates_updates_and_deletes_a_profile()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var create = await client.PostAsJsonAsync("/api/v1/qualityprofiles", new
        {
            name = "Anime",
            items = new[] { new { qualityId = 3, allowed = true }, new { qualityId = 7, allowed = true }, new { qualityId = 31, allowed = false } },
            cutoffQualityId = 7,
            languageId = 8,
            preferDualLanguage = true,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var anime = (await create.Content.ReadFromJsonAsync<QualityProfileResource>())!;
        Assert.Equal(["WEBDL-1080p", "Bluray-1080p", "Remux-2160p"], anime.Items.Select(i => i.Name));
        Assert.True(anime.UpgradeAllowed);
        Assert.True(anime.PreferDualLanguage);
        Assert.Equal($"/api/v1/qualityprofiles/{anime.Id}", create.Headers.Location?.ToString());

        // Reorder and rename; the cutoff moves with the allowed qualities.
        var update = await client.PutAsJsonAsync($"/api/v1/qualityprofiles/{anime.Id}", new
        {
            id = anime.Id,
            name = "Anime 4K",
            items = new[] { new { qualityId = 7, allowed = true }, new { qualityId = 3, allowed = false }, new { qualityId = 31, allowed = true } },
            cutoffQualityId = 31,
            upgradeAllowed = false,
            languageId = -2,
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await client.GetFromJsonAsync<QualityProfileResource>($"/api/v1/qualityprofiles/{anime.Id}"))!;
        Assert.Equal("Anime 4K", updated.Name);
        Assert.Equal([7, 3, 31], updated.Items.Select(i => i.QualityId));
        Assert.False(updated.UpgradeAllowed);
        Assert.False(updated.PreferDualLanguage);
        Assert.Equal(-2, updated.LanguageId);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/v1/qualityprofiles/999", new { name = "x" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/qualityprofiles/{anime.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/qualityprofiles/{anime.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/qualityprofiles/{anime.Id}")).StatusCode);
    }

    [Fact]
    public async Task Rejects_invalid_profiles_with_field_errors()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var empty = await client.PostAsJsonAsync("/api/v1/qualityprofiles", new { name = " " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(["cutoffQualityId", "items", "languageId", "name"], await ErrorKeysAsync(empty));

        var duplicate = await client.PostAsJsonAsync("/api/v1/qualityprofiles", new
        {
            name = "hd-1080P",
            items = new[] { new { qualityId = 7, allowed = true } },
            cutoffQualityId = 3,
            languageId = 0,
        });
        Assert.Equal(["cutoffQualityId", "languageId", "name"], await ErrorKeysAsync(duplicate));

        var unknown = await client.PostAsJsonAsync("/api/v1/qualityprofiles", new
        {
            name = "Broken",
            items = new[] { new { qualityId = 7, allowed = true }, new { qualityId = 77, allowed = true } },
            cutoffQualityId = 7,
            languageId = -1,
        });
        Assert.Equal(["items"], await ErrorKeysAsync(unknown));

        var twice = await client.PostAsJsonAsync("/api/v1/qualityprofiles", new
        {
            name = "Broken",
            items = new[] { new { qualityId = 7, allowed = true }, new { qualityId = 7, allowed = false } },
            cutoffQualityId = 7,
            languageId = -1,
        });
        Assert.Equal(["items"], await ErrorKeysAsync(twice));

        // Keeping its own name is fine when updating.
        var german = (await client.GetFromJsonAsync<QualityProfileResource>($"/api/v1/qualityprofiles/{DeutschHd}"))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/qualityprofiles/{DeutschHd}", german)).StatusCode);
    }

    [Fact]
    public async Task Delete_is_blocked_while_in_use_and_for_the_last_profile()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await AddRootFolderAsync(client, "movies", "Movie");

        Assert.True((await client.PutAsJsonAsync("/api/v1/settings/profiles", new { defaultQualityProfileId = DeutschHd })).IsSuccessStatusCode);
        var add = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root.Id, qualityProfileId = HdOnly });
        var movie = (await add.Content.ReadFromJsonAsync<MovieResource>())!;
        Assert.Equal(HdOnly, movie.QualityProfileId);

        var blocked = await client.DeleteAsync($"/api/v1/qualityprofiles/{HdOnly}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("in-use", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Deleting the default profile picks another one as default.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/qualityprofiles/{DeutschHd}")).StatusCode);
        var settings = (await client.GetFromJsonAsync<ProfileSettings>("/api/v1/settings/profiles"))!;
        Assert.NotEqual(DeutschHd, settings.DefaultQualityProfileId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/qualityprofiles/{settings.DefaultQualityProfileId}")).StatusCode);

        await client.DeleteAsync($"/api/v1/movies/{movie.Id}");
        foreach (var id in new[] { 1, 3, 5 })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/qualityprofiles/{id}")).StatusCode);
        }

        var last = await client.DeleteAsync($"/api/v1/qualityprofiles/{HdOnly}");
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Equal(HdOnly, (await client.GetFromJsonAsync<ProfileSettings>("/api/v1/settings/profiles"))!.DefaultQualityProfileId);
    }

    [Fact]
    public async Task Default_profile_is_used_for_new_movies_and_can_be_changed()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await AddRootFolderAsync(client, "movies", "Movie");

        var dune = (await (await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root.Id })).Content.ReadFromJsonAsync<MovieResource>())!;
        Assert.Equal(HdOnly, dune.QualityProfileId);

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/profiles", new { defaultQualityProfileId = 999 });
        Assert.Equal(["defaultQualityProfileId"], await ErrorKeysAsync(invalid));

        var saved = await client.PutAsJsonAsync("/api/v1/settings/profiles", new { defaultQualityProfileId = 3 });
        Assert.Equal(3, (await saved.Content.ReadFromJsonAsync<ProfileSettings>())!.DefaultQualityProfileId);

        var arrival = (await (await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 329865, rootFolderId = root.Id })).Content.ReadFromJsonAsync<MovieResource>())!;
        Assert.Equal(3, arrival.QualityProfileId);

        var tv = await AddRootFolderAsync(client, "tv", "Series");
        var unknownProfile = await client.PostAsJsonAsync("/api/v1/series", new { tmdbId = 1396, rootFolderId = tv.Id, qualityProfileId = 999 });
        Assert.Equal(HttpStatusCode.BadRequest, unknownProfile.StatusCode);
        Assert.Equal("Quality profile not found.", (await unknownProfile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Assigns_a_profile_to_a_movie_and_a_series()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var movies = await AddRootFolderAsync(client, "movies", "Movie");
        var tv = await AddRootFolderAsync(client, "tv", "Series");

        var movie = (await (await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = movies.Id })).Content.ReadFromJsonAsync<MovieResource>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/v1/movies/{movie.Id}/profile", new { qualityProfileId = 5 })).StatusCode);
        Assert.Equal(5, (await client.GetFromJsonAsync<MovieResource>($"/api/v1/movies/{movie.Id}"))!.QualityProfileId);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/v1/movies/{movie.Id}/profile", new { qualityProfileId = 999 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/v1/movies/999/profile", new { qualityProfileId = 1 })).StatusCode);

        var add = await client.PostAsJsonAsync("/api/v1/series", new { tmdbId = 1396, rootFolderId = tv.Id, qualityProfileId = 3 });
        var series = (await add.Content.ReadFromJsonAsync<SeriesResource>())!;
        Assert.Equal(3, series.QualityProfileId);
        await WaitForJobsAsync(client);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/v1/series/{series.Id}/profile", new { qualityProfileId = 1 })).StatusCode);
        var detail = (await client.GetFromJsonAsync<SeriesDetailResource>($"/api/v1/series/{series.Id}"))!;
        Assert.Equal(1, detail.Series.QualityProfileId);
        Assert.Equal(20, detail.Series.EpisodeCount);
    }

    [Fact]
    public async Task Import_uses_the_chosen_profile()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var root = await AddRootFolderAsync(client, "movies", "Movie");
        Directory.CreateDirectory(Path.Combine(root.Path, "Arrival (2016)"));
        Directory.CreateDirectory(Path.Combine(root.Path, "Dune (2021)"));

        var invalid = await client.PostAsJsonAsync("/api/v1/library-import", new
        {
            rootFolderId = root.Id,
            monitored = true,
            qualityProfileId = 999,
            items = new[] { new { folderName = "Arrival (2016)", tmdbId = 329865 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var import = await client.PostAsJsonAsync("/api/v1/library-import", new
        {
            rootFolderId = root.Id,
            monitored = true,
            qualityProfileId = 1,
            items = new[] { new { folderName = "Arrival (2016)", tmdbId = 329865 } },
        });
        Assert.Equal(HttpStatusCode.Accepted, import.StatusCode);
        await client.PostAsJsonAsync("/api/v1/library-import", new
        {
            rootFolderId = root.Id,
            monitored = true,
            items = new[] { new { folderName = "Dune (2021)", tmdbId = 438631 } },
        });
        await WaitForJobsAsync(client);

        var all = (await client.GetFromJsonAsync<MovieResource[]>("/api/v1/movies"))!;
        Assert.Equal([("Arrival", 1), ("Dune", HdOnly)], all.Select(m => (m.Title, m.QualityProfileId)));
    }

    [Fact]
    public async Task Updates_size_limits_in_bulk()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var invalid = await client.PutAsJsonAsync("/api/v1/qualitydefinitions", new object[]
        {
            new { qualityId = 3, minSize = -1, maxSize = 10, preferredSize = 5 },
            new { qualityId = 7, minSize = 20, maxSize = 10, preferredSize = 50 },
            new { qualityId = 77, minSize = 0 },
        });
        Assert.Equal(["[0].minSize", "[1].maxSize", "[1].preferredSize", "[2].qualityId"], await ErrorKeysAsync(invalid));

        var saved = await client.PutAsJsonAsync("/api/v1/qualitydefinitions", new object[]
        {
            new { qualityId = 3, title = "WEB 1080p", minSize = 5, maxSize = 60, preferredSize = 30 },
            new { qualityId = 31, minSize = 50, maxSize = (double?)null, preferredSize = (double?)null },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var all = (await saved.Content.ReadFromJsonAsync<QualityDefinition[]>())!;
        Assert.Equal(new QualityDefinition(3, "WEB 1080p", 5, 60, 30), all.Single(d => d.QualityId == 3));
        Assert.Equal(new QualityDefinition(31, "Remux-2160p", 50, null, null), all.Single(d => d.QualityId == 31));
        Assert.Equal(new QualityDefinition(5, "WEBDL-720p", 0, 100, 95), all.Single(d => d.QualityId == 5));
    }

    [Fact]
    public async Task Friends_can_read_but_not_change_profiles()
    {
        await _factory.CreateOwnerClientAsync();
        var passwords = _factory.Services.GetRequiredService<PasswordService>();
        _factory.Services.GetRequiredService<UserRepository>().Create("friend", passwords.Hash("friend password 123"), UserRole.Friend);

        var friend = _factory.CreateClient();
        (await friend.PostAsJsonAsync("/api/v1/auth/login", new { username = "friend", password = "friend password 123" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/qualityprofiles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/settings/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/qualitydefinitions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.DeleteAsync("/api/v1/qualityprofiles/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PutAsJsonAsync("/api/v1/settings/profiles", new { defaultQualityProfileId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PutAsJsonAsync("/api/v1/qualitydefinitions", Array.Empty<object>())).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/qualityprofiles")).StatusCode);
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
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

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_media, recursive: true);
    }
}
