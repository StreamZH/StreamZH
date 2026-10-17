// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using StreamZH.Core.Indexers;

namespace StreamZH.Indexers.Newznab;

/// <summary>Turns Newznab error answers and HTTP failures into <see cref="IndexerException"/>s without leaking the API key.</summary>
public static class NewznabErrors
{
    /// <summary>Code used for auth failures that come without a Newznab code (HTTP 401/403, missing key), so that <see cref="IndexerException.IsAuthError"/> holds.</summary>
    public const int AuthErrorCode = 100;

    /// <summary>Back-off for request limits without a Retry-After header (Radarr's minimum back-off).</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromHours(1);

    /// <summary>Maps a Newznab &lt;error/&gt; like Radarr's CheckError.</summary>
    public static IndexerException FromError(IndexerDefinition indexer, NewznabError error, bool requestHadApiKey, TimeSpan? retryAfter)
    {
        var description = error.Description;

        if (error.Code is >= 100 and <= 199)
        {
            return Create(indexer, $"{indexer.Name} rejected the API key: {description} (code {Code(error)}).", error.Code);
        }

        if (!requestHadApiKey
            && (string.Equals(description, "Missing parameter", StringComparison.OrdinalIgnoreCase)
                || description.Contains("apikey", StringComparison.OrdinalIgnoreCase)))
        {
            return Create(indexer, $"{indexer.Name} requires an API key.", AuthErrorCode);
        }

        if (IsRequestLimit(error))
        {
            return Create(indexer, $"{indexer.Name}: request limit reached ({description}).", error.Code, retryAfter ?? DefaultRetryAfter);
        }

        return Create(indexer, $"{indexer.Name} answered with an error: {description} (code {Code(error)}).", error.Code, retryAfter);
    }

    /// <summary>Maps an HTTP failure without a Newznab error body.</summary>
    public static IndexerException FromHttpStatus(IndexerDefinition indexer, HttpStatusCode status, TimeSpan? retryAfter)
    {
        var code = ((int)status).ToString(CultureInfo.InvariantCulture);
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                Create(indexer, $"{indexer.Name} rejected the request (HTTP {code}). Check the API key.", AuthErrorCode),
            HttpStatusCode.TooManyRequests =>
                Create(indexer, $"{indexer.Name}: too many requests (HTTP 429).", null, retryAfter ?? DefaultRetryAfter),
            HttpStatusCode.NotFound =>
                Create(indexer, $"{indexer.Name} answered HTTP 404. Check the URL and the API path."),
            >= HttpStatusCode.InternalServerError =>
                Create(indexer, $"{indexer.Name} is unavailable (HTTP {code}). Try again later.", null, retryAfter),
            _ => Create(indexer, $"{indexer.Name} answered HTTP {code}.", null, retryAfter),
        };
    }

    /// <summary>Builds the exception with every trace of the API key removed from the message (and drops an inner exception that mentions it).</summary>
    public static IndexerException Create(IndexerDefinition indexer, string message, int? code = null, TimeSpan? retryAfter = null, Exception? inner = null)
    {
        var safeInner = inner is not null && NewznabUrls.Redact(inner.ToString(), indexer.ApiKey) == inner.ToString() ? inner : null;
        return new IndexerException(NewznabUrls.Redact(message, indexer.ApiKey), code, retryAfter, safeInner);
    }

    // Newznab: 500 request limit reached, 501 download limit reached; some indexers answer 429 in the body.
    private static bool IsRequestLimit(NewznabError error) =>
        error.Code is 429 or 500 or 501
        || error.Description.Contains("limit reached", StringComparison.OrdinalIgnoreCase)
        || error.Description.Contains("too many requests", StringComparison.OrdinalIgnoreCase);

    private static string Code(NewznabError error) => error.Code.ToString(CultureInfo.InvariantCulture);
}
