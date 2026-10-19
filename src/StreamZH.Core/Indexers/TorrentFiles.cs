// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;

namespace StreamZH.Core.Indexers;

/// <summary>
/// What a Torznab download link delivers: a .torrent file (bencoded dictionary with an "info" key) or, when the indexer
/// redirects to one, a magnet link. <see cref="IIndexerClient.DownloadNzbAsync"/> returns a magnet link as
/// its UTF-8 bytes; <see cref="TryGetMagnet"/> tells the two apart.
/// </summary>
public static class TorrentFiles
{
    /// <summary>Largest .torrent file accepted (big season packs have a few MB of piece hashes).</summary>
    public const long MaxTorrentBytes = 20L * 1024 * 1024;

    private static readonly byte[] InfoKey = "4:info"u8.ToArray();

    /// <summary>A bencoded dictionary ("d…e") with an "info" dictionary.</summary>
    public static bool LooksLikeTorrent(ReadOnlySpan<byte> content) =>
        content.Length > 10 && content[0] == (byte)'d' && content[^1] == (byte)'e' && content.IndexOf(InfoKey) > 0;

    /// <summary>True when the content is a magnet link (as returned for magnet-only releases and magnet redirects).</summary>
    public static bool TryGetMagnet(ReadOnlySpan<byte> content, out string magnet)
    {
        magnet = string.Empty;
        if (content.Length < 8 || content.Length > 64 * 1024)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(content).Trim();
        if (!MagnetLinks.IsMagnet(text))
        {
            return false;
        }

        magnet = text;
        return true;
    }
}
