// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Indexers;
using StreamZH.Core.Quality;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Tests.Host;

/// <summary>1.0 RC: the "Get started" checklist follows the real configuration and is for owners and admins only.</summary>
public sealed class SetupGuideApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient()) { TempFolderIsStorage = false };
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-guide-").FullName;

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_media, recursive: true);
    }

    private static Dictionary<string, bool> Steps(JsonElement guide) =>
        guide.GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("done").GetBoolean());

    [Fact]
    public async Task Steps_are_done_when_the_configuration_has_them()
    {
        var owner = await _factory.CreateOwnerClientAsync();

        var fresh = await owner.GetFromJsonAsync<JsonElement>("/api/v1/setup/guide");
        Assert.Equal(["storage", "rootFolders", "tmdb", "language", "indexers", "downloads"], Steps(fresh).Keys);
        Assert.False(Steps(fresh)["storage"]);
        Assert.False(Steps(fresh)["rootFolders"]);
        Assert.True(Steps(fresh)["tmdb"]);
        Assert.Equal(1, fresh.GetProperty("done").GetInt32());
        Assert.Equal(6, fresh.GetProperty("total").GetInt32());
        Assert.False(Steps(fresh)["language"]);
        Assert.Equal("any", fresh.GetProperty("details").GetProperty("releaseLanguage").GetString());
        Assert.Equal("HD-1080p", fresh.GetProperty("details").GetProperty("defaultProfile").GetString());
        Assert.False(fresh.GetProperty("complete").GetBoolean());
        Assert.False(fresh.GetProperty("dismissed").GetBoolean());
        Assert.True(fresh.GetProperty("isOwner").GetBoolean());

        // The owner adds a media folder as storage location, a root folder, an indexer and a Usenet server.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/settings/storage", new { path = _media })).StatusCode);
        var movies = Directory.CreateDirectory(Path.Combine(_media, "Movies")).FullName;
        Assert.True((await owner.PostAsJsonAsync("/api/v1/rootfolders", new { path = movies, mediaType = "Movie" })).IsSuccessStatusCode);
        _factory.Services.GetRequiredService<IndexerRepository>().Add(
            new IndexerDefinition(0, "Indexer", IndexerDefinition.Newznab, "https://indexer.example.com", "/api", "key", [2000], [5000], 25, true, true, true),
            DateTime.UtcNow);
        Assert.True((await owner.PostAsJsonAsync("/api/v1/usenet/server", new { name = "Provider", host = "news.example.com", username = "johndoe", password = "secret" })).IsSuccessStatusCode);

        Assert.True((await owner.PutAsJsonAsync("/api/v1/setup/guide/language", new { language = "original" })).IsSuccessStatusCode);

        var done = await owner.GetFromJsonAsync<JsonElement>("/api/v1/setup/guide");
        Assert.All(Steps(done).Values, Assert.True);
        Assert.True(done.GetProperty("complete").GetBoolean());
        var details = done.GetProperty("details");
        Assert.Equal(_media, details.GetProperty("storageLocations")[0].GetString());
        Assert.Equal(1, details.GetProperty("movieRootFolders").GetInt32());
        Assert.Equal(0, details.GetProperty("seriesRootFolders").GetInt32());
        Assert.Equal(1, details.GetProperty("usenetServers").GetInt32());
        Assert.False(details.GetProperty("migrated").GetBoolean());
    }

    [Fact]
    public async Task Dismissing_is_kept_for_the_instance_and_friends_never_see_the_guide()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var admin = await _factory.CreateUserClientAsync("admin", UserRole.Admin);
        var friend = await _factory.CreateUserClientAsync("friend", UserRole.Friend);
        var requester = await _factory.CreateUserClientAsync("requester", UserRole.Requester);

        var forAdmin = await admin.GetFromJsonAsync<JsonElement>("/api/v1/setup/guide");
        Assert.False(forAdmin.GetProperty("isOwner").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.GetAsync("/api/v1/setup/guide")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/setup/guide")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PutAsJsonAsync("/api/v1/setup/guide", new { dismissed = true })).StatusCode);

        var hidden = await admin.PutAsJsonAsync("/api/v1/setup/guide", new { dismissed = true });
        Assert.True((await hidden.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dismissed").GetBoolean());
        Assert.True((await owner.GetFromJsonAsync<JsonElement>("/api/v1/setup/guide")).GetProperty("dismissed").GetBoolean());
        Assert.Equal("true", _factory.Services.GetRequiredService<ConfigRepository>().Get(ConfigKeys.SetupGuideDismissed));

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/setup/guide", new { })).StatusCode);
        var shown = await owner.PutAsJsonAsync("/api/v1/setup/guide", new { dismissed = false });
        Assert.False((await shown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("dismissed").GetBoolean());
    }

    [Fact]
    public async Task The_release_language_sets_the_default_quality_profile()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var profiles = _factory.Services.GetRequiredService<QualityProfileRepository>();
        var config = _factory.Services.GetRequiredService<ConfigRepository>();

        var german = await owner.PutAsJsonAsync("/api/v1/setup/guide/language", new { language = "german" });
        var guide = await german.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(Steps(guide)["language"]);
        Assert.Equal("german", guide.GetProperty("details").GetProperty("releaseLanguage").GetString());
        Assert.Equal(QualityDefaults.GermanProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture), config.Get(ConfigKeys.DefaultQualityProfileId));

        await owner.PutAsJsonAsync("/api/v1/setup/guide/language", new { language = "original" });
        Assert.Equal(QualityDefaults.DefaultProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture), config.Get(ConfigKeys.DefaultQualityProfileId));
        Assert.Equal(Language.Original.Id, profiles.Find(QualityDefaults.DefaultProfileId)!.LanguageId);

        var any = await (await owner.PutAsJsonAsync("/api/v1/setup/guide/language", new { language = "any" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("any", any.GetProperty("details").GetProperty("releaseLanguage").GetString());
        Assert.Equal(Language.Any.Id, profiles.Find(QualityDefaults.DefaultProfileId)!.LanguageId);

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/setup/guide/language", new { language = "klingon" })).StatusCode);
        var friend = await _factory.CreateUserClientAsync("friend", UserRole.Friend);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PutAsJsonAsync("/api/v1/setup/guide/language", new { language = "any" })).StatusCode);
    }
}
