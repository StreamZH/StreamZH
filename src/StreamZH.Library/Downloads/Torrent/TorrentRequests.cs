// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using StreamZH.Core.Downloads;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>Small helpers of the client implementations: the info hash of an add request and lenient JSON reading.</summary>
public static class TorrentRequests
{
    /// <summary>The info hash of the .torrent file or the magnet link of the request.</summary>
    /// <exception cref="DownloadClientException">Neither is usable.</exception>
    public static string Hash(TorrentAddRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.File is { Length: > 0 } file)
        {
            try
            {
                return TorrentMetadata.InfoHash(file);
            }
            catch (FormatException ex)
            {
                throw new DownloadClientException($"The .torrent file of '{request.Title}' is not valid: {ex.Message}", inner: ex);
            }
        }

        return TorrentMetadata.ParseMagnet(request.MagnetUri)?.InfoHash
            ?? throw new DownloadClientException($"'{request.Title}' has neither a valid .torrent file nor a magnet link with an info hash.");
    }

    public static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        } : null;

    public static long Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            // qBittorrent's ETA can exceed 64 bits; doubles cover the rest.
            JsonValueKind.Number => value.TryGetDouble(out var d) ? (long)Math.Clamp(d, long.MinValue, long.MaxValue) : 0,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            JsonValueKind.True => 1,
            _ => 0,
        };
    }

    public static double Double(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;

    public static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n != 0));
}
