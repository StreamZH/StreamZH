// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Template;

namespace StreamZH.Host.Api;

/// <summary>
/// The answer for /api/... requests no endpoint took (SV4 X-13). Routing leaves out a JSON endpoint when the request's
/// Content-Type does not fit it (text/plain, no body), and the request then lands here: when an endpoint with this path
/// and method exists and only the content type is wrong, the answer is 415 with the same message as
/// <see cref="ApiExceptionHandler"/>; otherwise a plain 404. Nothing runs and nothing is read, so no sign-in is needed
/// for the difference (the routes are public in the source code anyway).
/// </summary>
public static class ApiFallback
{
    public const string JsonMessage = "Send the body as JSON (Content-Type: application/json).";

    public static IResult Answer(HttpContext http, EndpointDataSource endpoints)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoints);
        return WrongContentType(http.Request, endpoints.Endpoints)
            ? Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: JsonMessage, detail: JsonMessage)
            : Results.NotFound();
    }

    /// <summary>True when an endpoint has this path and method but accepts other content types than the request's.</summary>
    public static bool WrongContentType(HttpRequest request, IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(endpoints);
        var mediaType = MediaType(request.ContentType);
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<IAcceptsMetadata>() is not { ContentTypes.Count: > 0, IsOptional: false } accepts
                || endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methods
                || !methods.HttpMethods.Contains(request.Method, StringComparer.OrdinalIgnoreCase)
                || endpoint.RoutePattern.RawText is null
                || !new TemplateMatcher(new RouteTemplate(endpoint.RoutePattern), new RouteValueDictionary()).TryMatch(request.Path, new RouteValueDictionary()))
            {
                continue;
            }

            if (!accepts.ContentTypes.Any(type => Fits(mediaType, MediaType(type))))
            {
                return true;
            }
        }

        return false;
    }

    private static string MediaType(string? contentType) =>
        (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

    private static bool Fits(string request, string accepted) =>
        request.Length > 0
        && (request == accepted
            || accepted == "*/*"
            || (accepted.EndsWith("/*", StringComparison.Ordinal) && request.StartsWith(accepted[..^1], StringComparison.Ordinal))
            || (accepted == "application/json" && request.EndsWith("+json", StringComparison.Ordinal)));
}
