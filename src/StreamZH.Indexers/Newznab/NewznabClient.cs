// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Core.Net;
using StreamZH.Indexers.Torznab;

namespace StreamZH.Indexers.Newznab;

/// <summary>
/// Newznab (Usenet) and Torznab (torrent) indexer client, ported from Radarr (movies) and Sonarr (TV): Torznab is the
/// same API with torrent attributes in the feed, so requests, paging, caps and errors are shared and only the feed
/// parser and the download differ. Stateless apart from the shared caps cache; the indexer definition comes with every call.
/// </summary>
public sealed class NewznabClient(
    HttpClient http,
    NewznabCapabilitiesCache capabilitiesCache,
    ILogger<NewznabClient>? logger = null,
    OutboundAddressGuard? guard = null,
    IConfiguredServiceHosts? configuredHosts = null) : IIndexerClient
{
    /// <summary>Pages per query; with 100 results per page this is Radarr's limit of 1000 results per query.</summary>
    public const int MaxPagesPerQuery = 10;

    public const int MaxResultsPerQuery = 1000;

    /// <summary>
    /// Pages per free text query (Search page). Fewer than for a library search: a broad term would otherwise spend
    /// ten API hits per indexer on results nobody scrolls to. Without a query (newest releases) only one page is read.
    /// </summary>
    public const int MaxPagesPerTextQuery = 3;

    public const long MaxNzbBytes = 50L * 1024 * 1024;

    public const long MaxApiResponseBytes = 16L * 1024 * 1024;

    private const string RssAccept = "application/rss+xml, text/rss+xml, application/xml, text/xml";
    private const string NzbAccept = "application/x-nzb, application/xml, text/xml, */*;q=0.8";
    private const string TorrentAccept = "application/x-bittorrent, */*;q=0.8";

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly OutboundAddressGuard _guard = guard ?? OutboundAddressGuard.Default;

    public async Task<IndexerCapabilities> GetCapabilitiesAsync(IndexerDefinition indexer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        var caps = await FetchCapabilitiesAsync(indexer, cancellationToken);
        capabilitiesCache.Set(indexer, caps, NewznabCapabilitiesCache.Lifetime);
        return caps.ToCapabilities();
    }

    public async Task<IReadOnlyList<ReleaseInfo>> SearchAsync(IndexerDefinition indexer, SearchCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(criteria);

        var caps = await GetCachedCapabilitiesAsync(indexer, cancellationToken);
        var plans = criteria switch
        {
            MovieSearchCriteria movie => NewznabRequestBuilder.ForMovie(movie, indexer.Categories, caps),
            EpisodeSearchCriteria episode => NewznabRequestBuilder.ForEpisodes(episode, indexer.TvCategories, caps),
            TextSearchCriteria text => NewznabRequestBuilder.ForText(text, TextDefaultCategories(indexer, text.Type), caps),
            _ => throw new ArgumentException($"Unsupported search criteria {criteria.GetType().Name}.", nameof(criteria)),
        };

        if (plans.Count == 0)
        {
            _logger.LogDebug("{Indexer} supports no search for {Criteria} with the configured categories", indexer.Name, criteria.GetType().Name);
            return [];
        }

        var maxPages = criteria switch
        {
            TextSearchCriteria { Query: var q } when string.IsNullOrWhiteSpace(q) => 1,
            TextSearchCriteria => MaxPagesPerTextQuery,
            _ => MaxPagesPerQuery,
        };

        var results = new List<ReleaseInfo>();
        foreach (var plan in plans)
        {
            foreach (var tier in plan.Tiers)
            {
                var found = false;
                foreach (var query in tier)
                {
                    var releases = await RunQueryAsync(indexer, query, caps.PageSize, maxPages, cancellationToken);
                    results.AddRange(releases);
                    found |= releases.Count > 0;
                }

                // Like Radarr's and Sonarr's request tiers: the title fallback only runs when the id search found nothing.
                if (found)
                {
                    break;
                }
            }
        }

        return Finish(indexer, results);
    }

    public async Task<IReadOnlyList<ReleaseInfo>> FetchRecentAsync(IndexerDefinition indexer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);

        List<int> categories = [.. indexer.Categories.Concat(indexer.TvCategories).Distinct()];
        if (categories.Count == 0)
        {
            return [];
        }

        var caps = await GetCachedCapabilitiesAsync(indexer, cancellationToken);
        var releases = await RunQueryAsync(indexer, NewznabRequestBuilder.Recent(categories), caps.PageSize, maxPages: 1, cancellationToken);
        return Finish(indexer, releases);
    }

    /// <summary>
    /// Usenet: the NZB file. Torznab: the .torrent file, or the magnet link as UTF-8 bytes when the release is magnet-only,
    /// the indexer redirects to a magnet link, or the .torrent cannot be fetched but the release has a magnet link
    /// (see <see cref="TorrentFiles.TryGetMagnet"/>).
    /// </summary>
    public async Task<byte[]> DownloadNzbAsync(IndexerDefinition indexer, ReleaseInfo release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(release);

        if (indexer.Protocol == DownloadProtocol.Torrent)
        {
            return await DownloadTorrentAsync(indexer, release, cancellationToken);
        }

        Uri.TryCreate(NewznabUrls.ApiEndpoint(indexer), UriKind.Absolute, out var endpoint);
        var url = NewznabUrls.Resolve(endpoint, release.DownloadUrl);
        if (url is null)
        {
            throw NewznabErrors.Create(indexer, $"{indexer.Name}: the release '{release.Title}' has no valid download link.");
        }

        url = NewznabUrls.WithApiKeyForIndexerHost(indexer, url);
        _logger.LogDebug("Downloading NZB from {Url}", NewznabUrls.Redact(url, indexer.ApiKey));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(NzbAccept);
        using var response = await SendAsync(indexer, request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadLimitedAsync(indexer, response.Content, MaxApiResponseBytes, cancellationToken);
            throw ErrorForStatus(indexer, response, errorBody, url);
        }

        var body = await ReadLimitedAsync(indexer, response.Content, MaxNzbBytes, cancellationToken);
        body = Gunzip(indexer, body);
        ValidateNzb(indexer, response, body, url);
        return body;
    }

    /// <summary>Radarr's Torznab download: follows redirects (also to a magnet link), validates the .torrent, falls back to the magnet link.</summary>
    private async Task<byte[]> DownloadTorrentAsync(IndexerDefinition indexer, ReleaseInfo release, CancellationToken cancellationToken)
    {
        if (MagnetLinks.IsMagnet(release.DownloadUrl))
        {
            return Encoding.UTF8.GetBytes(release.DownloadUrl.Trim());
        }

        Uri.TryCreate(NewznabUrls.ApiEndpoint(indexer), UriKind.Absolute, out var endpoint);
        var url = NewznabUrls.Resolve(endpoint, release.DownloadUrl);
        if (url is null)
        {
            return MagnetOr(release, NewznabErrors.Create(indexer, $"{indexer.Name}: the release '{release.Title}' has no valid download link."));
        }

        url = NewznabUrls.WithApiKeyForIndexerHost(indexer, url);
        _logger.LogDebug("Downloading torrent from {Url}", NewznabUrls.Redact(url, indexer.ApiKey));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(TorrentAccept);
            using var response = await SendAsync(indexer, request, cancellationToken);

            // The handler follows http(s) redirects itself; a redirect to a magnet link comes back as the 3xx answer.
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location
                && MagnetLinks.IsMagnet(location.OriginalString))
            {
                return Encoding.UTF8.GetBytes(location.OriginalString.Trim());
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await ReadLimitedAsync(indexer, response.Content, MaxApiResponseBytes, cancellationToken);
                throw ErrorForStatus(indexer, response, errorBody, url);
            }

            var body = await ReadLimitedAsync(indexer, response.Content, TorrentFiles.MaxTorrentBytes, cancellationToken);
            if (TorrentFiles.LooksLikeTorrent(body) || TorrentFiles.TryGetMagnet(body, out _))
            {
                return body;
            }

            if (!IsHtml(response, body) && body.Length > 0)
            {
                try
                {
                    if (NewznabRssParser.FindError(NewznabRssParser.Load(Decode(response, body))) is { } error)
                    {
                        throw NewznabErrors.FromError(indexer, error, HasApiKey(url), RetryAfter(response));
                    }
                }
                catch (XmlException)
                {
                    // Not XML: reported below.
                }
            }

            throw NewznabErrors.Create(indexer, IsHtml(response, body)
                ? $"{indexer.Name} answered with an HTML page instead of a .torrent file (blocked, logged out or download limit?)."
                : $"{indexer.Name} did not return a .torrent file.");
        }
        catch (IndexerException ex) when (!ex.IsAuthError && ex.RetryAfter is null && release.MagnetUrl is not null)
        {
            // Radarr: when the .torrent cannot be fetched, the magnet link still works.
            _logger.LogInformation("{Indexer}: .torrent of '{Title}' not available ({Message}), using the magnet link", indexer.Name, release.Title, ex.Message);
            return MagnetOr(release, ex);
        }
    }

    private static byte[] MagnetOr(ReleaseInfo release, IndexerException error) =>
        MagnetLinks.IsMagnet(release.MagnetUrl) ? Encoding.UTF8.GetBytes(release.MagnetUrl!.Trim()) : throw error;

    private async Task<NewznabCaps> FetchCapabilitiesAsync(IndexerDefinition indexer, CancellationToken cancellationToken)
    {
        var document = await GetXmlAsync(indexer, NewznabUrls.Build(indexer, "caps"), cancellationToken);
        try
        {
            return NewznabCaps.Parse(document);
        }
        catch (FormatException)
        {
            var api = indexer.Protocol == DownloadProtocol.Torrent ? "Torznab" : "Newznab";
            throw NewznabErrors.Create(indexer, $"{indexer.Name} did not return {api} capabilities. Check the URL and the API path.");
        }
    }

    /// <summary>Caps for a search: cached for 7 days; when they cannot be read, Radarr's defaults stand in (for an hour).</summary>
    private async Task<NewznabCaps> GetCachedCapabilitiesAsync(IndexerDefinition indexer, CancellationToken cancellationToken)
    {
        if (capabilitiesCache.Get(indexer) is { } cached)
        {
            return cached;
        }

        try
        {
            var caps = await FetchCapabilitiesAsync(indexer, cancellationToken);
            capabilitiesCache.Set(indexer, caps, NewznabCapabilitiesCache.Lifetime);
            return caps;
        }
        catch (IndexerException ex) when (!ex.IsAuthError && ex.RetryAfter is null)
        {
            // Wrong key and request limits would fail the search as well: those go to the caller instead.
            _logger.LogWarning("Could not read the capabilities of {Indexer}, using the defaults for now: {Message}", indexer.Name, ex.Message);
            capabilitiesCache.Set(indexer, NewznabCaps.Default, NewznabCapabilitiesCache.FallbackLifetime);
            return NewznabCaps.Default;
        }
    }

    /// <summary>Pages through one query: stops at a short page, at the indexer's total, or at the page/result limit.</summary>
    private async Task<List<ReleaseInfo>> RunQueryAsync(IndexerDefinition indexer, NewznabQuery query, int pageSize, int maxPages, CancellationToken cancellationToken)
    {
        var releases = new List<ReleaseInfo>();
        for (var page = 0; page < maxPages; page++)
        {
            var offset = page * pageSize;
            var url = NewznabUrls.Build(indexer, query.Function, query.Categories, offset, pageSize, query.Parameters);
            var document = await GetXmlAsync(indexer, url, cancellationToken);
            var feed = indexer.Protocol == DownloadProtocol.Torrent ? TorznabRssParser.Parse(document, url) : NewznabRssParser.Parse(document, url);
            releases.AddRange(feed.Releases);

            if (releases.Count >= MaxResultsPerQuery
                || feed.ItemCount < pageSize
                || (feed.Total is { } total && offset + feed.ItemCount >= total))
            {
                break;
            }
        }

        return releases;
    }

    /// <summary>GETs an API URL and returns the XML; HTTP errors, HTML pages, broken XML and &lt;error/&gt; answers become <see cref="IndexerException"/>s.</summary>
    private async Task<XDocument> GetXmlAsync(IndexerDefinition indexer, Uri url, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Requesting {Url}", NewznabUrls.Redact(url, indexer.ApiKey));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(RssAccept);
        using var response = await SendAsync(indexer, request, cancellationToken);
        var body = await ReadLimitedAsync(indexer, response.Content, MaxApiResponseBytes, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw ErrorForStatus(indexer, response, body, url);
        }

        if (IsHtml(response, body))
        {
            throw NewznabErrors.Create(indexer, $"{indexer.Name} answered with an HTML page instead of XML. The site may be blocked or down, or the URL is wrong.");
        }

        XDocument document;
        try
        {
            document = NewznabRssParser.Load(Decode(response, body));
        }
        catch (XmlException ex)
        {
            throw NewznabErrors.Create(indexer, $"{indexer.Name} returned invalid XML: {ex.Message}", inner: ex);
        }

        if (NewznabRssParser.FindError(document) is { } error)
        {
            throw NewznabErrors.FromError(indexer, error, HasApiKey(url), RetryAfter(response));
        }

        return document;
    }

    /// <summary>Redirects followed per request (Radarr's HttpClient follows them itself as well).</summary>
    public const int MaxRedirects = 5;

    /// <summary>
    /// Sends a GET and follows http(s) redirects itself (the handler's own redirects are off, see AddNewznabIndexers):
    /// .NET would try to follow a redirect to a magnet link too and fail, Torznab download links do exactly that. A
    /// redirect to another scheme (magnet:) or from https to http comes back as the 3xx answer.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(IndexerDefinition indexer, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(indexer, request, cancellationToken);
        var current = request.RequestUri!;
        for (var hop = 0; hop < MaxRedirects && IsRedirect(response) && response.Headers.Location is { } location; hop++)
        {
            var target = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (target.Scheme is not ("http" or "https") || (current.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp))
            {
                return response;
            }

            response.Dispose();
            var next = new HttpRequestMessage(HttpMethod.Get, target);
            foreach (var accept in request.Headers.Accept)
            {
                next.Headers.Accept.Add(accept);
            }

            current = target;
            response = await SendOnceAsync(indexer, next, cancellationToken);
        }

        return response;
    }

    /// <summary>The host and port of the indexer's configured URL; requests there are not restricted (0.12: another port is).</summary>
    private static string? IndexerHost(IndexerDefinition indexer) =>
        Uri.TryCreate(NewznabUrls.ApiEndpoint(indexer), UriKind.Absolute, out var endpoint) ? OutboundAddressGuard.HostAndPort(endpoint) : null;

    private static bool IsRedirect(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private async Task<HttpResponseMessage> SendOnceAsync(IndexerDefinition indexer, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            // Feed links and redirects to another host than the indexer's may not reach this computer, link-local or
            // cloud metadata addresses (checked here and again when the socket connects, see AddNewznabIndexers).
            if (IndexerHost(indexer) is { } trusted)
            {
                OutboundAddressGuard.Restrict(request, trusted, configuredHosts);
            }

            await _guard.CheckAsync(request, cancellationToken);
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException e) when ((e as BlockedDestinationException ?? e.InnerException as BlockedDestinationException) is { } ex)
        {
            _logger.LogWarning("{Indexer}: blocked a request to {Host}: {Message}", indexer.Name, request.RequestUri?.IdnHost, ex.Message);
            throw NewznabErrors.Create(indexer, $"{indexer.Name}: {ex.Message}", inner: ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw NewznabErrors.Create(indexer, $"{indexer.Name} did not answer in time.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw NewznabErrors.Create(indexer, $"{indexer.Name} is not reachable: {ex.Message}", inner: ex);
        }
    }

    private IndexerException ErrorForStatus(IndexerDefinition indexer, HttpResponseMessage response, byte[] body, Uri url)
    {
        var retryAfter = RetryAfter(response);

        // Radarr: an HTTP error that comes with a Newznab error document is reported as that error.
        if (body.Length > 0 && !IsHtml(response, body))
        {
            try
            {
                if (NewznabRssParser.FindError(NewznabRssParser.Load(Decode(response, body))) is { } error)
                {
                    return NewznabErrors.FromError(indexer, error, HasApiKey(url), retryAfter);
                }
            }
            catch (XmlException)
            {
                // Not XML: report the status code.
            }
        }

        return NewznabErrors.FromHttpStatus(indexer, response.StatusCode, retryAfter);
    }

    private void ValidateNzb(IndexerDefinition indexer, HttpResponseMessage response, byte[] body, Uri url)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                XmlResolver = null,
            };

            using var reader = XmlReader.Create(new MemoryStream(body, writable: false), settings);
            if (reader.MoveToContent() == XmlNodeType.Element)
            {
                if (string.Equals(reader.LocalName, "nzb", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (reader.LocalName == "error")
                {
                    var error = NewznabRssParser.FindError(XDocument.Load(reader.ReadSubtree()));
                    if (error is not null)
                    {
                        throw NewznabErrors.FromError(indexer, error, HasApiKey(url), RetryAfter(response));
                    }
                }
            }
        }
        catch (XmlException)
        {
            // Not XML at all: handled below.
        }

        if (IsHtml(response, body))
        {
            throw NewznabErrors.Create(indexer, $"{indexer.Name} answered with an HTML page instead of an NZB file (blocked, logged out or download limit?).");
        }

        throw NewznabErrors.Create(indexer, $"{indexer.Name} did not return an NZB file.");
    }

    private static async Task<byte[]> ReadLimitedAsync(IndexerDefinition indexer, HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            throw TooLarge(indexer, maxBytes);
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw TooLarge(indexer, maxBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Some indexers hand out .nzb.gz files without a Content-Encoding header.</summary>
    private static byte[] Gunzip(IndexerDefinition indexer, byte[] body)
    {
        if (body.Length < 2 || body[0] != 0x1f || body[1] != 0x8b)
        {
            return body;
        }

        try
        {
            using var gzip = new GZipStream(new MemoryStream(body, writable: false), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (output.Length + read > MaxNzbBytes)
                {
                    throw TooLarge(indexer, MaxNzbBytes);
                }

                output.Write(chunk, 0, read);
            }

            return output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw NewznabErrors.Create(indexer, $"{indexer.Name} returned a broken compressed NZB file.", inner: ex);
        }
    }

    private static IndexerException TooLarge(IndexerDefinition indexer, long maxBytes) =>
        NewznabErrors.Create(indexer, $"{indexer.Name} returned more than {maxBytes / (1024 * 1024)} MB.");

    private static string Decode(HttpResponseMessage response, byte[] body)
    {
        var encoding = Encoding.UTF8;
        if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // Unknown charset: stay with UTF-8.
            }
        }

        using var reader = new StreamReader(new MemoryStream(body, writable: false), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool IsHtml(HttpResponseMessage response, byte[] body)
    {
        if (response.Content.Headers.ContentType?.MediaType is { } mediaType && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var start = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 512)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return start.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) => RetryAfter(response, capabilitiesCache.Time.GetUtcNow());

    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - now : null);
        return delay > TimeSpan.Zero ? delay : null;
    }

    private static bool HasApiKey(Uri url) =>
        url.Query.Contains("apikey=", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<int> TextDefaultCategories(IndexerDefinition indexer, TextSearchType type) => type switch
    {
        TextSearchType.Movie => indexer.Categories,
        TextSearchType.Tv => indexer.TvCategories,
        _ => [],
    };

    private static List<ReleaseInfo> Finish(IndexerDefinition indexer, IEnumerable<ReleaseInfo> releases)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ReleaseInfo>();
        foreach (var release in releases)
        {
            if (seen.Add(release.Guid))
            {
                result.Add(release with { IndexerId = indexer.Id, Indexer = indexer.Name, IndexerPriority = indexer.Priority });
            }
        }

        return result;
    }
}
