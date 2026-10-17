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
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;
using StreamZH.Library.Indexers;
using StreamZH.Tests.Library;

namespace StreamZH.Tests.Host;

public sealed class IndexerApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();
    private readonly StubIndexerClient _client = new();

    [Fact]
    public async Task Creates_lists_updates_and_deletes_with_a_masked_api_key()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var create = await client.PostAsJsonAsync("/api/v1/indexer", NewIndexer("Usenet A", "secret-key"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<IndexerResource>())!;
        Assert.Equal($"/api/v1/indexer/{created.Id}", create.Headers.Location?.ToString());
        Assert.Equal(IndexerService.MaskedApiKey, created.ApiKey);
        Assert.Equal("https://indexer.example", created.BaseUrl);
        Assert.True(created.Status!.Healthy);

        // The key never shows up in any response.
        Assert.DoesNotContain("secret-key", await client.GetStringAsync("/api/v1/indexer"), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", await client.GetStringAsync($"/api/v1/indexer/{created.Id}"), StringComparison.Ordinal);

        // Sending the masked value back keeps the stored key.
        var update = await client.PutAsJsonAsync($"/api/v1/indexer/{created.Id}", created with { Name = "Usenet B", Priority = 5 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var service = _factory.Services.GetRequiredService<IndexerService>();
        Assert.Equal("secret-key", service.Find(created.Id)!.ApiKey);
        Assert.Equal("Usenet B", service.Find(created.Id)!.Name);

        await client.PutAsJsonAsync($"/api/v1/indexer/{created.Id}", created with { ApiKey = "new-key" });
        Assert.Equal("new-key", service.Find(created.Id)!.ApiKey);

        var list = (await client.GetFromJsonAsync<IndexerResource[]>("/api/v1/indexer"))!;
        Assert.Equal([created.Id], list.Select(i => i.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/indexer/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/v1/indexer/999", created)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/indexer/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/indexer/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Rejects_invalid_indexers_with_field_errors()
    {
        var client = await _factory.CreateOwnerClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/indexer", new { name = "", baseUrl = "not a url", priority = 99, categories = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["baseUrl", "categories", "name", "priority"],
            problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Shows_health_with_disabled_until_and_last_error()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var created = (await (await client.PostAsJsonAsync("/api/v1/indexer", NewIndexer("A", "key"))).Content.ReadFromJsonAsync<IndexerResource>())!;

        _factory.Services.GetRequiredService<IIndexerProvider>().RecordFailure(created.Id, new IndexerException("Request limit reached", 500));

        var status = (await client.GetFromJsonAsync<IndexerResource[]>("/api/v1/indexer"))!.Single().Status!;
        Assert.False(status.Healthy);
        Assert.Equal("Request limit reached", status.LastError);
        Assert.InRange(status.DisabledTill!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        Assert.Empty(_factory.Services.GetRequiredService<IIndexerProvider>().Active(IndexerUse.InteractiveSearch));
    }

    [Fact]
    public async Task Schema_has_defaults_for_a_new_newznab_indexer()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var schema = (await client.GetFromJsonAsync<IndexerResource>("/api/v1/indexer/schema"))!;

        Assert.Equal(0, schema.Id);
        Assert.Equal("Newznab", schema.Implementation);
        Assert.Equal("/api", schema.ApiPath);
        Assert.Equal(25, schema.Priority);
        Assert.Equal(string.Empty, schema.ApiKey);
        Assert.True(schema.EnableRss && schema.EnableAutomaticSearch && schema.EnableInteractiveSearch);
        Assert.Equal([2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060], schema.Categories);
        Assert.Equal([5030, 5040], schema.TvCategories);
    }

    [Fact]
    public async Task Test_returns_capabilities_and_uses_the_stored_key_for_a_masked_value()
    {
        var factory = WithIndexerClient();
        var client = await CreateOwnerClientAsync(factory);
        var created = (await (await client.PostAsJsonAsync("/api/v1/indexer", NewIndexer("A", "stored"))).Content.ReadFromJsonAsync<IndexerResource>())!;

        var response = await client.PostAsJsonAsync("/api/v1/indexer/test", created);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var caps = await response.Content.ReadFromJsonAsync<JsonElement>();
        var movies = caps.GetProperty("categories")[0];
        Assert.Equal(2000, movies.GetProperty("id").GetInt32());
        Assert.Equal("Movies/HD", movies.GetProperty("subCategories")[0].GetProperty("name").GetString());
        Assert.True(caps.GetProperty("movieSearchAvailable").GetBoolean());
        Assert.Equal("stored", _client.LastIndexer!.ApiKey);

        // Testing a new, unsaved indexer saves nothing.
        var fresh = await client.PostAsJsonAsync("/api/v1/indexer/test", NewIndexer("New", "typed-key"));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal("typed-key", _client.LastIndexer!.ApiKey);
        Assert.Single((await client.GetFromJsonAsync<IndexerResource[]>("/api/v1/indexer"))!);
    }

    [Fact]
    public async Task Test_returns_the_indexer_error_as_a_problem()
    {
        var factory = WithIndexerClient();
        var client = await CreateOwnerClientAsync(factory);
        _client.Error = new IndexerException("Request limit reached", 500, TimeSpan.FromHours(1));

        var response = await client.PostAsJsonAsync("/api/v1/indexer/test", NewIndexer("A", "key"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Request limit reached", problem.GetProperty("title").GetString());
        Assert.Equal("indexer-test-failed", problem.GetProperty("code").GetString());

        var invalid = await client.PostAsJsonAsync("/api/v1/indexer/test", new { baseUrl = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True((await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("baseUrl", out _));
    }

    [Fact]
    public async Task Test_without_an_indexer_client_is_unavailable()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/indexer/test", NewIndexer("A", "key"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Friends_can_read_but_not_change_indexers()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var created = (await (await owner.PostAsJsonAsync("/api/v1/indexer", NewIndexer("A", "key"))).Content.ReadFromJsonAsync<IndexerResource>())!;
        var passwords = _factory.Services.GetRequiredService<PasswordService>();
        _factory.Services.GetRequiredService<UserRepository>().Create("friend", passwords.Hash("friend password 123"), UserRole.Friend);

        var friend = _factory.CreateClient();
        (await friend.PostAsJsonAsync("/api/v1/auth/login", new { username = "friend", password = "friend password 123" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/indexer")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync($"/api/v1/indexer/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync("/api/v1/indexer", NewIndexer("B", "key"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PutAsJsonAsync($"/api/v1/indexer/{created.Id}", created)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.DeleteAsync($"/api/v1/indexer/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync("/api/v1/indexer/test", created)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/indexer")).StatusCode);
    }

    private static object NewIndexer(string name, string apiKey) => new
    {
        name,
        implementation = "Newznab",
        baseUrl = "https://indexer.example/",
        apiPath = "/api",
        apiKey,
        categories = new[] { 2000, 2040 },
        tvCategories = new[] { 5040 },
        priority = 25,
        enableRss = true,
        enableAutomaticSearch = true,
        enableInteractiveSearch = true,
    };

    private WebApplicationFactory<Program> WithIndexerClient() =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IIndexerClient>(_client)));

    private static async Task<HttpClient> CreateOwnerClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public void Dispose() => _factory.Dispose();
}
