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
using StreamZH.Library.Indexers;
using StreamZH.Tests.Library;

namespace StreamZH.Tests.Host;

public sealed class IndexerCatalogApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();
    private readonly CountingIndexerClient _inner = new();
    private readonly StubProwlarr _prowlarr = new();
    private readonly WebApplicationFactory<Program> _app;

    public IndexerCatalogApiTests()
    {
        _app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // Only the protocol client is replaced: every call goes through the tracking decorator.
            services.AddKeyedSingleton<IIndexerClient>(IndexerClientKeys.Inner, _inner);
            services.AddHttpClient<ProwlarrImportService>().ConfigurePrimaryHttpMessageHandler(() => _prowlarr);
        }));
    }

    [Fact]
    public async Task Definitions_list_the_catalog()
    {
        var client = await OwnerAsync();
        var definitions = await client.GetFromJsonAsync<JsonElement>("/api/v1/indexer/definitions");

        var generic = definitions.EnumerateArray().Single(d => d.GetProperty("key").GetString() == "generic-newznab");
        Assert.Equal(string.Empty, generic.GetProperty("baseUrl").GetString());
        Assert.Equal("/api", generic.GetProperty("apiPath").GetString());
        Assert.Equal(JsonValueKind.Null, generic.GetProperty("siteUrl").ValueKind);

        var house = definitions.EnumerateArray().Single(d => d.GetProperty("key").GetString() == "house-of-usenet");
        Assert.Equal("usenet", house.GetProperty("protocol").GetString());
        Assert.Equal("private", house.GetProperty("privacy").GetString());
        Assert.Equal("/api/v1/api", house.GetProperty("apiPath").GetString());
        Assert.True(house.GetProperty("supported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, house.GetProperty("unsupportedReason").ValueKind);
        Assert.Equal("Newznab", house.GetProperty("implementation").GetString());
    }

    [Fact]
    public async Task Resource_round_trips_the_new_fields_with_status_and_details()
    {
        var client = await OwnerAsync();
        var created = await CreateAsync(client, "Geek", new { definitionKey = "nzbgeek", enable = true, additionalParameters = "attrs=poster", vipExpiration = "2099-01-01", queryLimit = 2, grabLimit = 0, limitsUnit = "hour" });
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal("nzbgeek", created.GetProperty("definitionKey").GetString());
        Assert.Equal("hour", created.GetProperty("limitsUnit").GetString());
        Assert.Equal("2099-01-01", created.GetProperty("vipExpiration").GetString());
        Assert.Equal(2, created.GetProperty("queryLimit").GetInt32());

        var status = created.GetProperty("status");
        Assert.Equal(0, status.GetProperty("queriesUsed").GetInt32());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("limitReached").ValueKind);
        Assert.True(status.GetProperty("vipExpiresInDays").GetInt32() > 0);

        // Details before any test: no capabilities, the catalog entry, nothing sent to the indexer.
        var details = await client.GetFromJsonAsync<JsonElement>($"/api/v1/indexer/{id}/details");
        Assert.Equal(JsonValueKind.Null, details.GetProperty("capabilities").ValueKind);
        Assert.Equal("NZBgeek", details.GetProperty("definition").GetProperty("name").GetString());
        Assert.Equal("https://nzbgeek.info", details.GetProperty("definition").GetProperty("siteUrl").GetString());
        Assert.Equal(0, _inner.Caps);

        (await client.PostAsJsonAsync("/api/v1/indexer/test", created)).EnsureSuccessStatusCode();
        details = await client.GetFromJsonAsync<JsonElement>($"/api/v1/indexer/{id}/details");
        Assert.True(details.GetProperty("capabilities").GetProperty("searchAvailable").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, details.GetProperty("capabilitiesCheckedAt").ValueKind);
        Assert.Equal(id, details.GetProperty("indexer").GetProperty("id").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/indexer/999/details")).StatusCode);
    }

    [Fact]
    public async Task Rejects_invalid_new_fields_with_camel_case_keys()
    {
        var client = await OwnerAsync();
        var response = await client.PostAsJsonAsync("/api/v1/indexer", Indexer("A", new { additionalParameters = "a=b c", vipExpiration = "soon", queryLimit = -1, grabLimit = -1 }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(["additionalParameters", "grabLimit", "queryLimit", "vipExpiration"], errors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        var unit = await client.PostAsJsonAsync("/api/v1/indexer", Indexer("A", new { limitsUnit = "week" }));
        Assert.Equal(HttpStatusCode.BadRequest, unit.StatusCode);
        Assert.True((await unit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("limitsUnit", out _));
    }

    [Fact]
    public async Task Clone_uses_the_stored_key_of_the_source()
    {
        var client = await OwnerAsync();
        var source = await CreateAsync(client, "Source", null);
        var sourceId = source.GetProperty("id").GetInt32();

        var test = await client.PostAsJsonAsync("/api/v1/indexer/test", Indexer("Copy", new { apiKey = "********", cloneFromId = sourceId }));
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);

        var clone = await client.PostAsJsonAsync("/api/v1/indexer", Indexer("Copy", new { apiKey = "********", cloneFromId = sourceId }));
        Assert.Equal(HttpStatusCode.Created, clone.StatusCode);
        var cloneId = (await clone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal("secret-key", _app.Services.GetRequiredService<IndexerService>().Find(cloneId)!.ApiKey);

        var withoutSource = await client.PostAsJsonAsync("/api/v1/indexer", Indexer("Other", new { apiKey = "********" }));
        Assert.Equal(HttpStatusCode.BadRequest, withoutSource.StatusCode);
    }

    [Fact]
    public async Task Test_all_history_and_stats()
    {
        var client = await OwnerAsync();
        var a = (await CreateAsync(client, "A", null)).GetProperty("id").GetInt32();
        await CreateAsync(client, "Off", new { enable = false });

        var testAll = await client.PostAsync("/api/v1/indexer/testall", null);
        Assert.Equal(HttpStatusCode.OK, testAll.StatusCode);
        var results = await testAll.Content.ReadFromJsonAsync<JsonElement>();
        var only = Assert.Single(results.EnumerateArray());
        Assert.Equal((a, "A", true), (only.GetProperty("id").GetInt32(), only.GetProperty("name").GetString(), only.GetProperty("ok").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, only.GetProperty("message").ValueKind);

        // A search through the registered client lands in the indexer history.
        var service = _app.Services.GetRequiredService<IndexerService>();
        using (var scope = _app.Services.CreateScope())
        {
            var tracked = scope.ServiceProvider.GetRequiredService<IIndexerClient>();
            Assert.IsType<TrackedIndexerClient>(tracked);
            await tracked.SearchAsync(service.Find(a)!, new MovieSearchCriteria { Titles = ["Dune"], Year = 2021 }, CancellationToken.None);
        }

        var history = await client.GetFromJsonAsync<JsonElement>($"/api/v1/indexer/history?indexerId={a}&page=1&pageSize=10");
        Assert.Equal(2, history.GetProperty("totalRecords").GetInt32());
        var newest = history.GetProperty("records")[0];
        Assert.Equal("query", newest.GetProperty("eventType").GetString());
        Assert.Equal("interactive", newest.GetProperty("source").GetString());
        Assert.Equal("Dune (2021)", newest.GetProperty("query").GetString());
        Assert.Equal("A", newest.GetProperty("indexer").GetString());
        Assert.Equal(3, newest.GetProperty("results").GetInt32());
        var caps = history.GetProperty("records")[1];
        Assert.Equal(("caps", "test"), (caps.GetProperty("eventType").GetString(), caps.GetProperty("source").GetString()));

        var queries = await client.GetFromJsonAsync<JsonElement>("/api/v1/indexer/history?eventType=query");
        Assert.Equal(1, queries.GetProperty("totalRecords").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/indexer/history?eventType=nope")).StatusCode);

        var stats = await client.GetFromJsonAsync<JsonElement>("/api/v1/indexer/stats?days=7");
        var row = stats.GetProperty("indexers").EnumerateArray().Single(r => r.GetProperty("indexerId").GetInt32() == a);
        Assert.Equal(1, row.GetProperty("queries").GetInt32());
        Assert.Equal(0, row.GetProperty("grabs").GetInt32());
        Assert.Equal(8, stats.GetProperty("daily").GetArrayLength());
        Assert.Equal(1, stats.GetProperty("daily")[7].GetProperty("queries").GetInt32());
        Assert.Equal(2, stats.GetProperty("indexers").GetArrayLength());
    }

    [Fact]
    public async Task Imports_from_prowlarr()
    {
        var client = await OwnerAsync();
        var preview = await client.PostAsJsonAsync("/api/v1/indexer/import/prowlarr/preview", new { url = "http://prowlarr:9696", apiKey = StubProwlarr.ApiKey });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = await preview.Content.ReadAsStringAsync();
        Assert.DoesNotContain(StubProwlarr.ApiKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain("own-secret", body, StringComparison.Ordinal);
        var items = JsonDocument.Parse(body).RootElement;
        Assert.Equal("proxy", items[0].GetProperty("mode").GetString());
        Assert.Equal("direct", items[1].GetProperty("mode").GetString());
        Assert.Equal(1, items[0].GetProperty("prowlarrId").GetInt32());

        var import = await client.PostAsJsonAsync("/api/v1/indexer/import/prowlarr", new { url = "http://prowlarr:9696", apiKey = StubProwlarr.ApiKey, ids = new[] { 1, 4 } });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var result = await import.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NZBgeek", result.GetProperty("imported")[0].GetProperty("name").GetString());
        Assert.Equal("proxy", result.GetProperty("imported")[0].GetProperty("mode").GetString());

        // 0.11: the Torznab indexer comes along too (through Prowlarr).
        Assert.Equal(("Some Torznab", "proxy"), (result.GetProperty("imported")[1].GetProperty("name").GetString(), result.GetProperty("imported")[1].GetProperty("mode").GetString()));
        Assert.Empty(result.GetProperty("skipped").EnumerateArray());

        var wrongKey = await client.PostAsJsonAsync("/api/v1/indexer/import/prowlarr/preview", new { url = "http://prowlarr:9696", apiKey = "wrong" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongKey.StatusCode);
    }

    private async Task<HttpClient> OwnerAsync()
    {
        var client = _app.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string name, object? extra)
    {
        var response = await client.PostAsJsonAsync("/api/v1/indexer", Indexer(name, extra));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A valid indexer body, with the properties of <paramref name="extra"/> on top.</summary>
    private static Dictionary<string, object?> Indexer(string name, object? extra)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["implementation"] = "Newznab",
            ["baseUrl"] = "https://indexer.example",
            ["apiPath"] = "/api",
            ["apiKey"] = "secret-key",
            ["categories"] = new[] { 2000 },
            ["tvCategories"] = new[] { 5040 },
            ["priority"] = 25,
        };

        if (extra is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(extra).EnumerateObject())
            {
                body[property.Name] = property.Value;
            }
        }

        return body;
    }

    public void Dispose()
    {
        _app.Dispose();
        _factory.Dispose();
    }
}
