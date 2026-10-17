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
using StreamZH.Host.Api;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Host;

public sealed class SearchApiTests : IDisposable
{
    private readonly StreamZhFactory _base = new(new FakeTmdbClient());
    private readonly FakeIndexerProvider _indexers = new();
    private readonly FakeIndexerClient _client = new();
    private readonly WebApplicationFactory<Program> _factory;

    public SearchApiTests()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IIndexerProvider>(_indexers);
            services.AddSingleton<IIndexerClient>(_client);
        }));
        _indexers.Indexers.Add(FakeIndexerProvider.Definition(1, "Alpha"));
        _indexers.Indexers.Add(FakeIndexerProvider.Definition(2, "Beta"));
    }

    [Fact]
    public async Task Search_all_indexers_grab_and_history_without_media_item()
    {
        var client = await OwnerClientAsync();
        _client.Returns(1, Release("Dune.Part.Two.2024.German.DL.2160p.WEB.H265-GRP", 1) with { Categories = [2045] });
        _client.Handlers[2] = (_, _) => throw new IndexerException("Wrong API key", 100);

        var search = (await client.GetFromJsonAsync<FreeSearchResource>("/api/v1/search?query=dune&type=movie&categories=2000,2045&indexerIds=1,2"))!;

        Assert.Equal(2, search.Indexers);
        var failed = Assert.Single(search.FailedIndexers);
        Assert.Equal((2, "Beta", "Wrong API key"), (failed.IndexerId, failed.Indexer, failed.Message));
        var release = Assert.Single(search.Releases);
        Assert.Equal((1, "Alpha", "WEBDL-2160p"), (release.IndexerId, release.Indexer, release.Quality));
        Assert.Equal([2045], release.Categories);
        Assert.Contains("German", release.Languages);
        Assert.InRange(release.AgeHours, 48, 49);
        Assert.Equal(10, release.Grabs);

        var criteria = Assert.IsType<TextSearchCriteria>(_client.Searches.First(s => s.IndexerId == 1).Criteria);
        Assert.Equal(("dune", TextSearchType.Movie), (criteria.Query, criteria.Type));
        Assert.Equal([2000, 2045], criteria.Categories);

        var raw = await client.GetStringAsync("/api/v1/search?query=dune");
        Assert.Contains("\"ageHours\":", raw, StringComparison.Ordinal);
        Assert.Contains("\"failedIndexers\":", raw, StringComparison.Ordinal);

        var grab = await client.PostAsJsonAsync("/api/v1/search/grab", new { indexerId = 1, guid = release.Guid });
        Assert.Equal(HttpStatusCode.OK, grab.StatusCode);
        var grabbed = (await grab.Content.ReadFromJsonAsync<GrabResource>())!;
        // The built-in engine got it (category Other: no library item); its queue keeps the NZB as "<id>.nzb".
        Assert.Equal(("1.nzb", "Alpha"), (grabbed.FileName, grabbed.Indexer));
        var queued = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/v1/download")).GetProperty("items").EnumerateArray());
        Assert.Equal(("other", JsonValueKind.Null), (queued.GetProperty("category").GetString(), queued.GetProperty("mediaType").ValueKind));
        Assert.True(File.Exists(Path.Combine(grabbed.Folder, grabbed.FileName)));

        var history = await client.GetFromJsonAsync<JsonElement>("/api/v1/history");
        var entry = Assert.Single(history.GetProperty("records").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("mediaType").ValueKind);
        Assert.Equal("Grabbed", entry.GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task Bad_parameters_and_unknown_releases_are_readable_errors()
    {
        var client = await OwnerClientAsync();

        var badType = await client.GetAsync("/api/v1/search?query=x&type=music");
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        Assert.True((await badType.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("type", out _));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/search?categories=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/search?indexerIds=1,-2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/search?query={new string('x', 201)}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/search/grab", new { indexerId = 1, guid = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/search/grab", new { indexerId = 0, guid = "" })).StatusCode);

        _client.Returns(1, Release("Some.Release.2020.1080p.WEB-GRP", 1));
        var search = (await client.GetFromJsonAsync<FreeSearchResource>("/api/v1/search?query=some&indexerIds=1"))!;
        _client.Nzb = (_, _) => throw new IndexerException("Download limit reached", 501);
        var failed = await client.PostAsJsonAsync("/api/v1/search/grab", new { indexerId = 1, guid = search.Releases[0].Guid });
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
    }

    [Fact]
    public async Task Search_needs_a_signed_in_admin()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/search?query=x")).StatusCode);
    }

    private static ReleaseInfo Release(string title, int indexerId) => new()
    {
        Guid = $"guid-{title}",
        Title = title,
        DownloadUrl = $"https://indexer{indexerId}.example/get/{title}",
        InfoUrl = $"https://indexer{indexerId}.example/details/{title}",
        Size = 4L * 1024 * 1024 * 1024,
        PublishDate = DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(-5),
        Grabs = 10,
    };

    private async Task<HttpClient> OwnerClientAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _base.Dispose();
    }
}
