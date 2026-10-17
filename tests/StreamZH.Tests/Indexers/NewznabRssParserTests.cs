// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Indexers.Newznab;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

public class NewznabRssParserTests
{
    private static readonly Uri RequestUri = new("https://indexer.example/api?t=search&apikey=x");

    private static NewznabFeed Parse(string xml, Uri? requestUri = null) =>
        NewznabRssParser.Parse(NewznabRssParser.Load(xml), requestUri ?? RequestUri);

    [Fact]
    public void Parses_ids_grabs_categories_size_and_usenetdate()
    {
        var feed = Parse(Fixture("newznab_attrs.xml"));

        Assert.Equal(6, feed.ItemCount);
        Assert.Equal(6, feed.Total);
        Assert.Equal(0, feed.Offset);

        var dune = feed.Releases[0];
        Assert.Equal("https://indexer.example/details/aaa111", dune.Guid);
        Assert.Equal("Dune.Part.Two.2024.2160p.UHD.BluRay.x265-GROUP", dune.Title);
        Assert.Equal("https://indexer.example/getnzb/aaa111.nzb&i=1&r=SECRETKEY", dune.DownloadUrl);
        Assert.Equal("https://indexer.example/details/aaa111", dune.InfoUrl);
        Assert.Equal(45_000_000_000, dune.Size); // size attribute wins over the enclosure length
        Assert.Equal([2000, 2045], dune.Categories);
        Assert.Equal(42, dune.Grabs);
        Assert.Equal(693134, dune.TmdbId);
        Assert.Equal("tt15239678", dune.ImdbId);
        Assert.Equal(0, dune.TvdbId);

        // usenetdate is preferred over pubDate, like Radarr.
        Assert.Equal(new DateTimeOffset(2015, 3, 6, 9, 0, 0, TimeSpan.Zero), dune.PublishDate);
    }

    [Fact]
    public void Parses_alternative_attribute_names_and_dates_with_zone_abbreviations()
    {
        var feed = Parse(Fixture("newznab_attrs.xml"));

        var starWars = feed.Releases[1];
        Assert.Equal("bbb222", starWars.Guid);
        Assert.Equal(11, starWars.TmdbId);
        Assert.Equal("tt0076759", starWars.ImdbId);
        Assert.Equal(2_000_000_000, starWars.Size); // enclosure length when there is no size attribute
        Assert.Null(starWars.InfoUrl);
        Assert.Null(starWars.Grabs);

        // "EST" is dropped and the time taken as UTC, as Radarr does.
        Assert.Equal(new DateTimeOffset(2012, 2, 27, 11, 9, 39, TimeSpan.Zero), starWars.PublishDate);
    }

    [Fact]
    public void Survives_broken_fields()
    {
        var feed = Parse(Fixture("newznab_attrs.xml"));

        var breakingBad = feed.Releases[2];
        Assert.Equal(81189, breakingBad.TvdbId);
        Assert.Equal("tt0903747", breakingBad.ImdbId);
        Assert.Equal(0, breakingBad.TmdbId);
        Assert.Null(breakingBad.Grabs);
        Assert.Equal(default, breakingBad.PublishDate);
        Assert.Empty(breakingBad.Categories);
    }

    [Fact]
    public void Skips_items_without_title_or_download_link_and_uses_link_as_fallback()
    {
        var feed = Parse(Fixture("newznab_attrs.xml"));

        Assert.Equal(4, feed.Releases.Count);
        Assert.DoesNotContain(feed.Releases, r => r.Guid is "no-title" or "no-link");

        var linkOnly = feed.Releases[3];
        Assert.Equal("https://indexer.example/getnzb/linkonly.nzb", linkOnly.DownloadUrl);
        Assert.Equal(linkOnly.DownloadUrl, linkOnly.Guid); // no guid: the download link keeps deduplication working
        Assert.Equal(new DateTimeOffset(2021, 1, 5, 8, 0, 0, TimeSpan.Zero), linkOnly.PublishDate);
    }

    [Fact]
    public void Resolves_relative_urls_against_the_request()
    {
        var feed = Parse(Fixture("relative_urls.xml"), new Uri("http://indexer.local/api?t=search"));

        var release = Assert.Single(feed.Releases);
        Assert.Equal("Real.Time.With.Bill.Maher.2015.08.07.720p.HDTV.x264-BATV", release.Title);
        Assert.Equal("http://indexer.local/getnzb/123.nzb&i=123&r=123", release.DownloadUrl);
        Assert.Equal("http://indexer.local/details/123", release.InfoUrl);
        Assert.Equal(1_230_000_000, release.Size);
        Assert.Equal(new DateTimeOffset(2015, 8, 8, 13, 27, 16, TimeSpan.Zero), release.PublishDate);
    }

    [Fact]
    public void Ignores_torznab_feeds()
    {
        var feed = Parse(Fixture("torznab_feed.xml"));

        Assert.Empty(feed.Releases);
        Assert.Equal(1, feed.ItemCount);
    }

    [Fact]
    public void Prefers_the_nzb_enclosure()
    {
        var xml = """
            <rss><channel><item><title>A.Movie.2020-GRP</title>
              <enclosure url="https://indexer.example/a.torrent" type="application/x-bittorrent" length="1" />
              <enclosure url="https://indexer.example/a.nzb" type="application/x-nzb" length="2" />
            </item></channel></rss>
            """;

        var release = Assert.Single(Parse(xml).Releases);

        Assert.Equal("https://indexer.example/a.nzb", release.DownloadUrl);
        Assert.Equal(2, release.Size);
    }

    [Fact]
    public void Cleans_html_entities_and_invalid_characters()
    {
        var xml = "﻿  <rss><channel><item><title>Caf&eacute;.Society.2016.720p&nbsp;BluRay-GRP\u0001</title>"
            + "<link>https://indexer.example/a.nzb</link><unknown>&bogus;</unknown></item></channel></rss>";

        var release = Assert.Single(Parse(xml).Releases);

        Assert.Equal("Café.Society.2016.720p BluRay-GRP", release.Title);
    }

    [Fact]
    public void Feed_without_channel_is_empty() =>
        Assert.Empty(Parse("<rss version=\"2.0\" />").Releases);

    [Theory]
    [InlineData("Mon, 27 Feb 2012 11:09:39 -0500", "2012-02-27T16:09:39+00:00")]
    [InlineData("Sat, 31 Aug 2024 12:28:40 +0300", "2024-08-31T09:28:40+00:00")]
    [InlineData("2024-08-31T12:28:40Z", "2024-08-31T12:28:40+00:00")]
    [InlineData("Tue, 05 Jan 2021 08:00:00 GMT", "2021-01-05T08:00:00+00:00")]
    [InlineData("Tue, 05 Jan 2021 08:00:00 PST", "2021-01-05T08:00:00+00:00")]
    public void Parses_dates(string input, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), NewznabRssParser.ParseDate(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    public void Unparseable_dates_are_null(string? input) =>
        Assert.Null(NewznabRssParser.ParseDate(input));

    [Theory]
    [InlineData("0076759", "tt0076759")]
    [InlineData("76759", "tt0076759")]
    [InlineData("tt0076759", "tt0076759")]
    [InlineData("0", null)]
    public void Normalises_imdb_ids(string input, string? expected) =>
        Assert.Equal(expected, NewznabRssParser.NormalizeImdbId(input));
}
