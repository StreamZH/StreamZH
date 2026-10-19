// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only
// Portions based on Prowlarr (https://github.com/Prowlarr/Prowlarr), Copyright (c) Prowlarr contributors, GPL-3.0.

using System.Globalization;
using System.Text.Json.Serialization;
using StreamZH.Core.Downloads;
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
/// The indexers StreamZH knows: Prowlarr's Newznab presets plus a few more and Generic Newznab for everything else; since
/// 0.11 Prowlarr's Torznab presets and Generic Torznab (any tracker with a Torznab API, a Jackett or Prowlarr indexer).
/// </summary>
public static class IndexerCatalog
{
    public const string GenericNewznabKey = "generic-newznab";

    public const string GenericTorznabKey = "generic-torznab";

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
        Usenet("house-of-usenet", "House-of-Usenet", "https://house-of-usenet.com", "https://house-of-usenet.com", apiPath: "/api/v1/api", categories: AllCategoriesNoAdult),
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
        Usenet("treasure-maps", "Treasure Maps", "https://treasure-maps.com", "https://treasure-maps.com", categories: "2000,5000"),
        Usenet(GenericNewznabKey, "Generic Newznab", null, string.Empty, categories: string.Empty,
            description: "Any Usenet indexer that speaks the Newznab API; enter its URL and API key"),

        // Torrents (0.11): Prowlarr's Torznab presets.
        Torrent("animetosho-torznab", "AnimeTosho (Torrent)", "https://animetosho.org", "https://feed.animetosho.org", IndexerPrivacy.Public,
            categories: "2020,5070", description: "AnimeTosho (Torrent) is a public torrent indexer for anime"),
        Torrent("torrent-network", "Torrent Network", "https://tntracker.org", "https://tntracker.org", IndexerPrivacy.Private,
            apiPath: "/api/torznab/api", language: "de-DE", description: "Torrent Network (TN) is a German private tracker for TV, movies and general content"),
        Torrent(GenericTorznabKey, "Generic Torznab", null, string.Empty, IndexerPrivacy.Private, categories: string.Empty,
            description: "Any torrent indexer that speaks the Torznab API: a tracker's own API, or one indexer of Jackett or Prowlarr (its Torznab feed URL and API key)"),
    ];

    private static readonly Dictionary<string, IndexerCatalogEntry> ByKey = All.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    public static IndexerCatalogEntry? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var entry) ? entry : null;

    /// <summary>The entry an indexer was created from; older indexers without a key count as Generic Newznab (or Generic Torznab).</summary>
    public static IndexerCatalogEntry? For(IndexerDefinition indexer)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        return Find(indexer.DefinitionKey ?? GenericKey(indexer.Protocol == DownloadProtocol.Torrent ? IndexerProtocol.Torrent : IndexerProtocol.Usenet));
    }

    /// <summary>"generic-newznab" or "generic-torznab".</summary>
    public static string GenericKey(IndexerProtocol protocol) => protocol == IndexerProtocol.Torrent ? GenericTorznabKey : GenericNewznabKey;

    public static bool IsGeneric(string? key) =>
        string.Equals(key, GenericNewznabKey, StringComparison.OrdinalIgnoreCase) || string.Equals(key, GenericTorznabKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A catalog entry of the protocol with the same name (case-insensitive) or the same API host; Generic Newznab (or
    /// Generic Torznab) otherwise.
    /// </summary>
    public static IndexerCatalogEntry Match(string? name, string? baseUrl, IndexerProtocol protocol = IndexerProtocol.Usenet)
    {
        var candidates = All.Where(e => e.Protocol == protocol && !IsGeneric(e.Key)).ToList();
        var byName = candidates.FirstOrDefault(e => string.Equals(e.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var url))
        {
            var byHost = candidates.FirstOrDefault(e => e.BaseUrl.Length > 0
                && Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var known)
                && string.Equals(known.Host, url.Host, StringComparison.OrdinalIgnoreCase));
            if (byHost is not null)
            {
                return byHost;
            }
        }

        return ByKey[GenericKey(protocol)];
    }

    private static IndexerCatalogEntry Torrent(
        string key, string name, string? siteUrl, string baseUrl, IndexerPrivacy privacy, string apiPath = "/api",
        string categories = "2000,5000", string language = "en-US", string? description = null) =>
        new(
            key,
            name,
            IndexerDefinition.Torznab,
            IndexerProtocol.Torrent,
            language,
            privacy,
            description ?? $"{name} is a torrent indexer",
            siteUrl,
            baseUrl,
            apiPath,
            [.. categories.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => int.Parse(c, CultureInfo.InvariantCulture))],
            Supported: true,
            UnsupportedReason: null);

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
