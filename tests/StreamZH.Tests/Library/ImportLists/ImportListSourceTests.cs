// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text;
using System.Text.Json;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.ImportLists;
using StreamZH.Library.Notifications;
using StreamZH.Tests.Metadata;

namespace StreamZH.Tests.Library.ImportLists;

/// <summary>Every import list type against a fake of its service.</summary>
public class ImportListSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    // ---- TMDB ----

    [Fact]
    public async Task Tmdb_list_reads_pages_up_to_the_limit_and_keeps_the_media_type()
    {
        var tmdb = new FakeTmdbHttp();
        tmdb.Answer("/list/8245", uri => uri.Query.Contains("page=1", StringComparison.Ordinal)
            ? """{"name":"Mine","page":1,"total_pages":2,"items":[{"media_type":"movie","id":1,"title":"A","release_date":"2020-01-01"},{"media_type":"tv","id":2,"name":"Show"}]}"""
            : """{"name":"Mine","page":2,"total_pages":2,"items":[{"media_type":"movie","id":3,"title":"B"},{"media_type":"movie","id":4,"title":"C"}]}""");

        var fetch = await Fetch(new TmdbListSource(tmdb.Client), ImportListCatalog.TmdbList, MediaType.Movie, new { listId = "https://www.themoviedb.org/list/8245-mine", limit = 2 });

        Assert.Equal("Mine", fetch.Name);
        Assert.Equal([1, 3], fetch.Items.Select(i => i.TmdbId));
        Assert.Equal("2020-01-01", fetch.Items[0].ReleaseDate);
        Assert.All(fetch.Items, i => Assert.Equal(MediaType.Movie, i.MediaType));
    }

    [Fact]
    public async Task Tmdb_collection_person_company_and_keyword()
    {
        var tmdb = new FakeTmdbHttp();
        tmdb.Answer("/collection/10", """{"name":"Star Wars Collection","parts":[{"id":11,"title":"Star Wars"},{"id":1891,"title":"Empire"}]}""");
        tmdb.Answer("/person/525", """
            {"id":525,"name":"Nolan","movie_credits":{"cast":[{"id":5,"title":"Cameo","release_date":"2001-01-01"}],
             "crew":[{"id":6,"title":"Inception","release_date":"2010-07-15","department":"Directing"},{"id":7,"title":"Produced","release_date":"2015-01-01","department":"Production"},
                     {"id":6,"title":"Inception","release_date":"2010-07-15","department":"Writing"}]},"tv_credits":{"cast":[],"crew":[]}}
            """);
        tmdb.Answer("/discover/movie", uri => Uri.UnescapeDataString(uri.Query) switch
        {
            var q when q.Contains("with_companies=41077", StringComparison.Ordinal) => """{"page":1,"total_pages":1,"results":[{"id":8,"title":"Hereditary"}]}""",
            var q when q.Contains("with_keywords=4379", StringComparison.Ordinal) => """{"page":1,"total_pages":1,"results":[{"id":9,"title":"Looper"}]}""",
            _ => """{"page":1,"total_pages":1,"results":[]}""",
        });

        var collection = await Fetch(new TmdbListSource(tmdb.Client), ImportListCatalog.TmdbCollection, MediaType.Movie, new { collectionId = "10" });
        var director = await Fetch(new TmdbListSource(tmdb.Client), ImportListCatalog.TmdbPerson, MediaType.Movie, new { personId = "525", cast = false, departments = new[] { "Directing", "Writing" } });
        var castAndProduction = await Fetch(new TmdbListSource(tmdb.Client), ImportListCatalog.TmdbPerson, MediaType.Movie, new { personId = "525", cast = true, departments = new[] { "Production" } });
        var company = await Fetch(new TmdbListSource(tmdb.Client), ImportListCatalog.TmdbCompany, MediaType.Movie, new { companyId = "https://www.themoviedb.org/company/41077-a24" });
        var keyword = await Fetch(new TmdbListSource(tmdb.Client), ImportListCatalog.TmdbKeyword, MediaType.Movie, new { keywordId = "4379" });

        Assert.Equal([11, 1891], collection.Items.Select(i => i.TmdbId));
        Assert.Equal("Star Wars Collection", collection.Name);
        Assert.Equal([6], director.Items.Select(i => i.TmdbId));
        Assert.Equal([7, 5], castAndProduction.Items.Select(i => i.TmdbId)); // newest first
        Assert.Equal("Hereditary", Assert.Single(company.Items).Title);
        Assert.Equal("Looper", Assert.Single(keyword.Items).Title);
    }

    [Fact]
    public void Tmdb_popular_builds_discover_parameters_with_filters()
    {
        var settings = Settings(new
        {
            movieList = "upcoming",
            minVotes = 50,
            minRating = "6,5",
            movieGenres = new[] { "28", "878" },
            language = "EN",
            yearFrom = 2020,
            yearTo = 2027,
            certification = "PG, PG-13",
            certificationCountry = "de",
        });

        var p = TmdbListSource.PopularParameters(StreamZH.Metadata.Tmdb.TmdbMediaKind.Movie, settings, Now);

        Assert.Equal("popularity.desc", p["sort_by"]);
        Assert.Equal("2026-10-19", p["release_date.gte"]);
        Assert.Equal("2027-01-18", p["release_date.lte"]);
        Assert.Equal("2|3", p["with_release_type"]);
        Assert.Equal("50", p["vote_count.gte"]);
        Assert.Equal("6.5", p["vote_average.gte"]);
        Assert.Equal("28|878", p["with_genres"]);
        Assert.Equal("en", p["with_original_language"]);
        Assert.Equal("2020-01-01", p["primary_release_date.gte"]);
        Assert.Equal("2027-12-31", p["primary_release_date.lte"]);
        Assert.Equal("DE", p["certification_country"]);
        Assert.Equal("PG|PG-13", p["certification"]);

        var topRated = TmdbListSource.PopularParameters(StreamZH.Metadata.Tmdb.TmdbMediaKind.Series, Settings(new { seriesList = "topRated", certification = "PG" }), Now);
        Assert.Equal("vote_average.desc", topRated["sort_by"]);
        Assert.Equal("200", topRated["vote_count.gte"]);
        Assert.False(topRated.ContainsKey("certification"));

        var onTheAir = TmdbListSource.PopularParameters(StreamZH.Metadata.Tmdb.TmdbMediaKind.Series, Settings(new { seriesList = "onTheAir", yearFrom = 2010 }), Now);
        Assert.Equal("2026-10-18", onTheAir["air_date.gte"]);
        Assert.Equal("2026-10-25", onTheAir["air_date.lte"]);
        Assert.Equal("2010-01-01", onTheAir["first_air_date.gte"]);
    }

    [Fact]
    public async Task Tmdb_popular_pages_through_discover_and_reports_tmdb_errors()
    {
        var tmdb = new FakeTmdbHttp();
        var calls = 0;
        tmdb.Answer("/discover/movie", uri =>
        {
            calls++;
            var page = uri.Query.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
            return $$"""{"page":{{page}},"total_pages":9,"results":[{{string.Join(',', Enumerable.Range(page * 100, 20).Select(i => $$"""{"id":{{i}},"title":"M{{i}}"}"""))}}]}""";
        });

        var fetch = await Fetch(new TmdbListSource(tmdb.Client), ImportListCatalog.TmdbPopular, MediaType.Movie, new { limit = 30 });

        Assert.Equal(30, fetch.Items.Count);
        Assert.Equal(2, calls);
        var missing = await Assert.ThrowsAsync<ImportListException>(() =>
            Fetch(new TmdbListSource(tmdb.Client), ImportListCatalog.TmdbCollection, MediaType.Movie, new { collectionId = "77" }));
        Assert.Contains("TMDB: not found", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tmdb_validation_checks_numbers_rating_language_and_person_choices()
    {
        var source = new TmdbListSource(new FakeTmdbHttp().Client);
        var errors = new Dictionary<string, string[]>();
        source.Validate(ImportListCatalog.TmdbList, MediaType.Movie, Settings(new { listId = "best-list" }), errors);
        source.Validate(ImportListCatalog.TmdbPopular, MediaType.Movie, Settings(new { minRating = "11", language = "english", yearFrom = 2020, yearTo = 2010 }), errors);
        source.Validate(ImportListCatalog.TmdbPerson, MediaType.Movie, Settings(new { personId = "31", cast = false, departments = Array.Empty<string>() }), errors);

        Assert.Equal(["departments", "language", "listId", "minRating", "yearTo"], errors.Keys.Order());
        Assert.Equal(8245, ImportListHttp.TmdbNumber("https://www.themoviedb.org/de-DE/list/8245-best"));
        Assert.Equal(31, ImportListHttp.TmdbNumber(" 31 "));
        Assert.Null(ImportListHttp.TmdbNumber("tom-hanks"));
    }

    // ---- Trakt ----

    [Fact]
    public async Task Trakt_watchlist_sends_the_client_id_and_reads_wrapped_items()
    {
        var http = new ListHttp(request =>
        {
            Assert.Equal("2", request.Headers.GetValues("trakt-api-version").Single());
            Assert.Equal("my-client-id", request.Headers.GetValues("trakt-api-key").Single());
            return (HttpStatusCode.OK, request.RequestUri!.AbsolutePath switch
            {
                "/users/johndoe/watchlist/movies" => """
                    [{"type":"movie","movie":{"title":"Dune","year":2021,"ids":{"trakt":1,"slug":"dune-2021","imdb":"tt1160419","tmdb":438631}}},
                     {"type":"movie","movie":{"title":"No ids","year":2020,"ids":{"trakt":2}}}]
                    """,
                _ => "[]",
            });
        });

        var fetch = await Fetch(new TraktListSource(http), ImportListCatalog.Trakt, MediaType.Movie, new { clientId = "my-client-id", listKind = "watchlist", username = "the owner" });

        var dune = Assert.Single(fetch.Items);
        Assert.Equal((438631, "tt1160419", "Dune", 2021), (dune.TmdbId!.Value, dune.ImdbId, dune.Title, dune.Year));
        Assert.Contains("limit=100", http.Requests[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trakt_user_list_popular_and_trending()
    {
        var http = new ListHttp(request => (HttpStatusCode.OK, request.RequestUri!.AbsolutePath switch
        {
            "/users/johndoe/lists/sci-fi/items/shows" => """[{"type":"show","show":{"title":"Severance","year":2022,"ids":{"tvdb":371980,"imdb":"tt11280740","tmdb":95396}}}]""",
            "/shows/popular" => """[{"title":"Dark","year":2017,"ids":{"tvdb":334824,"tmdb":70523}}]""",
            "/movies/trending" => """[{"watchers":12,"movie":{"title":"Dune","year":2021,"ids":{"tmdb":438631}}}]""",
            _ => "[]",
        }));

        var list = await Fetch(new TraktListSource(http), ImportListCatalog.Trakt, MediaType.Series, new { clientId = "id", listKind = "userList", username = "johndoe", listName = "sci-fi" });
        var popular = await Fetch(new TraktListSource(http), ImportListCatalog.Trakt, MediaType.Series, new { clientId = "id", listKind = "popular", years = "2015-2020", limit = 5 });
        var trending = await Fetch(new TraktListSource(http), ImportListCatalog.Trakt, MediaType.Movie, new { clientId = "id", listKind = "trending" });

        Assert.Equal((95396, 371980), (Assert.Single(list.Items).TmdbId!.Value, list.Items[0].TvdbId!.Value));
        Assert.Equal(70523, Assert.Single(popular.Items).TmdbId);
        Assert.Contains("years=2015-2020", http.Requests[1].Query, StringComparison.Ordinal);
        Assert.Contains("limit=5", http.Requests[1].Query, StringComparison.Ordinal);
        Assert.Equal(438631, Assert.Single(trending.Items).TmdbId);
    }

    [Fact]
    public async Task Trakt_reads_further_pages_and_reports_a_bad_client_id()
    {
        var http = new ListHttp(request =>
        {
            var page = request.RequestUri!.Query.Contains("page=1&", StringComparison.Ordinal) ? 1 : 2;
            var count = page == 1 ? 100 : 3;
            return (HttpStatusCode.OK, $"[{string.Join(',', Enumerable.Range(page * 1000, count).Select(i => "{\"title\":\"M" + i + "\",\"year\":2020,\"ids\":{\"tmdb\":" + i + "}}"))}]");
        });
        var fetch = await Fetch(new TraktListSource(http), ImportListCatalog.Trakt, MediaType.Movie, new { clientId = "id", listKind = "popular", limit = 500 });
        Assert.Equal(103, fetch.Items.Count);
        Assert.Equal(2, http.Requests.Count);

        var rejected = new ListHttp(_ => (HttpStatusCode.Forbidden, "{}"));
        var ex = await Assert.ThrowsAsync<ImportListException>(() =>
            Fetch(new TraktListSource(rejected), ImportListCatalog.Trakt, MediaType.Movie, new { clientId = "bad", listKind = "trending" }));
        Assert.Equal("Trakt refused access (403)", ex.Message);
    }

    [Fact]
    public void Trakt_validation_needs_user_and_list_for_user_lists()
    {
        var source = new TraktListSource(new ListHttp(_ => (HttpStatusCode.OK, "[]")));
        var errors = new Dictionary<string, string[]>();
        source.Validate(ImportListCatalog.Trakt, MediaType.Movie, Settings(new { listKind = "userList" }), errors);
        source.Validate(ImportListCatalog.Trakt, MediaType.Movie, Settings(new { listKind = "popular", years = "last year" }), errors);
        Assert.Equal(["listName", "username", "years"], errors.Keys.Order());
    }

    // ---- Plex ----

    [Fact]
    public async Task Plex_watchlist_reads_guids_and_pages_with_the_token()
    {
        var http = new ListHttp(request =>
        {
            Assert.Equal("discover.provider.plex.tv", request.RequestUri!.Host);
            Assert.Equal("plex-token", request.Headers.GetValues("X-Plex-Token").Single());
            var start = request.RequestUri.Query.Contains("X-Plex-Container-Start=0", StringComparison.Ordinal);
            return (HttpStatusCode.OK, start
                ? "{\"MediaContainer\":{\"totalSize\":101,\"Metadata\":["
                  + """{"title":"Dune","year":2021,"type":"movie","Guid":[{"id":"imdb://tt1160419"},{"id":"tmdb://438631"},{"id":"tvdb://123"}]},"""
                  + string.Join(',', Enumerable.Range(1, 99).Select(i => "{\"title\":\"M" + i + "\",\"year\":2020,\"Guid\":[{\"id\":\"imdb://tt" + i.ToString("0000000", System.Globalization.CultureInfo.InvariantCulture) + "\"}]}"))
                  + "]}}"
                : """{"MediaContainer":{"totalSize":101,"Metadata":[{"title":"Arrival","year":2016,"Guid":[{"id":"imdb://tt2543164"}]},{"title":"No guid","year":2000}]}}""");
        });

        var fetch = await Fetch(new PlexWatchlistSource(http), ImportListCatalog.PlexWatchlist, MediaType.Movie, new { accessToken = "plex-token" });

        Assert.Equal(101, fetch.Items.Count);
        Assert.Equal((438631, "tt1160419"), (fetch.Items[0].TmdbId!.Value, fetch.Items[0].ImdbId));
        Assert.Null(fetch.Items[0].TvdbId); // TVDB ids only matter for series
        Assert.Equal("tt2543164", fetch.Items[^1].ImdbId);
        Assert.Contains("type=1", http.Requests[0].Query, StringComparison.Ordinal);
        Assert.Contains("includeGuids=1", http.Requests[0].Query, StringComparison.Ordinal);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Plex_series_watchlist_and_a_rejected_token()
    {
        var http = new ListHttp(_ => (HttpStatusCode.OK, """{"MediaContainer":{"totalSize":1,"Metadata":[{"title":"Severance","year":2022,"Guid":[{"id":"tvdb://371980"}]}]}}"""));
        var fetch = await Fetch(new PlexWatchlistSource(http), ImportListCatalog.PlexWatchlist, MediaType.Series, new { accessToken = "t" });
        Assert.Equal(371980, Assert.Single(fetch.Items).TvdbId);
        Assert.Contains("type=2", http.Requests[0].Query, StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<ImportListException>(() =>
            Fetch(new PlexWatchlistSource(new ListHttp(_ => (HttpStatusCode.Unauthorized, ""))), ImportListCatalog.PlexWatchlist, MediaType.Movie, new { accessToken = "old" }));
        Assert.Equal("Plex rejected the credentials (401)", ex.Message);
    }

    // ---- StreamZH ----

    [Fact]
    public async Task StreamZh_reads_the_other_library_with_the_api_key()
    {
        var http = new ListHttp(request =>
        {
            Assert.Equal("secret-key", request.Headers.GetValues("X-Api-Key").Single());
            Assert.Equal("/sub/api/v1/movies", request.RequestUri!.AbsolutePath);
            return (HttpStatusCode.OK, """[{"tmdbId":438631,"title":"Dune","year":2021,"monitored":true},{"tmdbId":329865,"title":"Arrival","year":2016,"monitored":false}]""");
        });

        var all = await Fetch(new StreamZhListSource(http), ImportListCatalog.StreamZh, MediaType.Movie, new { url = "http://other:8787/sub/", apiKey = "secret-key" });
        var monitored = await Fetch(new StreamZhListSource(http), ImportListCatalog.StreamZh, MediaType.Movie, new { url = "http://other:8787/sub", apiKey = "secret-key", onlyMonitored = true });

        Assert.Equal([438631, 329865], all.Items.Select(i => i.TmdbId!.Value));
        Assert.Equal(438631, Assert.Single(monitored.Items).TmdbId);
        var html = await Assert.ThrowsAsync<ImportListException>(() =>
            Fetch(new StreamZhListSource(new ListHttp(_ => (HttpStatusCode.OK, "<html>"))), ImportListCatalog.StreamZh, MediaType.Movie, new { url = "http://x", apiKey = "k" }));
        Assert.Contains("not JSON", html.Message, StringComparison.Ordinal);
    }

    // ---- RSS ----

    [Fact]
    public async Task Rss_reads_ids_from_elements_links_and_text()
    {
        var http = new ListHttp(_ => (HttpStatusCode.OK, """
            <?xml version="1.0"?>
            <rss version="2.0" xmlns:tmdb="https://themoviedb.org/rss">
              <channel>
                <item><title>Dune (2021)</title><tmdb:id>438631</tmdb:id></item>
                <item><title>Arrival</title><link>https://www.imdb.com/title/tt2543164/</link></item>
                <item><title>Blade Runner 2049</title><guid>https://www.themoviedb.org/movie/335984-blade-runner-2049</guid></item>
                <item><title>A show</title><link>https://www.themoviedb.org/tv/1396</link></item>
                <item><title>Nothing</title><link>https://example.com/1</link></item>
              </channel>
            </rss>
            """));

        var fetch = await Fetch(new RssListSource(http), ImportListCatalog.Rss, MediaType.Movie, new { url = "https://example.com/list.rss" });

        Assert.Equal(3, fetch.Items.Count);
        Assert.Equal(("Dune", 2021, 438631), (fetch.Items[0].Title, fetch.Items[0].Year, fetch.Items[0].TmdbId!.Value));
        Assert.Equal("tt2543164", fetch.Items[1].ImdbId);
        Assert.Equal(335984, fetch.Items[2].TmdbId);
    }

    [Fact]
    public async Task Rss_reads_atom_feeds_and_rejects_broken_ones()
    {
        var atom = new ListHttp(_ => (HttpStatusCode.OK, """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><title>Breaking Bad</title><link href="https://www.themoviedb.org/tv/1396-breaking-bad"/><tvdb>81189</tvdb></entry>
            </feed>
            """));
        var fetch = await Fetch(new RssListSource(atom), ImportListCatalog.Rss, MediaType.Series, new { url = "https://example.com/atom" });
        Assert.Equal((1396, 81189), (Assert.Single(fetch.Items).TmdbId!.Value, fetch.Items[0].TvdbId!.Value));

        var ex = await Assert.ThrowsAsync<ImportListException>(() =>
            Fetch(new RssListSource(new ListHttp(_ => (HttpStatusCode.OK, "not xml"))), ImportListCatalog.Rss, MediaType.Movie, new { url = "https://example.com" }));
        Assert.Equal("RSS feed: not a valid feed", ex.Message);
        var down = await Assert.ThrowsAsync<ImportListException>(() =>
            Fetch(new RssListSource(new ListHttp(_ => throw new HttpRequestException("connection refused"))), ImportListCatalog.Rss, MediaType.Movie, new { url = "https://example.com" }));
        Assert.Equal("RSS feed is not reachable (connection refused)", down.Message);
    }

    // ---- Helpers ----

    internal static ConnectionSettings Settings(object values) =>
        new(JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal));

    internal static Task<ImportListFetch> Fetch(IImportListSource source, string implementation, MediaType mediaType, object settings)
    {
        var s = Settings(settings);
        var definition = new ImportListDefinition(1, "Test", implementation, mediaType, true, true, false, "movieOnly", 1, null, "announced", s.Values);
        return source.FetchAsync(new ImportListContext(definition, s, Now), CancellationToken.None);
    }
}

/// <summary>HTTP fake for the import list sources: one answer function for every request.</summary>
internal sealed class ListHttp(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler, IHttpClientFactory
{
    public List<Uri> Requests { get; } = [];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var (status, body) = answer(request);
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
