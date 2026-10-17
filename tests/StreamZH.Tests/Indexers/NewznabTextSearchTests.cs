// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Indexers;
using StreamZH.Indexers.Newznab;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

public class NewznabTextSearchTests
{
    private static TextSearchCriteria Text(string query, TextSearchType type = TextSearchType.Search, params int[] categories) =>
        new() { Titles = [query], Query = query, Type = type, Categories = categories };

    private static NewznabQuery Only(IReadOnlyList<NewznabSearchPlan> plans) =>
        Assert.Single(Assert.Single(Assert.Single(plans).Tiers));

    [Fact]
    public void Free_text_uses_t_search_with_the_query_as_typed()
    {
        var query = Only(NewznabRequestBuilder.ForText(Text("The Matrix 1999 & more"), [], NewznabCaps.Default));

        Assert.Equal("search", query.Function);
        Assert.Empty(query.Categories);
        Assert.Equal("&q=The%20Matrix%201999%20%26%20more", query.Parameters);
    }

    [Theory]
    [InlineData(TextSearchType.Movie, "movie", new[] { 2000, 2040 })]
    [InlineData(TextSearchType.Tv, "tvsearch", new[] { 5000, 5040 })]
    public void Movie_and_tv_use_their_function_and_default_categories(TextSearchType type, string function, int[] defaults)
    {
        var query = Only(NewznabRequestBuilder.ForText(Text("dune", type), defaults, NewznabCaps.Default));

        Assert.Equal(function, query.Function);
        Assert.Equal(defaults, query.Categories);
        Assert.Equal("&q=dune", query.Parameters);
    }

    [Fact]
    public void Chosen_categories_win_over_the_defaults()
    {
        var query = Only(NewznabRequestBuilder.ForText(Text("dune", TextSearchType.Movie, 2045, 2045, 2050), [2000], NewznabCaps.Default));

        Assert.Equal([2045, 2050], query.Categories);
    }

    [Fact]
    public void Movie_search_without_q_falls_back_to_t_search()
    {
        var caps = NewznabCaps.Default with { MovieSearchParams = ["imdbid"] };

        Assert.Equal("search", Only(NewznabRequestBuilder.ForText(Text("dune", TextSearchType.Movie), [2000], caps)).Function);
        Assert.Empty(NewznabRequestBuilder.ForText(Text("dune", TextSearchType.Movie), [2000], caps with { SearchParams = null }));
    }

    [Fact]
    public void Empty_query_lists_the_newest_releases_without_q()
    {
        var query = Only(NewznabRequestBuilder.ForText(Text("  "), [], NewznabCaps.Default));

        Assert.Equal(("search", string.Empty), (query.Function, query.Parameters));
    }

    [Fact]
    public async Task Client_sends_t_search_without_cat_and_pages_at_most_three_times()
    {
        var handler = new StubIndexer
        {
            Respond = r =>
            {
                var offset = int.Parse(Query(r.RequestUri!)["offset"], System.Globalization.CultureInfo.InvariantCulture);
                return Xml(Feed(100, offset));
            },
        };

        var results = await Client(handler).SearchAsync(Indexer(), Text("star wars"), CancellationToken.None);

        Assert.Equal(NewznabClient.MaxPagesPerTextQuery, handler.ApiRequests.Count);
        Assert.Equal(
            "http://127.0.0.1:1234/api?t=search&extended=1&apikey=abcd1234secret&offset=0&limit=100&q=star%20wars",
            handler.ApiRequests[0].AbsoluteUri);
        Assert.Equal(300, results.Count);
        Assert.All(results, r => Assert.Equal(7, r.IndexerId));
    }

    [Fact]
    public async Task Client_reads_one_page_for_an_empty_query_and_sends_chosen_categories()
    {
        var handler = new StubIndexer { Respond = _ => Xml(Feed(100)) };

        await Client(handler).SearchAsync(Indexer(), Text(string.Empty, TextSearchType.Tv, 5030, 5040), CancellationToken.None);

        var url = Assert.Single(handler.ApiRequests);
        Assert.Equal("http://127.0.0.1:1234/api?t=tvsearch&cat=5030,5040&extended=1&apikey=abcd1234secret&offset=0&limit=100", url.ToString());
    }
}
