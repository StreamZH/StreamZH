// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.IO.Hashing;

namespace StreamZH.Usenet.Yenc;

/// <summary>
/// Decodes one yEnc encoded article body (=ybegin / =ypart / data / =yend).
/// NNTP dot-unstuffing must already be done by the transport layer.
/// </summary>
public static class YencDecoder
{
    public static YencPart Decode(ReadOnlySpan<byte> article)
    {
        Dictionary<string, string>? begin = null;
        Dictionary<string, string>? part = null;
        Dictionary<string, string>? end = null;

        var output = new byte[article.Length];
        var written = 0;
        var position = 0;

        while (position < article.Length)
        {
            var lineEnd = article[position..].IndexOf((byte)'\n');
            var line = lineEnd < 0 ? article[position..] : article.Slice(position, lineEnd);
            position = lineEnd < 0 ? article.Length : position + lineEnd + 1;

            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.StartsWith("=ybegin "u8))
            {
                begin = ParseKeywords(line[8..]);
                continue;
            }

            if (begin is null)
            {
                continue; // headers or noise before the yEnc block
            }

            if (line.StartsWith("=ypart "u8))
            {
                part = ParseKeywords(line[7..]);
                continue;
            }

            if (line.StartsWith("=yend"u8))
            {
                end = ParseKeywords(line.Length > 6 ? line[6..] : []);
                break;
            }

            written += DecodeLine(line, output.AsSpan(written));
        }

        if (begin is null)
        {
            throw new YencException("No =ybegin line found.");
        }

        if (end is null)
        {
            throw new YencException("No =yend line found, article is truncated.");
        }

        var data = output.AsSpan(0, written).ToArray();
        var actualCrc = Crc32.HashToUInt32(data);

        var partNumber = GetInt(begin, "part") ?? 1;
        var expectedCrc = GetHex(end, "pcrc32") ?? (part is null ? GetHex(end, "crc32") : null);
        var beginOffset = part is not null ? (GetLong(part, "begin") ?? 1) - 1 : 0;
        var expectedSize = GetLong(end, "size");

        return new YencPart(
            Name: begin.GetValueOrDefault("name", string.Empty),
            Part: partNumber,
            Total: GetInt(begin, "total"),
            FileSize: GetLong(begin, "size") ?? data.Length,
            Offset: beginOffset,
            Data: data,
            ExpectedCrc32: expectedCrc,
            ActualCrc32: actualCrc,
            SizeMatches: expectedSize is null || expectedSize == data.Length);
    }

    private static int DecodeLine(ReadOnlySpan<byte> line, Span<byte> destination)
    {
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var value = line[i];
            if (value == (byte)'=')
            {
                if (++i >= line.Length)
                {
                    break; // dangling escape at end of line
                }

                value = (byte)(line[i] - 64);
            }

            destination[count++] = (byte)(value - 42);
        }

        return count;
    }

    // "name" is always the last keyword and may contain spaces.
    private static Dictionary<string, string> ParseKeywords(ReadOnlySpan<byte> text)
    {
        var line = System.Text.Encoding.Latin1.GetString(text);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var nameIndex = line.IndexOf("name=", StringComparison.Ordinal);
        if (nameIndex >= 0)
        {
            result["name"] = line[(nameIndex + 5)..].Trim();
            line = line[..nameIndex];
        }

        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf('=');
            if (separator > 0)
            {
                result[token[..separator]] = token[(separator + 1)..];
            }
        }

        return result;
    }

    private static int? GetInt(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;

    private static long? GetLong(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;

    private static uint? GetHex(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) && uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ? r : null;
}

public sealed record YencPart(
    string Name,
    int Part,
    int? Total,
    long FileSize,
    long Offset,
    byte[] Data,
    uint? ExpectedCrc32,
    uint ActualCrc32,
    bool SizeMatches)
{
    public bool CrcMatches => ExpectedCrc32 is null || ExpectedCrc32 == ActualCrc32;

    public bool IsValid => CrcMatches && SizeMatches;
}

public sealed class YencException(string message) : Exception(message);