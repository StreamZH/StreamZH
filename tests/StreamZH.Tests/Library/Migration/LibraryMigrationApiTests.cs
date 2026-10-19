// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Events;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Migration;
using StreamZH.Library.Search;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Library.Migration;

/// <summary>Radarr's library through the migration API: a fake Radarr, the fake TMDB, the real host.</summary>
public sealed class LibraryMigrationApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LibraryFakeTmdb _tmdb = new();
    private readonly FakeArr _radarr = new("radarr");
    private readonly StreamZhFactory _factory;
    private readonly string _media = Path.Combine(Path.GetTempPath(), $"streamzh-libmig-api-{Guid.NewGuid():N}");

    public LibraryMigrationApiTests()
    {
        _factory = new StreamZhFactory(_tmdb);
        var file = Path.Combine(_media, "Freitag der 13. (2009)", "Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[2048]);
    }

    [Fact]
    public async Task Taking_over_Radarr_shows_the_movies_on_disk_and_grabs_nothing()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddHttpClient<ArrApiClient>().ConfigurePrimaryHttpMessageHandler(() => _radarr);
            services.Replace(ServiceDescriptor.Singleton<ITmdbFindClient>(_tmdb));
        }));
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
        factory.Services.GetRequiredService<ConfigRepository>().Set(ConfigKeys.IndexerSearchOnAdd, "true");
        var events = factory.Services.GetRequiredService<IEventBus>();
        var grabbed = 0;
        var searched = 0;
        using var onGrab = events.Subscribe<ReleaseGrabbed>((_, _) => { Interlocked.Increment(ref grabbed); return ValueTask.CompletedTask; });
        using var onSearch = events.Subscribe<AutomaticSearchFinished>((_, _) => { Interlocked.Increment(ref searched); return ValueTask.CompletedTask; });
        var request = new { url = "http://radarr:7878", apiKey = "secret", pathMappings = new[] { new { remote = "/movies", local = _media } } };

        var sources = await client.GetFromJsonAsync<JsonElement>("/api/v1/migration/sources", Json);
        var radarrSections = sources.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "radarr").GetProperty("sections");
        Assert.Equal(["movies", "history", "blocklist"], radarrSections.EnumerateArray().Select(s => s.GetProperty("key").GetString()).Where(k => k is "movies" or "history" or "blocklist"));

        var preview = await (await client.PostAsJsonAsync("/api/v1/migration/radarr/preview", request)).Content.ReadFromJsonAsync<MigrationPreview>(Json);
        var movies = preview!.Sections.Single(s => s.Key == "movies");
        Assert.Equal(new MigrationCounts(4, 0, 0, 1), movies.Counts);
        Assert.Equal(MigrationAction.Create, movies.Items.Single(i => i.Key == "movies:13207").Action);

        var started = await client.PostAsJsonAsync("/api/v1/migration/radarr/run", new
        {
            request.url,
            request.apiKey,
            request.pathMappings,
            sections = new[] { "movies", "history", "blocklist" },
            options = new Dictionary<string, string> { ["movies.monitor"] = "asIs" },
        });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<MigrationRun>(Json))!;
        for (var i = 0; i < 400 && run.Finished is null; i++)
        {
            await Task.Delay(25);
            run = (await client.GetFromJsonAsync<MigrationRun>($"/api/v1/migration/runs/{run.Id}", Json))!;
        }

        Assert.Equal(MigrationRunStatus.Done, run.Status);
        var report = run.Report.Sections.ToDictionary(s => s.Key);
        Assert.Equal((4, 1, 0), (report["movies"].Created, report["movies"].Skipped, report["movies"].Failed));
        Assert.Equal(4, report["history"].Created);
        Assert.Equal(1, report["blocklist"].Created);

        var library = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies", Json);
        var freitag = library.EnumerateArray().Single(m => m.GetProperty("tmdbId").GetInt32() == 13207);
        Assert.True(freitag.GetProperty("hasFile").GetBoolean());
        Assert.Equal("Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS.mkv", freitag.GetProperty("file").GetProperty("relativePath").GetString());
        Assert.False(library.EnumerateArray().Single(m => m.GetProperty("tmdbId").GetInt32() == 693134).GetProperty("hasFile").GetBoolean());
        var details = factory.Services.GetRequiredService<MediaFileDetailsRepository>()
            .Find(Path.Combine(_media, "Freitag der 13. (2009)", "Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS.mkv"))!;
        Assert.Equal(Quality.Bluray1080p.Id, details.QualityId);
        Assert.Equal("Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS", details.SceneName);

        // Nothing was searched or grabbed (search on add is on, but taking over is not adding by hand); Radarr was only read.
        await Task.Delay(300);
        Assert.Equal((0, 0), (grabbed, searched));
        var history = factory.Services.GetRequiredService<HistoryRepository>().Page(1, 50).Records;
        Assert.All(history, h => Assert.StartsWith("radarr:", h.Data["migratedFrom"], StringComparison.Ordinal));
        Assert.All(_radarr.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
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
