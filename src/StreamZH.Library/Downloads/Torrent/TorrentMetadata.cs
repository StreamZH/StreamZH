// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>A parsed magnet link: the BitTorrent v1 info hash (hex, lower case), the display name and the trackers.</summary>
public sealed record MagnetLink(string InfoHash, string? Name, IReadOnlyList<string> Trackers);

/// <summary>
/// The little StreamZH needs to know about a torrent before a client has it: the info hash of a .torrent file (SHA-1 of
/// the bencoded "info" dictionary) and the info hash of a magnet link (hex or base32 "xt=urn:btih:").
/// </summary>
public static class TorrentMetadata
{
    /// <summary>.torrent files of even large season packs stay well below this.</summary>
    public const int MaxTorrentBytes = 10 * 1024 * 1024;

    private const int MaxDepth = 64;

    /// <summary>A bencoded dictionary with an "info" key (cheap check, no full parse).</summary>
    public static bool LooksLikeTorrent(byte[]? content) =>
        content is { Length: > 10 } && content[0] == (byte)'d' && content.AsSpan(0, Math.Min(content.Length, 64 * 1024)).IndexOf("4:info"u8) > 0;

    /// <summary>The v1 info hash (40 hex characters, lower case).</summary>
    /// <exception cref="FormatException">Not a valid .torrent file.</exception>
    public static string InfoHash(byte[] torrent)
    {
        var (start, end) = InfoSpan(torrent);
        return Convert.ToHexStringLower(SHA1.HashData(torrent.AsSpan(start, end - start)));
    }

    /// <summary>The "name" of the info dictionary, or null.</summary>
    public static string? Name(byte[] torrent)
    {
        try
        {
            var (start, end) = InfoSpan(torrent);
            var position = start + 1;
            while (position < end - 1)
            {
                var key = ReadString(torrent, ref position);
                if (key.SequenceEqual("name"u8))
                {
                    return Encoding.UTF8.GetString(ReadString(torrent, ref position));
                }

                position = Skip(torrent, position, 1);
            }
        }
        catch (FormatException)
        {
        }

        return null;
    }

    /// <summary>Parses "magnet:?xt=urn:btih:…"; null when it is not a magnet link with a BitTorrent v1 hash.</summary>
    public static MagnetLink? ParseMagnet(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? hash = null;
        string? name = null;
        var trackers = new List<string>();
        foreach (var part in uri[8..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].ToLowerInvariant();
            string value;
            try
            {
                value = Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                continue;
            }

            switch (key)
            {
                case "xt" when hash is null && value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase):
                    hash = NormalizeHash(value[9..]);
                    break;
                case "dn":
                    name ??= value;
                    break;
                case "tr":
                    trackers.Add(value);
                    break;
            }
        }

        return hash is null ? null : new MagnetLink(hash, name, trackers);
    }

    /// <summary>40 hex characters (any case) or 32 base32 characters → 40 lower-case hex characters; null when neither.</summary>
    public static string? NormalizeHash(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 40 && text.All(char.IsAsciiHexDigit))
        {
            return text.ToLowerInvariant();
        }

        if (text.Length == 32 && Base32(text) is { Length: 20 } bytes)
        {
            return Convert.ToHexStringLower(bytes);
        }

        return null;
    }

    private static byte[]? Base32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>(20);
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant())
        {
            var index = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. output];
    }

    /// <summary>Start and end (exclusive) of the bencoded value of the top-level "info" key.</summary>
    private static (int Start, int End) InfoSpan(byte[] torrent)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        if (torrent.Length < 2 || torrent[0] != (byte)'d')
        {
            throw new FormatException("Not a .torrent file.");
        }

        var position = 1;
        while (position < torrent.Length && torrent[position] != (byte)'e')
        {
            var key = ReadString(torrent, ref position);
            var start = position;
            position = Skip(torrent, position, 1);
            if (key.SequenceEqual("info"u8))
            {
                if (torrent[start] != (byte)'d')
                {
                    throw new FormatException("The info entry of the .torrent file is not a dictionary.");
                }

                return (start, position);
            }
        }

        throw new FormatException("The .torrent file has no info entry.");
    }

    private static ReadOnlySpan<byte> ReadString(byte[] data, ref int position)
    {
        var colon = Array.IndexOf(data, (byte)':', position);
        if (colon <= position || colon - position > 10)
        {
            throw new FormatException("Invalid bencoded string.");
        }

        if (!int.TryParse(Encoding.ASCII.GetString(data, position, colon - position), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || colon + 1 + (long)length > data.Length)
        {
            throw new FormatException("Invalid bencoded string length.");
        }

        position = colon + 1 + length;
        return data.AsSpan(colon + 1, length);
    }

    /// <summary>Position after the bencoded value at <paramref name="position"/>.</summary>
    private static int Skip(byte[] data, int position, int depth)
    {
        if (position >= data.Length || depth > MaxDepth)
        {
            throw new FormatException("Truncated or too deeply nested .torrent file.");
        }

        switch (data[position])
        {
            case (byte)'i':
                var end = Array.IndexOf(data, (byte)'e', position + 1);
                return end < 0 ? throw new FormatException("Invalid bencoded integer.") : end + 1;
            case (byte)'l':
            case (byte)'d':
                var dictionary = data[position] == (byte)'d';
                position++;
                while (position < data.Length && data[position] != (byte)'e')
                {
                    if (dictionary)
                    {
                        ReadString(data, ref position);
                    }

                    position = Skip(data, position, depth + 1);
                }

                return position < data.Length ? position + 1 : throw new FormatException("Truncated .torrent file.");
            default:
                ReadString(data, ref position);
                return position;
        }
    }
}
