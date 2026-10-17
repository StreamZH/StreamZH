// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Status;
using StreamZH.Library.ImportLists;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Library.ImportLists;

namespace StreamZH.Tests.Host;

/// <summary>Settings → Import lists over HTTP: lists with masked secrets, test, preview, sync, options and exclusions.</summary>
public sealed class ImportListApiTests : IDisposable
{
    private const string Feed = """
        <rss><channel>
          <item><title>Dune (2021)</title><link>https://www.themoviedb.org/movie/438631</link></item>
          <item><title>Arrival (2016)</title><link>https://www.imdb.com/title/tt2543164/</link></item>
        </channel></rss>
        """;

    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly WebApplicationFactory<Program> _app;
    private readonly string _media = Path.Combine(Path.GetTempPath(), $"streamzh-lists-media-{Guid.NewGuid():N}");
    private (HttpStatusCode Status, string Body) _feed = (HttpStatusCode.OK, Feed);

    public ImportListApiTests()
    {
        var http = new ListHttp(request => request.RequestUri!.Host == "plex.example" ? (HttpStatusCode.Unauthorized, "") : _feed);
        _app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(ImportListHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => http);
            services.AddTransient<ITmdbListClient>(_ => new FakeListTmdb());
        }));
    }

    private async Task<HttpClient> OwnerAsync()
    {
        var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<int> RootFolderAsync(HttpClient client, string name, string mediaType)
    {
        var path = Directory.CreateDirectory(Path.Combine(_media, name)).FullName;
        var response = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path, mediaType });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static object Plex(int rootFolderId, string token = "plex-token", int id = 0, string name = "My watchlist") => new
    {
        id,
        name,
        implementation = "plexWatchlist",
        mediaType = "Movie",
        enabled = true,
        enableAuto = true,
        searchOnAdd = false,
        monitor = "movieOnly",
        rootFolderId,
        minimumAvailability = "announced",
        settings = new { accessToken = token },
    };

    private static object Rss(int rootFolderId, string name = "Feed", bool enableAuto = true) => new
    {
        name,
        implementation = "rssList",
        mediaType = "Movie",
        enabled = true,
        enableAuto,
        searchOnAdd = false,
        rootFolderId,
        settings = new { url = "https://lists.example/feed.rss" },
    };

    [Fact]
    public async Task Schema_lists_every_type_with_fields_and_choices()
    {
        var client = await OwnerAsync();

        var schema = await client.GetFromJsonAsync<JsonElement>("/api/v1/importlist/schema");

        var types = schema.GetProperty("types").EnumerateArray().ToList();
        Assert.Equal(ImportListCatalog.All.Count, types.Count);
        var collection = types.Single(t => t.GetProperty("key").GetString() == "tmdbCollection");
        Assert.Equal(["Movie"], collection.GetProperty("mediaTypes").EnumerateArray().Select(m => m.GetString()));
        var trakt = types.Single(t => t.GetProperty("key").GetString() == "traktList");
        var listName = trakt.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "listName");
        Assert.Equal("listKind", listName.GetProperty("when").GetProperty("field").GetString());
        Assert.Equal(6, schema.GetProperty("seriesMonitorOptions").GetArrayLength());
        Assert.Equal("movieAndCollection", schema.GetProperty("movieMonitorOptions")[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Create_update_and_delete_with_masked_secrets()
    {
        var client = await OwnerAsync();
        var movies = await RootFolderAsync(client, "movies", "Movie");

        var created = await client.PostAsJsonAsync("/api/v1/importlist", Plex(movies));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var resource = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = resource.GetProperty("id").GetInt32();
        Assert.Equal("********", resource.GetProperty("settings").GetProperty("accessToken").GetString());
        Assert.Equal("Plex watchlist", resource.GetProperty("implementationName").GetString());
        Assert.Equal("Movie", resource.GetProperty("mediaType").GetString());
        Assert.False(resource.GetProperty("status").GetProperty("failing").GetBoolean());

        // The mask keeps the stored token; a new value replaces it.
        var renamed = await client.PutAsJsonAsync($"/api/v1/importlist/{id}", Plex(movies, "********", id, "Watchlist"));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var stored = _app.Services.GetRequiredService<ImportListRepository>().Find(id)!;
        Assert.Equal("plex-token", stored.Settings["accessToken"].GetString());
        Assert.Equal("Watchlist", stored.Name);
        await client.PutAsJsonAsync($"/api/v1/importlist/{id}", Plex(movies, "new-token", id, "Watchlist"));
        Assert.Equal("new-token", _app.Services.GetRequiredService<ImportListRepository>().Find(id)!.Settings["accessToken"].GetString());

        var all = await client.GetFromJsonAsync<JsonElement>("/api/v1/importlist");
        Assert.Equal("********", all[0].GetProperty("settings").GetProperty("accessToken").GetString());
        Assert.DoesNotContain("new-token", all.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/importlist/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/importlist/{id}")).StatusCode);
    }

    [Fact]
    public async Task Validation_reports_every_field()
    {
        var client = await OwnerAsync();
        var series = await RootFolderAsync(client, "tv", "Series");

        var response = await client.PostAsJsonAsync("/api/v1/importlist", new
        {
            name = "",
            implementation = "tmdbCollection",
            mediaType = "Series",
            monitor = "all",
            rootFolderId = series,
            qualityProfileId = 999,
            settings = new { collectionId = "star-wars" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(
            ["collectionId", "mediaType", "monitor", "name", "qualityProfileId", "rootFolderId"],
            errors.EnumerateObject().Select(e => e.Name).Order());

        var trakt = await client.PostAsJsonAsync("/api/v1/importlist", new
        {
            name = "Trakt",
            implementation = "traktList",
            mediaType = "Series",
            rootFolderId = series,
            settings = new { listKind = "popular" },
        });
        var traktErrors = (await trakt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(["clientId"], traktErrors.EnumerateObject().Select(e => e.Name));

        var unknown = await client.PostAsJsonAsync("/api/v1/importlist", new { name = "x", implementation = "imdb", rootFolderId = series });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Test_and_preview_read_the_list_without_saving()
    {
        var client = await OwnerAsync();
        var movies = await RootFolderAsync(client, "movies", "Movie");
        await client.PostAsJsonAsync("/api/v1/importlist/exclusion", new { mediaType = "Movie", tmdbId = 329865, title = "Arrival", year = 2016 });

        var test = await client.PostAsJsonAsync("/api/v1/importlist/test", Rss(movies));
        var preview = await client.PostAsJsonAsync("/api/v1/importlist/preview", Rss(movies));

        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.Equal("Found 2 movies.", (await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        var items = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["new", "excluded"], items.Select(i => i.GetProperty("state").GetString()));
        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/v1/importlist") ?? []);

        _feed = (HttpStatusCode.NotFound, "");
        var failed = await client.PostAsJsonAsync("/api/v1/importlist/test", Rss(movies));
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var problem = await failed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RSS feed: list not found (404)", problem.GetProperty("title").GetString());
        Assert.Equal("import-list-failed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Sync_now_adds_the_items_and_add_and_origin_work()
    {
        var client = await OwnerAsync();
        var movies = await RootFolderAsync(client, "movies", "Movie");
        var auto = await (await client.PostAsJsonAsync("/api/v1/importlist", Rss(movies))).Content.ReadFromJsonAsync<JsonElement>();
        var manual = await (await client.PostAsJsonAsync("/api/v1/importlist", Rss(movies, "Manual", enableAuto: false))).Content.ReadFromJsonAsync<JsonElement>();
        var manualId = manual.GetProperty("id").GetInt32();

        // Manual list: "Add" in the preview adds one item with the list's options.
        var add = await client.PostAsJsonAsync($"/api/v1/importlist/{manualId}/add", new { tmdbId = 329865 });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Equal(["Arrival (2016)"], (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").EnumerateArray().Select(a => a.GetString()));
        var origin = await client.GetFromJsonAsync<JsonElement>("/api/v1/importlist/origin?mediaType=Movie&tmdbId=329865");
        Assert.Equal("Manual", origin.GetProperty("listName").GetString());
        var none = await client.GetFromJsonAsync<JsonElement>("/api/v1/importlist/origin?mediaType=Movie&tmdbId=1");
        Assert.Equal(JsonValueKind.Null, none.GetProperty("listId").ValueKind);

        var id = auto.GetProperty("id").GetInt32();
        var sync = await client.PostAsync($"/api/v1/importlist/{id}/sync", null);
        Assert.True((await sync.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetBoolean());
        JsonElement list = default;
        for (var i = 0; i < 100; i++)
        {
            list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/importlist/{id}");
            if (list.GetProperty("status").GetProperty("lastSync").ValueKind != JsonValueKind.Null)
            {
                break;
            }

            await Task.Delay(100);
        }

        var status = list.GetProperty("status");
        Assert.Equal(2, status.GetProperty("lastFound").GetInt32());
        Assert.Equal(1, status.GetProperty("lastAdded").GetInt32());
        Assert.Equal(["Dune (2021)"], status.GetProperty("lastAddedTitles").EnumerateArray().Select(t => t.GetString()));
        var library = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies");
        Assert.Equal(2, library.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/importlist/999/sync", null)).StatusCode);
    }

    [Fact]
    public async Task Options_set_the_task_interval_and_are_validated()
    {
        var client = await OwnerAsync();

        var defaults = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/importlists");
        Assert.Equal(24, defaults.GetProperty("syncInterval").GetInt32());
        Assert.Equal("disabled", defaults.GetProperty("cleanLibraryLevel").GetString());
        var tasks = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/tasks");
        Assert.Equal(1440, tasks.EnumerateArray().Single(t => t.GetProperty("key").GetString() == "import-list-sync").GetProperty("intervalMinutes").GetDouble());

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/importlists", new { syncInterval = 2, cleanLibraryLevel = "logOnly" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True((await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("syncInterval", out _));
        var badLevel = await client.PutAsJsonAsync("/api/v1/settings/importlists", new { syncInterval = 24, cleanLibraryLevel = "deleteEverything" });
        Assert.Equal(HttpStatusCode.BadRequest, badLevel.StatusCode);

        var saved = await client.PutAsJsonAsync("/api/v1/settings/importlists", new { syncInterval = 6, cleanLibraryLevel = "keepAndUnmonitor" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("keepAndUnmonitor", (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleanLibraryLevel").GetString());
        tasks = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/tasks");
        Assert.Equal(360, tasks.EnumerateArray().Single(t => t.GetProperty("key").GetString() == "import-list-sync").GetProperty("intervalMinutes").GetDouble());
        Assert.True((await (await client.PostAsync("/api/v1/importlist/sync", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetBoolean());
    }

    [Fact]
    public async Task Exclusions_can_be_added_listed_and_removed()
    {
        var client = await OwnerAsync();

        var added = await client.PostAsJsonAsync("/api/v1/importlist/exclusion", new { mediaType = "Series", tmdbId = 1396, title = "Breaking Bad", year = 2008 });
        var twice = await client.PostAsJsonAsync("/api/v1/importlist/exclusion", new { mediaType = "Series", tmdbId = 1396, title = "Breaking Bad", year = 2008 });
        var invalid = await client.PostAsJsonAsync("/api/v1/importlist/exclusion", new { mediaType = "Movie", tmdbId = 0 });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(HttpStatusCode.Created, twice.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/importlist/exclusion");
        var exclusion = Assert.Single(list.EnumerateArray());
        Assert.Equal(("Series", "Breaking Bad", 2008), (exclusion.GetProperty("mediaType").GetString(), exclusion.GetProperty("title").GetString(), exclusion.GetProperty("year").GetInt32()));

        var id = exclusion.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/importlist/exclusion/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/importlist/exclusion/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_list_failing_twice_shows_in_the_status_window()
    {
        var client = await OwnerAsync();
        var movies = await RootFolderAsync(client, "movies", "Movie");
        var created = await (await client.PostAsJsonAsync("/api/v1/importlist", Plex(movies))).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        var repository = _app.Services.GetRequiredService<ImportListRepository>();

        repository.SetFailure(id, DateTime.UtcNow, "Plex rejected the credentials (401)");
        Assert.DoesNotContain(await Notices(client), n => n.StartsWith(ImportListNotices.KeyPrefix, StringComparison.Ordinal));
        repository.SetFailure(id, DateTime.UtcNow, "Plex rejected the credentials (401)");
        Assert.Contains($"{ImportListNotices.KeyPrefix}{id}", await Notices(client));

        repository.SetSuccess(id, DateTime.UtcNow, 3, 0, []);
        Assert.DoesNotContain($"{ImportListNotices.KeyPrefix}{id}", await Notices(client));
    }

    [Fact]
    public async Task Only_admins_may_use_the_import_lists()
    {
        var anonymous = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/importlist")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/settings/importlists")).StatusCode);
    }

    private static async Task<List<string>> Notices(HttpClient client)
    {
        var ongoing = await client.GetFromJsonAsync<JsonElement>("/api/v1/status/ongoing");
        return [.. ongoing.GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("key").GetString()!)];
    }

    public void Dispose()
    {
        _app.Dispose();
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
