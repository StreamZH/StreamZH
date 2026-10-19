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
using StreamZH.Core.Media;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Requests;

/// <summary>Discover and requests over HTTP with an owner, a friend and a requester (who reach only their own data).</summary>
public sealed class RequestApiTests : IAsyncLifetime
{
    private static readonly int Dune = FakeTmdbClient.Dune.TmdbId;
    private static readonly int Arrival = FakeTmdbClient.Arrival.TmdbId;
    private static readonly int Show = FakeTmdbClient.Show.TmdbId;

    private readonly StreamZhFactory _root = new(new FakeTmdbClient());
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _owner = null!;
    private HttpClient _friend = null!;
    private HttpClient _requester = null!;
    private HttpClient _other = null!;

    public async Task InitializeAsync()
    {
        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ITmdbDiscoverClient>(new FakeDiscoverTmdb()))));
        _owner = _factory.CreateClient();
        (await _owner.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
        var folders = _factory.Services.GetRequiredService<RootFolderRepository>();
        folders.Add(Path.Combine(_root.DataDirectory, "movies"), MediaType.Movie);
        folders.Add(Path.Combine(_root.DataDirectory, "tv"), MediaType.Series);
        _friend = await LoginAsync("jane", UserRole.Friend);
        _requester = await LoginAsync("bob", UserRole.Requester);
        _other = await LoginAsync("carol", UserRole.Requester);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        _root.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_requester_discovers_requests_and_sees_only_their_own_requests()
    {
        var trending = await _requester.GetFromJsonAsync<JsonElement>("/api/v1/discover/movies/trending?page=1");
        Assert.Equal(3, trending.GetProperty("totalPages").GetInt32());
        var dune = trending.GetProperty("results")[0];
        Assert.Equal("movie", dune.GetProperty("mediaType").GetString());
        Assert.Equal("/api/v1/mediacover/w342/dune.jpg", dune.GetProperty("posterUrl").GetString());
        Assert.Equal("/api/v1/mediacover/w1280/dune-backdrop.jpg", dune.GetProperty("backdropUrl").GetString());
        Assert.False(dune.GetProperty("status").GetProperty("inLibrary").GetBoolean());
        Assert.Equal(JsonValueKind.Null, dune.GetProperty("status").GetProperty("requested").ValueKind);

        // Posters come through StreamZH for requesters too (a redirect while the image cache is off).
        Assert.NotEqual(HttpStatusCode.Forbidden, (await _requester.GetAsync("/api/v1/mediacover/w342/dune.jpg")).StatusCode);

        var created = await _requester.PostAsJsonAsync("/api/v1/requests", new { mediaType = "movie", tmdbId = Dune, note = "Please" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", request.GetProperty("status").GetString());
        Assert.Equal("web", request.GetProperty("source").GetString());
        Assert.Equal("bob", request.GetProperty("requestedByName").GetString());
        Assert.Equal("/api/v1/mediacover/w342/dune.jpg", request.GetProperty("posterUrl").GetString());
        var id = request.GetProperty("id").GetInt32();

        var after = await _requester.GetFromJsonAsync<JsonElement>("/api/v1/discover/search?query=dune");
        Assert.Equal("pending", after.GetProperty("results")[0].GetProperty("status").GetProperty("requested").GetString());
        Assert.Equal(id, after.GetProperty("results")[0].GetProperty("status").GetProperty("requestId").GetInt32());

        var details = await _requester.GetFromJsonAsync<JsonElement>($"/api/v1/discover/movie/{Dune}");
        Assert.Equal("PG-13", details.GetProperty("certification").GetString());
        Assert.Equal(id, Assert.Single(details.GetProperty("requests").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Empty((await _other.GetFromJsonAsync<JsonElement>($"/api/v1/discover/movie/{Dune}")).GetProperty("requests").EnumerateArray());

        // Own list, even when asking for everything; somebody else's request does not exist for them.
        var mine = await _requester.GetFromJsonAsync<JsonElement>("/api/v1/requests?mine=false");
        Assert.Equal(1, mine.GetProperty("totalRecords").GetInt32());
        Assert.Equal(0, (await _other.GetFromJsonAsync<JsonElement>("/api/v1/requests")).GetProperty("totalRecords").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await _other.GetAsync($"/api/v1/requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _other.DeleteAsync($"/api/v1/requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _requester.GetAsync($"/api/v1/requests/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _requester.DeleteAsync($"/api/v1/requests/{id}")).StatusCode);
    }

    [Fact]
    public async Task Requesters_and_friends_cannot_reach_the_admin_endpoints()
    {
        var id = await CreateAsync(_requester, new { mediaType = "movie", tmdbId = Dune });
        foreach (var client in new[] { _requester, _friend })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/requests/{id}/approve", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/requests/{id}/decline", new { reason = "no" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/requests/{id}/retry", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/requests/count")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/settings/requests")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/settings/requests", new { enabled = false })).StatusCode);
        }

        // The friend's own view: not the requester's request.
        Assert.Equal(HttpStatusCode.NotFound, (await _friend.GetAsync($"/api/v1/requests/{id}")).StatusCode);
        Assert.Equal(0, (await _friend.GetFromJsonAsync<JsonElement>("/api/v1/requests")).GetProperty("totalRecords").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/discover/movies/popular")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/requests")).StatusCode);
    }

    [Fact]
    public async Task The_owner_sees_everything_approves_declines_and_counts()
    {
        var movie = await CreateAsync(_requester, new { mediaType = "movie", tmdbId = Dune });
        var series = await CreateAsync(_other, new { mediaType = "series", tmdbId = Show, seasons = new[] { 1 } });
        var friend = await _friend.PostAsJsonAsync("/api/v1/requests", new { mediaType = "movie", tmdbId = Arrival });
        Assert.Equal("processing", (await friend.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var count = await _owner.GetFromJsonAsync<JsonElement>("/api/v1/requests/count");
        Assert.Equal((2, 1), (count.GetProperty("pending").GetInt32(), count.GetProperty("processing").GetInt32()));
        Assert.Equal(3, (await _owner.GetFromJsonAsync<JsonElement>("/api/v1/requests")).GetProperty("totalRecords").GetInt32());
        Assert.Equal(2, (await _owner.GetFromJsonAsync<JsonElement>("/api/v1/requests?status=pending")).GetProperty("totalRecords").GetInt32());
        Assert.Equal(1, (await _owner.GetFromJsonAsync<JsonElement>("/api/v1/requests?mediaType=series")).GetProperty("totalRecords").GetInt32());
        Assert.Equal(0, (await _owner.GetFromJsonAsync<JsonElement>("/api/v1/requests?mine=true")).GetProperty("totalRecords").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.GetAsync("/api/v1/requests?status=done")).StatusCode);

        var approved = await _owner.PostAsync($"/api/v1/requests/{movie}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var body = await approved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("processing", body.GetProperty("status").GetString());
        Assert.Equal(StreamZhFactory.OwnerName, body.GetProperty("decidedByName").GetString());
        Assert.Equal("bob", body.GetProperty("requestedByName").GetString());

        // The requester sees their own name but not the admin's.
        var own = await _requester.GetFromJsonAsync<JsonElement>($"/api/v1/requests/{movie}");
        Assert.Equal(("bob", JsonValueKind.Null), (own.GetProperty("requestedByName").GetString(), own.GetProperty("decidedByName").ValueKind));
        Assert.Equal(1, (await _owner.GetFromJsonAsync<JsonElement>($"/api/v1/requests?tmdbId={Dune}&mediaType=movie")).GetProperty("totalRecords").GetInt32());
        Assert.Equal(1, (await _requester.GetFromJsonAsync<JsonElement>($"/api/v1/requests?tmdbId={Dune}")).GetProperty("totalRecords").GetInt32());
        Assert.Equal(0, (await _other.GetFromJsonAsync<JsonElement>($"/api/v1/requests?tmdbId={Dune}")).GetProperty("totalRecords").GetInt32());
        Assert.NotNull(_factory.Services.GetRequiredService<MovieRepository>().FindByTmdbId(Dune));

        var declined = await _owner.PostAsJsonAsync($"/api/v1/requests/{series}/decline", new { reason = "Not this one" });
        Assert.Equal("Not this one", (await declined.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.PostAsync($"/api/v1/requests/{series}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.PostAsync("/api/v1/requests/9999/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Duplicates_return_the_existing_request_and_limits_and_bad_input_have_field_errors()
    {
        var first = await CreateAsync(_requester, new { mediaType = "series", tmdbId = Show, seasons = "all" });
        var duplicate = await _other.PostAsJsonAsync("/api/v1/requests", new { mediaType = "series", tmdbId = Show, seasons = new[] { 2 } });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("already-requested", problem.GetProperty("code").GetString());
        Assert.Equal(first, problem.GetProperty("request").GetProperty("id").GetInt32());

        // Somebody else's request: title and state only, no user.
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("request").GetProperty("requestedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("request").GetProperty("requestedByName").ValueKind);

        var settings = await _owner.GetFromJsonAsync<JsonElement>("/api/v1/settings/requests");
        Assert.True(settings.GetProperty("autoApproveFriends").GetBoolean());
        Assert.Equal(7, settings.GetProperty("limitDays").GetInt32());
        var bad = await _owner.PutAsJsonAsync("/api/v1/settings/requests", new { limitDays = 0, seriesMonitor = "some", defaultMovieRootFolderId = 999 });
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("limitDays", out _));
        Assert.True(errors.TryGetProperty("seriesMonitor", out _));
        Assert.True(errors.TryGetProperty("defaultMovieRootFolderId", out _));
        var saved = await _owner.PutAsJsonAsync("/api/v1/settings/requests", new { defaultMovieLimit = 1, limitDays = 7, seriesMonitor = "future" });
        Assert.Equal("future", (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("seriesMonitor").GetString());

        await CreateAsync(_requester, new { mediaType = "movie", tmdbId = Dune });
        var limited = await _requester.PostAsJsonAsync("/api/v1/requests", new { mediaType = "movie", tmdbId = Arrival });
        Assert.Equal(HttpStatusCode.BadRequest, limited.StatusCode);
        var limit = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("limit-reached", limit.GetProperty("code").GetString());
        Assert.Contains("1 movie every 7 days", limit.GetProperty("errors").GetProperty("limit")[0].GetString(), StringComparison.Ordinal);

        var invalid = await _requester.PostAsJsonAsync("/api/v1/requests", new { mediaType = "book", tmdbId = 1 });
        Assert.True((await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("mediaType", out _));
        var seasons = await _requester.PostAsJsonAsync("/api/v1/requests", new { mediaType = "series", tmdbId = Show, seasons = "some" });
        Assert.True((await seasons.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("seasons", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await _requester.PostAsJsonAsync("/api/v1/requests", new { mediaType = "movie", tmdbId = 999 })).StatusCode);
    }

    [Fact]
    public async Task Discover_details_show_seasons_with_their_state_and_unknown_routes_are_404()
    {
        var id = await CreateAsync(_requester, new { mediaType = "series", tmdbId = Show, seasons = new[] { 2 } });

        var details = await _requester.GetFromJsonAsync<JsonElement>($"/api/v1/discover/series/{Show}");
        var seasons = details.GetProperty("seasons").EnumerateArray().ToList();
        Assert.Equal([1, 2], seasons.Select(s => s.GetProperty("seasonNumber").GetInt32()));
        Assert.Equal(JsonValueKind.Null, seasons[0].GetProperty("status").GetProperty("requested").ValueKind);
        Assert.Equal("pending", seasons[1].GetProperty("status").GetProperty("requested").GetString());
        Assert.Equal(id, seasons[1].GetProperty("status").GetProperty("requestId").GetInt32());
        Assert.Equal(("Breaking Bad", "series", "AMC"), (details.GetProperty("title").GetString(), details.GetProperty("mediaType").GetString(), details.GetProperty("network").GetString()));
        Assert.Equal("pending", details.GetProperty("status").GetProperty("requested").GetString());
        Assert.Equal("/api/v1/mediacover/w342/s2.jpg", seasons[1].GetProperty("posterUrl").GetString());
        Assert.False(details.GetProperty("automationHold").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await _requester.GetAsync("/api/v1/discover/movies/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _requester.GetAsync("/api/v1/discover/books/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _requester.GetAsync("/api/v1/discover/movie/12345")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _requester.GetAsync("/api/v1/discover/search?query=")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _requester.GetAsync("/api/v1/discover/series/topRated")).StatusCode);
    }

    [Fact]
    public async Task Requesters_may_open_the_live_connection()
    {
        Assert.Equal(HttpStatusCode.OK, (await _requester.PostAsync("/hub/negotiate?negotiateVersion=1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync("/hub/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Fact]
    public async Task Request_triggers_are_stored_per_connection_and_only_for_notification_services()
    {
        var schema = await _owner.GetFromJsonAsync<JsonElement>("/api/v1/connection/schema");
        bool Has(string key, string trigger) => schema.EnumerateArray().Single(t => t.GetProperty("key").GetString() == key)
            .GetProperty("triggers").EnumerateArray().Any(t => t.GetString() == trigger);
        Assert.True(Has("discord", "requestCreated"));
        Assert.True(Has("webhook", "requestAvailable"));
        Assert.False(Has("plex", "requestCreated"));
        Assert.False(schema.EnumerateArray().Single(t => t.GetProperty("key").GetString() == "discord").GetProperty("defaults").GetProperty("onRequestCreated").GetBoolean());

        var created = await _owner.PostAsJsonAsync("/api/v1/connection", new
        {
            name = "Requests hook",
            implementation = "webhook",
            onRequestCreated = true,
            onRequestAvailable = true,
            settings = new { url = "http://192.168.1.10:9000/hook" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var resource = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(resource.GetProperty("onRequestCreated").GetBoolean());
        Assert.False(resource.GetProperty("onRequestApproved").GetBoolean());
        Assert.True(resource.GetProperty("onRequestAvailable").GetBoolean());
    }

    private async Task<int> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/requests", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<HttpClient> LoginAsync(string name, UserRole role)
    {
        var passwords = _factory.Services.GetRequiredService<PasswordService>();
        _factory.Services.GetRequiredService<UserRepository>().Create(name, passwords.Hash("a long test password"), role);
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = name, password = "a long test password" })).EnsureSuccessStatusCode();
        return client;
    }
}
