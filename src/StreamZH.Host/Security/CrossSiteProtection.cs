// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Tray;
using StreamZH.Host.Auth;
using StreamZH.Host.General;

namespace StreamZH.Host.Security;

/// <summary>
/// Cross-site request protection (0.12, security review S1-01/S1-07). Browsers send cookies, Basic credentials and the
/// local network address along with requests that other web pages start, so a page on the internet (or another site on
/// the same host or domain) could otherwise change settings, restore a backup or restart StreamZH in the name of a
/// signed-in user or a local visitor. Every changing request (POST, PUT, PATCH, DELETE) must therefore show that it comes
/// from StreamZH's own pages: the browser says so (Sec-Fetch-Site same-origin, or none for a typed address), its Origin is
/// this host, or it carries a header no other site can add without StreamZH's consent (CORS): X-StreamZH-Request (sent by
/// the web app on every call), X-Api-Key or the tray token header. Requests signed in with the API key are always fine.
/// The live connection (/hub) also refuses WebSocket and polling requests from a foreign Origin.
/// </summary>
public static class CrossSiteProtection
{
    public const string Header = "X-StreamZH-Request";

    public static IApplicationBuilder UseCrossSiteProtection(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (Check(context) is { } problem)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CrossSiteProtection))
                .LogWarning("Refused {Method} {Path} from {Address}: {Problem}", context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress, problem);
            await Results.Problem(
                    "This request did not come from StreamZH's own pages and was refused. Scripts and other tools sign in with the API key (X-Api-Key header).",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Cross-site request refused")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    });

    /// <summary>Null when the request may go on, else why not.</summary>
    public static string? Check(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;

        // The API key proves itself; it is never sent by browsers on their own.
        if (context.User.Identity?.AuthenticationType == AuthSchemes.ApiKey)
        {
            return null;
        }

        var safe = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method);
        if (safe)
        {
            // WebSocket and polling of the live connection: a foreign Origin must not read the events.
            return request.Path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase)
                && request.Headers.Origin.FirstOrDefault() is { Length: > 0 } origin
                && !OriginMatches(context, origin)
                    ? $"foreign origin {origin} on the live connection"
                    : null;
        }

        if (request.Headers.ContainsKey(Header) || request.Headers.ContainsKey(AuthSchemes.ApiKeyHeader) || request.Headers.ContainsKey(TrayContract.Header))
        {
            return null;
        }

        // A browser that sends Sec-Fetch-Site has the last word (0.12, V-04): same-site, cross-site and anything else are
        // refused, even when the Origin is the application URL. The Origin only counts for browsers without Sec-Fetch-Site.
        var site = request.Headers["Sec-Fetch-Site"].FirstOrDefault();
        if (site is { Length: > 0 })
        {
            return string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase) || string.Equals(site, "none", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"Sec-Fetch-Site {site}";
        }

        if (request.Headers.Origin.FirstOrDefault() is { Length: > 0 } requestOrigin && OriginMatches(context, requestOrigin))
        {
            return null;
        }

        return "no proof of a same-origin request (Origin, Sec-Fetch-Site or X-StreamZH-Request)";
    }

    /// <summary>The Origin is this request's host (port included, default ports ignored) or the application URL.</summary>
    public static bool OriginMatches(HttpContext context, string origin)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || originUri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var (hostName, hostPort) = HostAccess.ParseHostHeader(context.Request.Headers.Host.ToString());
        if (hostName.Length > 0 && string.Equals(hostName, HostAccess.Normalize(originUri.Host), StringComparison.Ordinal))
        {
            // A Host without port: a default port (behind a TLS proxy the scheme seen here may be http, the browser's https).
            if (hostPort is { } port ? port == originUri.Port : originUri.IsDefaultPort)
            {
                return true;
            }
        }

        var applicationUrl = context.RequestServices.GetService<HostSettings>()?.ApplicationUrl;
        return applicationUrl is not null
            && Uri.TryCreate(applicationUrl, UriKind.Absolute, out var configured)
            && string.Equals(configured.Scheme, originUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(HostAccess.Normalize(configured.Host), HostAccess.Normalize(originUri.Host), StringComparison.Ordinal)
            && configured.Port == originUri.Port;
    }
}
