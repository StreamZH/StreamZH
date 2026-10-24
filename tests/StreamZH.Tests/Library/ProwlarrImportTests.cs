// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Indexers;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Indexers;

namespace StreamZH.Tests.Library;

/// <summary>Serves a Prowlarr /api/v1/indexer answer shaped like Prowlarr's IndexerResource (ProviderResource fields via SchemaBuilder).</summary>
public sealed class StubProwlarr : HttpMessageHandler
{
    public const string ApiKey = "prowlarr-key-123";

    /// <summary>
    /// 1 NZBgeek (Newznab, key masked as Prowlarr always does) → own address, needs key; 2 "Own Newznab" (key readable) → direct;
    /// 3 "Keyless" (Newznab without key, Prowlarr sends "") → direct; 4 Torznab (torrent, key masked, seed settings) → own
    /// address, needs key; 5 Cardigann torrent → proxy (no API of its own); 6 NZBIndex (usenet scraper, no key field) → proxy.
    /// </summary>
    public const string IndexerJson = """
        [
          {
            "id": 1, "name": "NZBgeek", "implementationName": "Newznab", "implementation": "Newznab", "configContract": "NewznabSettings",
            "infoLink": "https://wiki.servarr.com/prowlarr/supported-indexers#newznab", "tags": [],
            "fields": [
              { "order": 0, "name": "baseUrl", "label": "URL", "value": "https://api.nzbgeek.info", "type": "textbox", "advanced": false, "privacy": "normal", "isFloat": false },
              { "order": 1, "name": "apiPath", "label": "API Path", "value": "/api", "type": "textbox", "advanced": true, "privacy": "normal", "isFloat": false },
              { "order": 2, "name": "apiKey", "label": "API Key", "value": "********", "type": "textbox", "advanced": false, "privacy": "apiKey", "isFloat": false },
              { "order": 3, "name": "additionalParameters", "label": "Additional Parameters", "value": "&attrs=poster", "type": "textbox", "advanced": true, "privacy": "normal", "isFloat": false },
              { "order": 4, "name": "vipExpiration", "label": "VIP Expiration", "value": "2027-03-01", "type": "textbox", "advanced": false, "privacy": "normal", "isFloat": false },
              { "order": 5, "name": "baseSettings.queryLimit", "label": "Query Limit", "value": 1000, "type": "number", "advanced": true, "privacy": "normal", "isFloat": false },
              { "order": 6, "name": "baseSettings.grabLimit", "label": "Grab Limit", "value": 100, "type": "number", "advanced": true, "privacy": "normal", "isFloat": false },
              { "order": 7, "name": "baseSettings.limitsUnit", "label": "Limits Unit", "value": 0, "type": "select", "advanced": true, "privacy": "normal", "isFloat": false }
            ],
            "indexerUrls": ["https://api.nzbgeek.info"], "legacyUrls": [], "definitionName": "Newznab", "description": "Newznab is an API search specification for Usenet",
            "language": "en-US", "enable": true, "redirect": false, "supportsRss": true, "supportsSearch": true, "supportsRedirect": true, "supportsPagination": true,
            "appProfileId": 1, "protocol": "usenet", "privacy": "private",
            "capabilities": {
              "id": 0, "limitsMax": 100, "limitsDefault": 100, "supportsRawSearch": false,
              "categories": [
                { "id": 2000, "name": "Movies", "subCategories": [ { "id": 2040, "name": "Movies/HD", "subCategories": [] }, { "id": 2045, "name": "Movies/UHD", "subCategories": [] } ] },
                { "id": 5000, "name": "TV", "subCategories": [ { "id": 5040, "name": "TV/HD", "subCategories": [] } ] },
                { "id": 7000, "name": "Books", "subCategories": [] },
                { "id": 100001, "name": "Custom", "subCategories": [] }
              ],
              "searchParams": ["q"], "tvSearchParams": ["q", "season", "ep", "tvdbId"], "movieSearchParams": ["q", "imdbId", "tmdbId"],
              "musicSearchParams": [], "bookSearchParams": []
            },
            "priority": 10, "downloadClientId": 0, "added": "2026-01-01T00:00:00Z",
            "status": null, "sortName": "nzbgeek"
          },
          {
            "id": 2, "name": "Own Newznab", "implementationName": "Newznab", "implementation": "Newznab", "configContract": "NewznabSettings", "tags": [],
            "fields": [
              { "order": 0, "name": "baseUrl", "value": "https://own.example/" },
              { "order": 1, "name": "apiPath", "value": "/newznab/api" },
              { "order": 2, "name": "apiKey", "value": "own-secret", "privacy": "apiKey" },
              { "order": 3, "name": "additionalParameters", "value": null },
              { "order": 4, "name": "vipExpiration", "value": "" },
              { "order": 5, "name": "baseSettings.queryLimit", "value": null },
              { "order": 6, "name": "baseSettings.grabLimit", "value": 5 },
              { "order": 7, "name": "baseSettings.limitsUnit", "value": 1 }
            ],
            "enable": false, "supportsRss": false, "supportsSearch": true, "protocol": "usenet", "privacy": "private",
            "capabilities": { "categories": [ { "id": 5000, "name": "TV", "subCategories": [ { "id": 5030, "name": "TV/SD", "subCategories": [] } ] } ] },
            "priority": 60
          },
          {
            "id": 3, "name": "Keyless", "implementation": "Newznab", "tags": [],
            "fields": [
              { "name": "baseUrl", "value": "https://keyless.example" },
              { "name": "apiPath", "value": "/api" },
              { "name": "apiKey", "value": "", "privacy": "apiKey" }
            ],
            "enable": true, "protocol": "usenet", "capabilities": { "categories": [] }, "priority": 25
          },
          {
            "id": 4, "name": "Some Torznab", "implementation": "Torznab", "tags": [],
            "fields": [
              { "name": "baseUrl", "value": "https://torznab.example" }, { "name": "apiPath", "value": "/api" }, { "name": "apiKey", "value": "********" },
              { "name": "torrentBaseSettings.appMinimumSeeders", "value": 3 }, { "name": "torrentBaseSettings.seedRatio", "value": 1.5 },
              { "name": "torrentBaseSettings.seedTime", "value": 4320 }, { "name": "torrentBaseSettings.packSeedTime", "value": 10080 },
              { "name": "torrentBaseSettings.preferMagnetUrl", "value": false }
            ],
            "enable": true, "protocol": "torrent", "privacy": "private",
            "capabilities": { "categories": [ { "id": 2000, "name": "Movies", "subCategories": [ { "id": 2040, "name": "Movies/HD", "subCategories": [] } ] },
              { "id": 5000, "name": "TV", "subCategories": [ { "id": 5040, "name": "TV/HD", "subCategories": [] } ] }, { "id": 100021, "name": "Custom", "subCategories": [] } ] },
            "priority": 25
          },
          {
            "id": 5, "name": "1337x", "implementation": "Cardigann", "definitionName": "1337x", "tags": [],
            "fields": [
              { "name": "definitionFile", "value": "1337x" }, { "name": "baseUrl", "value": null },
              { "name": "torrentBaseSettings.appMinimumSeeders", "value": null }, { "name": "torrentBaseSettings.seedRatio", "value": "0" },
              { "name": "torrentBaseSettings.seedTime", "value": null }, { "name": "torrentBaseSettings.packSeedTime", "value": null }
            ],
            "enable": true, "protocol": "torrent", "privacy": "public", "priority": 25
          },
          {
            "id": 6, "name": "NZBIndex", "implementation": "NzbIndex", "tags": [],
            "fields": [ { "name": "baseUrl", "value": null } ],
            "enable": true, "protocol": "usenet", "privacy": "public",
            "capabilities": { "categories": [ { "id": 2000, "name": "Movies", "subCategories": [] } ] }, "priority": 30
          }
        ]
        """;

    public List<HttpRequestMessage> Requests { get; } = [];

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>The indexer list served; <see cref="IndexerJson"/> unless a test needs another one.</summary>
    public string Json { get; set; } = IndexerJson;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (!request.Headers.TryGetValues("X-Api-Key", out var keys) || keys.Single() != ApiKey)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        return Task.FromResult(request.RequestUri!.AbsolutePath == "/api/v1/indexer"
            ? new HttpResponseMessage(Status) { Content = new StringContent(Json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class ProwlarrImportTests : IDisposable
{
    private const string Url = "http://prowlarr:9696/";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");
    private readonly IndexerService _indexers;
    private readonly StubProwlarr _prowlarr = new();
    private readonly ProwlarrImportService _import;

    public ProwlarrImportTests()
    {
        var database = new Database(_file);
        database.Migrate();
        _indexers = new IndexerService(
            new IndexerRepository(database), new IndexerStatusRepository(database), TimeProvider.System, NullLogger<IndexerService>.Instance);
        _import = new ProwlarrImportService(new HttpClient(_prowlarr), _indexers, NullLogger<ProwlarrImportService>.Instance);
    }

    [Fact]
    public async Task Preview_decides_support_and_mode_per_indexer()
    {
        _indexers.Create(new IndexerDefinition(0, "keyless", "Newznab", "https://x.example", "/api", "k", [2000], [], 25, true, true, true));

        var preview = await _import.PreviewAsync(Url, StubProwlarr.ApiKey, CancellationToken.None);

        var request = Assert.Single(_prowlarr.Requests);
        Assert.Equal("http://prowlarr:9696/api/v1/indexer", request.RequestUri!.ToString());
        Assert.Equal([1, 2, 3, 4, 5, 6], preview.Select(p => p.ProwlarrId));

        var geek = preview[0];
        Assert.Equal(("NZBgeek", "usenet", "Newznab", true, false, ProwlarrImportMode.NeedsKey), (geek.Name, geek.Protocol, geek.Implementation, geek.Supported, geek.AlreadyExists, geek.Mode));

        // Always the provider's own address, never Prowlarr's proxy, even though Prowlarr hides the key.
        Assert.Equal(("https://api.nzbgeek.info", "/api"), (geek.BaseUrl, geek.ApiPath));

        var own = preview[1];
        Assert.Equal((ProwlarrImportMode.Direct, "https://own.example", "/newznab/api"), (own.Mode, own.BaseUrl, own.ApiPath));

        Assert.Equal((ProwlarrImportMode.Direct, true), (preview[2].Mode, preview[2].AlreadyExists));

        // 0.11: torrent indexers come along: a Torznab tracker at its own address, a scraper without an API through Prowlarr.
        Assert.All(preview.Skip(3).Take(2), p => Assert.Equal(("torrent", true, (string?)null), (p.Protocol, p.Supported, p.Reason)));
        Assert.Equal(("Torznab", ProwlarrImportMode.NeedsKey, "https://torznab.example", "https://torznab.example/api"),
            (preview[3].Implementation, preview[3].Mode, preview[3].BaseUrl, preview[3].OwnUrl));
        Assert.Equal(("Cardigann", ProwlarrImportMode.Proxy, "http://prowlarr:9696/5", (string?)null),
            (preview[4].Implementation, preview[4].Mode, preview[4].BaseUrl, preview[4].OwnUrl));

        Assert.Equal((true, ProwlarrImportMode.Proxy, "http://prowlarr:9696/6"), (preview[5].Supported, preview[5].Mode, preview[5].BaseUrl));
    }

    [Fact]
    public async Task Own_api_key_imports_the_indexer_directly_without_prowlarr()
    {
        var preview = await _import.PreviewAsync(Url, StubProwlarr.ApiKey, CancellationToken.None);
        Assert.NotNull(preview[0].OwnUrl);
        Assert.DoesNotContain("prowlarr", preview[0].OwnUrl!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(preview[4].OwnUrl);

        var result = await _import.ImportAsync(Url, StubProwlarr.ApiKey, [1], CancellationToken.None, new Dictionary<int, string> { [1] = " my-geek-key " });

        var imported = Assert.Single(result.Imported);
        Assert.Equal(ProwlarrImportMode.Direct, imported.Mode);
        var geek = _indexers.Find(imported.Id)!;
        Assert.Equal("my-geek-key", geek.ApiKey);
        Assert.Equal(preview[0].OwnUrl, geek.BaseUrl + geek.ApiPath);
    }

    [Fact]
    public async Task Import_uses_the_providers_address_and_skips_the_rest()
    {
        _indexers.Create(new IndexerDefinition(0, "Keyless", "Newznab", "https://x.example", "/api", "k", [2000], [], 25, true, true, true));

        var result = await _import.ImportAsync(Url, StubProwlarr.ApiKey, [1, 2, 3, 4, 6, 99], CancellationToken.None);

        Assert.Equal(
            [("NZBgeek", ProwlarrImportMode.NeedsKey), ("Own Newznab", ProwlarrImportMode.Direct), ("Some Torznab", ProwlarrImportMode.NeedsKey), ("NZBIndex", ProwlarrImportMode.Proxy)],
            result.Imported.Select(i => (i.Name, i.Mode)));
        Assert.Equal(
            [(99, "Not found in Prowlarr."), (3, "An indexer with this name already exists.")],
            result.Skipped.Select(s => (s.ProwlarrId, s.Reason)));

        var geek = _indexers.Find(result.Imported[0].Id)!;
        // Prowlarr's key never goes to an indexer; without its own key the indexer waits switched off.
        Assert.Equal(("https://api.nzbgeek.info", "/api", string.Empty), (geek.BaseUrl, geek.ApiPath, geek.ApiKey));
        Assert.Equal([2000, 2040, 2045], geek.Categories);
        Assert.Equal([5000, 5040], geek.TvCategories);
        Assert.Equal((10, false, "nzbgeek"), (geek.Priority, geek.Enable, geek.DefinitionKey));
        Assert.Equal((1000, 100, IndexerLimitsUnit.Day, "2027-03-01"), (geek.QueryLimit!.Value, geek.GrabLimit!.Value, geek.LimitsUnit, geek.VipExpiration));
        Assert.Equal("&attrs=poster", geek.AdditionalParameters);

        var own = _indexers.Find(result.Imported[1].Id)!;
        Assert.Equal(("https://own.example", "/newznab/api", "own-secret"), (own.BaseUrl, own.ApiPath, own.ApiKey));
        Assert.Equal(IndexerService.DefaultMovieCategories, own.Categories);
        Assert.Equal([5000, 5030], own.TvCategories);
        Assert.Equal((50, false, false, true), (own.Priority, own.Enable, own.EnableRss, own.EnableAutomaticSearch));
        Assert.Equal((null, 5, IndexerLimitsUnit.Hour), (own.QueryLimit, own.GrabLimit!.Value, own.LimitsUnit));
        Assert.Null(own.VipExpiration);
        Assert.Equal(IndexerCatalog.GenericNewznabKey, own.DefinitionKey);

        var torznab = _indexers.Find(result.Imported[2].Id)!;
        Assert.Equal((IndexerDefinition.Torznab, Core.Downloads.DownloadProtocol.Torrent, "https://torznab.example", "/api", string.Empty, false),
            (torznab.Implementation, torznab.Protocol, torznab.BaseUrl, torznab.ApiPath, torznab.ApiKey, torznab.Enable));
        Assert.Equal([2000, 2040], torznab.Categories);
        Assert.Equal([5000, 5040], torznab.TvCategories);
        Assert.Equal((3, 1.5, 4320, 10080), (torznab.MinimumSeeders, torznab.SeedRatio!.Value, torznab.SeedTimeMinutes!.Value, torznab.SeasonPackSeedTimeMinutes!.Value));
        Assert.Equal(IndexerCatalog.GenericTorznabKey, torznab.DefinitionKey);

        var scraper = _indexers.Find(result.Imported[3].Id)!;
        Assert.Equal(("http://prowlarr:9696/6", StubProwlarr.ApiKey, true), (scraper.BaseUrl, scraper.ApiKey, scraper.Enable));
        Assert.Equal([2000], scraper.Categories);
        Assert.Equal(IndexerService.DefaultTvCategories, scraper.TvCategories);
    }

    [Fact]
    public async Task A_preset_without_address_in_prowlarr_gets_the_catalog_address()
    {
        _prowlarr.Json = """
            [
              {
                "id": 7, "name": "House-of-Usenet", "implementation": "Newznab", "protocol": "usenet", "enable": true, "priority": 25,
                "fields": [ { "name": "baseUrl", "value": null }, { "name": "apiPath", "value": null }, { "name": "apiKey", "value": "********" } ],
                "capabilities": { "categories": [ { "id": 2000, "name": "Movies", "subCategories": [] } ] }
              }
            ]
            """;

        var result = await _import.ImportAsync(Url, StubProwlarr.ApiKey, [7], CancellationToken.None, new Dictionary<int, string> { [7] = "hou-key" });

        var imported = Assert.Single(result.Imported);
        Assert.Equal(ProwlarrImportMode.Direct, imported.Mode);
        var hou = _indexers.Find(imported.Id)!;
        Assert.Equal(("https://house-of-usenet.com", "/api/v1/api", "hou-key", true, "house-of-usenet"), (hou.BaseUrl, hou.ApiPath, hou.ApiKey, hou.Enable, hou.DefinitionKey));
        Assert.DoesNotContain("prowlarr", hou.BaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connection_problems_become_field_errors()
    {
        var wrongKey = await Assert.ThrowsAsync<ValidationFailedException>(() => _import.PreviewAsync(Url, "wrong", CancellationToken.None));
        Assert.Equal(["apiKey"], wrongKey.Errors.Keys);

        var invalid = await Assert.ThrowsAsync<ValidationFailedException>(() => _import.PreviewAsync("prowlarr", "", CancellationToken.None));
        Assert.Equal(["apiKey", "url"], invalid.Errors.Keys.Order(StringComparer.Ordinal));

        var notFound = await Assert.ThrowsAsync<ValidationFailedException>(() => _import.PreviewAsync(Url + "base", StubProwlarr.ApiKey, CancellationToken.None));
        Assert.Contains("No Prowlarr API", notFound.Errors["url"][0], StringComparison.Ordinal);

        _prowlarr.Status = HttpStatusCode.InternalServerError;
        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => _import.PreviewAsync(Url, StubProwlarr.ApiKey, CancellationToken.None));
        Assert.Contains("HTTP 500", error.Errors["url"][0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_login_page_in_front_of_prowlarr_is_explained()
    {
        var login = new ProwlarrImportService(new HttpClient(new LoginPageHandler()), _indexers, NullLogger<ProwlarrImportService>.Instance);

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => login.PreviewAsync("https://prowlarr.example.ch", StubProwlarr.ApiKey, CancellationToken.None));

        var message = error.Errors["url"][0];
        Assert.Contains("login", message, StringComparison.Ordinal);
        Assert.Contains("(app.example.ch)", message, StringComparison.Ordinal);
        Assert.Contains("local address", message, StringComparison.Ordinal);
    }

    /// <summary>Behaves like HttpClient after following a reverse proxy's redirect to its login page.</summary>
    private sealed class LoginPageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://app.example.ch/?next=https%3A%2F%2Fprowlarr.example.ch%2Fapi%2Fv1%2Findexer"),
                Content = new StringContent("<!doctype html><title>Sign in</title>", System.Text.Encoding.UTF8, "text/html"),
            });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(file);
        }
    }
}
