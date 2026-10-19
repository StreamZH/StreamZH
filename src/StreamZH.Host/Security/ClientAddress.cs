// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Sockets;

namespace StreamZH.Host.Security;

/// <summary>
/// The key for per-client limits: the IPv4 address, or the /64 network of an IPv6 address (one home or one server
/// usually gets a whole /64, so counting single IPv6 addresses would let one attacker rotate through billions).
/// </summary>
public static class ClientAddress
{
    public static string Partition(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    public static string Partition(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Partition(context.Connection.RemoteIpAddress);
    }
}
