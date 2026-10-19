// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>
/// The download queue everyone reads (Activity, status window, "already in the queue" checks, the import): the built-in
/// Usenet engine's downloads followed by the torrents of the download clients.
/// </summary>
public sealed class CombinedDownloadQueue(DownloadEngine engine, TorrentTracker torrents) : IDownloadQueue
{
    public IReadOnlyList<DownloadItem> Active() => [.. engine.Active(), .. torrents.Active()];

    public DownloadItem? Find(int id) => torrents.Find(id) ?? engine.Find(id);
}
