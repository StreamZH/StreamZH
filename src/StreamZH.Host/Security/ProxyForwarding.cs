// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;

namespace StreamZH.Host.Security;

/// <summary>
/// Reverse proxies (0.12, security review S1-09/S1-10): only for a request whose connection comes from a configured
/// trusted proxy, the client address and the scheme (X-Forwarded-Proto) are taken from the headers. Then rate limits
/// count the real client and the sign-in cookie is marked Secure behind an HTTPS proxy. From anybody else the headers
/// change nothing (anyone can send them). Same idea as ASP.NET's ForwardedHeadersMiddleware with KnownNetworks, but read
/// live from the settings.
/// <para>
/// Which header holds the client ("Client IP header", V-08): X-Forwarded-For (right to left, past trusted hops),
/// X-Real-IP or CF-Connecting-IP, as the proxy sets it. "Auto" takes X-Forwarded-For or X-Real-IP, whichever is there;
/// when both are there and name different clients, one of them came from the client itself and nothing tells which,
/// so the request is counted as coming from the proxy (and a warning says to choose the header).
/// </para>
/// </summary>
public static class ProxyForwarding
{
    private const string PeerKey = "streamzh:proxy-peer";
    private static int _warnedConflict;

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
        var access = context.RequestServices.GetRequiredService<AccessSettings>();
        if (Apply(context, access.TrustedProxies, access.ClientIpHeader) == ClientSource.Conflict && Interlocked.Exchange(ref _warnedConflict, 1) == 0)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ProxyForwarding)).LogWarning(
                "A trusted proxy sent X-Forwarded-For and X-Real-IP with different client addresses; such requests count as coming from the proxy. Choose the header your proxy sets under Settings → General → Security → Client IP header");
        }

        return next(context);
    });

    /// <summary>Where the client address of a forwarded request came from.</summary>
    public enum ClientSource
    {
        /// <summary>Not from a trusted proxy, or no forwarding header.</summary>
        None,
        Header,

        /// <summary>Auto: X-Forwarded-For and X-Real-IP disagree; the proxy is taken as the client.</summary>
        Conflict,
    }

    public static ClientSource Apply(HttpContext context, NetworkList trusted, string clientIpHeader = AccessSettings.ClientIpHeaderAuto)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trusted);
        var peer = context.Connection.RemoteIpAddress;
        if (trusted.IsEmpty || !trusted.Contains(peer))
        {
            return ClientSource.None;
        }

        var headers = context.Request.Headers;
        var fromChain = FromChain(headers["X-Forwarded-For"], trusted);
        var realIp = headers["X-Real-IP"].FirstOrDefault() is { Length: > 0 } real ? Parse(real) : null;
        IPAddress? client;
        var source = ClientSource.Header;
        switch (clientIpHeader)
        {
            case AccessSettings.ClientIpHeaderForwardedFor:
                client = fromChain;
                break;
            case AccessSettings.ClientIpHeaderRealIp:
                client = realIp;
                break;
            case AccessSettings.ClientIpHeaderCloudflare:
                client = headers["CF-Connecting-IP"].FirstOrDefault() is { Length: > 0 } cf ? Parse(cf) : null;
                break;
            default:
                if (fromChain is not null && realIp is not null && !Same(fromChain, realIp))
                {
                    // One of the two was written by the client; without knowing which, neither is believed.
                    client = peer;
                    source = ClientSource.Conflict;
                }
                else
                {
                    client = fromChain ?? realIp;
                }

                break;
        }

        if (client is null && !headers.ContainsKey("X-Forwarded-Proto"))
        {
            return ClientSource.None;
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

        return client is null ? ClientSource.None : source;
    }

    // From the right: skip the trusted proxies of the chain; the first other address is the client.
    private static IPAddress? FromChain(Microsoft.Extensions.Primitives.StringValues forwarded, NetworkList trusted)
    {
        var chain = forwarded.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
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

        return client;
    }

    private static bool Same(IPAddress a, IPAddress b) =>
        (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b);

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
