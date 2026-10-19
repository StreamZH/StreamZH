// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Sockets;

namespace StreamZH.Host.Security;

/// <summary>
/// A list of IP addresses and networks as people type them in the settings: "192.168.1.0/24, 10.0.0.5, fd00::/8",
/// separated by commas, spaces or new lines. A single address counts as /32 (IPv4) or /128 (IPv6).
/// </summary>
public sealed class NetworkList
{
    public static readonly NetworkList Empty = new([]);

    private NetworkList(IReadOnlyList<IPNetwork> networks) => Networks = networks;

    public IReadOnlyList<IPNetwork> Networks { get; }

    public bool IsEmpty => Networks.Count == 0;

    /// <summary>Parses the list; <paramref name="problem"/> names the first entry that is not an address or network.</summary>
    public static NetworkList Parse(string? text, out string? problem)
    {
        problem = null;
        var networks = new List<IPNetwork>();
        foreach (var entry in Split(text))
        {
            if (TryParseEntry(entry, out var network))
            {
                networks.Add(network);
            }
            else
            {
                problem ??= $"\"{entry}\" is not an IP address or network (e.g. 192.168.1.0/24).";
            }
        }

        return new NetworkList(networks);
    }

    /// <summary>Like <see cref="Parse(string?, out string?)"/>, ignoring entries that cannot be read.</summary>
    public static NetworkList Parse(string? text) => Parse(text, out _);

    /// <summary>The entries in a normal form ("192.168.1.0/24, 10.0.0.5/32"), for storing.</summary>
    public string Format() => string.Join(", ", Networks.Select(Format));

    public bool Contains(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var network in Networks)
        {
            if (network.BaseAddress.AddressFamily == address.AddressFamily && network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<string> Split(string? text) =>
        (text ?? string.Empty).Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseEntry(string entry, out IPNetwork network)
    {
        network = default;
        var text = entry.Trim('[', ']');
        if (!text.Contains('/', StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(text, out var single))
            {
                return false;
            }

            if (single.IsIPv4MappedToIPv6)
            {
                single = single.MapToIPv4();
            }

            network = new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        // IPNetwork.TryParse refuses host bits ("192.168.1.5/24"); people type that, so mask them off.
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (!IPAddress.TryParse(text[..slash], out var address) || !int.TryParse(text[(slash + 1)..], out var prefix))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > bits)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        network = new IPNetwork(new IPAddress(bytes), prefix);
        return true;
    }

    private static string Format(IPNetwork network) => $"{network.BaseAddress}/{network.PrefixLength}";
}
