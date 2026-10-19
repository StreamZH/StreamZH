// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace StreamZH.Core.Net;

/// <summary>A request to a destination that untrusted data (an indexer feed, a redirect) may not reach.</summary>
public sealed class BlockedDestinationException(string message) : HttpRequestException(message);

/// <summary>The hosts of the indexers and download clients the admin configured (0.12, V-10).</summary>
public interface IConfiguredServiceHosts
{
    bool Contains(string host);
}

/// <summary>
/// Keeps links from indexer feeds and redirects away from this computer, the local network and cloud metadata
/// services (blind SSRF). A request carries the host the admin configured (<see cref="TrustedHostKey"/>): that host is
/// reached as it is, also on the LAN (Prowlarr and Jackett live there and hand out links to themselves). Every other host
/// must resolve only to public addresses: not loopback, link-local, unspecified, multicast, a metadata address, nor
/// (since 0.12, V-10) a private range (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7), unless it is the host of another
/// configured indexer or download client (<see cref="AlsoTrustedKey"/>).
/// The check runs before the request (<see cref="CheckAsync"/>) and again when the socket connects
/// (<see cref="ConnectAsync"/>), so a DNS answer that changes in between (DNS rebinding) is caught as well. Through an
/// outgoing proxy only the check before the request runs (the proxy resolves the name itself; a name that does not
/// resolve here is refused).
/// </summary>
/// <param name="resolve">DNS (replaceable for tests).</param>
/// <param name="usesProxy">True when a request to this address goes through an outgoing proxy (default: never).</param>
public sealed class OutboundAddressGuard(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, Func<Uri, bool>? usesProxy = null)
{
    /// <summary>The host the request may reach without the check (the configured indexer's host).</summary>
    public static readonly HttpRequestOptionsKey<string> TrustedHostKey = new("StreamZH.TrustedHost");

    /// <summary>Further hosts the request may reach without the check: other configured indexers and download clients.</summary>
    public static readonly HttpRequestOptionsKey<IConfiguredServiceHosts> AlsoTrustedKey = new("StreamZH.AlsoTrusted");

    public static OutboundAddressGuard Default { get; } = new();

    // Metadata services that sit outside the link-local range (Alibaba Cloud, Azure wire server, AWS over IPv6).
    private static readonly IPAddress[] MetadataAddresses =
    [
        IPAddress.Parse("100.100.100.200"),
        IPAddress.Parse("168.63.129.16"),
        IPAddress.Parse("fd00:ec2::254"),
    ];

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = resolve ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));

    /// <summary>Marks a request: hosts other than <paramref name="trustedHost"/> (and <paramref name="alsoTrusted"/>) are checked.</summary>
    public static void Restrict(HttpRequestMessage request, string trustedHost, IConfiguredServiceHosts? alsoTrusted = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(TrustedHostKey, trustedHost);
        if (alsoTrusted is not null)
        {
            request.Options.Set(AlsoTrustedKey, alsoTrusted);
        }
    }

    /// <summary>True when the request is marked and goes to another host than the trusted ones.</summary>
    public static bool IsRestricted(HttpRequestMessage request, string host) =>
        request.Options.TryGetValue(TrustedHostKey, out var trusted) && !SameHost(trusted, host)
        && !(request.Options.TryGetValue(AlsoTrustedKey, out var also) && also.Contains(host.Trim('[', ']')));

    /// <summary>
    /// What a restricted request may reach (0.12, V-10): <see cref="IsAllowed"/> and not a private, CGNAT or unique
    /// local address either.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsAllowed(address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127));
        }

        // fc00::/7 unique local, fec0::/10 site-local (deprecated); IPv4 inside NAT64/compatible forms.
        if ((b[0] & 0xfe) == 0xfc || (b[0] == 0xfe && (b[1] & 0xc0) == 0xc0))
        {
            return false;
        }

        var nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0);
        var compatible = b.Take(12).All(x => x == 0);
        return !(nat64 || compatible) || IsPublic(new IPAddress(b[12..]));
    }

    public static bool SameHost(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim('[', ']'), b.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);

    /// <summary>False for loopback, link-local, unspecified, multicast, broadcast and cloud metadata addresses.</summary>
    public static bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (MetadataAddresses.Any(address.Equals))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 or 127 => false,                 // "this network", loopback
                169 when b[1] == 254 => false,     // link-local, 169.254.169.254 metadata
                >= 224 => false,                   // multicast, reserved, broadcast
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            // NAT64 (64:ff9b::/96) and IPv4-compatible (::a.b.c.d) forms: judge the embedded IPv4 address.
            var bytes = address.GetAddressBytes();
            var nat64 = bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes.Skip(4).Take(8).All(x => x == 0);
            var compatible = bytes.Take(12).All(x => x == 0);
            return !(nat64 || compatible) || IsAllowed(new IPAddress(bytes[12..]));
        }

        return false;
    }

    /// <summary>
    /// Before sending: throws <see cref="BlockedDestinationException"/> when the request is restricted and its host is or
    /// resolves to a blocked address. A name that does not resolve is left to the connection (it fails there).
    /// </summary>
    public async Task CheckAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        if (uri is null || !IsRestricted(request, uri.IdnHost))
        {
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await ResolveAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Without a proxy the connection fails anyway (and the connect callback checks again); through a proxy the
            // proxy could resolve the name to anything, so it is refused.
            if (usesProxy?.Invoke(uri) == true)
            {
                throw Blocked(uri.IdnHost);
            }

            return;
        }

        if (addresses.Length > 0 && !addresses.All(IsPublic))
        {
            throw Blocked(uri.IdnHost);
        }
    }

    /// <summary>
    /// True when <paramref name="uri"/> would go through <paramref name="proxy"/> (not bypassed); for the
    /// <c>usesProxy</c> argument.
    /// </summary>
    public static bool ThroughProxy(IWebProxy? proxy, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        try
        {
            return proxy is not null && !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is { } via && via != uri;
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or UriFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// For <see cref="SocketsHttpHandler.ConnectCallback"/>: resolves the host itself and connects only to allowed
    /// addresses when the request is restricted. Connections to a proxy are not checked here (the proxy resolves the
    /// name; <see cref="CheckAsync"/> already ran).
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.DnsEndPoint;
        var request = context.InitialRequestMessage;
        var toTarget = SameHost(endpoint.Host, request.RequestUri?.IdnHost);
        var addresses = await ResolveAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        if (toTarget && IsRestricted(request, endpoint.Host))
        {
            addresses = [.. addresses.Where(IsPublic)];
            if (addresses.Length == 0)
            {
                throw Blocked(endpoint.Host);
            }
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>A handler with <see cref="ConnectAsync"/> as its connect callback.</summary>
    public SocketsHttpHandler Apply(SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler.ConnectCallback = ConnectAsync;
        return handler;
    }

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await _resolve(host, cancellationToken).ConfigureAwait(false);

    private static BlockedDestinationException Blocked(string host) =>
        new($"{host} points to this computer, the local network or a cloud metadata address (or cannot be resolved); links from indexers may not go there (only to the indexer itself, other configured indexers and download clients, or the internet).");
}
