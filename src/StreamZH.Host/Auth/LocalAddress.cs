// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Sockets;

namespace StreamZH.Host.Auth;

/// <summary>
/// "Local address" as in Radarr's "Disabled for Local Addresses": this computer and the private networks
/// (10/8, 172.16/12, 192.168/16, 127/8, ::1, fe80::/10, fc00::/7).
/// </summary>
public static class LocalAddress
{
    private static readonly string[] ForwardedHeaders = ["X-Forwarded-For", "X-Real-IP", "Forwarded"];

    public static bool IsLocal(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    /// <summary>
    /// The request comes from a local address. Behind a reverse proxy on the same computer every request looks local,
    /// so the forwarding headers (X-Forwarded-For, X-Real-IP, Forwarded) must name local addresses only as well.
    /// </summary>
    public static bool IsLocalRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsLocal(context.Connection.RemoteIpAddress))
        {
            return false;
        }

        foreach (var header in ForwardedHeaders)
        {
            foreach (var value in context.Request.Headers[header])
            {
                foreach (var part in (value ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var candidate = part;
                    if (header == "Forwarded")
                    {
                        if (!part.StartsWith("for=", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        candidate = part[4..].Trim('"');
                    }

                    if (!IsLocal(ParseAddress(candidate)))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// A host name or IP address on the local network: localhost, a private IP, a name without dots ("nas") or a
    /// ".local"/".lan"/".home.arpa" name. Used for "bypass proxy" and "certificate validation" on outgoing requests.
    /// </summary>
    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var name = host.Trim().Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(name, out var address))
        {
            return IsLocal(address);
        }

        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || !name.Contains('.', StringComparison.Ordinal)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"1.2.3.4", "1.2.3.4:5678", "[::1]:80" or "::1"; null for "unknown" and obfuscated identifiers.</summary>
    private static IPAddress? ParseAddress(string value)
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
