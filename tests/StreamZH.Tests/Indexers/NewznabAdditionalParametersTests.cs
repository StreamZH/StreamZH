// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text;
using StreamZH.Core.Indexers;
using StreamZH.Indexers.Newznab;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

public class NewznabAdditionalParametersTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("attrs=poster", "&attrs=poster")]
    [InlineData("&attrs=poster", "&attrs=poster")]
    [InlineData("&&dl=1&&attrs=poster,size&", "&dl=1&attrs=poster%2Csize")]
    [InlineData("t=search&apikey=x&OFFSET=5&limit=1&maxage=100", "&maxage=100")]
    [InlineData("novalue&=x&q=a b", "&q=a%20b")]
    [InlineData("q=a%20b", "&q=a%20b")]
    public void Parses_additional_parameters_safely(string? input, string expected) =>
        Assert.Equal(expected, NewznabRequestBuilder.AdditionalParameters(input));

    [Fact]
    public async Task Are_appended_to_caps_search_and_rss_but_not_to_the_nzb_download()
    {
        var handler = new StubIndexer
        {
            Respond = r => r.RequestUri!.AbsolutePath.Contains("getnzb", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<nzb></nzb>", Encoding.UTF8, "application/x-nzb") }
                : Xml(Feed(1)),
        };
        var indexer = Indexer() with { AdditionalParameters = "&attrs=poster&t=bad&limit=5" };
        var client = Client(handler);

        await client.GetCapabilitiesAsync(indexer, CancellationToken.None);
        await client.SearchAsync(indexer, new MovieSearchCriteria { Titles = ["Star Wars"], Year = 1977, TmdbId = 11 }, CancellationToken.None);
        await client.FetchRecentAsync(indexer, CancellationToken.None);
        await client.DownloadNzbAsync(indexer, new ReleaseInfo { Guid = "g", Title = "T", DownloadUrl = "http://127.0.0.1:1234/getnzb/g.nzb" }, CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
        var api = handler.Requests.Where(r => r.AbsolutePath == "/api").ToList();
        Assert.Equal(3, api.Count);
        Assert.All(api, url =>
        {
            Assert.EndsWith("&attrs=poster", url.Query, StringComparison.Ordinal);
            Assert.Single(url.Query.Split('&'), p => p.StartsWith("t=", StringComparison.Ordinal) || p.StartsWith("?t=", StringComparison.Ordinal));
        });
        Assert.Equal("http://127.0.0.1:1234/api?t=movie&cat=2000,2040&extended=1&apikey=abcd1234secret&offset=0&limit=100&tmdbid=11&attrs=poster", api[1].ToString());
        Assert.DoesNotContain("attrs", handler.Requests.Single(r => r.AbsolutePath.Contains("getnzb", StringComparison.Ordinal)).Query, StringComparison.Ordinal);
    }
}
