// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Library.Migration;
using StreamZH.Tests.Library.Migration;

namespace StreamZH.Tests.Host;

/// <summary>The Radarr settings sections through POST /api/v1/migration/radarr/preview, against a fake Radarr v5.</summary>
public sealed class MigrationSettingsApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();
    private readonly ArrFixtureHandler _radarr = new() { App = "radarr5" };
    private readonly string _movies = Path.Combine(Path.GetTempPath(), $"streamzh-migration-api-{Guid.NewGuid():N}", "movies");

    [Fact]
    public async Task Radarr_preview_lists_the_settings_sections_with_their_items()
    {
        Directory.CreateDirectory(_movies);
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<ArrApiClient>().ConfigurePrimaryHttpMessageHandler(() => _radarr)));
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/v1/migration/radarr/preview", new
        {
            url = "http://radarr:7878",
            apiKey = ArrFixtureHandler.ApiKey,
            pathMappings = new[] { new { remote = "/movies", local = _movies } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("5.26.2.10099", root.GetProperty("app").GetProperty("version").GetString());
        string[] keys =
            ["rootFolders", "qualityDefinitions", "tags", "customFormats", "qualityProfiles", "releaseProfiles", "naming", "mediaManagement", "notifications", "importListExclusions"];
        var sections = root.GetProperty("sections").EnumerateArray()
            .Where(s => keys.Contains(s.GetProperty("key").GetString()))
            .ToDictionary(s => s.GetProperty("key").GetString()!);
        Assert.Equal(keys, sections.Keys);
        Assert.All(sections.Values, s => Assert.DoesNotContain(s.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.StartsWith("Could not read", StringComparison.Ordinal)));

        var profile = sections["qualityProfiles"].GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == "qualityProfiles:4");
        Assert.Equal("update", profile.GetProperty("action").GetString());
        Assert.Equal("Deutsch 1080p", profile.GetProperty("label").GetString());
        Assert.Equal(6, sections["customFormats"].GetProperty("counts").GetProperty("create").GetInt32());
        var plex = sections["notifications"].GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == "notifications:1");
        Assert.True(plex.GetProperty("needsSecret").GetBoolean());

        var paths = root.GetProperty("paths").EnumerateArray().ToList();
        Assert.Contains(paths, p => p.GetProperty("remote").GetString() == "/movies" && p.GetProperty("exists").GetBoolean());
        Assert.Contains(paths, p => p.GetProperty("remote").GetString() == "/movies-uhd" && !p.GetProperty("exists").GetBoolean());

        // The preview only read Radarr.
        Assert.All(_radarr.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Contains(_radarr.Requests, r => r.Path == "/api/v3/qualityprofile");
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(Path.GetDirectoryName(_movies)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
