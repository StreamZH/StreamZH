// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Tests.Host;
using StreamZH.Torrent;

namespace StreamZH.Tests.Torrents;

/// <summary>GET/PUT /api/v1/settings/torrentengine, /api/v1/torrentengine/* and the engine's lifecycle in the host.</summary>
public sealed class TorrentEngineApiTests : IDisposable
{
    private const string SettingsUrl = "/api/v1/settings/torrentengine";

    private readonly StreamZhFactory _root = new();
    private readonly WebApplicationFactory<Program> _factory;

    public TorrentEngineApiTests() =>
        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton(new TorrentEngineHostOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200))))));

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();
    }

    [Fact]
    public async Task Settings_have_safe_defaults_and_save_partially()
    {
        var owner = await OwnerAsync();
        var defaults = await owner.GetFromJsonAsync<JsonElement>(SettingsUrl);
        Assert.Equal(TorrentEngineOptions.DefaultListenPort, defaults.GetProperty("listenPort").GetInt32());
        Assert.Equal(string.Empty, defaults.GetProperty("bindAddress").GetString());
        Assert.True(defaults.GetProperty("dht").GetBoolean());
        Assert.True(defaults.GetProperty("pex").GetBoolean());
        Assert.False(defaults.GetProperty("localPeerDiscovery").GetBoolean());
        Assert.False(defaults.GetProperty("portForwarding").GetBoolean());
        Assert.Equal("prefer", defaults.GetProperty("encryption").GetString());
        Assert.Equal(1.0, defaults.GetProperty("seedRatio").GetDouble());
        Assert.Equal(JsonValueKind.Null, defaults.GetProperty("seedTimeMinutes").ValueKind);
        Assert.Equal(0, defaults.GetProperty("downloadLimitKBps").GetInt64());

        var saved = await owner.PutAsJsonAsync(SettingsUrl, new { listenPort = 52000, encryption = "require", seedRatio = (double?)null, seedTimeMinutes = 120, uploadLimitKBps = 2048 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(52000, body.GetProperty("listenPort").GetInt32());
        Assert.Equal("require", body.GetProperty("encryption").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("seedRatio").ValueKind);
        Assert.Equal(120, body.GetProperty("seedTimeMinutes").GetInt32());
        Assert.Equal(2048, body.GetProperty("uploadLimitKBps").GetInt64());
        Assert.True(body.GetProperty("dht").GetBoolean());

        var options = _factory.Services.GetRequiredService<TorrentEngineSettings>().Current;
        Assert.Equal(52000, options.ListenPort);
        Assert.Equal(TorrentEncryptionMode.Require, options.Encryption);
        Assert.Null(options.SeedRatio);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected_per_field()
    {
        var owner = await OwnerAsync();
        var response = await owner.PutAsJsonAsync(SettingsUrl, new
        {
            listenPort = 70000,
            bindAddress = "vpn0",
            encryption = "maybe",
            dht = "yes",
            maxConnections = 0,
            seedRatio = "lots",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        foreach (var field in new[] { "listenPort", "bindAddress", "encryption", "dht", "maxConnections", "seedRatio" })
        {
            Assert.True(errors.TryGetProperty(field, out _), field);
        }

        Assert.Equal(TorrentEngineOptions.DefaultListenPort, _factory.Services.GetRequiredService<TorrentEngineSettings>().Current.ListenPort);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsync(SettingsUrl, JsonContent.Create(new[] { 1, 2 }))).StatusCode);
    }

    [Fact]
    public async Task Members_read_admins_change_requesters_get_nothing()
    {
        await OwnerAsync();
        var friend = await UserAsync("friend1", UserRole.Friend);
        var requester = await UserAsync("requester1", UserRole.Requester);
        const string hash = "0123456789abcdef0123456789abcdef01234567";

        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync(SettingsUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/torrentengine/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/torrentengine/torrents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PutAsJsonAsync(SettingsUrl, new { listenPort = 52001 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsync($"/api/v1/torrentengine/torrents/{hash}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.DeleteAsync($"/api/v1/torrentengine/torrents/{hash}?deleteData=true")).StatusCode);

        foreach (var url in new[] { SettingsUrl, "/api/v1/torrentengine/status", "/api/v1/torrentengine/torrents" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(SettingsUrl)).StatusCode);
    }

    [Fact]
    public async Task The_engine_runs_only_while_a_StreamZH_client_is_enabled()
    {
        var owner = await OwnerAsync();
        var port = TorrentEngineTestBed.FreePort();
        (await owner.PutAsJsonAsync(SettingsUrl, new { listenPort = port, bindAddress = "127.0.0.1", dht = false })).EnsureSuccessStatusCode();
        var engine = _factory.Services.GetRequiredService<TorrentEngine>();
        Assert.Contains(_factory.Services.GetServices<ITorrentClient>(), c => c.Implementation == DownloadClientDefinition.BuiltInTorrent);

        var status = await owner.GetFromJsonAsync<JsonElement>("/api/v1/torrentengine/status");
        Assert.False(status.GetProperty("running").GetBoolean());
        Assert.False(status.GetProperty("wanted").GetBoolean());
        Assert.Equal("stopped", status.GetProperty("portState").GetString());
        Assert.Equal(0, status.GetProperty("problems").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(status.GetProperty("engineVersion").GetString()));
        Assert.Empty(await owner.GetFromJsonAsync<JsonElement[]>("/api/v1/torrentengine/torrents") ?? []);

        var clients = _factory.Services.GetRequiredService<DownloadClientRepository>();
        var id = clients.Add(new DownloadClientDefinition(0, "StreamZH", DownloadClientDefinition.BuiltInTorrent, DownloadProtocol.Torrent, true, 1, new Dictionary<string, JsonElement>()));
        await TorrentEngineTestBed.WaitUntilAsync(() => engine.IsRunning, "the engine to start", 20);
        status = await owner.GetFromJsonAsync<JsonElement>("/api/v1/torrentengine/status");
        Assert.True(status.GetProperty("wanted").GetBoolean());
        Assert.True(status.GetProperty("running").GetBoolean());
        Assert.Equal(port, status.GetProperty("listenPort").GetInt32());

        // New settings restart the running engine with them.
        var newPort = TorrentEngineTestBed.FreePort();
        (await owner.PutAsJsonAsync(SettingsUrl, new { listenPort = newPort })).EnsureSuccessStatusCode();
        await TorrentEngineTestBed.WaitUntilAsync(() => engine.IsRunning && engine.Status().ListenPort == newPort, "the restart", 20);

        // Unknown torrents: 404, also for something that is not a hash at all.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync("/api/v1/torrentengine/torrents/0123456789abcdef0123456789abcdef01234567/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync("/api/v1/torrentengine/torrents/..%2F..%2Fetc/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync("/api/v1/torrentengine/torrents/xyz")).StatusCode);

        var client = clients.Find(id)!;
        clients.Update(client with { Enable = false });
        await TorrentEngineTestBed.WaitUntilAsync(() => !engine.IsRunning, "the engine to stop", 20);
        Assert.False(engine.Wanted);
    }

    private async Task<HttpClient> OwnerAsync()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<HttpClient> UserAsync(string name, UserRole role)
    {
        var passwords = _factory.Services.GetRequiredService<PasswordService>();
        _factory.Services.GetRequiredService<UserRepository>().Create(name, passwords.Hash("member password 123"), role);
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = name, password = "member password 123" })).EnsureSuccessStatusCode();
        return client;
    }
}
