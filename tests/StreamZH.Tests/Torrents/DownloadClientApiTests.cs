// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Core.Media;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.Downloads.Torrent;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Torrents;

public sealed class DownloadClientApiTests : IAsyncLifetime
{
    private const string Password = "qbit-secret-pass";
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly FakeQBittorrent _qbit = new() { Username = "admin", Password = Password };
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        await _qbit.StartAsync();
        _owner = await _factory.CreateOwnerClientAsync();
    }

    public async Task DisposeAsync()
    {
        await _qbit.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private object NewClient(string name = "qBittorrent", string password = Password, int? port = null, string implementation = "qBittorrent") => new
    {
        name,
        implementation,
        enable = true,
        priority = 1,
        removeCompleted = true,
        fields = new object[]
        {
            new { name = "host", value = "127.0.0.1" },
            new { name = "port", value = port ?? _qbit.Port },
            new { name = "username", value = "admin" },
            new { name = "password", value = password },
            new { name = "category", value = "streamzh" },
        },
    };

    private static JsonElement Field(JsonElement client, string name) =>
        client.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);

    private async Task<HttpClient> LoginAsync(string name, UserRole role)
    {
        var passwords = _factory.Services.GetRequiredService<PasswordService>();
        _factory.Services.GetRequiredService<UserRepository>().Create(name, passwords.Hash("a long test password"), role);
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = name, password = "a long test password" })).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Schema_lists_the_external_clients_with_their_fields()
    {
        var schema = await _owner.GetFromJsonAsync<JsonElement>("/api/v1/downloadclient/schema");
        var names = schema.EnumerateArray().Select(s => s.GetProperty("implementation").GetString()).ToList();
        Assert.Equal(["qBittorrent", "Transmission", "Deluge", "StreamZH"], names);

        var qbit = schema[0];
        Assert.Equal(("torrent", 0), (qbit.GetProperty("protocol").GetString(), qbit.GetProperty("id").GetInt32()));
        Assert.Equal(8080, Field(qbit, "port").GetProperty("value").GetInt32());
        Assert.Equal(("text", "number", "checkbox"), (Field(qbit, "host").GetProperty("type").GetString(),
            Field(qbit, "port").GetProperty("type").GetString(), Field(qbit, "useSsl").GetProperty("type").GetString()));
        Assert.Equal("password", Field(qbit, "password").GetProperty("privacy").GetString());
        Assert.True(Field(qbit, "urlBase").GetProperty("advanced").GetBoolean());
        Assert.True(Field(qbit, "host").GetProperty("required").GetBoolean());
        Assert.False(string.IsNullOrEmpty(Field(qbit, "category").GetProperty("helpText").GetString()));
        Assert.Equal("/transmission/", Field(schema[1], "urlBase").GetProperty("value").GetString());
        Assert.DoesNotContain(schema[2].GetProperty("fields").EnumerateArray(), f => f.GetProperty("name").GetString() == "username");
    }

    [Fact]
    public async Task Create_edit_and_delete_keep_the_password_secret()
    {
        var response = await _owner.PostAsJsonAsync("/api/v1/downloadclient", NewClient());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal($"/api/v1/downloadclient/{id}", response.Headers.Location!.ToString());
        Assert.Equal("********", Field(created, "password").GetProperty("value").GetString());
        Assert.DoesNotContain(Password, await (await _owner.GetAsync("/api/v1/downloadclient")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, await (await _owner.GetAsync($"/api/v1/downloadclient/{id}")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var repository = _factory.Services.GetRequiredService<DownloadClientRepository>();
        Assert.Equal(Password, repository.Find(id)!.Text("password"));

        // The masked value keeps the stored password; everything else changes.
        var edited = await _owner.PutAsJsonAsync($"/api/v1/downloadclient/{id}", NewClient("qBittorrent Box", "********"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(("qBittorrent Box", Password), (repository.Find(id)!.Name, repository.Find(id)!.Text("password")));
        await _owner.PutAsJsonAsync($"/api/v1/downloadclient/{id}", NewClient("qBittorrent Box", "new-pass"));
        Assert.Equal("new-pass", repository.Find(id)!.Text("password"));

        var invalid = await _owner.PostAsJsonAsync("/api/v1/downloadclient", NewClient("qBittorrent Box", port: 70000));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("port", out _));
        var unknown = await _owner.PostAsJsonAsync("/api/v1/downloadclient", NewClient("Other", implementation: "uTorrent"));
        Assert.True((await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("implementation", out _));
        var badHost = await _owner.PostAsJsonAsync("/api/v1/downloadclient", new
        {
            name = "Bad", implementation = "Deluge", fields = new object[] { new { name = "host", value = "http://box/" }, new { name = "category", value = "Upper Case" } },
        });
        var badErrors = (await badHost.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(badErrors.TryGetProperty("host", out _));
        Assert.True(badErrors.TryGetProperty("category", out _));

        // The web UI sends a settings object (numbers may come as text, empty numbers as null = default).
        var fromUi = await _owner.PostAsJsonAsync("/api/v1/downloadclient", new
        {
            id = 0, name = "Transmission", implementation = "Transmission", protocol = "torrent", enable = true, priority = 2, removeCompleted = false,
            settings = new Dictionary<string, object?> { ["host"] = "torrent-box", ["port"] = "9091", ["urlBase"] = null, ["password"] = "rpc", ["useSsl"] = false },
        });
        Assert.Equal(HttpStatusCode.Created, fromUi.StatusCode);
        var transmission = repository.FindByName("Transmission")!;
        Assert.Equal((9091, "/transmission/", "rpc", false), (transmission.Number("port"), transmission.Text("urlBase"), transmission.Text("password"), transmission.RemoveCompleted));

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/downloadclient/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync($"/api/v1/downloadclient/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.DeleteAsync($"/api/v1/downloadclient/{id}")).StatusCode);
    }

    [Fact]
    public async Task Test_uses_the_stored_password_and_names_the_failing_field()
    {
        var created = await (await _owner.PostAsJsonAsync("/api/v1/downloadclient", NewClient())).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        var ok = await _owner.PostAsJsonAsync("/api/v1/downloadclient/test", new
        {
            id,
            name = "qBittorrent",
            implementation = "qBittorrent",
            fields = new object[]
            {
                new { name = "host", value = "127.0.0.1" }, new { name = "port", value = _qbit.Port }, new { name = "username", value = "admin" },
                new { name = "password", value = "********" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var info = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("qBittorrent", "5.0.4"), (info.GetProperty("name").GetString(), info.GetProperty("version").GetString()));
        Assert.Contains("streamzh", info.GetProperty("categories").EnumerateArray().Select(c => c.GetString()));

        var wrong = await _owner.PostAsJsonAsync("/api/v1/downloadclient/test", NewClient(password: "wrong-pass"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var problem = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("download-client-test-failed", "password"), (problem.GetProperty("code").GetString(), problem.GetProperty("field").GetString()));
        Assert.True(problem.GetProperty("errors").TryGetProperty("password", out _));
        Assert.DoesNotContain("wrong-pass", problem.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Friends_read_but_cannot_change_and_requesters_see_nothing()
    {
        var created = await (await _owner.PostAsJsonAsync("/api/v1/downloadclient", NewClient())).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "downloads", "torrents"));
        var mapping = await _owner.PostAsJsonAsync("/api/v1/remotepathmapping", new { host = "127.0.0.1", remotePath = "/downloads", localPath = Path.Combine(_factory.DataDirectory, "downloads", "torrents") });
        Assert.Equal(HttpStatusCode.Created, mapping.StatusCode);
        var mappingId = (await mapping.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var friend = await LoginAsync("jane", UserRole.Friend);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/downloadclient")).StatusCode);
        Assert.DoesNotContain(Password, await (await friend.GetAsync("/api/v1/downloadclient")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/downloadclient/schema")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/remotepathmapping")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync("/api/v1/downloadclient", NewClient("Other"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PutAsJsonAsync($"/api/v1/downloadclient/{id}", NewClient())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.DeleteAsync($"/api/v1/downloadclient/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync("/api/v1/downloadclient/test", NewClient())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync("/api/v1/remotepathmapping", new { host = "x", remotePath = "/a", localPath = "/b" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.DeleteAsync($"/api/v1/remotepathmapping/{mappingId}")).StatusCode);

        var requester = await LoginAsync("bob", UserRole.Requester);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/downloadclient")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/downloadclient/schema")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/remotepathmapping")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/downloadclient")).StatusCode);
    }

    [Fact]
    public async Task Remote_path_mapping_crud_and_validation()
    {
        var local = Path.Combine(_factory.DataDirectory, "downloads", "mapped");
        Directory.CreateDirectory(local);
        var created = await (await _owner.PostAsJsonAsync("/api/v1/remotepathmapping", new { host = "torrent-box", remotePath = "/downloads", localPath = local }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal("/downloads/", created.GetProperty("remotePath").GetString());

        var bad = await _owner.PostAsJsonAsync("/api/v1/remotepathmapping", new { host = "", remotePath = "downloads", localPath = "relative" });
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(3, errors.EnumerateObject().Count());

        var updated = await _owner.PutAsJsonAsync($"/api/v1/remotepathmapping/{id}", new { host = "torrent-box", remotePath = "/data", localPath = local });
        Assert.Equal("/data/", (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("remotePath").GetString());
        Assert.Single((await _owner.GetFromJsonAsync<JsonElement>("/api/v1/remotepathmapping")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/remotepathmapping/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.DeleteAsync($"/api/v1/remotepathmapping/{id}")).StatusCode);
    }

    [Fact]
    public async Task Activity_shows_torrents_and_removes_them_from_the_client()
    {
        var created = await (await _owner.PostAsJsonAsync("/api/v1/downloadclient", NewClient())).Content.ReadFromJsonAsync<JsonElement>();
        var clientId = created.GetProperty("id").GetInt32();
        var tracker = _factory.Services.GetRequiredService<TorrentTracker>();
        var item = await tracker.AddAsync(new DownloadRequest([], "Generated.Activity.2024.1080p", DownloadCategory.Movie, MediaLink.None)
        {
            Protocol = DownloadProtocol.Torrent,
            TorrentFile = TorrentFixtures.Create("Generated.Activity.2024.1080p"),
        });
        _qbit.Torrent(item.ExternalId!).Progress = 0.25;
        await tracker.PollAsync();

        var queue = await _owner.GetFromJsonAsync<JsonElement>("/api/v1/download");
        var listed = queue.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == item.Id);
        Assert.Equal(("torrent", "qBittorrent", clientId), (listed.GetProperty("protocol").GetString(), listed.GetProperty("downloadClient").GetString(), listed.GetProperty("downloadClientId").GetInt32()));
        Assert.Equal((item.ExternalId, "downloading", 0.25), (listed.GetProperty("infoHash").GetString(), listed.GetProperty("status").GetString(), listed.GetProperty("progress").GetDouble()));
        Assert.Equal(7, listed.GetProperty("seeders").GetInt32());
        Assert.Equal(0, listed.GetProperty("uploadSpeedBps").GetInt64());
        Assert.True(queue.GetProperty("speedBps").GetInt64() >= 524288);

        var detail = await _owner.GetFromJsonAsync<JsonElement>($"/api/v1/download/{item.Id}");
        Assert.Equal("torrent", detail.GetProperty("protocol").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.PostAsync($"/api/v1/download/{item.Id}/pause", null)).StatusCode);

        _qbit.Torrent(item.ExternalId!).Phase = FakePhase.Seeding;
        _qbit.Torrent(item.ExternalId!).Ratio = 0.5;
        await tracker.PollAsync();
        var history = await _owner.GetFromJsonAsync<JsonElement>("/api/v1/download/history");
        var finished = history.GetProperty("records").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == item.Id);
        Assert.Equal((true, 0.5), (finished.GetProperty("seeding").GetBoolean(), finished.GetProperty("ratio").GetDouble()));

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/download/{item.Id}?removeFromClient=true&deleteFiles=true")).StatusCode);
        Assert.Equal((item.ExternalId!, true), Assert.Single(_qbit.Removed));
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync($"/api/v1/download/{item.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_unreachable_client_shows_in_the_status_window_and_the_list()
    {
        var created = await (await _owner.PostAsJsonAsync("/api/v1/downloadclient", NewClient())).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        var tracker = _factory.Services.GetRequiredService<TorrentTracker>();
        await tracker.AddAsync(new DownloadRequest([], "Generated.Health", DownloadCategory.Movie, MediaLink.None)
        {
            Protocol = DownloadProtocol.Torrent,
            MagnetUrl = TorrentFixtures.Magnet("Generated.Health"),
        });
        _qbit.Broken = true;
        await tracker.PollAsync();

        var ongoing = await _owner.GetFromJsonAsync<JsonElement>("/api/v1/status/ongoing");
        var notice = ongoing.GetProperty("notices").EnumerateArray().Single(n => n.GetProperty("key").GetString() == $"download-client-{id}");
        Assert.Equal(("error", "/settings/downloadclients"), (notice.GetProperty("level").GetString(), notice.GetProperty("link").GetString()));
        Assert.Contains("500", notice.GetProperty("sub").GetString(), StringComparison.Ordinal);
        var status = (await _owner.GetFromJsonAsync<JsonElement>($"/api/v1/downloadclient/{id}")).GetProperty("status");
        Assert.False(status.GetProperty("ok").GetBoolean());

        _qbit.Broken = false;
        await tracker.PollAsync();
        ongoing = await _owner.GetFromJsonAsync<JsonElement>("/api/v1/status/ongoing");
        Assert.DoesNotContain(ongoing.GetProperty("notices").EnumerateArray(), n => n.GetProperty("key").GetString() == $"download-client-{id}");
    }
}
