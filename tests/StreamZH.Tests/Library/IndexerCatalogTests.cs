// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Indexers;
using StreamZH.Library.Indexers;

namespace StreamZH.Tests.Library;

public class IndexerCatalogTests
{
    [Fact]
    public void Keys_and_names_are_unique()
    {
        Assert.Equal(IndexerCatalog.All.Count, IndexerCatalog.All.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(IndexerCatalog.All.Count, IndexerCatalog.All.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(IndexerCatalog.All, e => Assert.Matches("^[a-z0-9-]+$", e.Key));
    }

    [Fact]
    public void Contains_prowlarrs_presets_plus_house_of_usenet_treasure_maps_and_generic()
    {
        string[] expected =
        [
            "abNZB", "altHUB", "AnimeTosho (Usenet)", "DOGnzb", "DrunkenSlug", "GingaDADDY", "Miatrix", "Newz69", "NinjaCentral", "Nzb.life",
            "NZBCat", "NZBFinder", "NZBgeek", "NzbNoob", "NZBNDX", "NzbPlanet", "NZBStars", "Tabula Rasa", "House-of-Usenet", "Treasure Maps",
            "Generic Newznab",
        ];
        var usenet = IndexerCatalog.All.Where(e => e.Protocol == IndexerProtocol.Usenet).ToList();
        Assert.Equal(expected.Order(StringComparer.Ordinal), usenet.Select(e => e.Name).Order(StringComparer.Ordinal));

        // 0.11: Prowlarr's Torznab presets and Generic Torznab.
        Assert.Equal(
            ["AnimeTosho (Torrent)", "Torrent Network", "Generic Torznab"],
            IndexerCatalog.All.Where(e => e.Protocol == IndexerProtocol.Torrent).Select(e => e.Name));
        Assert.All(IndexerCatalog.All.Where(e => e.Protocol == IndexerProtocol.Torrent), e => Assert.Equal(IndexerDefinition.Torznab, e.Implementation));

        Assert.All(usenet, e =>
        {
            Assert.Equal(IndexerDefinition.Newznab, e.Implementation);
            Assert.Equal(IndexerProtocol.Usenet, e.Protocol);
            Assert.Equal(IndexerPrivacy.Private, e.Privacy);
            Assert.Equal("en-US", e.Language);
            Assert.True(e.Supported);
            Assert.Null(e.UnsupportedReason);
            Assert.False(string.IsNullOrWhiteSpace(e.Description));
        });
    }

    [Fact]
    public void Generic_newznab_has_no_url()
    {
        var generic = IndexerCatalog.Find(IndexerCatalog.GenericNewznabKey)!;
        Assert.Equal("Generic Newznab", generic.Name);
        Assert.Equal(string.Empty, generic.BaseUrl);
        Assert.Null(generic.SiteUrl);
        Assert.Equal("/api", generic.ApiPath);
        Assert.Same(IndexerCatalog.All.Last(e => e.Protocol == IndexerProtocol.Usenet), generic);
    }

    [Fact]
    public void Presets_have_prowlarrs_urls_paths_and_categories()
    {
        var house = IndexerCatalog.Find("house-of-usenet")!;
        Assert.Equal("https://house-of-usenet.com", house.BaseUrl);
        Assert.Equal("/api/v1/api", house.ApiPath);

        Assert.Equal("/api/v1/api", IndexerCatalog.Find("tabula-rasa")!.ApiPath);
        Assert.Equal("https://treasure-maps.com", IndexerCatalog.Find("treasure-maps")!.BaseUrl);
        Assert.Equal("/api", IndexerCatalog.Find("treasure-maps")!.ApiPath);

        var geek = IndexerCatalog.Find("NZBgeek")!;
        Assert.Equal("https://api.nzbgeek.info", geek.BaseUrl);
        Assert.Equal("https://nzbgeek.info", geek.SiteUrl);
        Assert.Equal([1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000], geek.Categories);
        Assert.Equal("NZBgeek is a private Usenet indexer", geek.Description);

        Assert.Equal([2020, 5070], IndexerCatalog.Find("animetosho-usenet")!.Categories);
        Assert.Empty(IndexerCatalog.Find("gingadaddy")!.Categories);
        Assert.All(IndexerCatalog.All.Where(e => !IndexerCatalog.IsGeneric(e.Key)), e => Assert.StartsWith("https://", e.BaseUrl, StringComparison.Ordinal));
    }

    [Fact]
    public void Matches_by_name_then_host_else_generic()
    {
        Assert.Equal("nzbgeek", IndexerCatalog.Match("nzbgeek", null).Key);
        Assert.Equal("dognzb", IndexerCatalog.Match("My dog", "https://api.dognzb.cr/").Key);
        Assert.Equal(IndexerCatalog.GenericNewznabKey, IndexerCatalog.Match("Other", "https://indexer.example").Key);
        Assert.Equal(IndexerCatalog.GenericNewznabKey, IndexerCatalog.For(new IndexerDefinition(1, "x", "Newznab", "", "/api", "", [2000], [], 25, true, true, true))!.Key);
    }
}
