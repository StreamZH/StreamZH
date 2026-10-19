// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;

namespace StreamZH.Core.Indexers;

/// <summary>Reads info hashes from magnet links and builds magnet links from an info hash (BEP 9).</summary>
public static class MagnetLinks
{
    public const string Scheme = "magnet:";

    public static bool IsMagnet(string? value) =>
        value is not null && value.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The info hash of a magnet link as lower-case hex: "xt=urn:btih:" with 40 hex or 32 base32 characters (v1), else
    /// "xt=urn:btmh:1220…" (v2, SHA-256). Null when the link has none.
    /// </summary>
    public static string? InfoHash(string? magnet)
    {
        if (!IsMagnet(magnet))
        {
            return null;
        }

        var query = magnet!.Trim()[Scheme.Length..].TrimStart('?');
        string? v2 = null;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=', StringComparison.Ordinal);
            if (index < 0 || !part[..index].StartsWith("xt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(part[(index + 1)..]).Trim();
            if (value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
            {
                if (NormalizeInfoHash(value["urn:btih:".Length..]) is { } v1)
                {
                    return v1;
                }
            }
            else if (value.StartsWith("urn:btmh:1220", StringComparison.OrdinalIgnoreCase))
            {
                var hex = value["urn:btmh:1220".Length..];
                v2 ??= hex.Length == 64 && IsHex(hex) ? hex.ToLowerInvariant() : null;
            }
        }

        return v2;
    }

    /// <summary>40 hex characters (or 32 base32 characters) → 40 lower-case hex characters; 64 hex (v2) stays; null for anything else.</summary>
    public static string? NormalizeInfoHash(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if ((text.Length == 40 || text.Length == 64) && IsHex(text))
        {
            return text.ToLowerInvariant();
        }

        return text.Length == 32 && FromBase32(text) is { Length: 20 } bytes ? Convert.ToHexStringLower(bytes) : null;
    }

    /// <summary>"magnet:?xt=urn:btih:{hash}&amp;dn={title}" plus the trackers given (none: DHT and PEX find peers).</summary>
    public static string Build(string infoHash, string? title, IEnumerable<string>? trackers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infoHash);
        var builder = new StringBuilder("magnet:?xt=urn:btih:").Append(infoHash.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("&dn=").Append(Uri.EscapeDataString(title.Trim()));
        }

        foreach (var tracker in trackers ?? [])
        {
            builder.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        }

        return builder.ToString();
    }

    private static bool IsHex(string text) => text.All(Uri.IsHexDigit);

    private static byte[]? FromBase32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>(20);
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xff));
            }
        }

        return [.. output];
    }
}
