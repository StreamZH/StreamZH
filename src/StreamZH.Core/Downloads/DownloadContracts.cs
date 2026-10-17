// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Events;

namespace StreamZH.Core.Downloads;

/// <summary>
/// Fetches articles from the configured Usenet servers through a shared connection pool.
/// Servers are tried by priority; a missing article (430) moves on to the next server.
/// </summary>
public interface IArticleSource
{
    /// <summary>Total connections of all enabled servers; the download engine runs at most this many fetches at once.</summary>
    int MaxConnections { get; }

    /// <exception cref="ArticleNotFoundException">No enabled server has the article.</exception>
    /// <exception cref="UsenetServerException">No server could be reached at all.</exception>
    Task<DecodedArticle> FetchAsync(string messageId, IReadOnlyList<string> groups, DateTimeOffset posted, CancellationToken cancellationToken);
}

/// <summary>Where grabbed releases go: the built-in engine, or a folder another downloader watches.</summary>
public interface IDownloadClient
{
    /// <summary>"StreamZH" or "Blackhole folder", shown in history.</summary>
    string Name { get; }

    /// <returns>A reference for history: the download id for the engine, the NZB path for the folder.</returns>
    Task<string> AddAsync(DownloadRequest request, CancellationToken cancellationToken);
}

/// <summary>The built-in download queue.</summary>
public interface IDownloadQueue
{
    IReadOnlyList<DownloadItem> Active();

    DownloadItem? Find(int id);
}

/// <summary>Repairs and unpacks a finished download (par2 + archives).</summary>
public interface IPostProcessor
{
    Task<PostProcessResult> RunAsync(PostProcessJob job, IProgress<PostProcessProgress> progress, CancellationToken cancellationToken);
}

/// <summary>A download was added, changed state, or was removed (not sent for plain progress).</summary>
public sealed record DownloadChanged(int DownloadId) : IAppEvent;

/// <summary>Throttled (about once per second) progress of all active downloads, for live bars.</summary>
public sealed record DownloadProgress(IReadOnlyList<DownloadProgressItem> Items) : IAppEvent;

public sealed record DownloadProgressItem(int Id, DownloadStatus Status, double Progress, long SpeedBps, double? EtaSeconds, double? StepProgress, string? StatusMessage);

/// <summary>Something the status window should re-read (jobs, notices). No payload: the UI fetches /api/v1/status/ongoing.</summary>
public sealed record OngoingChanged : IAppEvent;
