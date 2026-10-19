// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Prowlarr (https://github.com/Prowlarr/Prowlarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Indexers.Newznab;
using StreamZH.Indexers.Torznab;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

public class TorznabParserTests
{
    private static readonly Uri RequestUri = new("http://jackett.example:9117/api/v2.0/indexers/exampletracker/results/torznab/api?t=search");

    private static NewznabFeed Parse(string fixture) => TorznabRssParser.Parse(NewznabRssParser.Load(Fixture(fixture)), RequestUri);

    [Fact]
    public void Jackett_item_with_every_torznab_attribute()
    {
        var feed = Parse("torznab_jackett.xml");

        Assert.Equal(6, feed.ItemCount);
        var release = feed.Releases[0];
        Assert.Equal("Some.Movie.2020.1080p.BluRay.x264-GRP", release.Title);
        Assert.Equal("https://tracker.example.org/details/1001", release.Guid);
        Assert.Equal("https://tracker.example.org/details/1001", release.InfoUrl);
        Assert.StartsWith("http://jackett.example:9117/dl/exampletracker/?jackett_apikey=", release.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal(8589934592, release.Size);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero), release.PublishDate);
        Assert.Equal([2000, 2040, 100001], release.Categories);
        Assert.Equal((603, "tt0133093", 120), (release.TmdbId, release.ImdbId, release.Grabs));
        Assert.Equal(DownloadProtocol.Torrent, release.Protocol);
        Assert.Equal((57, 61), (release.Seeders!.Value, release.Peers!.Value));
        Assert.Equal("9c1e8039dd32f16e7d82f60e20b7e522ef490757", release.InfoHash);
        Assert.StartsWith("magnet:?xt=urn:btih:9C1E8039", release.MagnetUrl, StringComparison.Ordinal);
        Assert.Equal((0d, 1d), (release.DownloadVolumeFactor!.Value, release.UploadVolumeFactor!.Value));
        Assert.Equal((1d, 172800L), (release.MinimumRatio!.Value, release.MinimumSeedTimeSeconds!.Value));
        Assert.Equal(IndexerFlagBits.Freeleech, release.IndexerFlags);
    }

    [Fact]
    public void Magnet_only_release_takes_the_hash_from_the_base32_magnet_and_sums_seeders_and_leechers()
    {
        var release = Parse("torznab_jackett.xml").Releases[1];

        Assert.StartsWith("magnet:?xt=urn:btih:QQIZAATI", release.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal(release.DownloadUrl, release.MagnetUrl);
        Assert.Equal("8411900268d72084dc798ada753204d2f6a82c2e", release.InfoHash);
        Assert.Null(release.InfoUrl);
        Assert.Equal((3, 12), (release.Seeders!.Value, release.Peers!.Value));
        Assert.Equal(
            IndexerFlagBits.Halfleech | IndexerFlagBits.DoubleUpload | IndexerFlagBits.Internal | IndexerFlagBits.Scene,
            release.IndexerFlags);
    }

    [Fact]
    public void Info_hash_only_release_gets_a_magnet_link_without_trackers()
    {
        var release = Parse("torznab_jackett.xml").Releases[2];

        Assert.Equal("bc5b34242f910d6a779f6daf4dcce89a478f3baa", release.InfoHash);
        Assert.Equal("magnet:?xt=urn:btih:bc5b34242f910d6a779f6daf4dcce89a478f3baa&dn=Another.Movie.2019.2160p.UHD.BluRay.x265-GRP", release.DownloadUrl);
        Assert.Equal(release.DownloadUrl, release.MagnetUrl);
        Assert.Equal("bc5b34242f910d6a779f6daf4dcce89a478f3baa", release.Guid);
        Assert.Equal(53687091200, release.Size);
        Assert.Equal(12, release.Seeders);
        Assert.Null(release.Peers);
    }

    [Fact]
    public void Missing_seeders_and_size_stay_unknown_and_items_without_title_or_link_are_skipped()
    {
        var feed = Parse("torznab_jackett.xml");
        var release = feed.Releases[3];

        Assert.Equal("Old.Movie.1999.720p.HDTV.x264-GRP", release.Title);
        Assert.Equal("https://tracker.example.org/download/1004.torrent", release.DownloadUrl);
        Assert.Equal(0, release.Size);
        Assert.Null(release.Seeders);
        Assert.Null(release.Peers);
        Assert.Null(release.InfoHash);
        Assert.Null(release.MagnetUrl);
        Assert.Null(release.DownloadVolumeFactor);
        Assert.Equal(0, release.IndexerFlags);

        Assert.Equal(4, feed.Releases.Count);
        Assert.DoesNotContain(feed.Releases, r => r.Title.StartsWith("Nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void Prowlarr_item_with_freeleech_tag_and_tracker_minimums()
    {
        var release = Assert.Single(Parse("torznab_prowlarr.xml").Releases);

        Assert.StartsWith("http://prowlarr.example:9696/5/download?apikey=", release.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal((140, 152, 512), (release.Seeders!.Value, release.Peers!.Value, release.Grabs!.Value));
        Assert.Equal(16106127360, release.Size);
        Assert.Equal("283d77dd50745e82fd732406b405ca3e4d389ad9", release.InfoHash);
        Assert.Null(release.MagnetUrl);
        Assert.Equal((0.8, 259200L), (release.MinimumRatio!.Value, release.MinimumSeedTimeSeconds!.Value));
        Assert.Equal(IndexerFlagBits.Freeleech | IndexerFlagBits.Internal, release.IndexerFlags);
        Assert.Equal(["freeleech", "internal"], IndexerFlagBits.ToNames(release.IndexerFlags));
    }

    [Fact]
    public void A_newznab_feed_added_as_torznab_gives_nothing_and_newznab_attributes_are_read_too()
    {
        Assert.Empty(TorznabRssParser.Parse(NewznabRssParser.Load(Feed(3)), RequestUri).Releases);

        var mixed = """
            <rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/"><channel>
            <newznab:response offset="0" total="1" />
            <item><title>Some.Movie.2020.1080p.WEB-DL-GRP</title><guid>x1</guid><link>/download/x1.torrent</link>
            <newznab:attr name="seeders" value="4" /><newznab:attr name="size" value="1000" /></item>
            </channel></rss>
            """;
        var feed = TorznabRssParser.Parse(NewznabRssParser.Load(mixed), RequestUri);
        var release = Assert.Single(feed.Releases);
        Assert.Equal(("http://jackett.example:9117/download/x1.torrent", 4, 1000L), (release.DownloadUrl, release.Seeders!.Value, release.Size));
        Assert.Equal(1, feed.Total);
    }

    [Fact]
    public void The_newznab_parser_still_ignores_torznab_feeds()
    {
        Assert.Empty(NewznabRssParser.Parse(NewznabRssParser.Load(Fixture("torznab_jackett.xml")), RequestUri).Releases);
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:9C1E8039DD32F16E7D82F60E20B7E522EF490757&dn=x", "9c1e8039dd32f16e7d82f60e20b7e522ef490757")]
    [InlineData("magnet:?dn=x&xt=urn:btih:TQPIAOO5GLYW47MC6YHCBN7FELXUSB2X", "9c1e8039dd32f16e7d82f60e20b7e522ef490757")]
    [InlineData("magnet:?xt=urn%3Abtih%3A9c1e8039dd32f16e7d82f60e20b7e522ef490757", "9c1e8039dd32f16e7d82f60e20b7e522ef490757")]
    [InlineData("magnet:?xt=urn:btmh:1220aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("magnet:?xt=urn:btih:nothex&dn=x", null)]
    [InlineData("https://tracker.example.org/1.torrent", null)]
    [InlineData(null, null)]
    public void Info_hash_from_magnet_links(string? magnet, string? expected) => Assert.Equal(expected, MagnetLinks.InfoHash(magnet));

    [Fact]
    public void Built_magnet_links_escape_the_name()
    {
        Assert.Equal("magnet:?xt=urn:btih:abc&dn=A%20B%26C&tr=udp%3A%2F%2Ftracker.example.org%3A1337",
            MagnetLinks.Build("ABC", "A B&C", ["udp://tracker.example.org:1337"]));
    }

    [Fact]
    public void Torrent_content_detection()
    {
        Assert.True(TorrentFiles.LooksLikeTorrent(TorznabClientTests.TinyTorrent()));
        Assert.False(TorrentFiles.LooksLikeTorrent("<html>nope</html>"u8));
        Assert.True(TorrentFiles.TryGetMagnet(" magnet:?xt=urn:btih:abc "u8, out var magnet));
        Assert.Equal("magnet:?xt=urn:btih:abc", magnet);
        Assert.False(TorrentFiles.TryGetMagnet(TorznabClientTests.TinyTorrent(), out _));
    }

    [Theory]
    [InlineData(1.0, 1.0, new string[0], 0)]
    [InlineData(0.0, 1.0, new string[0], IndexerFlagBits.Freeleech)]
    [InlineData(0.25, 1.0, new string[0], IndexerFlagBits.Freeleech75)]
    [InlineData(0.75, 1.0, new string[0], IndexerFlagBits.Freeleech25)]
    [InlineData(1.0, 2.0, new[] { "Nuked", "unknown" }, IndexerFlagBits.DoubleUpload | IndexerFlagBits.Nuked)]
    public void Flags_from_volume_factors_and_tags(double download, double upload, string[] tags, int expected) =>
        Assert.Equal(expected, IndexerFlagBits.FromTorznab(download, upload, tags));

    [Fact]
    public void Radarr_flag_bits_map_to_sonarrs()
    {
        Assert.Equal(1 | 8 | 16 | 128, IndexerFlagBits.ToSonarr(IndexerFlagBits.Freeleech | IndexerFlagBits.Internal | IndexerFlagBits.Scene | IndexerFlagBits.Nuked));
        Assert.Equal(0, IndexerFlagBits.ToSonarr(IndexerFlagBits.PtpGolden | IndexerFlagBits.PtpApproved));
    }
}
