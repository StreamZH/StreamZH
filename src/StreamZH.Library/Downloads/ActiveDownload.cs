// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Win32.SafeHandles;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Usenet.Nzb;

namespace StreamZH.Library.Downloads;

/// <summary>One file of a download in memory. Counters are guarded by the engine lock, the handle by the file itself.</summary>
internal sealed class ActiveFile
{
    public ActiveFile(DownloadFileRecord record, NzbFile nzb)
    {
        Id = record.Id;
        Index = record.FileIndex;
        Nzb = nzb;
        Name = record.FileName;
        Kind = nzb.Kind;
        Wanted = record.Wanted;
        Done = SegmentBits.Load(record.Done, nzb.Segments.Count);
        Missing = SegmentBits.Load(record.Missing, nzb.Segments.Count);
        Recount();
    }

    public int Id { get; }
    public int Index { get; }
    public NzbFile Nzb { get; }
    public string Name { get; }
    public NzbFileKind Kind { get; }
    public bool Wanted { get; set; }
    public byte[] Done { get; }
    public byte[] Missing { get; }
    public int Processed { get; set; }
    public long ProcessedBytes { get; set; }
    public int MissingArticles { get; set; }
    public long MissingBytes { get; set; }
    public int InFlight { get; set; }
    public bool Dirty { get; set; }

    /// <summary>Real file size from the yEnc header (0 until the first article arrived).</summary>
    public long KnownSize { get; set; }

    public SafeFileHandle? Handle { get; set; }

    public int Segments => Nzb.Segments.Count;

    public long Bytes => Nzb.Bytes;

    public bool Complete => Processed >= Segments;

    public bool IsProcessed(int segment) => SegmentBits.Get(Done, segment) || SegmentBits.Get(Missing, segment);

    public void Recount()
    {
        Processed = 0;
        ProcessedBytes = 0;
        MissingArticles = 0;
        MissingBytes = 0;
        for (var i = 0; i < Segments; i++)
        {
            var bytes = Nzb.Segments[i].Bytes;
            if (SegmentBits.Get(Missing, i))
            {
                MissingArticles++;
                MissingBytes += bytes;
            }

            if (IsProcessed(i))
            {
                Processed++;
                ProcessedBytes += bytes;
            }
        }
    }

    /// <summary>Forgets the missing segments so they are requested again (retry).</summary>
    public void ClearMissing()
    {
        Array.Clear(Missing);
        Recount();
        Dirty = true;
    }

    public DownloadFileRecord ToRecord(int downloadId) => new()
    {
        Id = Id,
        DownloadId = downloadId,
        FileIndex = Index,
        FileName = Name,
        Kind = (int)Kind,
        TotalBytes = Bytes,
        Segments = Segments,
        Wanted = Wanted,
        DownloadedBytes = ProcessedBytes,
        MissingArticles = MissingArticles,
        Done = (byte[])Done.Clone(),
        Missing = (byte[])Missing.Clone(),
    };
}

/// <summary>A download the engine works on (queued, downloading, paused or post-processing). Guarded by the engine lock.</summary>
internal sealed class ActiveDownload
{
    public required int Id { get; init; }
    public required string Title { get; set; }
    public DownloadCategory Category { get; set; }
    public DownloadStatus Status { get; set; }
    public int Priority { get; set; }
    public int SortOrder { get; set; }
    public int MissingArticles => Files.Sum(f => f.MissingArticles);
    public string? Error { get; set; }
    public string? StatusMessage { get; set; }
    public double? StepProgress { get; set; }
    public string? Indexer { get; init; }
    public MediaLink Link { get; init; } = MediaLink.None;
    public string? Password { get; init; }
    public required string NzbPath { get; init; }
    public required string WorkFolder { get; init; }
    public string? OutputPath { get; set; }
    public DateTime Added { get; init; }
    public DateTime? Completed { get; set; }
    public List<ActiveFile> Files { get; } = [];

    /// <summary>Segments to fetch, in order. Built when the download first gets a connection.</summary>
    public Queue<(ActiveFile File, int Segment)>? Pending { get; set; }

    /// <summary>Segments handed back (pause, server trouble); fetched before <see cref="Pending"/>.</summary>
    public Stack<(ActiveFile File, int Segment)> Retry { get; } = new();

    public int InFlight { get; set; }
    public CancellationTokenSource Cancellation { get; set; } = new();

    /// <summary>Set when the download was deleted or failed: late articles are dropped, files stay closed.</summary>
    public bool Closed { get; set; }

    /// <summary>Bytes actually received (for the speed), not saved.</summary>
    public long ReceivedBytes { get; set; }

    public SpeedMeter Speed { get; } = new();

    public long TotalBytes => Files.Where(f => f.Wanted).Sum(f => f.Bytes);

    public long DownloadedBytes => Files.Where(f => f.Wanted).Sum(f => f.ProcessedBytes);

    public bool IsDownloadPhase => Status is DownloadStatus.Queued or DownloadStatus.Downloading;

    public bool IsPostProcessing => Status is DownloadStatus.Verifying or DownloadStatus.Repairing or DownloadStatus.Extracting;

    public bool HasWork => Retry.Count > 0 || (Pending?.Count ?? 1) > 0;

    /// <summary>Par2 index files first (smallest first), then the data in name order, then wanted recovery volumes (smallest first).</summary>
    public void BuildPending()
    {
        var queue = new Queue<(ActiveFile, int)>();
        foreach (var file in Files.Where(f => f.Wanted).OrderBy(Rank).ThenBy(f => Rank(f) == 1 ? 0 : f.Bytes)
                     .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            Enqueue(queue, file);
        }

        Retry.Clear();
        Pending = queue;
    }

    public void EnqueueFile(ActiveFile file)
    {
        if (Pending is null)
        {
            return; // built (with this file) when the download starts
        }

        Enqueue(Pending, file);
    }

    public DownloadItem ToItem()
    {
        var downloading = Status == DownloadStatus.Downloading;
        var speed = downloading ? Speed.BytesPerSecond : 0;
        var remaining = Math.Max(0, TotalBytes - DownloadedBytes);
        return new DownloadItem
        {
            Id = Id,
            Title = Title,
            Category = Category,
            Status = Status,
            Priority = Priority,
            TotalBytes = TotalBytes,
            DownloadedBytes = DownloadedBytes,
            SpeedBps = speed,
            Eta = speed > 0 ? TimeSpan.FromSeconds(remaining / (double)speed) : null,
            StepProgress = StepProgress,
            StatusMessage = StatusMessage,
            MissingArticles = MissingArticles,
            Error = Error,
            Indexer = Indexer,
            Link = Link,
            OutputPath = OutputPath,
            Added = new DateTimeOffset(Added, TimeSpan.Zero),
            Completed = Completed is { } c ? new DateTimeOffset(c, TimeSpan.Zero) : null,
        };
    }

    public DownloadRecord ToRecord() => new()
    {
        Id = Id,
        Title = Title,
        Category = Category,
        Status = Status,
        Priority = Priority,
        TotalBytes = TotalBytes,
        DownloadedBytes = DownloadedBytes,
        MissingArticles = MissingArticles,
        Error = Error,
        StatusMessage = StatusMessage,
        Indexer = Indexer,
        MediaType = Link.MediaType,
        MovieId = Link.MovieId,
        SeriesId = Link.SeriesId,
        EpisodeIds = Link.EpisodeIds,
        Password = Password,
        NzbPath = NzbPath,
        WorkFolder = WorkFolder,
        OutputPath = OutputPath,
        Added = Added,
        Completed = Completed,
        SortOrder = SortOrder,
    };

    private static int Rank(ActiveFile file) => file.Kind switch
    {
        NzbFileKind.Par2Index => 0,
        NzbFileKind.Par2Recovery => 2,
        _ => 1,
    };

    private static void Enqueue(Queue<(ActiveFile, int)> queue, ActiveFile file)
    {
        for (var i = 0; i < file.Segments; i++)
        {
            if (!file.IsProcessed(i))
            {
                queue.Enqueue((file, i));
            }
        }
    }
}
