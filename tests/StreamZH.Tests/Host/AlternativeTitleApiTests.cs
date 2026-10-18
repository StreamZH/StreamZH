// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;
using StreamZH.Library;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Host;

public sealed class AlternativeTitleApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-media-").FullName;

    private static readonly TmdbTitles DuneTitles = new(
        [new("Dune: Part One", "US", null), new("Дюна", "RU", null)],
        [new("de", "DE", "Der Wüstenplanet"), new("en", "US", "Dune")]);

    private static readonly TmdbTitles ShowTitles = new([], [new("de", "DE", "Breaking Bad – Die Serie")]);

    [Fact]
    public async Task Titles_are_fetched_on_add_listed_and_yours_can_be_added_and_removed()
    {
        using var app = WithTitles();
        var client = await CreateOwnerAsync(app);
        var movie = await AddMovieAsync(client);

        var titles = (await client.GetFromJsonAsync<AlternativeTitleResource[]>($"/api/v1/movies/{movie.Id}/titles"))!;
        Assert.Equal(
            [("Der Wüstenplanet", "translation", "German"), ("Dune: Part One", "tmdb", "English")],
            titles.Select(t => (t.Title, t.Source, t.Language)).OrderBy(t => t.Title, StringComparer.Ordinal));

        var added = await client.PostAsJsonAsync($"/api/v1/movies/{movie.Id}/titles", new { title = "Wüstenplanet 2021" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var mine = (await added.Content.ReadFromJsonAsync<AlternativeTitleResource>())!;
        Assert.Equal(("Wüstenplanet 2021", "user", (string?)null, (int?)null), (mine.Title, mine.Source, mine.Language, mine.SeasonNumber));

        Assert.Equal(["title"], await ErrorKeysAsync(await client.PostAsJsonAsync($"/api/v1/movies/{movie.Id}/titles", new { title = "der wuestenplanet" })));
        Assert.Equal(["seasonNumber"], await ErrorKeysAsync(await client.PostAsJsonAsync($"/api/v1/movies/{movie.Id}/titles", new { title = "Other", seasonNumber = 1 })));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/movies/999/titles", new { title = "Other" })).StatusCode);

        var tmdbTitle = titles.Single(t => t.Source == "tmdb");
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/v1/movies/{movie.Id}/titles/{tmdbTitle.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/movies/{movie.Id}/titles/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/movies/{movie.Id}/titles/{mine.Id}")).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<AlternativeTitleResource[]>($"/api/v1/movies/{movie.Id}/titles"))!.Length);
    }

    [Fact]
    public async Task Series_titles_can_be_limited_to_a_season_and_the_release_test_names_the_match()
    {
        using var app = WithTitles();
        var client = await CreateOwnerAsync(app);
        var root = await AddRootFolderAsync(client, "tv", "Series");
        var series = (await (await client.PostAsJsonAsync("/api/v1/series", new { tmdbId = 1396, rootFolderId = root.Id })).Content.ReadFromJsonAsync<SeriesResource>())!;

        var added = await client.PostAsJsonAsync($"/api/v1/series/{series.Id}/titles", new { title = "Breaking Bad Zwei", seasonNumber = 2 });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var titles = (await client.GetFromJsonAsync<AlternativeTitleResource[]>($"/api/v1/series/{series.Id}/titles"))!;
        Assert.Equal(["Breaking Bad Zwei", "Breaking Bad – Die Serie"], titles.Select(t => t.Title).Order(StringComparer.Ordinal));
        Assert.Equal(2, titles.Single(t => t.Source == "user").SeasonNumber);

        var german = await client.GetFromJsonAsync<JsonElement>($"/api/v1/parse?title={Uri.EscapeDataString("Breaking.Bad.Die.Serie.S01E01.German.DL.1080p.WEB.h264-GRP")}&seriesId={series.Id}");
        Assert.True(german.GetProperty("decision").GetProperty("rejections").EnumerateArray().All(r => r.GetProperty("code").GetString() != "wrongMedia"));
        var library = german.GetProperty("library");
        Assert.Equal("series", library.GetProperty("mediaType").GetString());
        Assert.Equal("Breaking Bad – Die Serie", library.GetProperty("matchedTitle").GetString());
        Assert.Equal("translation", library.GetProperty("source").GetString());
        Assert.Equal("German", library.GetProperty("language").GetString());

        var wrongSeason = await client.GetFromJsonAsync<JsonElement>($"/api/v1/library/match?title={Uri.EscapeDataString("Breaking.Bad.Zwei.S01E01.German.1080p.WEB.h264-GRP")}");
        Assert.Equal(JsonValueKind.Null, wrongSeason.GetProperty("match").ValueKind);
        var rightSeason = await client.GetFromJsonAsync<JsonElement>($"/api/v1/library/match?title={Uri.EscapeDataString("Breaking.Bad.Zwei.S02E01.German.1080p.WEB.h264-GRP")}");
        Assert.Equal(series.Id, rightSeason.GetProperty("match").GetProperty("id").GetInt32());
        Assert.Equal("user", rightSeason.GetProperty("match").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Friends_can_read_titles_but_not_change_them()
    {
        using var app = WithTitles();
        var owner = await CreateOwnerAsync(app);
        var movie = await AddMovieAsync(owner);
        var passwords = app.Services.GetRequiredService<PasswordService>();
        app.Services.GetRequiredService<UserRepository>().Create("friend", passwords.Hash("friend password 123"), UserRole.Friend);
        var friend = app.CreateClient();
        (await friend.PostAsJsonAsync("/api/v1/auth/login", new { username = "friend", password = "friend password 123" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync($"/api/v1/movies/{movie.Id}/titles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync($"/api/v1/movies/{movie.Id}/titles", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.DeleteAsync($"/api/v1/movies/{movie.Id}/titles/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync($"/api/v1/movies/{movie.Id}/titles")).StatusCode);
    }

    [Fact]
    public async Task The_search_setting_is_part_of_the_indexer_settings()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/indexers");
        Assert.True(settings.GetProperty("searchAlternativeTitles").GetBoolean());

        var changed = JsonSerializer.Deserialize<Dictionary<string, object?>>(settings.GetRawText())!;
        changed["searchAlternativeTitles"] = false;
        var saved = await client.PutAsJsonAsync("/api/v1/settings/indexers", changed);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.False((await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("searchAlternativeTitles").GetBoolean());

        // Clients that do not know the field yet keep the default.
        changed.Remove("searchAlternativeTitles");
        var old = await client.PutAsJsonAsync("/api/v1/settings/indexers", changed);
        Assert.True((await old.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("searchAlternativeTitles").GetBoolean());
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(_media, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithTitles() =>
        _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ITmdbTitlesClient>(new FakeTitles())));

    private static async Task<HttpClient> CreateOwnerAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<MovieResource> AddMovieAsync(HttpClient client)
    {
        var root = await AddRootFolderAsync(client, "movies", "Movie");
        var response = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = root.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MovieResource>())!;
    }

    private async Task<RootFolderInfo> AddRootFolderAsync(HttpClient client, string name, string mediaType)
    {
        var path = Directory.CreateDirectory(Path.Combine(_media, name)).FullName;
        var response = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path, mediaType });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RootFolderInfo>())!;
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
    }

    private sealed class FakeTitles : ITmdbTitlesClient
    {
        public Task<TmdbTitles> GetMovieTitlesAsync(int tmdbId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tmdbId == 438631 ? DuneTitles : TmdbTitles.None);

        public Task<TmdbTitles> GetSeriesTitlesAsync(int tmdbId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tmdbId == 1396 ? ShowTitles : TmdbTitles.None);
    }
}
