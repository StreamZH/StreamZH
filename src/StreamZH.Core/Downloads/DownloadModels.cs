// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;

namespace StreamZH.Core.Downloads;

/// <summary>A Usenet provider account. Several can be configured: lower <see cref="Priority"/> first, the others fill gaps (backup/block accounts).</summary>
/// <param name="Priority">0 = primary. Higher numbers are only asked for articles the lower ones do not have.</param>
/// <param name="RetentionDays">0 = unknown/unlimited; articles older than this are not requested from this server.</param>
public sealed record UsenetServer(
    int Id,
    string Name,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Password,
    int Connections,
    int Priority,
    bool Enabled,
    int RetentionDays);

/// <summary>One decoded yEnc part, ready to be written at <see cref="Offset"/> into <see cref="FileName"/>.</summary>
public sealed record DecodedArticle(string FileName, long FileSize, long Offset, byte[] Data, bool CrcOk);

/// <summary>The article is not available on any enabled server (expired, taken down or never complete).</summary>
public sealed class ArticleNotFoundException(string messageId) : Exception($"Article <{messageId}> was not found on any server.")
{
    public string MessageId { get; } = messageId;
}

/// <summary>A server could not be used (connection, TLS, login). <see cref="IsAuthError"/>: wrong user name or password.</summary>
public sealed class UsenetServerException(string message, bool isAuthError = false, Exception? inner = null) : Exception(message, inner)
{
    public bool IsAuthError { get; } = isAuthError;
}

public enum DownloadCategory
{
    Movie,
    Series,
    Other,
}

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Verifying,
    Repairing,
    Extracting,
    Completed,
    Failed,
}

/// <summary>What a download belongs to in the library (for import in 0.6). All empty for downloads from the Search page.</summary>
public sealed record MediaLink(MediaType? MediaType, int? MovieId, int? SeriesId, IReadOnlyList<int> EpisodeIds)
{
    public static MediaLink None { get; } = new(null, null, null, []);
}

/// <summary>A new download: the NZB file plus where it came from.</summary>
public sealed record DownloadRequest(
    byte[] Nzb,
    string Title,
    DownloadCategory Category,
    MediaLink Link,
    int Priority = 0,
    string? Indexer = null,
    string? Password = null);

/// <summary>The state of one download as the UI and the status window see it.</summary>
public sealed record DownloadItem
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public DownloadCategory Category { get; init; }
    public DownloadStatus Status { get; init; }
    public int Priority { get; init; }
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }

    /// <summary>Bytes per second over the last few seconds; 0 when not downloading.</summary>
    public long SpeedBps { get; init; }

    public TimeSpan? Eta { get; init; }

    /// <summary>Progress of the current post-processing step (verify, repair, extract), 0..1.</summary>
    public double? StepProgress { get; init; }

    /// <summary>Human readable detail, e.g. "Repairing 3 blocks" or "Extracting part 4 of 12".</summary>
    public string? StatusMessage { get; init; }

    public int MissingArticles { get; init; }
    public string? Error { get; init; }
    public string? Indexer { get; init; }
    public MediaLink Link { get; init; } = MediaLink.None;

    /// <summary>Final folder with the extracted files (after <see cref="DownloadStatus.Completed"/>).</summary>
    public string? OutputPath { get; init; }

    public DateTimeOffset Added { get; init; }
    public DateTimeOffset? Completed { get; init; }

    public double Progress => TotalBytes <= 0 ? 0 : Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1);
}

/// <summary>Input for post-processing a finished download.</summary>
/// <param name="WorkFolder">Folder with the downloaded files (par2, rar, mkv …).</param>
/// <param name="TargetFolder">Where the final files go (created if needed).</param>
public sealed record PostProcessJob(int DownloadId, string Title, string WorkFolder, string TargetFolder, string? Password);

public enum PostProcessStage
{
    Verifying,
    Repairing,
    Extracting,
    Cleaning,
}

public sealed record PostProcessProgress(PostProcessStage Stage, double Progress, string? Message);

/// <param name="Repaired">Blocks were repaired with par2.</param>
public sealed record PostProcessResult(bool Success, string? Error, string OutputFolder, IReadOnlyList<string> Files, bool Repaired);
