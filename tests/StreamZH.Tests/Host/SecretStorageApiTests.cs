// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Data;

namespace StreamZH.Tests.Host;

/// <summary>0.12: the running app stores secrets encrypted, every API still masks them, and an unreadable one shows a notice.</summary>
public sealed class SecretStorageApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-secret-api-").FullName;

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_media, recursive: true);
    }

    private string Raw(string sql)
    {
        using var connection = _factory.Services.GetRequiredService<Database>().Open();
        return string.Join("\n", connection.Query<string?>(sql));
    }

    private static async Task Created(HttpResponseMessage response) =>
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task Secrets_are_encrypted_in_the_database_and_masked_by_every_api()
    {
        var owner = await _factory.CreateOwnerClientAsync();

        // The API key created at the first start is encrypted, and still works.
        Assert.StartsWith(SecretCodec.Prefix, Raw("SELECT Value FROM Config WHERE Key = 'ApiKey'"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateApiKeyClient().GetAsync("/api/v1/indexer")).StatusCode);

        await Created(await owner.PostAsJsonAsync("/api/v1/indexer", new
        {
            name = "Indexer", implementation = "Newznab", baseUrl = "https://indexer.example", apiPath = "/api", apiKey = "secret-indexer-key",
            categories = new[] { 2000 }, tvCategories = new[] { 5040 }, priority = 25,
        }));
        await Created(await owner.PostAsJsonAsync("/api/v1/usenet/server", new { name = "Provider", host = "news.example.com", username = "user", password = "secret-usenet-pass" }));
        await Created(await owner.PostAsJsonAsync("/api/v1/connection", new
        {
            name = "Hook", implementation = "webhook", onGrab = true, settings = new { url = "https://hooks.example.com/x", username = "u", password = "secret-hook-pass" },
        }));
        await Created(await owner.PostAsJsonAsync("/api/v1/downloadclient", new
        {
            name = "qBittorrent", implementation = "qBittorrent", enable = true, priority = 1,
            fields = new object[] { new { name = "host", value = "qbit.local" }, new { name = "password", value = "secret-qbit-pass" } },
        }));
        var root = await (await owner.PostAsJsonAsync("/api/v1/rootfolders", new { path = Directory.CreateDirectory(Path.Combine(_media, "m")).FullName, mediaType = "Movie" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await Created(await owner.PostAsJsonAsync("/api/v1/importlist", new
        {
            name = "Watchlist", implementation = "plexWatchlist", mediaType = "Movie", enabled = true, enableAuto = false,
            rootFolderId = root.GetProperty("id").GetInt32(), settings = new { accessToken = "secret-plex-token" },
        }));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/settings/general", new
        {
            proxyEnabled = true, proxyType = "http", proxyHostname = "proxy.example", proxyPort = 3128, proxyUsername = "p", proxyPassword = "secret-proxy-pass",
        })).StatusCode);

        var raw = string.Join("\n", Raw("SELECT ApiKey FROM Indexers"), Raw("SELECT Password FROM UsenetServers"), Raw("SELECT Settings FROM Connections"),
            Raw("SELECT Settings FROM DownloadClients"), Raw("SELECT Settings FROM ImportLists"), Raw("SELECT Value FROM Config"));
        Assert.DoesNotContain("secret-", raw, StringComparison.Ordinal);

        foreach (var path in new[] { "/api/v1/indexer", "/api/v1/usenet/server", "/api/v1/connection", "/api/v1/downloadclient", "/api/v1/importlist", "/api/v1/settings/general" })
        {
            var body = await owner.GetStringAsync(path);
            Assert.DoesNotContain("secret-", body, StringComparison.Ordinal);
            Assert.True(path.EndsWith("general", StringComparison.Ordinal) || body.Contains("********", StringComparison.Ordinal), path);
        }

        // The secrets still work: the stored values are read back.
        Assert.Equal("secret-indexer-key", _factory.Services.GetRequiredService<IndexerRepository>().All().Single().ApiKey);
        Assert.Equal("secret-proxy-pass", _factory.Services.GetRequiredService<ConfigRepository>().Get(ConfigKeys.ProxyPassword));
    }

    [Fact]
    public async Task A_secret_saved_with_other_keys_shows_a_notice_instead_of_failing()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        await Created(await owner.PostAsJsonAsync("/api/v1/usenet/server", new { name = "Provider", host = "news.example.com", username = "user", password = "pass" }));
        using (var connection = _factory.Services.GetRequiredService<Database>().Open())
        {
            connection.Execute("UPDATE UsenetServers SET Password = 'enc:v1:CfDJ8-from-another-installation'");
        }

        var servers = await owner.GetFromJsonAsync<JsonElement>("/api/v1/usenet/server");
        Assert.Equal(string.Empty, servers[0].GetProperty("password").GetString());
        var ongoing = await owner.GetFromJsonAsync<JsonElement>("/api/v1/status/ongoing");
        var notice = ongoing.GetProperty("notices").EnumerateArray().Single(n => n.GetProperty("key").GetString()!.StartsWith("secret-unreadable", StringComparison.Ordinal));
        Assert.Equal("Usenet server Provider: secret unreadable", notice.GetProperty("title").GetString());
        Assert.Equal("/settings/usenet", notice.GetProperty("link").GetString());

        // Entered again: the notice is gone.
        var id = servers[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v1/usenet/server/{id}", new { id, name = "Provider", host = "news.example.com", username = "user", password = "typed-again" })).StatusCode);
        ongoing = await owner.GetFromJsonAsync<JsonElement>("/api/v1/status/ongoing");
        Assert.DoesNotContain(ongoing.GetProperty("notices").EnumerateArray(), n => n.GetProperty("key").GetString()!.StartsWith("secret-unreadable", StringComparison.Ordinal));
    }
}
