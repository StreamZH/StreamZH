// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;

namespace StreamZH.Host.Security;

/// <summary>
/// Reverse proxies (0.12, security review S1-09/S1-10): only for a request whose connection comes from a configured
/// trusted proxy, the client address (X-Forwarded-For, else X-Real-IP) and the scheme (X-Forwarded-Proto) are taken
/// from the headers. Then rate limits count the real client and the sign-in cookie is marked Secure behind an HTTPS
/// proxy. From anybody else the headers change nothing (anyone can send them). Same idea as ASP.NET's
/// ForwardedHeadersMiddleware with KnownNetworks, but read live from the settings.
/// </summary>
public static class ProxyForwarding
{
    private const string PeerKey = "streamzh:proxy-peer";

    /// <summary>The address of the connection itself (the proxy for forwarded requests).</summary>
    public static IPAddress? Peer(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(PeerKey, out var peer) ? peer as IPAddress : context.Connection.RemoteIpAddress;
    }

    /// <summary>The request came through a trusted proxy and its forwarding headers were applied.</summary>
    public static bool ViaTrustedProxy(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.ContainsKey(PeerKey);
    }

    public static IApplicationBuilder UseTrustedProxyForwarding(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        Apply(context, context.RequestServices.GetRequiredService<AccessSettings>().TrustedProxies);
        return next(context);
    });

    public static void Apply(HttpContext context, NetworkList trusted)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trusted);
        var peer = context.Connection.RemoteIpAddress;
        if (trusted.IsEmpty || !trusted.Contains(peer))
        {
            return;
        }

        var forwarded = context.Request.Headers["X-Forwarded-For"];
        var chain = forwarded.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (chain.Count == 0 && context.Request.Headers["X-Real-IP"].FirstOrDefault() is { Length: > 0 } realIp)
        {
            chain.Add(realIp.Trim());
        }

        if (chain.Count == 0 && !context.Request.Headers.ContainsKey("X-Forwarded-Proto"))
        {
            return;
        }

        // From the right: skip the trusted proxies of the chain; the first other address is the client.
        IPAddress? client = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (Parse(chain[i]) is not { } address)
            {
                break;
            }

            client = address;
            if (!trusted.Contains(address))
            {
                break;
            }
        }

        context.Items[PeerKey] = peer;
        if (client is not null)
        {
            context.Connection.RemoteIpAddress = client;
        }

        var proto = context.Request.Headers["X-Forwarded-Proto"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.Equals(proto, "https", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Scheme = "https";
        }
        else if (string.Equals(proto, "http", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Scheme = "http";
        }
    }

    /// <summary>"1.2.3.4", "1.2.3.4:5678", "[::1]:80" or "::1".</summary>
    private static IPAddress? Parse(string value)
    {
        var text = value.Trim().Trim('"');
        if (IPAddress.TryParse(text, out var address))
        {
            return address;
        }

        if (text.StartsWith('[') && text.IndexOf(']', StringComparison.Ordinal) is var end and > 0)
        {
            return IPAddress.TryParse(text[1..end], out address) ? address : null;
        }

        var colon = text.LastIndexOf(':');
        return colon > 0 && IPAddress.TryParse(text[..colon], out address) ? address : null;
    }
}
