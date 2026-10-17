// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using StreamZH.Core.Indexers;

namespace StreamZH.Indexers.Newznab;

/// <summary>One parsed page of a Newznab feed.</summary>
/// <param name="ItemCount">Items in the feed before any were skipped; decides whether another page is worth asking for.</param>
/// <param name="Total">The indexer's total hit count from &lt;newznab:response total=".."/&gt;, if sent.</param>
public sealed record NewznabFeed(IReadOnlyList<ReleaseInfo> Releases, int ItemCount, int? Offset, int? Total);

/// <summary>A Newznab &lt;error code=".." description=".."/&gt; answer.</summary>
public sealed record NewznabError(int Code, string Description);

/// <summary>Parses Newznab RSS feeds (search results) like Radarr's and Sonarr's NewznabRssParser.</summary>
public static partial class NewznabRssParser
{
    public const string NzbMimeType = "application/x-nzb";

    public static readonly XNamespace Ns = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    private static readonly string[] TorrentMimeTypes = ["application/x-bittorrent", "application/x-bittorrent;x-scheme-handler/magnet"];

    /// <summary>Loads XML the way Radarr does: HTML entities and characters that are invalid in XML are cleaned up first.</summary>
    /// <exception cref="XmlException">The content is not XML.</exception>
    public static XDocument Load(string content)
    {
        var cleaned = InvalidXmlCharRegex().Replace(HtmlEntityRegex().Replace(content, ReplaceEntity), string.Empty)
            .TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(cleaned), settings);
        return XDocument.Load(reader);
    }

    /// <summary>The first &lt;error&gt; element of the document, or null.</summary>
    public static NewznabError? FindError(XDocument document)
    {
        var error = document.Descendants("error").FirstOrDefault();
        if (error is null)
        {
            return null;
        }

        var code = int.TryParse(error.Attribute("code")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 900;
        var description = error.Attribute("description")?.Value?.Trim();
        return new NewznabError(code, string.IsNullOrEmpty(description) ? error.Value.Trim() : description);
    }

    /// <summary>Reads the items of a feed. Relative links are resolved against <paramref name="requestUri"/>; items without title or download link are skipped.</summary>
    public static NewznabFeed Parse(XDocument document, Uri requestUri)
    {
        var channel = document.Root?.Element("channel");
        if (channel is null)
        {
            return new NewznabFeed([], 0, null, null);
        }

        var items = channel.Elements("item").ToList();
        var response = channel.Element(Ns + "response");
        var offset = ParseInt(response?.Attribute("offset")?.Value);
        var total = ParseInt(response?.Attribute("total")?.Value);

        // Radarr: a feed that only offers torrents is a Torznab feed added as Newznab; nothing in it is usable.
        var types = items.SelectMany(i => i.Elements("enclosure")).Select(e => e.Attribute("type")?.Value).OfType<string>().Distinct().ToList();
        if (types.Count > 0 && !types.Contains(NzbMimeType, StringComparer.OrdinalIgnoreCase)
            && types.Any(t => TorrentMimeTypes.Contains(t, StringComparer.OrdinalIgnoreCase)))
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

    /// <summary>Parses an RSS date; Newznab's own usenetdate attribute uses the same format.</summary>
    public static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (DateTimeOffset.TryParse(text, DateTimeFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal, out var date))
        {
            return date.ToUniversalTime();
        }

        // "Sat, 08 Aug 2015 14:27:16 EST": .NET does not know time zone abbreviations; Radarr drops them and assumes UTC.
        text = TimeZoneSuffixRegex().Replace(text, string.Empty);
        return DateTimeOffset.TryParse(text, DateTimeFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal, out date)
            ? date.ToUniversalTime()
            : null;
    }

    /// <summary>"tt0076759", "0076759", "76759" → "tt0076759"; null for missing or zero ids.</summary>
    public static string? NormalizeImdbId(string? value) =>
        NewznabRequestBuilder.ImdbDigits(value) is { } digits ? "tt" + digits : null;

    private static ReleaseInfo? ParseItem(XElement item, Uri requestUri)
    {
        var title = item.Element("title")?.Value.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var enclosure = PickEnclosure(item);
        var downloadUrl = ResolveUrl(enclosure?.Attribute("url")?.Value, requestUri) ?? ResolveUrl(item.Element("link")?.Value, requestUri);
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

        var infoUrl = ResolveUrl(comments, requestUri)
            ?? (Uri.TryCreate(guid, UriKind.Absolute, out var guidUrl) && guidUrl.Scheme is "http" or "https" ? guidUrl.AbsoluteUri : null);

        var size = ParseLong(Attribute(item, "size"));
        if (size is not > 0)
        {
            size = ParseLong(enclosure?.Attribute("length")?.Value);
        }

        return new ReleaseInfo
        {
            Guid = string.IsNullOrEmpty(guid) ? downloadUrl : guid,
            Title = title,
            DownloadUrl = downloadUrl,
            InfoUrl = infoUrl,
            Size = size is > 0 ? size.Value : 0,
            PublishDate = ParseDate(Attribute(item, "usenetdate")) ?? ParseDate(item.Element("pubDate")?.Value) ?? default,
            Categories = [.. Attributes(item, "category").Select(ParseInt).OfType<int>().Distinct()],
            Grabs = ParseInt(Attribute(item, "grabs")),
            TmdbId = PositiveInt(Attribute(item, "tmdbid") ?? Attribute(item, "tmdb")),
            TvdbId = PositiveInt(Attribute(item, "tvdbid")),
            ImdbId = NormalizeImdbId(Attribute(item, "imdb") ?? Attribute(item, "imdbid")),
        };
    }

    // Radarr prefers the enclosure with the NZB mime type; an enclosure without a type is taken as well.
    private static XElement? PickEnclosure(XElement item)
    {
        var enclosures = item.Elements("enclosure").Where(e => !string.IsNullOrWhiteSpace(e.Attribute("url")?.Value)).ToList();
        return enclosures.FirstOrDefault(e => string.Equals(e.Attribute("type")?.Value, NzbMimeType, StringComparison.OrdinalIgnoreCase))
            ?? enclosures.FirstOrDefault(e => string.IsNullOrWhiteSpace(e.Attribute("type")?.Value))
            ?? (enclosures.Count == 1 && !TorrentMimeTypes.Contains(enclosures[0].Attribute("type")?.Value, StringComparer.OrdinalIgnoreCase) ? enclosures[0] : null);
    }

    private static string? ResolveUrl(string? value, Uri requestUri) => NewznabUrls.Resolve(requestUri, value)?.AbsoluteUri;

    private static string? Attribute(XElement item, string name) => Attributes(item, name).FirstOrDefault();

    private static IEnumerable<string> Attributes(XElement item, string name) =>
        item.Elements(Ns + "attr")
            .Where(a => string.Equals(a.Attribute("name")?.Value, name, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Attribute("value")?.Value)
            .OfType<string>();

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static int PositiveInt(string? value) => ParseInt(value) is { } v && v > 0 ? v : 0;

    private static long? ParseLong(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static string ReplaceEntity(Match match)
    {
        var decoded = WebUtility.HtmlDecode(match.Value);
        return decoded != match.Value && decoded.Length == 1
            ? string.Concat("&#", ((int)decoded[0]).ToString(CultureInfo.InvariantCulture), ";")
            : "&amp;" + match.Value[1..];
    }

    // Named entities that XML itself knows stay as they are.
    [GeneratedRegex("&(?!(?:amp|lt|gt|quot|apos);)[a-z]+;", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlEntityRegex();

    [GeneratedRegex(@"[^\x09\x0A\x0D -퟿-�]")]
    private static partial Regex InvalidXmlCharRegex();

    [GeneratedRegex(@"\s[A-Z]{2,4}$")]
    private static partial Regex TimeZoneSuffixRegex();
}
