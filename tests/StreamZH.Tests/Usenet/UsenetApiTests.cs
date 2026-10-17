// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;
using StreamZH.Library.Usenet;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Usenet;

public sealed class UsenetApiTests : IAsyncLifetime
{
    private readonly StreamZhFactory _factory = new();
    private readonly FakeNntpServer _server = new() { Username = "user", Password = "provider-pass" };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Creates_lists_updates_and_deletes_with_a_masked_password()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var create = await client.PostAsJsonAsync("/api/v1/usenet/server", NewServer("Eweka", "provider-pass"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<UsenetServerResource>())!;
        Assert.Equal($"/api/v1/usenet/server/{created.Id}", create.Headers.Location?.ToString());
        Assert.Equal(UsenetServerService.MaskedPassword, created.Password);
        Assert.Equal(563, created.Port);
        Assert.True(created.UseSsl);
        Assert.Equal(20, created.Connections);

        Assert.DoesNotContain("provider-pass", await client.GetStringAsync("/api/v1/usenet/server"), StringComparison.Ordinal);
        Assert.DoesNotContain("provider-pass", await client.GetStringAsync($"/api/v1/usenet/server/{created.Id}"), StringComparison.Ordinal);

        var update = await client.PutAsJsonAsync($"/api/v1/usenet/server/{created.Id}", created with { Name = "Eweka NL", Priority = 3 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var service = _factory.Services.GetRequiredService<UsenetServerService>();
        Assert.Equal("provider-pass", service.Find(created.Id)!.Password);
        Assert.Equal(3, service.Find(created.Id)!.Priority);
        Assert.Equal(20, _factory.Services.GetRequiredService<IArticleSource>().MaxConnections);

        var list = (await client.GetFromJsonAsync<UsenetServerResource[]>("/api/v1/usenet/server"))!;
        Assert.Equal(["Eweka NL"], list.Select(s => s.Name));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/usenet/server/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/v1/usenet/server/999", created)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/usenet/server/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/usenet/server/{created.Id}")).StatusCode);
        Assert.Equal(0, _factory.Services.GetRequiredService<IArticleSource>().MaxConnections);
    }

    [Fact]
    public async Task Rejects_invalid_servers_with_field_errors()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/usenet/server", new { name = "", host = "", port = 70000, connections = 101, priority = -1, retentionDays = -5 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["connections", "host", "name", "port", "priority", "retentionDays"],
            problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Test_connects_with_the_stored_password()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var created = (await (await client.PostAsJsonAsync("/api/v1/usenet/server", LocalServer("provider-pass"))).Content.ReadFromJsonAsync<UsenetServerResource>())!;

        var response = await client.PostAsJsonAsync("/api/v1/usenet/server/test", created);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Contains("logged in", body.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(body.GetProperty("latencyMs").GetInt32() >= 0);
    }

    [Fact]
    public async Task Failed_test_is_a_problem_with_a_code()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/usenet/server/test", LocalServer("wrong-pass"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("server-test-failed", body, StringComparison.Ordinal);
        Assert.Contains("Check the user name and password.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-pass", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_shows_every_server_with_connections_and_speed()
    {
        var client = await _factory.CreateOwnerClientAsync();
        await client.PostAsJsonAsync("/api/v1/usenet/server", LocalServer("provider-pass"));
        _server.AddYenc("a@x", YencArticles.Random(5000));
        await _factory.Services.GetRequiredService<IArticleSource>().FetchAsync("a@x", [], DateTimeOffset.UtcNow, CancellationToken.None);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/usenet/status");

        var server = status.GetProperty("servers")[0];
        Assert.Equal("Local", server.GetProperty("name").GetString());
        Assert.Equal(0, server.GetProperty("connectionsInUse").GetInt32());
        Assert.Equal(2, server.GetProperty("connections").GetInt32());
        Assert.True(server.GetProperty("healthy").GetBoolean());
        Assert.True(server.GetProperty("bytesToday").GetInt64() > 5000);
        Assert.Equal(JsonValueKind.Null, server.GetProperty("lastError").ValueKind);
        Assert.True(status.GetProperty("speedBps").GetInt64() > 0);
    }

    [Fact]
    public async Task Friends_can_read_but_not_change_servers()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var created = (await (await owner.PostAsJsonAsync("/api/v1/usenet/server", NewServer("A", "pw"))).Content.ReadFromJsonAsync<UsenetServerResource>())!;
        var passwords = _factory.Services.GetRequiredService<PasswordService>();
        _factory.Services.GetRequiredService<UserRepository>().Create("friend", passwords.Hash("friend password 123"), UserRole.Friend);
        var friend = _factory.CreateClient();
        (await friend.PostAsJsonAsync("/api/v1/auth/login", new { username = "friend", password = "friend password 123" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/usenet/server")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/usenet/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync("/api/v1/usenet/server", NewServer("B", "pw"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PutAsJsonAsync($"/api/v1/usenet/server/{created.Id}", created)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.DeleteAsync($"/api/v1/usenet/server/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync("/api/v1/usenet/server/test", created)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/usenet/server")).StatusCode);
    }

    [Fact]
    public async Task Expert_settings_round_trip_and_the_quota_counter_can_be_reset()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/usenet/server", new
        {
            name = "Block",
            host = "127.0.0.1",
            port = _server.Port,
            useSsl = false,
            username = "user",
            password = "provider-pass",
            connections = 2,
            timeoutSeconds = 90,
            certificateCheck = "minimal",
            articlesPerRequest = 2,
            onFailure = "pause",
            expiresOn = "2027-01-31",
            quotaGb = 0.5,
            notes = "Bought in October",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<UsenetServerResource>())!;
        Assert.Equal(90, created.TimeoutSeconds);
        Assert.Equal("minimal", created.CertificateCheck);
        Assert.Equal(2, created.ArticlesPerRequest);
        Assert.Equal("pause", created.OnFailure);
        Assert.Equal("2027-01-31", created.ExpiresOn);
        Assert.Equal(0.5, created.QuotaGb);
        Assert.Equal(0, created.QuotaUsedBytes);
        Assert.NotNull(created.QuotaSince);
        Assert.Equal("Bought in October", created.Notes);
        Assert.Equal(4, _factory.Services.GetRequiredService<IArticleSource>().MaxConnections);

        _server.AddYenc("a@x", YencArticles.Random(5000));
        await _factory.Services.GetRequiredService<IArticleSource>().FetchAsync("a@x", [], DateTimeOffset.UtcNow, CancellationToken.None);
        var listed = (await client.GetFromJsonAsync<UsenetServerResource[]>("/api/v1/usenet/server"))!.Single();
        Assert.True(listed.QuotaUsedBytes > 5000);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/usenet/status");
        Assert.Equal(listed.QuotaUsedBytes, status.GetProperty("servers")[0].GetProperty("quotaUsedBytes").GetInt64());
        Assert.False(status.GetProperty("servers")[0].GetProperty("quotaExhausted").GetBoolean());

        // Saving the unchanged resource keeps every setting and the counter.
        var saved = (await (await client.PutAsJsonAsync($"/api/v1/usenet/server/{created.Id}", listed)).Content.ReadFromJsonAsync<UsenetServerResource>())!;
        Assert.Equal(listed with { QuotaUsedBytes = saved.QuotaUsedBytes }, saved);
        Assert.Equal(listed.QuotaUsedBytes, saved.QuotaUsedBytes);

        var reset = await client.PostAsync($"/api/v1/usenet/server/{created.Id}/quota/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(0, (await reset.Content.ReadFromJsonAsync<UsenetServerResource>())!.QuotaUsedBytes);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/usenet/server/999/quota/reset", null)).StatusCode);
    }

    [Fact]
    public async Task Rejects_invalid_expert_settings_with_field_errors()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/usenet/server", new
        {
            name = "A",
            host = "news.example.com",
            certificateCheck = "lenient",
            onFailure = "explode",
            quotaGb = -1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["certificateCheck", "onFailure", "quotaGb"],
            problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        response = await client.PostAsJsonAsync("/api/v1/usenet/server", new { name = "A", host = "news.example.com", timeoutSeconds = 301, articlesPerRequest = 0, expiresOn = "soon" });
        problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["articlesPerRequest", "expiresOn", "timeoutSeconds"],
            problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Status_window_shows_an_expiring_account()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var expires = DateOnly.FromDateTime(DateTime.Now).AddDays(3);
        await client.PostAsJsonAsync("/api/v1/usenet/server", new
        {
            name = "Newshosting",
            host = "news.example.com",
            username = "user",
            password = "pw",
            expiresOn = expires.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        });

        var ongoing = await client.GetFromJsonAsync<JsonElement>("/api/v1/status/ongoing");

        var notice = ongoing.GetProperty("notices").EnumerateArray().Single(n => n.GetProperty("key").GetString()!.StartsWith("usenet-expiry-", StringComparison.Ordinal));
        Assert.Equal(
            $"Newshosting account expires on {expires.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture)}",
            notice.GetProperty("title").GetString());
        Assert.Equal("warn", notice.GetProperty("level").GetString());
    }

    private static object NewServer(string name, string password) => new { name, host = "news.example.com", username = "user", password };

    private object LocalServer(string password) => new
    {
        name = "Local",
        host = "127.0.0.1",
        port = _server.Port,
        useSsl = false,
        username = "user",
        password,
        connections = 2,
        priority = 0,
        enabled = true,
        retentionDays = 0,
    };
}
