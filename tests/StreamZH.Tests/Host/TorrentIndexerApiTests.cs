// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Decisions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;
using StreamZH.Library.Indexers;
using StreamZH.Library.Search;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Host;

/// <summary>0.11 API: Torznab indexers with seed settings, protocol preference and delays, torrent fields of releases, pending releases, roles.</summary>
public sealed class TorrentIndexerApiTests : IDisposable
{
    private readonly StreamZhFactory _base = new(new FakeTmdbClient());
    private readonly FakeIndexerProvider _indexers = new();
    private readonly FakeIndexerClient _client = new();
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-media-").FullName;
    private WebApplicationFactory<Program> _factory;

    public TorrentIndexerApiTests() => _factory = _base;

    private static object NewTorznab(string name, object? seed = null) => new
    {
        name,
        implementation = "Torznab",
        baseUrl = "https://jackett.example:9117/api/v2.0/indexers/exampletracker/results/torznab/",
        apiPath = "/api",
        apiKey = "tracker-secret",
        categories = new[] { 2000, 2040 },
        tvCategories = new[] { 5040 },
        priority = 25,
        definitionKey = "generic-torznab",
        minimumSeeders = 3,
        seedRatio = 1.5,
        seedTimeMinutes = 4320,
        seasonPackSeedTimeMinutes = (int?)null,
    };

    [Fact]
    public async Task Torznab_indexers_take_and_return_their_seed_settings_with_a_masked_key()
    {
        var client = await OwnerAsync();

        var schema = await client.GetFromJsonAsync<JsonElement>("/api/v1/indexer/schema?implementation=Torznab");
        Assert.Equal(("Torznab", "torrent", 1), (schema.GetProperty("implementation").GetString(), schema.GetProperty("protocol").GetString(), schema.GetProperty("minimumSeeders").GetInt32()));

        var create = await client.PostAsJsonAsync("/api/v1/indexer", NewTorznab("Tracker"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await create.Content.ReadAsStringAsync();
        Assert.DoesNotContain("tracker-secret", body, StringComparison.Ordinal);
        var created = JsonDocument.Parse(body).RootElement;
        Assert.Equal("torrent", created.GetProperty("protocol").GetString());
        Assert.Equal((3, 1.5, 4320), (created.GetProperty("minimumSeeders").GetInt32(), created.GetProperty("seedRatio").GetDouble(), created.GetProperty("seedTimeMinutes").GetInt32()));
        Assert.Equal(JsonValueKind.Null, created.GetProperty("seasonPackSeedTimeMinutes").ValueKind);
        Assert.Equal(IndexerService.MaskedApiKey, created.GetProperty("apiKey").GetString());

        // A client that sends the resource back (masked key, protocol ignored) keeps the key; the settings change.
        var id = created.GetProperty("id").GetInt32();
        var resource = JsonSerializer.Deserialize<IndexerResource>(body, JsonSerializerOptions.Web)!;
        var update = await client.PutAsJsonAsync($"/api/v1/indexer/{id}", resource with { MinimumSeeders = 10, SeedRatio = null, Protocol = DownloadProtocol.Usenet });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var stored = _factory.Services.GetRequiredService<IndexerService>().Find(id)!;
        Assert.Equal(("tracker-secret", 10, (double?)null, DownloadProtocol.Torrent), (stored.ApiKey, stored.MinimumSeeders, stored.SeedRatio, stored.Protocol));

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/indexer");
        Assert.Equal("torrent", list[0].GetProperty("protocol").GetString());

        var catalog = await client.GetFromJsonAsync<JsonElement>("/api/v1/indexer/definitions");
        Assert.Contains(catalog.EnumerateArray(), e => e.GetProperty("key").GetString() == "generic-torznab" && e.GetProperty("protocol").GetString() == "torrent");
    }

    [Fact]
    public async Task Invalid_seed_settings_are_field_errors()
    {
        var client = await OwnerAsync();

        var response = await client.PostAsJsonAsync("/api/v1/indexer", new
        {
            name = "Tracker", implementation = "Torznab", baseUrl = "https://tracker.example.org", apiPath = "/api", apiKey = "k",
            categories = new[] { 2000 }, minimumSeeders = -1, seedRatio = -1, seedTimeMinutes = 0,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(["minimumSeeders", "seedRatio", "seedTimeMinutes"], errors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        var unknown = await client.PostAsJsonAsync("/api/v1/indexer", new { name = "X", implementation = "Cardigann", baseUrl = "https://x.example", categories = new[] { 2000 } });
        Assert.True((await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("implementation", out _));
    }

    [Fact]
    public async Task Protocol_preference_and_delays_are_saved_validated_and_kept_when_left_out()
    {
        var client = await OwnerAsync();
        const string Url = "/api/v1/settings/indexers";

        var defaults = await client.GetFromJsonAsync<JsonElement>(Url);
        Assert.Equal(("usenet", 0, 0, true),
            (defaults.GetProperty("preferredProtocol").GetString(), defaults.GetProperty("usenetDelay").GetInt32(), defaults.GetProperty("torrentDelay").GetInt32(),
                defaults.GetProperty("bypassDelayIfHighestQuality").GetBoolean()));

        var invalid = await client.PutAsJsonAsync(Url, Options(new { preferredProtocol = "torrent", usenetDelay = -1, torrentDelay = 20_000 }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(["torrentDelay", "usenetDelay"], errors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Use 0 to 10080 minutes.", errors.GetProperty("torrentDelay")[0].GetString());

        // An unknown value cannot be read at all (like any malformed body): refused, nothing stored.
        var bad = await client.PutAsJsonAsync(Url, Options(new { preferredProtocol = "carrier-pigeon" }));
        Assert.False(bad.IsSuccessStatusCode);
        Assert.Equal("usenet", (await client.GetFromJsonAsync<JsonElement>(Url)).GetProperty("preferredProtocol").GetString());

        var saved = await client.PutAsJsonAsync(Url, Options(new { preferredProtocol = "torrent", usenetDelay = 30, torrentDelay = 0, bypassDelayIfHighestQuality = false }));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var rules = _factory.Services.GetRequiredService<IndexerOptions>().Rules();
        Assert.Equal((ProtocolPreference.Torrent, 30, 0, false), (rules.PreferredProtocol, rules.UsenetDelayMinutes, rules.TorrentDelayMinutes, rules.BypassDelayIfHighestQuality));

        // An older client without the 0.11 fields does not reset them.
        var older = await client.PutAsJsonAsync(Url, Options(null));
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);
        var body = await older.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("torrent", 30), (body.GetProperty("preferredProtocol").GetString(), body.GetProperty("usenetDelay").GetInt32()));

        var noPreference = await client.PutAsJsonAsync(Url, Options(new { preferredProtocol = "noPreference" }));
        Assert.Equal("noPreference", (await noPreference.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("preferredProtocol").GetString());
    }

    [Fact]
    public async Task Manual_search_rows_show_protocol_seeders_peers_and_flags()
    {
        UseFakeIndexers();
        var client = await OwnerAsync();
        var movie = await AddMovieAsync(client);
        _client.Returns(1,
            new ReleaseInfo
            {
                Guid = "t1", Title = "Dune.2021.German.DL.1080p.BluRay.x264-GRP", DownloadUrl = "magnet:?xt=urn:btih:9c1e8039dd32f16e7d82f60e20b7e522ef490757",
                Size = 8L * 1024 * 1024 * 1024, PublishDate = DateTimeOffset.UtcNow.AddDays(-2), Protocol = DownloadProtocol.Torrent, Seeders = 0, Peers = 4,
                IndexerFlags = IndexerFlagBits.Freeleech | IndexerFlagBits.Internal, DownloadVolumeFactor = 0, UploadVolumeFactor = 1, MinimumRatio = 1, MinimumSeedTimeSeconds = 3600,
            },
            new ReleaseInfo
            {
                Guid = "n1", Title = "Dune.2021.German.DL.1080p.WEB.H264-GRP", DownloadUrl = "https://alpha.example/get/n1",
                Size = 4L * 1024 * 1024 * 1024, PublishDate = DateTimeOffset.UtcNow.AddDays(-2),
            });

        var search = await client.GetFromJsonAsync<JsonElement>($"/api/v1/release?movieId={movie.GetProperty("id").GetInt32()}");
        var rows = search.GetProperty("releases").EnumerateArray().ToDictionary(r => r.GetProperty("guid").GetString()!);

        var torrent = rows["t1"];
        Assert.Equal(("torrent", 0, 4), (torrent.GetProperty("protocol").GetString(), torrent.GetProperty("seeders").GetInt32(), torrent.GetProperty("peers").GetInt32()));
        Assert.Equal(["freeleech", "internal"], torrent.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal((33, 0d, 1d, 3600L), (torrent.GetProperty("indexerFlags").GetInt32(), torrent.GetProperty("downloadVolumeFactor").GetDouble(),
            torrent.GetProperty("minimumRatio").GetDouble(), torrent.GetProperty("minimumSeedTime").GetInt64()));
        Assert.False(torrent.GetProperty("accepted").GetBoolean());
        Assert.Contains(torrent.GetProperty("rejections").EnumerateArray(), r => r.GetProperty("code").GetString() == RejectionCodes.MinimumSeeders);

        var usenet = rows["n1"];
        Assert.Equal(("usenet", JsonValueKind.Null), (usenet.GetProperty("protocol").GetString(), usenet.GetProperty("seeders").ValueKind));
        Assert.Empty(usenet.GetProperty("flags").EnumerateArray());
    }

    [Fact]
    public async Task Pending_releases_are_listed_for_members_and_removed_by_admins()
    {
        var owner = await OwnerAsync();
        var store = _factory.Services.GetRequiredService<PendingReleaseService>();
        var options = _factory.Services.GetRequiredService<IndexerOptions>();
        options.Save(options.Values with { TorrentDelay = 90 });
        var published = DateTimeOffset.UtcNow.AddMinutes(-10);
        var release = new ReleaseInfo
        {
            Guid = "t1", Title = "Dune.2021.German.DL.1080p.BluRay.x264-GRP", DownloadUrl = "magnet:?xt=urn:btih:abc", IndexerId = 3, Indexer = "Tracker",
            PublishDate = published, Protocol = DownloadProtocol.Torrent, Seeders = 12, IndexerFlags = IndexerFlagBits.Freeleech,
        };
        var candidate = new ReleaseCandidate(new Core.Quality.ParsedRelease { ReleaseTitle = release.Title, Title = "Dune", CleanTitle = "dune" }, 0, "Tracker", 25, published, release.DownloadUrl, "t1");
        store.Add([new ReleaseDecision(release, null, new Decision(candidate, [new Rejection(RejectionCodes.MinimumAgeDelay, "Waiting", RejectionKind.Temporary)]),
            new SearchSubject(MediaType.Movie, 7, null, "Dune"), [])]);

        var friend = await UserAsync("friend", UserRole.Friend);
        var list = await friend.GetFromJsonAsync<JsonElement>("/api/v1/release/pending");
        var entry = Assert.Single(list.EnumerateArray());
        Assert.Equal(("torrent", 7, "Waiting", 12), (entry.GetProperty("protocol").GetString(), entry.GetProperty("movieId").GetInt32(), entry.GetProperty("reason").GetString(), entry.GetProperty("seeders").GetInt32()));
        Assert.Equal(published.AddMinutes(90), entry.GetProperty("releaseAfter").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
        Assert.Equal(["freeleech"], entry.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));

        var id = entry.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.DeleteAsync($"/api/v1/release/pending/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/release/pending/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/v1/release/pending/{id}")).StatusCode);
    }

    [Fact]
    public async Task Requesters_cannot_read_or_change_indexers_settings_or_pending_releases()
    {
        await OwnerAsync();
        var requester = await UserAsync("requester", UserRole.Requester);

        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/indexer")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.PostAsJsonAsync("/api/v1/indexer", NewTorznab("X"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/settings/indexers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/release/pending")).StatusCode);

        var friend = await UserAsync("friend", UserRole.Friend);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/indexer")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync("/api/v1/indexer", NewTorznab("X"))).StatusCode);
    }

    private void UseFakeIndexers()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IIndexerProvider>(_indexers);
            services.AddSingleton<IIndexerClient>(_client);
        }));
        _indexers.Indexers.Add(FakeIndexerProvider.Definition(1, "Alpha") with { Implementation = IndexerDefinition.Torznab, MinimumSeeders = 1 });
    }

    private static object Options(object? extra)
    {
        var values = new Dictionary<string, object?>
        {
            ["minimumAge"] = 0, ["retention"] = 0, ["maximumSize"] = 0, ["allowHardcodedSubs"] = false, ["whitelistedHardcodedSubs"] = Array.Empty<string>(),
            ["minimumAvailability"] = "inCinemas", ["availabilityDelay"] = 0, ["rssSyncInterval"] = 60, ["missingSearchInterval"] = 0, ["searchOnAdd"] = false,
        };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                values[property.Name] = property.GetValue(extra);
            }
        }

        return values;
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

    private async Task<JsonElement> AddMovieAsync(HttpClient client)
    {
        var path = Directory.CreateDirectory(Path.Combine(_media, "movies")).FullName;
        var root = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path, mediaType = "Movie" });
        Assert.Equal(HttpStatusCode.Created, root.StatusCode);
        var rootId = (await root.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var response = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = rootId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public void Dispose()
    {
        _factory.Dispose();
        _base.Dispose();
        try
        {
            Directory.Delete(_media, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
