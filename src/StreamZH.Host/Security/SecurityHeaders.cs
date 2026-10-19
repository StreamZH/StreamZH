// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Host.Security;

/// <summary>
/// Browser security headers on every response (0.12, security review S1-11): no framing (clickjacking), no MIME
/// sniffing, no referrer to other sites, and a Content Security Policy that lets the web app load scripts, styles and
/// connections only from StreamZH itself (posters may come from TMDB's image server; after a port change the restart
/// screen probes the same host on the new port). A response that sets its own policy keeps it (the TMDB SVG images
/// get <c>sandbox</c>, see MetadataEndpoints).
/// </summary>
public static partial class SecurityHeaders
{
    public const string SandboxPolicy = "sandbox";

    private const string Policy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob: https://image.tmdb.org; " +
        "font-src 'self' data:; manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            if (!headers.ContainsKey("Content-Security-Policy"))
            {
                headers.ContentSecurityPolicy = ContentSecurityPolicy(HostAccess.HostName(context));
            }

            return Task.CompletedTask;
        });
        return next(context);
    });

    /// <summary>The policy for a page served under <paramref name="host"/>.</summary>
    public static string ContentSecurityPolicy(string? host)
    {
        var connect = "connect-src 'self' ws: wss:";
        if (host is { Length: > 0 } && SafeHost().IsMatch(host))
        {
            connect += $" http://{host}:* https://{host}:*";
        }

        return $"{Policy}; {connect}";
    }

    // A host name, IPv4 address or bracketed IPv6 address, nothing that could end or extend the policy.
    [GeneratedRegex(@"^(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+)$")]
    private static partial Regex SafeHost();
}
