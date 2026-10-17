// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only
// Portions based on Prowlarr (https://github.com/Prowlarr/Prowlarr), Copyright (c) Prowlarr contributors, GPL-3.0.

using System.Globalization;
using System.Text.Json.Serialization;
using StreamZH.Core.Indexers;

namespace StreamZH.Library.Indexers;

[JsonConverter(typeof(JsonStringEnumConverter<IndexerProtocol>))]
public enum IndexerProtocol
{
    [JsonStringEnumMemberName("usenet")]
    Usenet,

    [JsonStringEnumMemberName("torrent")]
    Torrent,
}

[JsonConverter(typeof(JsonStringEnumConverter<IndexerPrivacy>))]
public enum IndexerPrivacy
{
    [JsonStringEnumMemberName("private")]
    Private,

    [JsonStringEnumMemberName("semiPrivate")]
    SemiPrivate,

    [JsonStringEnumMemberName("public")]
    Public,
}

/// <summary>
/// A known indexer to start from (Prowlarr's "Add Indexer" list). <see cref="BaseUrl"/> is empty for Generic Newznab;
/// <see cref="Categories"/> are the top-level Newznab categories the site offers.
/// </summary>
public sealed record IndexerCatalogEntry(
    string Key,
    string Name,
    string Implementation,
    IndexerProtocol Protocol,
    string Language,
    IndexerPrivacy Privacy,
    string Description,
    string? SiteUrl,
    string BaseUrl,
    string ApiPath,
    IReadOnlyList<int> Categories,
    bool Supported,
    string? UnsupportedReason);

/// <summary>
/// The indexers StreamZH knows: Prowlarr's Newznab presets plus a few more, and Generic Newznab for everything else.
/// Torrent definitions arrive with the torrent engine (1.0).
/// </summary>
public static class IndexerCatalog
{
    public const string GenericNewznabKey = "generic-newznab";

    private const string AllCategoriesNoAdult = "1000,2000,3000,4000,5000,6000,7000";

    public static readonly IReadOnlyList<IndexerCatalogEntry> All =
    [
        Usenet("abnzb", "abNZB", "https://abnzb.com", "https://abnzb.com", categories: "2000,3000,4000,5000,6000,7000,8000"),
        Usenet("althub", "altHUB", "https://althub.co.za", "https://api.althub.co.za", categories: "2000,3000,4000,5000,7000"),
        Usenet("animetosho-usenet", "AnimeTosho (Usenet)", "https://animetosho.org", "https://feed.animetosho.org", categories: "2020,5070",
            description: "AnimeTosho (Usenet) is a Usenet indexer for anime"),
        Usenet("dognzb", "DOGnzb", "https://dognzb.cr", "https://api.dognzb.cr"),
        Usenet("drunkenslug", "DrunkenSlug", "https://drunkenslug.com", "https://drunkenslug.com", categories: AllCategoriesNoAdult),
        Usenet("gingadaddy", "GingaDADDY", "https://www.gingadaddy.com", "https://www.gingadaddy.com", categories: string.Empty),
        Usenet("house-of-usenet", "House-of-Usenet", "https://house-of-usenet.com", "https://house-of-usenet.com", apiPath: "/api/v1/api", categories: string.Empty),
        Usenet("miatrix", "Miatrix", "https://www.miatrix.com", "https://www.miatrix.com"),
        Usenet("newz69", "Newz69", "https://newz69.keagaming.com", "https://newz69.keagaming.com"),
        Usenet("ninjacentral", "NinjaCentral", "https://ninjacentral.co.za", "https://ninjacentral.co.za"),
        Usenet("nzb-life", "Nzb.life", "https://nzb.life", "https://api.nzb.life"),
        Usenet("nzbcat", "NZBCat", "https://nzb.cat", "https://nzb.cat", categories: AllCategoriesNoAdult),
        Usenet("nzbfinder", "NZBFinder", "https://nzbfinder.ws", "https://nzbfinder.ws", categories: "2000,3000,5000,6000,7000"),
        Usenet("nzbgeek", "NZBgeek", "https://nzbgeek.info", "https://api.nzbgeek.info"),
        Usenet("nzbndx", "NZBNDX", "https://www.nzbndx.com", "https://www.nzbndx.com"),
        Usenet("nzbnoob", "NzbNoob", "https://www.nzbnoob.com", "https://www.nzbnoob.com", categories: "2000,3000,4000,5000,6000,7000,8000"),
        Usenet("nzbplanet", "NzbPlanet", "https://nzbplanet.net", "https://api.nzbplanet.net"),
        Usenet("nzbstars", "NZBStars", "https://nzbstars.com", "https://nzbstars.com", categories: AllCategoriesNoAdult),
        Usenet("tabula-rasa", "Tabula Rasa", "https://www.tabula-rasa.pw", "https://www.tabula-rasa.pw", apiPath: "/api/v1/api", categories: AllCategoriesNoAdult),
        Usenet("treasure-maps", "Treasure Maps", "https://treasure-maps.com", "https://treasure-maps.com", categories: string.Empty),
        Usenet(GenericNewznabKey, "Generic Newznab", null, string.Empty, categories: string.Empty,
            description: "Any Usenet indexer that speaks the Newznab API; enter its URL and API key"),
    ];

    private static readonly Dictionary<string, IndexerCatalogEntry> ByKey = All.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    public static IndexerCatalogEntry? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var entry) ? entry : null;

    /// <summary>The entry an indexer was created from; older indexers without a key count as Generic Newznab.</summary>
    public static IndexerCatalogEntry? For(IndexerDefinition indexer) => Find(indexer.DefinitionKey ?? GenericNewznabKey);

    /// <summary>A catalog entry with the same name (case-insensitive) or the same API host, Generic Newznab otherwise.</summary>
    public static IndexerCatalogEntry Match(string? name, string? baseUrl)
    {
        var byName = All.FirstOrDefault(e => e.Key != GenericNewznabKey && string.Equals(e.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var url))
        {
            var byHost = All.FirstOrDefault(e => e.BaseUrl.Length > 0
                && Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var known)
                && string.Equals(known.Host, url.Host, StringComparison.OrdinalIgnoreCase));
            if (byHost is not null)
            {
                return byHost;
            }
        }

        return ByKey[GenericNewznabKey];
    }

    private static IndexerCatalogEntry Usenet(
        string key, string name, string? siteUrl, string baseUrl, string apiPath = "/api", string categories = "1000,2000,3000,4000,5000,6000,7000,8000", string? description = null) =>
        new(
            key,
            name,
            IndexerDefinition.Newznab,
            IndexerProtocol.Usenet,
            "en-US",
            IndexerPrivacy.Private,
            description ?? $"{name} is a private Usenet indexer",
            siteUrl,
            baseUrl,
            apiPath,
            [.. categories.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => int.Parse(c, CultureInfo.InvariantCulture))],
            Supported: true,
            UnsupportedReason: null);
}
