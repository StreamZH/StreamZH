// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr), Sonarr (https://github.com/Sonarr/Sonarr) and Prowlarr (https://github.com/Prowlarr/Prowlarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Xml.Linq;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Indexers.Newznab;

namespace StreamZH.Indexers.Torznab;

/// <summary>
/// Parses Torznab feeds (Jackett, Prowlarr, trackers with a Torznab API) like Radarr's TorznabRssParser and
/// TorrentRssParser: the enclosure (or link) is the download URL and may be a magnet link; seeders, peers (or
/// seeders + leechers), info hash, magnet URL, volume factors, minimum ratio/seed time and tags come from the
/// torznab:attr elements (newznab:attr is accepted too, some servers use that namespace). A missing size, missing seeders
/// or a release with only an info hash still give a usable release.
/// </summary>
public static class TorznabRssParser
{
    public const string TorrentMimeType = "application/x-bittorrent";

    public static readonly XNamespace Ns = "http://torznab.com/schemas/2015/feed";

    private static readonly string[] UsenetMimeTypes = [NewznabRssParser.NzbMimeType, "application/x-nzb-compressed"];

    /// <summary>Reads the items of a feed; relative links are resolved against <paramref name="requestUri"/>.</summary>
    public static NewznabFeed Parse(XDocument document, Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(document);
        var channel = document.Root?.Element("channel");
        if (channel is null)
        {
            return new NewznabFeed([], 0, null, null);
        }

        var items = channel.Elements("item").ToList();
        var response = channel.Element(Ns + "response") ?? channel.Element(NewznabRssParser.Ns + "response");
        var offset = ParseInt(response?.Attribute("offset")?.Value);
        var total = ParseInt(response?.Attribute("total")?.Value);

        // Radarr's PostProcess: a feed with only NZB enclosures is a Newznab indexer added as Torznab; nothing in it is usable.
        var types = items.SelectMany(i => i.Elements("enclosure")).Select(e => e.Attribute("type")?.Value).OfType<string>().Distinct().ToList();
        if (types.Count > 0 && !types.Contains(TorrentMimeType, StringComparer.OrdinalIgnoreCase)
            && types.Any(t => UsenetMimeTypes.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            return new NewznabFeed([], items.Count, offset, total);
        }

        var releases = new List<ReleaseInfo>(items.Count);
        foreach (var item in items)
        {
            if (ParseItem(item, requestUri) is { } release)
            {
                releases.Add(release);
            }
        }

        return new NewznabFeed(releases, items.Count, offset, total);
    }

    private static ReleaseInfo? ParseItem(XElement item, Uri requestUri)
    {
        var title = item.Element("title")?.Value.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var magnet = FirstMagnet(Attribute(item, "magneturl"), EnclosureUrl(item), item.Element("link")?.Value);
        var infoHash = MagnetLinks.NormalizeInfoHash(Attribute(item, "infohash")) ?? MagnetLinks.InfoHash(magnet);

        // Radarr uses the enclosure URL; the link element when there is none. A magnet link is a valid download URL.
        var downloadUrl = ResolveDownload(EnclosureUrl(item), requestUri) ?? ResolveDownload(item.Element("link")?.Value, requestUri) ?? magnet;
        if (downloadUrl is null && infoHash is not null)
        {
            // Only an info hash (some trackers' RSS): a magnet link without trackers, DHT and PEX find the peers.
            magnet = MagnetLinks.Build(infoHash, title);
            downloadUrl = magnet;
        }

        if (downloadUrl is null)
        {
            return null;
        }

        var guid = item.Element("guid")?.Value.Trim();
        var comments = item.Element("comments")?.Value.Trim();
        if (comments is not null && comments.EndsWith("#comments", StringComparison.OrdinalIgnoreCase))
        {
            comments = comments[..^"#comments".Length];
        }

        var infoUrl = Resolve(comments, requestUri)
            ?? (Uri.TryCreate(guid, UriKind.Absolute, out var guidUrl) && guidUrl.Scheme is "http" or "https" ? guidUrl.AbsoluteUri : null);

        var seeders = ParseInt(Attribute(item, "seeders"));
        var leechers = ParseInt(Attribute(item, "leechers"));
        var peers = ParseInt(Attribute(item, "peers")) ?? (seeders is { } s && leechers is { } l ? s + l : null);
        var downloadFactor = ParseDouble(Attribute(item, "downloadvolumefactor"));
        var uploadFactor = ParseDouble(Attribute(item, "uploadvolumefactor"));

        return new ReleaseInfo
        {
            Guid = string.IsNullOrEmpty(guid) ? infoHash ?? downloadUrl : guid,
            Title = title,
            DownloadUrl = downloadUrl,
            InfoUrl = infoUrl,
            Size = Size(item),
            PublishDate = NewznabRssParser.ParseDate(item.Element("pubDate")?.Value) ?? default,
            Categories = [.. Attributes(item, "category").Concat(item.Elements("category").Select(c => c.Value)).Select(ParseInt).OfType<int>().Distinct()],
            Grabs = ParseInt(Attribute(item, "grabs")) ?? ParseInt(item.Element("grabs")?.Value),
            TmdbId = PositiveInt(Attribute(item, "tmdbid") ?? Attribute(item, "tmdb")),
            TvdbId = PositiveInt(Attribute(item, "tvdbid") ?? Attribute(item, "tvdb")),
            ImdbId = NewznabRssParser.NormalizeImdbId(Attribute(item, "imdb") ?? Attribute(item, "imdbid")),
            Protocol = DownloadProtocol.Torrent,
            Seeders = seeders is >= 0 ? seeders : null,
            Peers = peers is >= 0 ? peers : null,
            InfoHash = infoHash,
            MagnetUrl = magnet,
            DownloadVolumeFactor = downloadFactor is >= 0 ? downloadFactor : null,
            UploadVolumeFactor = uploadFactor is >= 0 ? uploadFactor : null,
            MinimumRatio = ParseDouble(Attribute(item, "minimumratio")) is > 0 and var ratio ? ratio : null,
            MinimumSeedTimeSeconds = ParseLong(Attribute(item, "minimumseedtime")) is > 0 and var seconds ? seconds : null,
            IndexerFlags = IndexerFlagBits.FromTorznab(downloadFactor ?? 1, uploadFactor ?? 1, Attributes(item, "tag")),
        };
    }

    /// <summary>The torznab size attribute, Jackett's &lt;size&gt; element, the enclosure length; 0 when unknown.</summary>
    private static long Size(XElement item)
    {
        foreach (var value in new[] { Attribute(item, "size"), item.Element("size")?.Value, item.Element("enclosure")?.Attribute("length")?.Value })
        {
            if (ParseLong(value) is > 0 and var size)
            {
                return size;
            }
        }

        return 0;
    }

    private static string? EnclosureUrl(XElement item)
    {
        var enclosures = item.Elements("enclosure").Where(e => !string.IsNullOrWhiteSpace(e.Attribute("url")?.Value)).ToList();
        var preferred = enclosures.FirstOrDefault(e => string.Equals(e.Attribute("type")?.Value, TorrentMimeType, StringComparison.OrdinalIgnoreCase))
            ?? enclosures.FirstOrDefault();
        return preferred?.Attribute("url")?.Value.Trim();
    }

    /// <summary>http(s) links resolved against the request; magnet links as they are; anything else null.</summary>
    private static string? ResolveDownload(string? value, Uri requestUri) =>
        MagnetLinks.IsMagnet(value) ? value!.Trim() : Resolve(value, requestUri);

    private static string? Resolve(string? value, Uri requestUri) => NewznabUrls.Resolve(requestUri, value)?.AbsoluteUri;

    private static string? FirstMagnet(params string?[] values) =>
        values.FirstOrDefault(MagnetLinks.IsMagnet)?.Trim();

    private static string? Attribute(XElement item, string name) => Attributes(item, name).FirstOrDefault();

    private static IEnumerable<string> Attributes(XElement item, string name) =>
        item.Elements()
            .Where(e => e.Name.LocalName == "attr" && (e.Name.Namespace == Ns || e.Name.Namespace == NewznabRssParser.Ns))
            .Where(a => string.Equals(a.Attribute("name")?.Value, name, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Attribute("value")?.Value)
            .OfType<string>();

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static int PositiveInt(string? value) => ParseInt(value) is { } v && v > 0 ? v : 0;

    private static long? ParseLong(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : null;
}
