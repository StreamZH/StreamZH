// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using StreamZH.Core.Media;

namespace StreamZH.Library.ImportLists;

/// <summary>The movies or series of another StreamZH (its /api/v1/movies or /api/v1/series with the API key).</summary>
public sealed class StreamZhListSource(IHttpClientFactory httpFactory) : IImportListSource
{
    private const string Service = "StreamZH";

    public IReadOnlyList<string> Implementations { get; } = [ImportListCatalog.StreamZh];

    public async Task<ImportListFetch> FetchAsync(ImportListContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var s = context.Settings;
        var baseUrl = (s.Text("url") ?? throw new ImportListException("StreamZH: the URL is missing")).TrimEnd('/');
        var path = context.MediaType == MediaType.Series ? "series" : "movies";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}/api/v1/{path}"));
        request.Headers.Add("X-Api-Key", s.Text("apiKey") ?? string.Empty);
        request.Headers.Accept.ParseAdd("application/json");
        var body = await ImportListHttp.SendAsync(httpFactory.CreateClient(ImportListHttp.ClientName), request, Service, cancellationToken);
        return new ImportListFetch(Parse(body, context.MediaType, s.Bool("onlyMonitored")));
    }

    public static IReadOnlyList<ImportListItem> Parse(string body, MediaType mediaType, bool onlyMonitored)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ImportListException("StreamZH: unexpected answer (is the URL right?)");
            }

            var items = new List<ImportListItem>();
            foreach (var m in document.RootElement.EnumerateArray())
            {
                if (onlyMonitored && !(m.TryGetProperty("monitored", out var mon) && mon.ValueKind == JsonValueKind.True))
                {
                    continue;
                }

                if (m.TryGetProperty("tmdbId", out var id) && id.TryGetInt32(out var tmdbId) && tmdbId > 0)
                {
                    var title = m.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    var year = m.TryGetProperty("year", out var y) && y.TryGetInt32(out var yy) ? yy : 0;
                    items.Add(new ImportListItem(mediaType, title, year, tmdbId));
                }
            }

            return items;
        }
        catch (JsonException ex)
        {
            throw new ImportListException("StreamZH: the answer is not JSON (is the URL right?)", ex);
        }
    }
}

/// <summary>
/// A generic RSS or Atom feed. Each item needs an id: a tmdb/tmdbid/imdb/tvdb element (any namespace), a TMDB or IMDb
/// address in link, guid or description, or a plain tt… id.
/// </summary>
public sealed partial class RssListSource(IHttpClientFactory httpFactory) : IImportListSource
{
    private const string Service = "RSS feed";

    public IReadOnlyList<string> Implementations { get; } = [ImportListCatalog.Rss];

    public async Task<ImportListFetch> FetchAsync(ImportListContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var url = context.Settings.Text("url") ?? throw new ImportListException("RSS feed: the URL is missing");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        var body = await ImportListHttp.SendAsync(httpFactory.CreateClient(ImportListHttp.ClientName), request, Service, cancellationToken);
        var items = Parse(body, context.MediaType);
        return new ImportListFetch([.. items.Take(context.Limit(500))]);
    }

    public static IReadOnlyList<ImportListItem> Parse(string body, MediaType mediaType)
    {
        XDocument document;
        try
        {
            // No DTDs and no external entities from a foreign feed.
            using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new ImportListException("RSS feed: not a valid feed", ex);
        }

        var entries = document.Descendants().Where(e => e.Name.LocalName is "item" or "entry").ToList();
        var items = new List<ImportListItem>();
        foreach (var entry in entries)
        {
            // Elements by name, also with their prefix ("tmdb:id" from xmlns:tmdb="…").
            string? Value(params string[] names) =>
                entry.Elements().FirstOrDefault(e => names.Contains(Name(e), StringComparer.OrdinalIgnoreCase) || names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase)) is { } el
                    ? (el.Value.Trim() is { Length: > 0 } v ? v : el.Attribute("href")?.Value)
                    : null;

            var text = string.Join(' ', new[] { Value("link"), Value("guid", "atom:id"), Value("description", "summary", "content") }.OfType<string>());
            var media = mediaType == MediaType.Series ? "tv" : "movie";
            int? tmdb = Number(Value("tmdb", "tmdbid", "tmdb_id", "tmdb:id"))
                ?? (TmdbLinkRegex().Match(text) is { Success: true } m && m.Groups["kind"].Value == media ? Number(m.Groups["id"].Value) : null);
            var imdb = ImportListHttp.ImdbId(Value("imdb", "imdbid", "imdb_id", "imdb:id")) ?? ImportListHttp.ImdbId(text);
            var tvdb = mediaType == MediaType.Series ? Number(Value("tvdb", "tvdbid", "tvdb_id", "tvdb:id")) : null;
            if (tmdb is null && imdb is null && tvdb is null)
            {
                continue;
            }

            var (title, year) = TitleAndYear(Value("title") ?? string.Empty);
            items.Add(new ImportListItem(mediaType, title, year, tmdb, imdb, tvdb));
        }

        return items;
    }

    /// <summary>"prefix:local" for elements in a prefixed namespace, else the local name; Atom's own id counts as "atom:id".</summary>
    private static string Name(XElement element)
    {
        if (element.Name.Namespace == XNamespace.None)
        {
            return element.Name.LocalName;
        }

        if (element.Name.NamespaceName == "http://www.w3.org/2005/Atom")
        {
            return $"atom:{element.Name.LocalName}";
        }

        return element.GetPrefixOfNamespace(element.Name.Namespace) is { Length: > 0 } prefix ? $"{prefix}:{element.Name.LocalName}" : element.Name.LocalName;
    }

    private static int? Number(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    /// <summary>"Dune (2021)" → ("Dune", 2021).</summary>
    private static (string Title, int Year) TitleAndYear(string title)
    {
        var match = TitleYearRegex().Match(title);
        return match.Success ? (match.Groups["title"].Value.Trim(), int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture)) : (title.Trim(), 0);
    }

    [GeneratedRegex(@"themoviedb\.org/(?<kind>movie|tv)/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbLinkRegex();

    [GeneratedRegex(@"^(?<title>.+?)\s*\((?<year>(19|20|21)\d{2})\)\s*$")]
    private static partial Regex TitleYearRegex();
}
