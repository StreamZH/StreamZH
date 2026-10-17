// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Events;

namespace StreamZH.Core.Media;

/// <summary>
/// Files were imported into the library (moved, copied or hard-linked into the movie or series folder and renamed).
/// Media servers (Plex, Jellyfin, Emby), notifications and metadata writers (NFO, artwork) react to it.
/// </summary>
/// <param name="Files">Full paths of the imported video files in the library.</param>
/// <param name="Folder">The movie folder, or the series folder for episodes.</param>
/// <param name="IsUpgrade">An existing file was replaced by a better one.</param>
/// <param name="ReleaseTitle">The release the files came from (null for a manual import of loose files).</param>
public sealed record MediaImported(
    MediaType MediaType,
    int? MovieId,
    int? SeriesId,
    IReadOnlyList<int> EpisodeIds,
    string Title,
    IReadOnlyList<string> Files,
    string Folder,
    bool IsUpgrade,
    string? ReleaseTitle,
    int? DownloadId) : IAppEvent;

/// <summary>A file was renamed in the library (Media Management → rename). Media servers refresh the folder.</summary>
public sealed record MediaRenamed(MediaType MediaType, int? MovieId, int? SeriesId, string Folder) : IAppEvent;
