// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Xml.Linq;
using StreamZH.Core.Indexers;
using StreamZH.Indexers.Newznab;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

public class NewznabCapabilitiesTests
{
    private static readonly string RadarrCaps = Fixture("newznab_caps.xml");

    private static NewznabCaps Parse(string xml) => NewznabCaps.Parse(XDocument.Parse(xml));

    [Fact]
    public void Reports_page_size()
    {
        var caps = Parse(RadarrCaps);

        Assert.Equal(25, caps.DefaultPageSize);
        Assert.Equal(60, caps.MaxPageSize);
        Assert.Equal(60, caps.PageSize);
    }

    [Fact]
    public void Uses_default_page_size_if_missing()
    {
        var caps = Parse(RadarrCaps.Replace("<limits", "<abclimits", StringComparison.Ordinal));

        Assert.Equal(100, caps.DefaultPageSize);
        Assert.Equal(100, caps.MaxPageSize);
    }

    [Theory]
    [InlineData(30, 25, 30)]
    [InlineData(250, 25, 100)]
    [InlineData(250, 200, 100)]
    [InlineData(0, 0, 100)]
    public void Page_size_is_the_maximum_but_never_above_100(int max, int @default, int expected)
    {
        var caps = new NewznabCaps { MaxPageSize = max, DefaultPageSize = @default };

        Assert.Equal(expected, caps.PageSize);
    }

    [Fact]
    public void Available_searches_without_supported_params_keep_the_defaults()
    {
        var caps = Parse(RadarrCaps);

        Assert.Equal(["q"], caps.SearchParams);
        Assert.Equal(["q", "imdbid", "imdbtitle", "imdbyear"], caps.MovieSearchParams);
        Assert.Equal(["q", "rid", "season", "ep"], caps.TvSearchParams);
        Assert.False(caps.MovieAggregateIdSearch);
        Assert.False(caps.TvAggregateIdSearch);
        Assert.Equal("sphinx", caps.TextSearchEngine);
        Assert.Equal("sphinx", caps.MovieTextSearchEngine);
    }

    [Fact]
    public void Uses_specified_search_engines()
    {
        var caps = Parse(RadarrCaps
            .Replace("<search ", "<search searchEngine=\"raw\" ", StringComparison.Ordinal)
            .Replace("<movie-search ", "<movie-search searchEngine=\"raw2\" ", StringComparison.Ordinal)
            .Replace("<tv-search ", "<tv-search searchEngine=\"raw3\" ", StringComparison.Ordinal));

        Assert.Equal("raw", caps.TextSearchEngine);
        Assert.Equal("raw2", caps.MovieTextSearchEngine);
        Assert.Equal("raw3", caps.TvTextSearchEngine);
    }

    [Fact]
    public void Parses_supported_params_categories_and_skips_broken_ids()
    {
        var caps = Parse(Fixture("newznab_caps_full.xml"));

        Assert.Equal(75, caps.DefaultPageSize);
        Assert.Equal(200, caps.MaxPageSize);
        Assert.Equal(100, caps.PageSize);
        Assert.Equal(["q", "imdbid", "tmdbid"], caps.MovieSearchParams);
        Assert.Equal(["q", "tvdbid", "imdbid", "tmdbid", "season", "ep"], caps.TvSearchParams);
        Assert.True(caps.MovieAggregateIdSearch);
        Assert.True(caps.TvAggregateIdSearch);
        Assert.Equal("raw", caps.TextSearchEngine);
        Assert.Equal("raw2", caps.MovieTextSearchEngine);

        Assert.Equal([2000, 5000], caps.Categories.Select(c => c.Id));
        Assert.Equal("Movies", caps.Categories[0].Name);
        Assert.Equal([2040, 2045], caps.Categories[0].SubCategories.Select(c => c.Id));
        Assert.Equal("UHD", caps.Categories[0].SubCategories[1].Name);
    }

    [Fact]
    public void Unavailable_searches_have_no_params()
    {
        var caps = Parse(RadarrCaps
            .Replace("<movie-search available=\"yes\"", "<movie-search available=\"no\"", StringComparison.Ordinal)
            .Replace("<tv-search available=\"yes\"/>", string.Empty, StringComparison.Ordinal));

        Assert.Null(caps.MovieSearchParams);
        Assert.Null(caps.TvSearchParams);

        var contract = caps.ToCapabilities();
        Assert.False(contract.MovieSearchAvailable);
        Assert.False(contract.TvSearchAvailable);
        Assert.True(contract.SearchAvailable);
    }

    [Fact]
    public void Does_not_throw_on_unexpected_xml_data()
    {
        var caps = Parse(RadarrCaps.Replace("5030", "asdf", StringComparison.Ordinal));

        Assert.Equal(7, caps.Categories[0].SubCategories.Count);
    }

    [Fact]
    public void Rejects_documents_that_are_not_caps() =>
        Assert.Throws<FormatException>(() => Parse("<rss><channel /></rss>"));

    [Fact]
    public async Task Test_button_reads_caps_with_the_api_key()
    {
        var handler = new StubIndexer { CapsXml = Fixture("newznab_caps_full.xml") };
        var client = Client(handler);

        var caps = await client.GetCapabilitiesAsync(Indexer(), CancellationToken.None);

        Assert.Equal(200, caps.MaxPageSize);
        Assert.Equal(75, caps.DefaultPageSize);
        Assert.True(caps.SearchAvailable);
        Assert.Equal(["q", "imdbid", "tmdbid"], caps.MovieSearchParams);
        Assert.Equal(2, caps.Categories.Count);
        Assert.Equal("http://127.0.0.1:1234/api?t=caps&apikey=abcd1234secret", Assert.Single(handler.Requests).ToString());
    }

    [Fact]
    public async Task Does_not_request_the_same_caps_twice()
    {
        var handler = new StubIndexer();
        var client = Client(handler);
        var indexer = Indexer();

        await client.FetchRecentAsync(indexer, CancellationToken.None);
        await client.FetchRecentAsync(indexer, CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Query.Contains("t=caps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cache_is_per_indexer_url_and_key_and_expires_after_seven_days()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var cache = new NewznabCapabilitiesCache(time);
        var handler = new StubIndexer();
        var client = Client(handler, cache);

        await client.FetchRecentAsync(Indexer(), CancellationToken.None);
        await client.FetchRecentAsync(Indexer(apiKey: "other-key"), CancellationToken.None);
        await client.FetchRecentAsync(Indexer(baseUrl: "http://127.0.0.1:1234"), CancellationToken.None); // same after trimming
        Assert.Equal(2, handler.Requests.Count(r => r.Query.Contains("t=caps", StringComparison.Ordinal)));

        time.Now += TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);
        await client.FetchRecentAsync(Indexer(), CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count(r => r.Query.Contains("t=caps", StringComparison.Ordinal)));
    }

    [Fact]
    public void Cache_key_does_not_contain_the_api_key()
    {
        var key = NewznabCapabilitiesCache.KeyOf(Indexer());

        Assert.DoesNotContain(ApiKey, key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, key.Length);
    }

    [Fact]
    public async Task Test_button_throws_on_errors_instead_of_falling_back()
    {
        var handler = new StubIndexer { CapsXml = Fixture("unauthorized.xml") };
        var ex = await Assert.ThrowsAsync<IndexerException>(() => Client(handler).GetCapabilitiesAsync(Indexer(), CancellationToken.None));

        Assert.Equal(100, ex.Code);
        Assert.True(ex.IsAuthError);
        Assert.Contains("Incorrect user credentials", ex.Message, StringComparison.Ordinal);

        handler.CapsXml = "<caps><limits max=\"5\" default=\"5\"/>"; // broken XML
        var invalid = await Assert.ThrowsAsync<IndexerException>(() => Client(handler).GetCapabilitiesAsync(Indexer(), CancellationToken.None));
        Assert.Contains("invalid XML", invalid.Message, StringComparison.Ordinal);

        handler.CapsXml = "<rss><channel/></rss>";
        var notCaps = await Assert.ThrowsAsync<IndexerException>(() => Client(handler).GetCapabilitiesAsync(Indexer(), CancellationToken.None));
        Assert.Contains("did not return Newznab capabilities", notCaps.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_button_reports_unreachable_indexer()
    {
        var client = new NewznabClient(new HttpClient(new ThrowingHandler()), new NewznabCapabilitiesCache());

        var ex = await Assert.ThrowsAsync<IndexerException>(() => client.GetCapabilitiesAsync(Indexer(), CancellationToken.None));

        Assert.Contains("is not reachable", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_falls_back_to_default_caps_when_caps_are_broken()
    {
        var handler = new StubIndexer { CapsXml = "this is not xml" };
        var client = Client(handler);
        var criteria = new MovieSearchCriteria { Titles = ["Star Wars"], Year = 1977, TmdbId = 11, ImdbId = "tt0076759" };

        await client.SearchAsync(Indexer(), criteria, CancellationToken.None);

        // Radarr's defaults: movie-search with q and imdbid, no tmdbid, 100 per page.
        var url = handler.ApiRequests[0];
        var query = Query(url);
        Assert.Equal("movie", query["t"]);
        Assert.Equal("0076759", query["imdbid"]);
        Assert.False(query.ContainsKey("tmdbid"));
        Assert.Equal("100", query["limit"]);
    }

    [Fact]
    public async Task Search_does_not_fall_back_on_auth_errors()
    {
        var handler = new StubIndexer { CapsXml = Fixture("unauthorized.xml") };

        var ex = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler).SearchAsync(Indexer(), new MovieSearchCriteria { Titles = ["Dune"], Year = 2021 }, CancellationToken.None));

        Assert.True(ex.IsAuthError);
        Assert.Empty(handler.ApiRequests);
    }

    [Fact]
    public async Task Fallback_caps_are_retried_after_an_hour_and_a_successful_test_replaces_them()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var cache = new NewznabCapabilitiesCache(time);
        var handler = new StubIndexer { CapsXml = "broken" };
        var client = Client(handler, cache);
        var indexer = Indexer();

        await client.FetchRecentAsync(indexer, CancellationToken.None);
        Assert.Same(NewznabCaps.Default, cache.Get(indexer));

        time.Now += TimeSpan.FromMinutes(61);
        Assert.Null(cache.Get(indexer));

        handler.CapsXml = Fixture("newznab_caps_full.xml");
        await client.GetCapabilitiesAsync(indexer, CancellationToken.None);
        Assert.Equal(200, cache.Get(indexer)!.MaxPageSize);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException($"Connection refused ({request.RequestUri})", null, HttpStatusCode.ServiceUnavailable);
    }
}
