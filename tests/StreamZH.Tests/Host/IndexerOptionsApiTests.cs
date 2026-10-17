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
using StreamZH.Library.Search;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Host;

/// <summary>Settings → Indexers: options, release profiles, the release test page, the release table and search on add.</summary>
public sealed class IndexerOptionsApiTests : IDisposable
{
    private readonly StreamZhFactory _base = new(new FakeTmdbClient());
    private readonly FakeIndexerProvider _indexers = new();
    private readonly FakeIndexerClient _client = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-media-").FullName;

    public IndexerOptionsApiTests()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IIndexerProvider>(_indexers);
            services.AddSingleton<IIndexerClient>(_client);
        }));
        _indexers.Indexers.Add(FakeIndexerProvider.Definition(1, "Alpha"));
    }

    [Fact]
    public async Task Options_have_defaults_validate_and_save()
    {
        var client = await OwnerClientAsync();

        var defaults = (await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/indexers"))!;
        Assert.Equal(60, defaults.GetProperty("rssSyncInterval").GetInt32());
        Assert.Equal("inCinemas", defaults.GetProperty("minimumAvailability").GetString());
        Assert.False(defaults.GetProperty("allowHardcodedSubs").GetBoolean());
        Assert.Equal(0, defaults.GetProperty("missingSearchInterval").GetInt32());

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/indexers", Options(rssSyncInterval: 5, missingSearchInterval: 2, minimumAge: -1, tags: ["a,b"]));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("Use 0 (off) or 10 to 120 minutes.", errors.GetProperty("rssSyncInterval")[0].GetString());
        Assert.Equal("Use 0 (off) or 6 to 720 hours.", errors.GetProperty("missingSearchInterval")[0].GetString());
        Assert.True(errors.TryGetProperty("minimumAge", out _));
        Assert.True(errors.TryGetProperty("whitelistedHardcodedSubs", out _));

        var saved = await client.PutAsJsonAsync("/api/v1/settings/indexers", Options(rssSyncInterval: 15, minimumAge: 20, tags: [" DKSUBS ", "dksubs"], availability: "released"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(15, body.GetProperty("rssSyncInterval").GetInt32());
        Assert.Equal("released", body.GetProperty("minimumAvailability").GetString());
        Assert.Equal(["DKSUBS"], body.GetProperty("whitelistedHardcodedSubs").EnumerateArray().Select(t => t.GetString()));

        // System → Tasks shows the RSS sync with the new interval.
        var tasks = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/tasks");
        var rss = tasks.EnumerateArray().Single(t => t.GetProperty("key").GetString() == "rss-sync");
        Assert.Equal("RSS sync", rss.GetProperty("name").GetString());
        Assert.Contains(tasks.EnumerateArray(), t => t.GetProperty("key").GetString() == "missing-search");
    }

    [Fact]
    public async Task Release_profiles_are_validated_and_stored()
    {
        var client = await OwnerClientAsync();

        var invalid = await client.PostAsJsonAsync("/api/v1/releaseprofile", new
        {
            name = "",
            enabled = true,
            required = new[] { "/(unclosed/" },
            preferred = new[] { new { term = "x265", score = 0 } },
            indexerIds = new[] { 99 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.StartsWith("'/(unclosed/' is not a valid regular expression", errors.GetProperty("required")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal("'x265': use a score from -10000 to 10000, not 0.", errors.GetProperty("preferred")[0].GetString());
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("indexerIds", out _));

        var empty = await client.PostAsJsonAsync("/api/v1/releaseprofile", new { name = "Nothing", enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var created = await client.PostAsJsonAsync("/api/v1/releaseprofile", new
        {
            name = " No cams ",
            enabled = true,
            ignored = new[] { "HDCAM", "/\\bTS\\b/i" },
            preferred = new[] { new { term = "-GOOD", score = 50 } },
            indexerIds = new[] { 1 },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var profile = (await created.Content.ReadFromJsonAsync<ReleaseProfileResource>())!;
        Assert.Equal(("No cams", true), (profile.Name, profile.Enabled));
        Assert.Equal(["HDCAM", "/\\bTS\\b/i"], profile.Ignored!);
        Assert.Equal([1], profile.IndexerIds!);

        var updated = await client.PutAsJsonAsync($"/api/v1/releaseprofile/{profile.Id}", profile with { Enabled = false, IndexerIds = [] });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var list = (await client.GetFromJsonAsync<ReleaseProfileResource[]>("/api/v1/releaseprofile"))!;
        Assert.False(Assert.Single(list).Enabled);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/v1/releaseprofile/999", profile)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/releaseprofile/{profile.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/releaseprofile/{profile.Id}")).StatusCode);
    }

    [Fact]
    public async Task Release_test_and_manual_search_show_profiles_scores_and_reasons()
    {
        var client = await OwnerClientAsync();
        var movie = await AddMovieAsync(client);
        await client.PostAsJsonAsync("/api/v1/releaseprofile", new
        {
            name = "Groups",
            enabled = true,
            ignored = new[] { "-BAD" },
            preferred = new[] { new { term = "-GOOD", score = 50 }, new { term = "x265", score = 10 } },
        });
        await client.PostAsJsonAsync("/api/v1/releaseprofile", new { name = "Only Alpha", enabled = true, required = new[] { "German" }, indexerIds = new[] { 1 } });

        var parsed = (await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/parse?title=Dune.2021.German.DL.KORSUB.1080p.WEB.x265-GOOD&movieId={movie.Id}&sizeMb=6000"))!;
        Assert.Equal(60, parsed.GetProperty("preferredScore").GetInt32());
        Assert.Equal("KORSUB", parsed.GetProperty("hardcodedSubs").GetString());
        var groups = Assert.Single(parsed.GetProperty("releaseProfiles").EnumerateArray());
        Assert.Equal(["-GOOD", "x265"], groups.GetProperty("preferredMatched").EnumerateArray().Select(p => p.GetProperty("term").GetString()));
        var decision = parsed.GetProperty("decision");
        Assert.False(decision.GetProperty("accepted").GetBoolean());
        Assert.Equal("Hardcoded subs found: KORSUB", Assert.Single(decision.GetProperty("rejections").EnumerateArray()).GetProperty("message").GetString());
        Assert.Equal(60, decision.GetProperty("preferredScore").GetInt32());

        // With the indexer, its own profile applies too; without a movie every profile is shown.
        var withIndexer = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/parse?title=Dune.2021.1080p.WEB.x264-BAD&movieId={movie.Id}&indexerId=1"))!;
        Assert.Equal(2, withIndexer.GetProperty("releaseProfiles").GetArrayLength());
        Assert.Contains(
            "Release profile 'Groups': contains '-BAD'; Release profile 'Only Alpha': does not contain 'German'",
            withIndexer.GetProperty("decision").GetProperty("rejections").EnumerateArray().Select(r => r.GetProperty("message").GetString()));
        var parseOnly = (await client.GetFromJsonAsync<JsonElement>("/api/v1/parse?title=Dune.2021.1080p.WEB.x264-BAD"))!;
        Assert.Equal(2, parseOnly.GetProperty("releaseProfiles").GetArrayLength());
        Assert.True(parseOnly.GetProperty("releaseProfiles")[1].GetProperty("limited").GetBoolean());

        _client.Returns(1,
            Release("Dune.2021.German.DL.1080p.WEB.H264-OTHER"),
            Release("Dune.2021.German.DL.1080p.WEB.x265-GOOD"),
            Release("Dune.2021.German.DL.1080p.WEB.H264-BAD"));
        var search = (await client.GetFromJsonAsync<ReleaseSearchResource>($"/api/v1/release?movieId={movie.Id}"))!;
        Assert.Equal(
            [("Dune.2021.German.DL.1080p.WEB.x265-GOOD", 60, true), ("Dune.2021.German.DL.1080p.WEB.H264-OTHER", 0, true), ("Dune.2021.German.DL.1080p.WEB.H264-BAD", 0, false)],
            search.Releases.Select(r => (r.Title, r.PreferredScore, r.Accepted)));
        Assert.Equal("MustNotContainPresent", Assert.Single(search.Releases[2].Rejections).Code);
    }

    [Fact]
    public async Task Search_on_add_follows_the_dialog_or_the_default()
    {
        var client = await OwnerClientAsync();
        var root = await AddRootFolderAsync(client, "movies", "Movie");
        _client.Returns(1, Release("Dune.2021.German.DL.1080p.WEB.H264-GRP"), Release("Arrival.2016.German.DL.1080p.WEB.H264-GRP"));

        // Default off: no search.
        var plain = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root.Id });
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        await WaitForJobsAsync(client);
        Assert.Empty(_client.Searches);

        // Ticked in the add dialog: searched and grabbed.
        var searched = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 329865, rootFolderId = root.Id, searchForMissing = true });
        var arrival = (await searched.Content.ReadFromJsonAsync<MovieResource>())!;
        await WaitForJobsAsync(client);
        Assert.Single(_client.Searches);
        Assert.Single((await client.GetFromJsonAsync<HistoryPage>($"/api/v1/history?movieId={arrival.Id}"))!.Records);
    }

    private static object Options(int rssSyncInterval = 60, int missingSearchInterval = 0, int minimumAge = 0, string[]? tags = null, string availability = "inCinemas") => new
    {
        minimumAge,
        retention = 0,
        maximumSize = 0,
        allowHardcodedSubs = false,
        whitelistedHardcodedSubs = tags ?? [],
        minimumAvailability = availability,
        availabilityDelay = 0,
        rssSyncInterval,
        missingSearchInterval,
        searchOnAdd = false,
    };

    private static ReleaseInfo Release(string title) => new()
    {
        Guid = $"guid-{title}",
        Title = title,
        DownloadUrl = $"https://alpha.example/get/{title}",
        Size = 4L * 1024 * 1024 * 1024,
        PublishDate = DateTimeOffset.UtcNow.AddDays(-2),
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
