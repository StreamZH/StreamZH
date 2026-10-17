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
    /// 1 NZBgeek (Newznab, key masked as Prowlarr always does) → proxy; 2 "Own Newznab" (key readable) → direct;
    /// 3 "Keyless" (Newznab without key, Prowlarr sends "") → direct; 4 Torznab (torrent); 5 Cardigann torrent; 6 NZBIndex (usenet scraper, no key field) → proxy.
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
            "fields": [ { "name": "baseUrl", "value": "https://torznab.example" }, { "name": "apiPath", "value": "/api" }, { "name": "apiKey", "value": "********" } ],
            "enable": true, "protocol": "torrent", "capabilities": { "categories": [ { "id": 2000, "name": "Movies", "subCategories": [] } ] }, "priority": 25
          },
          {
            "id": 5, "name": "1337x", "implementation": "Cardigann", "definitionName": "1337x", "tags": [],
            "fields": [ { "name": "definitionFile", "value": "1337x" }, { "name": "baseUrl", "value": null } ],
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

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (!request.Headers.TryGetValues("X-Api-Key", out var keys) || keys.Single() != ApiKey)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        return Task.FromResult(request.RequestUri!.AbsolutePath == "/api/v1/indexer"
            ? new HttpResponseMessage(Status) { Content = new StringContent(IndexerJson, Encoding.UTF8, "application/json") }
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
        Assert.Equal(("NZBgeek", "usenet", "Newznab", true, false, ProwlarrImportMode.Proxy), (geek.Name, geek.Protocol, geek.Implementation, geek.Supported, geek.AlreadyExists, geek.Mode));
        Assert.Equal(("http://prowlarr:9696/1", "/api"), (geek.BaseUrl, geek.ApiPath));

        var own = preview[1];
        Assert.Equal((ProwlarrImportMode.Direct, "https://own.example", "/newznab/api"), (own.Mode, own.BaseUrl, own.ApiPath));

        Assert.Equal((ProwlarrImportMode.Direct, true), (preview[2].Mode, preview[2].AlreadyExists));

        Assert.All(preview.Skip(3).Take(2), p =>
        {
            Assert.Equal("torrent", p.Protocol);
            Assert.False(p.Supported);
            Assert.Equal("Torrent indexers follow in StreamZH 1.0", p.Reason);
        });

        Assert.Equal((true, ProwlarrImportMode.Proxy, "http://prowlarr:9696/6"), (preview[5].Supported, preview[5].Mode, preview[5].BaseUrl));
    }

    [Fact]
    public async Task Own_api_key_imports_the_indexer_directly_without_prowlarr()
    {
        var preview = await _import.PreviewAsync(Url, StubProwlarr.ApiKey, CancellationToken.None);
        Assert.NotNull(preview[0].OwnUrl);
        Assert.DoesNotContain("prowlarr", preview[0].OwnUrl!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(preview[3].OwnUrl);

        var result = await _import.ImportAsync(Url, StubProwlarr.ApiKey, [1], CancellationToken.None, new Dictionary<int, string> { [1] = " my-geek-key " });

        var imported = Assert.Single(result.Imported);
        Assert.Equal(ProwlarrImportMode.Direct, imported.Mode);
        var geek = _indexers.Find(imported.Id)!;
        Assert.Equal("my-geek-key", geek.ApiKey);
        Assert.Equal(preview[0].OwnUrl, geek.BaseUrl + geek.ApiPath);
    }

    [Fact]
    public async Task Import_creates_direct_and_proxy_indexers_and_skips_the_rest()
    {
        _indexers.Create(new IndexerDefinition(0, "Keyless", "Newznab", "https://x.example", "/api", "k", [2000], [], 25, true, true, true));

        var result = await _import.ImportAsync(Url, StubProwlarr.ApiKey, [1, 2, 3, 4, 6, 99], CancellationToken.None);

        Assert.Equal([("NZBgeek", ProwlarrImportMode.Proxy), ("Own Newznab", ProwlarrImportMode.Direct), ("NZBIndex", ProwlarrImportMode.Proxy)],
            result.Imported.Select(i => (i.Name, i.Mode)));
        Assert.Equal(
            [(99, "Not found in Prowlarr."), (3, "An indexer with this name already exists."), (4, "Torrent indexers follow in StreamZH 1.0")],
            result.Skipped.Select(s => (s.ProwlarrId, s.Reason)));

        var geek = _indexers.Find(result.Imported[0].Id)!;
        Assert.Equal(("http://prowlarr:9696/1", "/api", StubProwlarr.ApiKey), (geek.BaseUrl, geek.ApiPath, geek.ApiKey));
        Assert.Equal([2000, 2040, 2045], geek.Categories);
        Assert.Equal([5000, 5040], geek.TvCategories);
        Assert.Equal((10, true, "nzbgeek"), (geek.Priority, geek.Enable, geek.DefinitionKey));
        Assert.Equal((1000, 100, IndexerLimitsUnit.Day, "2027-03-01"), (geek.QueryLimit!.Value, geek.GrabLimit!.Value, geek.LimitsUnit, geek.VipExpiration));
        Assert.Equal(string.Empty, geek.AdditionalParameters);

        var own = _indexers.Find(result.Imported[1].Id)!;
        Assert.Equal(("https://own.example", "/newznab/api", "own-secret"), (own.BaseUrl, own.ApiPath, own.ApiKey));
        Assert.Equal(IndexerService.DefaultMovieCategories, own.Categories);
        Assert.Equal([5000, 5030], own.TvCategories);
        Assert.Equal((50, false, false, true), (own.Priority, own.Enable, own.EnableRss, own.EnableAutomaticSearch));
        Assert.Equal((null, 5, IndexerLimitsUnit.Hour), (own.QueryLimit, own.GrabLimit!.Value, own.LimitsUnit));
        Assert.Null(own.VipExpiration);
        Assert.Equal(IndexerCatalog.GenericNewznabKey, own.DefinitionKey);

        var scraper = _indexers.Find(result.Imported[2].Id)!;
        Assert.Equal([2000], scraper.Categories);
        Assert.Equal(IndexerService.DefaultTvCategories, scraper.TvCategories);
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
