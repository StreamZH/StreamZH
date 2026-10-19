// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StreamZH.Core.Indexers;
using StreamZH.Core.Security;

namespace StreamZH.Indexers.Newznab;

/// <summary>Builds Newznab API URLs and keeps API keys out of anything that is shown or logged.</summary>
public static partial class NewznabUrls
{
    public const string Redacted = SecretUrls.Redacted;

    /// <summary>"https://indexer.example" + "/api" (trailing slashes removed, a leading slash added to the path).</summary>
    public static string ApiEndpoint(IndexerDefinition indexer)
    {
        var baseUrl = indexer.BaseUrl.Trim().TrimEnd('/');
        var path = indexer.ApiPath.Trim().TrimEnd('/');
        if (path.Length > 0 && path[0] != '/')
        {
            path = "/" + path;
        }

        return baseUrl + path;
    }

    /// <summary>
    /// One API call in Radarr's parameter order: t, cat, extended, apikey, offset, limit, then the search parameters
    /// (which must start with '&amp;' and be escaped already).
    /// </summary>
    public static Uri Build(IndexerDefinition indexer, string function, IReadOnlyCollection<int>? categories = null, int? offset = null, int? limit = null, string parameters = "")
    {
        var url = new StringBuilder(ApiEndpoint(indexer)).Append("?t=").Append(function);

        if (categories is not null)
        {
            // No categories (a free text search over everything): leave cat= out, an empty one confuses some indexers.
            if (categories.Count > 0)
            {
                url.Append("&cat=").Append(string.Join(',', categories.Distinct().Select(c => c.ToString(CultureInfo.InvariantCulture))));
            }

            url.Append("&extended=1");
        }

        if (!string.IsNullOrWhiteSpace(indexer.ApiKey))
        {
            url.Append("&apikey=").Append(Uri.EscapeDataString(indexer.ApiKey.Trim()));
        }

        if (offset is { } o && limit is { } l)
        {
            url.Append(CultureInfo.InvariantCulture, $"&offset={o}&limit={l}");
        }

        url.Append(parameters);

        // The indexer's "Additional parameters" go on every API call (caps, search, RSS), never on NZB downloads.
        url.Append(NewznabRequestBuilder.AdditionalParameters(indexer.AdditionalParameters));
        return new Uri(url.ToString(), UriKind.Absolute);
    }

    /// <summary>
    /// Resolves a link from a feed against <paramref name="baseUri"/> (Radarr resolves against the request URL); without a base only absolute links work.
    /// Only http(s) results count; "/path" is treated as relative (on Unix .NET would read it as a file path).
    /// </summary>
    public static Uri? Resolve(Uri? baseUri, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)
            && !(absolute.IsFile && !text.StartsWith("file:", StringComparison.OrdinalIgnoreCase)))
        {
            return absolute.Scheme is "http" or "https" ? absolute : null;
        }

        return baseUri is not null
            && Uri.TryCreate(text, UriKind.Relative, out var relative)
            && Uri.TryCreate(baseUri, relative, out var combined)
            && combined.Scheme is "http" or "https"
            ? combined
            : null;
    }

    /// <summary>
    /// Adds the API key to a download link when it points to the indexer's host and carries no key yet
    /// (Newznab links usually have it already as "r=").
    /// </summary>
    public static Uri WithApiKeyForIndexerHost(IndexerDefinition indexer, Uri url)
    {
        var apiKey = indexer.ApiKey.Trim();
        if (apiKey.Length == 0
            || !Uri.TryCreate(ApiEndpoint(indexer), UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Host, url.Host, StringComparison.OrdinalIgnoreCase)
            || ApiKeyParameterRegex().IsMatch(url.Query)
            || url.OriginalString.Contains(apiKey, StringComparison.Ordinal)
            || url.OriginalString.Contains(Uri.EscapeDataString(apiKey), StringComparison.Ordinal))
        {
            return url;
        }

        var builder = new UriBuilder(url);
        var query = builder.Query.TrimStart('?');
        builder.Query = (query.Length > 0 ? query + "&" : string.Empty) + "apikey=" + Uri.EscapeDataString(apiKey);
        return builder.Uri;
    }

    /// <summary>Removes API keys from text: well-known key parameters in URLs and any literal occurrence of <paramref name="apiKey"/>.</summary>
    public static string Redact(string? text, string? apiKey)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // 0.12: the shared list of secret parameters (also used for the history and release links).
        var result = SecretUrls.Redact(text);

        var key = apiKey?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            result = result.Replace(key, Redacted, StringComparison.Ordinal);
            var escaped = Uri.EscapeDataString(key);
            if (escaped != key)
            {
                result = result.Replace(escaped, Redacted, StringComparison.Ordinal);
            }
        }

        return result;
    }

    public static string Redact(Uri? url, string? apiKey) => Redact(url?.ToString(), apiKey);

    [GeneratedRegex(@"(?:^|[?&])apikey=", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyParameterRegex();
}
