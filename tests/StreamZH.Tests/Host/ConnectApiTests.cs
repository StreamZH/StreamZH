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
using Microsoft.Extensions.Hosting;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Status;
using StreamZH.Library.Metadata;
using StreamZH.Library.Notifications;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Connect;

namespace StreamZH.Tests.Host;

public sealed class ConnectApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();
    private readonly FakeHttp _http = new();
    private readonly CountingImages _images = new();
    private readonly WebApplicationFactory<Program> _app;

    public ConnectApiTests()
    {
        _app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(ConnectHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => _http);
            services.AddSingleton(new ConnectOptions([TimeSpan.FromMilliseconds(1)]));
            services.AddTransient<ITmdbImageClient>(_ => _images);
        }));
    }

    private async Task<HttpClient> OwnerAsync()
    {
        var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static object Plex(string name = "Plex", string token = "plex-token", int id = 0) => new
    {
        id,
        name,
        implementation = "plex",
        enable = true,
        onImport = true,
        onUpgrade = true,
        onRename = true,
        onGrab = true,
        settings = new { host = "plex.local", port = 32400, authToken = token, mapFrom = "/media", mapTo = "/data" },
    };

    [Fact]
    public async Task Create_list_update_and_delete_with_masked_secrets()
    {
        var client = await OwnerAsync();

        var schema = await client.GetFromJsonAsync<JsonElement>("/api/v1/connection/schema");
        Assert.Equal(12, schema.GetArrayLength());
        var plexType = schema.EnumerateArray().Single(t => t.GetProperty("key").GetString() == "plex");
        Assert.Equal("mediaServer", plexType.GetProperty("kind").GetString());
        Assert.Equal(32400, plexType.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "port").GetProperty("default").GetInt32());
        Assert.True(plexType.GetProperty("defaults").GetProperty("onImport").GetBoolean());

        var created = await client.PostAsJsonAsync("/api/v1/connection", Plex());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var resource = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = resource.GetProperty("id").GetInt32();
        Assert.Equal("********", resource.GetProperty("settings").GetProperty("authToken").GetString());
        Assert.Equal("Plex Media Server", resource.GetProperty("implementationName").GetString());
        Assert.False(resource.GetProperty("onGrab").GetBoolean());
        Assert.True(resource.GetProperty("settings").GetProperty("updateLibrary").GetBoolean());
        Assert.False(resource.GetProperty("status").GetProperty("failing").GetBoolean());

        var raw = _app.Services.GetRequiredService<ConnectionRepository>().Find(id)!;
        Assert.Equal("plex-token", new ConnectionSettings(raw.Settings).Text("authToken"));

        // Saving with the mask keeps the token; a new name is stored.
        var updated = await client.PutAsJsonAsync($"/api/v1/connection/{id}", Plex(name: "Living room", token: "********", id: id));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        raw = _app.Services.GetRequiredService<ConnectionRepository>().Find(id)!;
        Assert.Equal(("Living room", "plex-token"), (raw.Name, new ConnectionSettings(raw.Settings).Text("authToken")));

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/connection");
        Assert.Equal("Living room", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());
        Assert.DoesNotContain("plex-token", list.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/connection/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/connection/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/connection/{id}")).StatusCode);
    }

    [Fact]
    public async Task Validation_errors_are_keyed_by_field()
    {
        var client = await OwnerAsync();

        var response = await client.PostAsJsonAsync("/api/v1/connection", new
        {
            name = "",
            implementation = "discord",
            settings = new { webHookUrl = "", avatar = "not a url" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("webHookUrl", out _));
        Assert.True(errors.TryGetProperty("avatar", out _));

        var unknown = await client.PostAsJsonAsync("/api/v1/connection", new { name = "x", implementation = "fax" });
        Assert.True((await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("implementation", out _));
    }

    [Fact]
    public async Task Test_uses_the_stored_secret_and_reports_failures()
    {
        var client = await OwnerAsync();
        _http.Respond = r => r.Header("X-Plex-Token") == "plex-token"
            ? (HttpStatusCode.OK, """{"MediaContainer":{"Directory":[{"key":"1","type":"movie","Location":[{"path":"/data/movies"}]}]}}""")
            : (HttpStatusCode.Unauthorized, string.Empty);
        var created = await (await client.PostAsJsonAsync("/api/v1/connection", Plex())).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        var ok = await client.PostAsJsonAsync("/api/v1/connection/test", Plex(token: "********", id: id));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Connected to Plex: 1 movie and 0 TV libraries.", (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        var bad = await client.PostAsJsonAsync("/api/v1/connection/test", Plex(token: "wrong"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Plex Media Server: unauthorized", problem.GetProperty("title").GetString());
        Assert.Equal("connection-test-failed", problem.GetProperty("code").GetString());

        // A new connection cannot borrow another one's secret through the mask.
        var masked = await client.PostAsJsonAsync("/api/v1/connection/test", Plex(token: "********"));
        Assert.Equal(HttpStatusCode.BadRequest, masked.StatusCode);
    }

    [Fact]
    public async Task A_failing_delivery_shows_in_the_list_and_the_status_window()
    {
        var client = await OwnerAsync();
        _http.Respond = _ => (HttpStatusCode.Unauthorized, string.Empty);
        var created = await (await client.PostAsJsonAsync("/api/v1/connection", Plex())).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        await _app.Services.GetRequiredService<IEventBus>().PublishAsync(
            new MediaImported(MediaType.Movie, null, null, [], "Dune", ["/media/movies/Dune (2021)/Dune.mkv"], "/media/movies/Dune (2021)", false, null, null));
        await _app.Services.GetRequiredService<ConnectDispatcher>().WhenIdleAsync();

        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/connection/{id}");
        Assert.True(listed.GetProperty("status").GetProperty("failing").GetBoolean());
        Assert.Equal("unauthorized", listed.GetProperty("status").GetProperty("lastError").GetString());

        var ongoing = await client.GetFromJsonAsync<OngoingResource>("/api/v1/status/ongoing");
        var notice = Assert.Single(ongoing!.Notices, n => n.Key == $"connect-failed-{id}");
        Assert.Equal(("warn", "Plex: unauthorized", "/settings/connect"), (notice.Level, notice.Title, notice.Link));

        // Saving the connection again clears the error.
        await client.PutAsJsonAsync($"/api/v1/connection/{id}", Plex(token: "********", id: id));
        ongoing = await client.GetFromJsonAsync<OngoingResource>("/api/v1/status/ongoing");
        Assert.DoesNotContain(ongoing!.Notices, n => n.Key.StartsWith("connect-failed-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_issues_and_their_end_reach_the_webhook()
    {
        var client = await OwnerAsync();
        await client.PostAsJsonAsync("/api/v1/connection", new
        {
            name = "Hook",
            implementation = "webhook",
            onHealthIssue = true,
            onHealthRestored = true,
            includeHealthWarnings = true,
            settings = new { url = "https://hooks.example.com/streamzh" },
        });
        var watcher = _app.Services.GetServices<IHostedService>().OfType<ConnectHealthWatcher>().Single();
        var dispatcher = _app.Services.GetRequiredService<ConnectDispatcher>();

        watcher.Check();
        await dispatcher.WhenIdleAsync();
        var issues = _http.Requests.Select(r => r.Json).ToList();
        Assert.Contains(issues, j => j.GetProperty("eventType").GetString() == "Health" && j.GetProperty("type").GetString() == "no-indexer");
        Assert.DoesNotContain(issues, j => j.GetProperty("type").GetString()!.StartsWith("connect-", StringComparison.Ordinal));

        var count = _http.Requests.Count;
        watcher.Check();
        await dispatcher.WhenIdleAsync();
        Assert.Equal(count, _http.Requests.Count);

        _app.Services.GetRequiredService<IndexerRepository>().Add(
            new IndexerDefinition(0, "Geek", IndexerDefinition.Newznab, "https://api.nzbgeek.info", "/api", "key", [2000], [5000], 25, true, true, true),
            DateTime.UtcNow);
        watcher.Check();
        await dispatcher.WhenIdleAsync();
        var restored = _http.Last.Json;
        Assert.Equal(("HealthRestored", "no-indexer"), (restored.GetProperty("eventType").GetString(), restored.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Metadata_settings_kodi_options_and_write_action()
    {
        var client = await OwnerAsync();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/metadata");
        Assert.Equal("US", settings.GetProperty("certificationCountry").GetString());
        Assert.False(settings.GetProperty("imageCache").GetBoolean());
        Assert.Contains(settings.GetProperty("certificationCountries").EnumerateArray(), c => c.GetString() == "CH");

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/metadata", new { certificationCountry = "XX" });
        Assert.True((await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("certificationCountry", out _));

        var saved = await client.PutAsJsonAsync("/api/v1/settings/metadata", new { certificationCountry = "ch", nfoOriginalTitle = true, imageCache = true });
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("CH", true, true, "en-US"), (body.GetProperty("certificationCountry").GetString(), body.GetProperty("nfoOriginalTitle").GetBoolean(), body.GetProperty("imageCache").GetBoolean(), body.GetProperty("language").GetString()));

        var write = await client.PostAsync("/api/v1/metadata/write", null);
        Assert.Equal(HttpStatusCode.BadRequest, write.StatusCode);

        var kodi = await client.GetFromJsonAsync<KodiMetadataOptions>("/api/v1/settings/metadata/kodi");
        Assert.Equal(new KodiMetadataOptions(), kodi);
        var changed = await client.PutAsJsonAsync("/api/v1/settings/metadata/kodi", kodi! with { Enabled = true, UseMovieNfo = true, EpisodeImages = false });
        Assert.Equal(kodi with { Enabled = true, UseMovieNfo = true, EpisodeImages = false }, await changed.Content.ReadFromJsonAsync<KodiMetadataOptions>());
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/v1/metadata/write", null)).StatusCode);
    }

    [Fact]
    public async Task Media_covers_redirect_to_tmdb_or_come_from_the_cache()
    {
        var client = await OwnerAsync();

        var redirect = await client.GetAsync("/api/v1/mediacover/w342/abc.jpg");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("https://image.tmdb.org/t/p/w342/abc.jpg", redirect.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/mediacover/w342/..%2Fsecret.jpg")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/mediacover/huge/abc.jpg")).StatusCode);

        await client.PutAsJsonAsync("/api/v1/settings/metadata", new { imageCache = true });
        var cached = await client.GetAsync("/api/v1/mediacover/w342/abc.jpg");
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.Equal("image/jpeg", cached.Content.Headers.ContentType!.MediaType);
        Assert.Equal("w342/abc.jpg", await cached.Content.ReadAsStringAsync());
        await client.GetAsync("/api/v1/mediacover/w342/abc.jpg");
        Assert.Equal(1, _images.Count);
        Assert.True(File.Exists(Path.Combine(_factory.DataDirectory, "MediaCover", "w342", "abc.jpg")));

        var anonymous = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/mediacover/w342/abc.jpg")).StatusCode);
    }

    [Fact]
    public async Task Connections_require_an_admin()
    {
        var anonymous = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/connection")).StatusCode);
    }

    public void Dispose()
    {
        _app.Dispose();
        _factory.Dispose();
    }

    private sealed class CountingImages : ITmdbImageClient
    {
        private int _count;

        public int Count => _count;

        public async Task DownloadAsync(string path, string size, Stream destination, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            await destination.WriteAsync(Encoding.UTF8.GetBytes($"{size}{path}"), cancellationToken);
        }
    }
}
