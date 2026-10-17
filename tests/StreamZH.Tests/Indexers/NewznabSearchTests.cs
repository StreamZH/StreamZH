// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Indexers;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

public class NewznabSearchTests
{
    private static readonly MovieSearchCriteria StarWars = new() { Titles = ["Star Wars"], Year = 1977, TmdbId = 11, ImdbId = "tt0076759" };

    [Fact]
    public async Task Movie_search_sends_exact_parameters_and_stamps_results()
    {
        var handler = new StubIndexer { Respond = _ => Xml(Feed(3)) };

        var results = await Client(handler).SearchAsync(Indexer(), StarWars, CancellationToken.None);

        var url = Assert.Single(handler.ApiRequests);
        Assert.Equal("http://127.0.0.1:1234/api?t=movie&cat=2000,2040&extended=1&apikey=abcd1234secret&offset=0&limit=100&tmdbid=11&imdbid=0076759", url.ToString());
        Assert.Equal(3, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(7, r.IndexerId);
            Assert.Equal("TestIndexer", r.Indexer);
            Assert.Equal(12, r.IndexerPriority);
        });
    }

    [Fact]
    public async Task Text_tier_runs_only_when_the_id_search_found_nothing()
    {
        var handler = new StubIndexer
        {
            Respond = r => Xml(r.RequestUri!.Query.Contains("q=", StringComparison.Ordinal) ? Feed(2, prefix: "text") : Feed(0)),
        };

        var results = await Client(handler).SearchAsync(Indexer(), StarWars with { Titles = ["Star Wars", "Krieg der Sterne"] }, CancellationToken.None);

        Assert.Equal(3, handler.ApiRequests.Count);
        Assert.Equal("11", Query(handler.ApiRequests[0])["tmdbid"]);
        Assert.Equal("Star%20Wars%201977", Query(handler.ApiRequests[1])["q"]);
        Assert.Equal("Krieg%20der%20Sterne%201977", Query(handler.ApiRequests[2])["q"]);
        Assert.Equal("movie", Query(handler.ApiRequests[2])["t"]);

        // Both title queries returned the same two guids: deduplicated.
        Assert.Equal(["text0", "text1"], results.Select(r => r.Guid).Order());
    }

    [Fact]
    public async Task Text_tier_is_skipped_when_the_id_search_found_something()
    {
        var handler = new StubIndexer { Respond = _ => Xml(Feed(1)) };

        await Client(handler).SearchAsync(Indexer(), StarWars, CancellationToken.None);

        Assert.Single(handler.ApiRequests);
    }

    [Fact]
    public async Task Pages_until_a_short_page_with_offsets_of_the_page_size()
    {
        var handler = new StubIndexer
        {
            CapsXml = Caps(limits: """<limits max="50" default="25" />"""),
            Respond = r =>
            {
                var offset = int.Parse(Query(r.RequestUri!)["offset"], System.Globalization.CultureInfo.InvariantCulture);
                return Xml(Feed(offset < 100 ? 50 : 10, offset));
            },
        };

        var results = await Client(handler).SearchAsync(Indexer(), StarWars, CancellationToken.None);

        Assert.Equal(["0", "50", "100"], handler.ApiRequests.Select(u => Query(u)["offset"]));
        Assert.All(handler.ApiRequests, u => Assert.Equal("50", Query(u)["limit"]));
        Assert.Equal(110, results.Count);
    }

    [Fact]
    public async Task Stops_paging_at_the_reported_total()
    {
        var handler = new StubIndexer
        {
            Respond = r =>
            {
                var offset = int.Parse(Query(r.RequestUri!)["offset"], System.Globalization.CultureInfo.InvariantCulture);
                return Xml(Feed(100, offset, total: 200));
            },
        };

        var results = await Client(handler).SearchAsync(Indexer(), StarWars, CancellationToken.None);

        Assert.Equal(2, handler.ApiRequests.Count);
        Assert.Equal(200, results.Count);
    }

    [Fact]
    public async Task Does_not_get_unlimited_pages()
    {
        var handler = new StubIndexer
        {
            Respond = r =>
            {
                var offset = int.Parse(Query(r.RequestUri!)["offset"], System.Globalization.CultureInfo.InvariantCulture);
                return Xml(Feed(100, offset, total: 100_000));
            },
        };

        var results = await Client(handler).SearchAsync(Indexer(), StarWars, CancellationToken.None);

        Assert.Equal(10, handler.ApiRequests.Count);
        Assert.Equal(1000, results.Count);
    }

    [Fact]
    public async Task Short_page_counts_raw_items_not_parsed_releases()
    {
        // 100 items of which one has no title: still a full page, so the next page is requested.
        var full = Feed(99).Replace("</channel>", "<item><title></title><guid>x</guid></item></channel>", StringComparison.Ordinal);
        var handler = new StubIndexer { Respond = r => Xml(Query(r.RequestUri!)["offset"] == "0" ? full : Feed(0)) };

        await Client(handler).SearchAsync(Indexer(), StarWars, CancellationToken.None);

        Assert.Equal(2, handler.ApiRequests.Count);
    }

    [Fact]
    public async Task Series_search_uses_tv_categories_and_one_request_per_episode()
    {
        var handler = new StubIndexer
        {
            CapsXml = Caps(tvSearch: """<tv-search available="yes" supportedParams="q,tvdbid,imdbid,season,ep" />"""),
            Respond = r => Xml(Feed(1, prefix: "ep" + Query(r.RequestUri!)["ep"])),
        };
        var criteria = new EpisodeSearchCriteria { Titles = ["Breaking Bad"], TvdbId = 81189, ImdbId = "tt0903747", SeasonNumber = 1, EpisodeNumbers = [1, 2] };

        var results = await Client(handler).SearchAsync(Indexer(tvCategories: [5030, 5040]), criteria, CancellationToken.None);

        Assert.Equal(
            [
                "http://127.0.0.1:1234/api?t=tvsearch&cat=5030,5040&extended=1&apikey=abcd1234secret&offset=0&limit=100&tvdbid=81189&imdbid=tt0903747&season=1&ep=1",
                "http://127.0.0.1:1234/api?t=tvsearch&cat=5030,5040&extended=1&apikey=abcd1234secret&offset=0&limit=100&tvdbid=81189&imdbid=tt0903747&season=1&ep=2",
            ],
            handler.ApiRequests.Select(u => u.ToString()));
        Assert.Equal(["ep10", "ep20"], results.Select(r => r.Guid));
    }

    [Fact]
    public async Task Daily_and_season_searches_send_sonarr_parameters()
    {
        var handler = new StubIndexer();
        var client = Client(handler);
        var daily = new EpisodeSearchCriteria { Titles = ["The Daily Show"], TvdbId = 71256, AirDate = "2026-10-16" };
        var season = new EpisodeSearchCriteria { Titles = ["Breaking Bad"], TvdbId = 81189, SeasonNumber = 2 };

        await client.SearchAsync(Indexer(), daily, CancellationToken.None);
        await client.SearchAsync(Indexer(), season, CancellationToken.None);

        var dailyQuery = Query(handler.ApiRequests[0]);
        Assert.Equal("2026", dailyQuery["season"]);
        Assert.Equal("10/16", dailyQuery["ep"]);
        Assert.Equal("71256", dailyQuery["tvdbid"]);

        // Id search found nothing, so the title fallback ran; then the season pack search.
        Assert.Equal("Daily%20Show", Query(handler.ApiRequests[1])["q"]);
        var seasonQuery = Query(handler.ApiRequests[2]);
        Assert.Equal("2", seasonQuery["season"]);
        Assert.False(seasonQuery.ContainsKey("ep"));
    }

    [Fact]
    public async Task No_requests_without_categories()
    {
        var handler = new StubIndexer();

        var results = await Client(handler).SearchAsync(Indexer(categories: []), StarWars, CancellationToken.None);
        var recent = await Client(handler).FetchRecentAsync(Indexer(categories: [], tvCategories: []), CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(recent);
        Assert.Empty(handler.ApiRequests);
    }

    [Fact]
    public async Task Fetch_recent_uses_search_over_movie_and_tv_categories_one_page()
    {
        var handler = new StubIndexer { Respond = _ => Xml(Fixture("newznab_nzb_su.xml")) };

        var releases = await Client(handler).FetchRecentAsync(Indexer(categories: [2000, 2040], tvCategories: [5000, 2040]), CancellationToken.None);

        var url = Assert.Single(handler.ApiRequests);
        Assert.Equal("http://127.0.0.1:1234/api?t=search&cat=2000,2040,5000&extended=1&apikey=abcd1234secret&offset=0&limit=100", url.ToString());

        // Radarr's should_parse_recent_feed_from_newznab_nzb_su.
        Assert.Equal(100, releases.Count);
        var first = releases[0];
        Assert.Equal("White.Collar.S03E05.720p.HDTV.X264-DIMENSION", first.Title);
        Assert.Equal("http://nzb.su/getnzb/24967ef4c2e26296c65d3bbfa97aa8fe.nzb&i=37292&r=xxx", first.DownloadUrl);
        Assert.Equal("http://nzb.su/details/24967ef4c2e26296c65d3bbfa97aa8fe", first.InfoUrl);
        Assert.Equal(new DateTimeOffset(2012, 2, 27, 16, 9, 39, TimeSpan.Zero), first.PublishDate);
        Assert.Equal(1183105773, first.Size);
        Assert.Equal(7, first.IndexerId);
        Assert.Equal("TestIndexer", first.Indexer);
        Assert.Equal([5000, 5040], first.Categories);
    }

    [Fact]
    public async Task Cancellation_is_not_turned_into_an_indexer_error()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(new StubIndexer()).SearchAsync(Indexer(), StarWars, cts.Token));
    }
}
