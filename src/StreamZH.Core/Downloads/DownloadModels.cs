// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;

namespace StreamZH.Core.Downloads;

/// <summary>A Usenet provider account. Several can be configured: lower <see cref="Priority"/> first, the others fill gaps (backup/block accounts).</summary>
/// <param name="Priority">0 = primary. Higher numbers are only asked for articles the lower ones do not have.</param>
/// <param name="RetentionDays">0 = unknown/unlimited; articles older than this are not requested from this server.</param>
/// <param name="TimeoutSeconds">Read timeout per connection (the connect timeout is at most this long).</param>
/// <param name="CertificateCheck">How strictly the SSL certificate is checked (only with <see cref="UseSsl"/>).</param>
/// <param name="ArticlesPerRequest">Pipelining: BODY commands sent on one connection before the first answer is read.</param>
/// <param name="OnFailure">What happens while the server cannot be used (connection or login errors).</param>
/// <param name="ExpiresOn">"yyyy-MM-dd" when the account ends; only for a notice in the status window.</param>
/// <param name="QuotaBytes">Download quota (block account, plan limit); null = none. When used up the server is skipped.</param>
/// <param name="QuotaUsedBytes">Bytes downloaded since <paramref name="QuotaSince"/> (the quota was set or the counter reset).</param>
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
    int RetentionDays,
    int TimeoutSeconds = UsenetServer.DefaultTimeoutSeconds,
    CertificateCheck CertificateCheck = CertificateCheck.Strict,
    int ArticlesPerRequest = UsenetServer.DefaultArticlesPerRequest,
    ServerFailureAction OnFailure = ServerFailureAction.Skip,
    string? ExpiresOn = null,
    long? QuotaBytes = null,
    long QuotaUsedBytes = 0,
    DateTime? QuotaSince = null,
    string Notes = "")
{
    public const int DefaultTimeoutSeconds = 60;
    public const int DefaultArticlesPerRequest = 1;

    /// <summary>One "GB" of a quota: providers count in powers of ten.</summary>
    public const long BytesPerGb = 1_000_000_000;

    /// <summary><see cref="ExpiresOn"/> as a date; null when not set or not a valid date.</summary>
    public DateOnly? ExpiryDate =>
        DateOnly.TryParseExact(ExpiresOn, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : null;

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds > 0 ? TimeoutSeconds : DefaultTimeoutSeconds);
}

/// <summary>How the SSL certificate of a Usenet server is checked.</summary>
public enum CertificateCheck
{
    /// <summary>Full validation by the operating system: trusted, not expired, issued for the host name.</summary>
    Strict,

    /// <summary>A certificate is required and must be issued for the host name, but may be self-signed, untrusted or expired.</summary>
    Minimal,

    /// <summary>Anything is accepted. Unsafe: someone in between can read the login.</summary>
    Off,
}

/// <summary>What happens while a server cannot be used (connection refused, timeouts, login or SSL errors, quota used up).</summary>
public enum ServerFailureAction
{
    /// <summary>The server is skipped for a few minutes; the other servers carry on.</summary>
    Skip,

    /// <summary>"Required": downloads pause until the server works again, so articles are not wrongly marked as missing.</summary>
    PauseDownloads,

    /// <summary>"Optional": an unreliable server, skipped for longer; its errors do not show in the status window.</summary>
    IgnoreLonger,
}

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
/// <param name="Priority">-1 low … 2 force; null = the priority of the category (normal when it has none).</param>
/// <param name="CategoryName">A download category by name (Settings → Downloads → Categories); null = the default one of <paramref name="Category"/>.</param>
public sealed record DownloadRequest(
    byte[] Nzb,
    string Title,
    DownloadCategory Category,
    MediaLink Link,
    int? Priority = null,
    string? Indexer = null,
    string? Password = null,
    string? CategoryName = null)
{
    /// <summary>0.11: Torrent requests carry <see cref="TorrentFile"/> or <see cref="MagnetUrl"/> instead of an NZB (Nzb is empty).</summary>
    public DownloadProtocol Protocol { get; init; } = DownloadProtocol.Usenet;

    public byte[]? TorrentFile { get; init; }

    public string? MagnetUrl { get; init; }

    /// <summary>Torrent only: seed limits from the indexer (null = client default).</summary>
    public double? SeedRatio { get; init; }

    public int? SeedTimeMinutes { get; init; }

    /// <summary>The download client to use (0.11, DownloadClients.Id); null = the default client for the protocol.</summary>
    public int? ClientId { get; init; }
}

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

    /// <summary>Name of the download category ("movies", "series", "other" or a custom one); its folder holds the result.</summary>
    public string? CategoryName { get; init; }

    /// <summary>Duplicate detection found the same release in the queue or the history (only when it is set to "tag" or "pause").</summary>
    public bool Duplicate { get; init; }

    /// <summary>0.11: Usenet downloads run in the built-in engine; torrents in a torrent client (external or built in).</summary>
    public DownloadProtocol Protocol { get; init; } = DownloadProtocol.Usenet;

    /// <summary>Torrent: the DownloadClients row that handles it.</summary>
    public int? ClientId { get; init; }

    /// <summary>Torrent: the info hash in the client.</summary>
    public string? ExternalId { get; init; }

    /// <summary>Torrent: still seeding after completion (files must be hardlinked/copied on import, never moved).</summary>
    public bool Seeding { get; init; }

    public double? Ratio { get; init; }

    public long UploadedBytes { get; init; }

    public int? Seeders { get; init; }

    public int? Peers { get; init; }

    public double Progress => TotalBytes <= 0 ? 0 : Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1);
}

/// <summary>Input for post-processing a finished download.</summary>
/// <param name="WorkFolder">Folder with the downloaded files (par2, rar, mkv …).</param>
/// <param name="TargetFolder">Where the final files go (created if needed).</param>
/// <param name="KeepFiles">After success, leave the work folder (par2, archives, nfo …) instead of deleting it.</param>
/// <param name="Level">What is done (the category's post-processing): nothing, repair, repair + unpack, or all of it plus cleanup.</param>
/// <param name="ContinueWhenUnverified">Unpack and complete even when par2 cannot verify or repair the files.</param>
public sealed record PostProcessJob(
    int DownloadId,
    string Title,
    string WorkFolder,
    string TargetFolder,
    string? Password,
    bool KeepFiles = false,
    PostProcessLevel Level = PostProcessLevel.Full,
    bool ContinueWhenUnverified = false);

/// <summary>Post-processing of a category, like SABnzbd's "+Repair", "+Unpack" and "+Delete".</summary>
public enum PostProcessLevel
{
    /// <summary>Files are moved to the completed folder as they are.</summary>
    None,

    /// <summary>Verify and repair with par2; archives and par2 files are moved along.</summary>
    Repair,

    /// <summary>Repair and unpack; the archives and par2 files stay next to the unpacked files.</summary>
    Unpack,

    /// <summary>Repair, unpack and delete archives, par2 and the cleanup list (the default).</summary>
    Full,
}

public enum PostProcessStage
{
    Verifying,
    Repairing,
    Extracting,
    Cleaning,
}

public sealed record PostProcessProgress(PostProcessStage Stage, double Progress, string? Message);

/// <param name="Repaired">Blocks were repaired with par2.</param>
/// <param name="Warning">Something the user should know about a successful run (unverified files, unwanted files left out).</param>
public sealed record PostProcessResult(bool Success, string? Error, string OutputFolder, IReadOnlyList<string> Files, bool Repaired, string? Warning = null);
