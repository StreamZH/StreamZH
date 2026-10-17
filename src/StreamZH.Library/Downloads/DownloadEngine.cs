// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using System.Xml;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Search;
using StreamZH.Parsing.Releases;
using StreamZH.Usenet.Nzb;

namespace StreamZH.Library.Downloads;

/// <summary>One file of a download as the detail view shows it. <see cref="Wanted"/> is false for held-back par2 recovery volumes.</summary>
public sealed record DownloadFileInfo(string Name, long Size, long Downloaded, NzbFileKind Kind, int MissingArticles, bool Wanted);

/// <summary>State of the whole queue. <see cref="StatusMessage"/> explains why nothing downloads (disk full, no server, …).</summary>
public sealed record DownloadQueueStatus(long SpeedBps, bool Paused, long? FreeSpaceBytes, long SpeedLimitKBps, string? StatusMessage);

public enum DownloadActionResult
{
    Ok,
    NotFound,

    /// <summary>Not possible in the current state (e.g. retry of a download that did not fail).</summary>
    Invalid,
}

/// <summary>
/// The built-in Usenet downloader (SABnzbd style). Workers, one per server connection, take the next missing segment
/// of the first download in queue order (priority, then position), so one download gets all connections and the next
/// one starts as soon as the first runs out of unassigned segments. Decoded articles are written in place at their
/// offset; which segments are done is kept as a bitmap per file and saved every tick, so a restart resumes where it
/// stopped. Par2 recovery volumes are held back: they are only fetched when articles are missing or damaged (just enough
/// to cover the missing bytes), or when post-processing reports "Not enough recovery blocks" while volumes are still
/// held back (then all of them, and post-processing runs once more).
/// </summary>
public sealed class DownloadEngine(
    DownloadRepository repository,
    HistoryRepository history,
    DownloadSettings settings,
    ConfigRepository config,
    IArticleSource articles,
    IPostProcessor postProcessor,
    IEventBus events,
    IFreeSpaceProvider freeSpace,
    DownloadEngineOptions options,
    ILogger<DownloadEngine> logger) : BackgroundService, IDownloadQueue
{
    public const int MinPriority = -1;
    public const int MaxPriority = 2;
    private const int MaxWorkers = 500;

    /// <summary>Start of the post-processor's error when the par2 volumes in the work folder cannot repair the damage.</summary>
    public const string NotEnoughRecoveryBlocks = "Not enough recovery blocks";

    private readonly Lock _lock = new();
    private readonly Lock _checkpointLock = new();
    private readonly SemaphoreSlim _addLock = new(1, 1);
    private readonly Dictionary<int, ActiveDownload> _items = [];

    // Completed or failed, but not saved yet: Find answers from memory until the database has the final state.
    private readonly Dictionary<int, ActiveDownload> _finishing = [];
    private readonly HashSet<ActiveDownload> _dirty = [];
    private readonly ConcurrentQueue<IAppEvent> _outbox = new();
    private readonly ConcurrentQueue<HistoryItem> _historyOutbox = new();
    private readonly Channel<int> _postProcess = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly AsyncPulse _work = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Task> _workers = [];
    private readonly SpeedLimiter _limiter = new(() => settings.SpeedLimitKBps);
    private List<ActiveDownload> _order = [];
    private bool _loaded;
    private bool _saveNow;
    private bool _userPaused;
    private bool _diskPaused;
    private string? _diskMessage;
    private long? _freeBytes;
    private int _maxConnections;
    private DateTime _serverRetryAt = DateTime.MinValue;
    private TimeSpan _serverDelay = TimeSpan.Zero;
    private string? _serverMessage;

    private enum SegmentResult
    {
        Done,
        Damaged,
        Missing,
    }

    // ---------------------------------------------------------------- queue (IDownloadQueue)

    public IReadOnlyList<DownloadItem> Active()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return [.. _order.Select(d => d.ToItem())];
        }
    }

    public DownloadItem? Find(int id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_items.TryGetValue(id, out var active) || _finishing.TryGetValue(id, out active))
            {
                return active.ToItem();
            }
        }

        return repository.Find(id) is { } record ? ToItem(record) : null;
    }

    /// <summary>Completed and failed downloads, newest first.</summary>
    public (IReadOnlyList<DownloadItem> Items, int Total) History(int page, int pageSize)
    {
        var (records, total) = repository.History(page, pageSize);
        return ([.. records.Select(ToItem)], total);
    }

    /// <summary>The files of a download (NZB order), or null when the download does not exist.</summary>
    public IReadOnlyList<DownloadFileInfo>? Files(int id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_items.TryGetValue(id, out var active))
            {
                return [.. active.Files.Select(f => new DownloadFileInfo(f.Name, f.Bytes, f.ProcessedBytes, f.Kind, f.MissingArticles, f.Wanted))];
            }
        }

        if (repository.Find(id) is null)
        {
            return null;
        }

        return [.. repository.Files(id).Select(f => new DownloadFileInfo(f.FileName, f.TotalBytes, f.DownloadedBytes, (NzbFileKind)f.Kind, f.MissingArticles, f.Wanted))];
    }

    public DownloadQueueStatus Status()
    {
        EnsureLoaded();
        var connections = Math.Max(0, articles.MaxConnections);
        var free = _freeBytes ?? freeSpace.FreeBytes(settings.IncompleteFolder);
        lock (_lock)
        {
            string? message;
            if (_diskPaused)
            {
                message = _diskMessage;
            }
            else if (_userPaused)
            {
                message = null;
            }
            else if (connections == 0 && _order.Any(d => d.IsDownloadPhase))
            {
                message = "No Usenet server is configured. Add one in Settings → Usenet servers.";
            }
            else
            {
                message = _serverMessage;
            }

            return new DownloadQueueStatus(_order.Sum(d => d.ToItem().SpeedBps), _userPaused || _diskPaused, free, settings.SpeedLimitKBps, message);
        }
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Parses and stores the NZB and puts it at the end of the queue (within its priority).</summary>
    /// <exception cref="LibraryException">The NZB is empty or invalid, or it could not be stored.</exception>
    public async Task<DownloadItem> AddAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureLoaded();
        var nzb = ParseNzb(request.Nzb);
        var title = request.Title.Trim() is { Length: > 0 } t ? t
            : nzb.Meta.TryGetValue("title", out var metaTitle) && metaTitle.Length > 0 ? metaTitle : "Download";
        var priority = Math.Clamp(request.Priority, MinPriority, MaxPriority);

        await _addLock.WaitAsync(cancellationToken);
        try
        {
            string workFolder;
            lock (_lock)
            {
                workFolder = UniqueFolder(settings.IncompleteFolder, title, _items.Values.Select(d => d.WorkFolder));
            }

            var names = UniqueFileNames(nzb.Files);
            var onlyRecovery = nzb.Files.All(f => f.Kind == NzbFileKind.Par2Recovery);
            var fileRecords = nzb.Files.Select((f, i) => new DownloadFileRecord
            {
                FileIndex = i,
                FileName = names[i],
                Kind = (int)f.Kind,
                TotalBytes = f.Bytes,
                Segments = f.Segments.Count,
                Wanted = onlyRecovery || f.Kind != NzbFileKind.Par2Recovery,
                Done = SegmentBits.Create(f.Segments.Count),
                Missing = SegmentBits.Create(f.Segments.Count),
            }).ToList();

            var record = repository.Add(
                new DownloadRecord
                {
                    Title = title,
                    Category = request.Category,
                    Status = DownloadStatus.Queued,
                    Priority = priority,
                    TotalBytes = fileRecords.Where(f => f.Wanted).Sum(f => f.TotalBytes),
                    Indexer = request.Indexer,
                    MediaType = request.Link.MediaType,
                    MovieId = request.Link.MovieId,
                    SeriesId = request.Link.SeriesId,
                    EpisodeIds = request.Link.EpisodeIds,
                    Password = string.IsNullOrWhiteSpace(request.Password) ? nzb.Password : request.Password,
                    NzbPath = string.Empty,
                    WorkFolder = workFolder,
                    Added = DateTime.UtcNow,
                },
                fileRecords);

            var nzbPath = Path.Combine(settings.NzbQueueFolder, $"{record.Id.ToString(CultureInfo.InvariantCulture)}.nzb");
            try
            {
                Directory.CreateDirectory(settings.NzbQueueFolder);
                await File.WriteAllBytesAsync(nzbPath, request.Nzb, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                repository.Delete(record.Id);
                throw new LibraryException($"Could not store the NZB file in {settings.NzbQueueFolder}: {ex.Message}", LibraryError.Invalid);
            }

            repository.SetPaths(record.Id, nzbPath, workFolder);
            var download = Activate(record with { NzbPath = nzbPath }, nzb, repository.Files(record.Id));

            DownloadItem item;
            lock (_lock)
            {
                _items[download.Id] = download;
                Resort();
                MarkChanged(download);
                _outbox.Enqueue(new OngoingChanged());
                item = download.ToItem();
            }

            logger.LogInformation(
                "Queued download {Id} '{Title}' ({Files} files, {Bytes} bytes) from {Indexer}", item.Id, title, nzb.Files.Count, item.TotalBytes, request.Indexer ?? "upload");
            _work.Pulse();
            await FlushAsync();
            return item;
        }
        finally
        {
            _addLock.Release();
        }
    }

    public async Task<DownloadActionResult> PauseAsync(int id)
    {
        EnsureLoaded();
        DownloadActionResult result;
        lock (_lock)
        {
            if (!_items.TryGetValue(id, out var d))
            {
                return Exists(id) ? DownloadActionResult.Invalid : DownloadActionResult.NotFound;
            }

            if (d.IsDownloadPhase)
            {
                d.Status = DownloadStatus.Paused;
                d.StatusMessage = null;
                CancelInFlight(d);
                d.Speed.Reset();
                MarkChanged(d);
                result = DownloadActionResult.Ok;
            }
            else
            {
                // Paused already, or post-processing (runs to the end, like SABnzbd).
                result = d.Status == DownloadStatus.Paused ? DownloadActionResult.Ok : DownloadActionResult.Invalid;
            }
        }

        await FlushAsync();
        return result;
    }

    public async Task<DownloadActionResult> ResumeAsync(int id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_items.TryGetValue(id, out var d))
            {
                return Exists(id) ? DownloadActionResult.Invalid : DownloadActionResult.NotFound;
            }

            if (d.Status == DownloadStatus.Paused)
            {
                d.Status = DownloadStatus.Queued;
                MarkChanged(d);
            }
        }

        _work.Pulse();
        await FlushAsync();
        return DownloadActionResult.Ok;
    }

    /// <summary>Pauses the whole queue (kept over restarts). Articles being fetched right now still finish.</summary>
    public async Task PauseAllAsync()
    {
        EnsureLoaded();
        lock (_lock)
        {
            _userPaused = true;
            QueueChanged();
        }

        config.Set(ConfigKeys.DownloadsPaused, "true");
        logger.LogInformation("Download queue paused");
        await FlushAsync();
    }

    public async Task ResumeAllAsync()
    {
        EnsureLoaded();
        lock (_lock)
        {
            _userPaused = false;
            QueueChanged();
        }

        config.Set(ConfigKeys.DownloadsPaused, "false");
        logger.LogInformation("Download queue resumed");
        _work.Pulse();
        await FlushAsync();
    }

    /// <summary>Puts a failed download back into the queue. Segments already written are kept; missing ones are asked for again.</summary>
    /// <exception cref="LibraryException">The queued NZB file is gone or unreadable.</exception>
    public async Task<DownloadActionResult> RetryAsync(int id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_items.ContainsKey(id))
            {
                return DownloadActionResult.Invalid;
            }
        }

        Checkpoint(); // a download that failed a moment ago may not be saved yet
        var record = repository.Find(id);
        if (record is null)
        {
            return DownloadActionResult.NotFound;
        }

        if (record.Status != DownloadStatus.Failed)
        {
            return DownloadActionResult.Invalid;
        }

        NzbDocument nzb;
        try
        {
            nzb = ParseNzb(await File.ReadAllBytesAsync(record.NzbPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LibraryException($"The NZB of this download is gone ({ex.Message}). Grab the release again.", LibraryError.Invalid);
        }

        var files = repository.Files(id);
        if (files.Count != nzb.Files.Count)
        {
            throw new LibraryException("The stored NZB does not match this download any more. Grab the release again.", LibraryError.Invalid);
        }

        var download = Activate(record with { Status = DownloadStatus.Queued, Error = null, StatusMessage = null, Completed = null, OutputPath = null }, nzb, files);
        foreach (var file in download.Files)
        {
            file.ClearMissing();
        }

        lock (_lock)
        {
            _finishing.Remove(download.Id);
            _items[download.Id] = download;
            Resort();
            MarkChanged(download);
            _outbox.Enqueue(new OngoingChanged());
        }

        logger.LogInformation("Retrying download {Id} '{Title}'", id, record.Title);
        _work.Pulse();
        await FlushAsync();
        return DownloadActionResult.Ok;
    }

    /// <summary>
    /// Changes priority, category and/or position (0 = top of the queue; the order applies within a priority).
    /// Null when the download is not in the queue (unknown, completed or failed).
    /// </summary>
    public async Task<DownloadItem?> UpdateAsync(int id, int? priority, DownloadCategory? category, int? position)
    {
        EnsureLoaded();
        DownloadItem item;
        List<(int, int)>? orders = null;
        lock (_lock)
        {
            if (!_items.TryGetValue(id, out var d))
            {
                return null;
            }

            if (priority is { } p)
            {
                d.Priority = Math.Clamp(p, MinPriority, MaxPriority);
            }

            if (category is { } c)
            {
                d.Category = c;
            }

            Resort();
            if (position is { } target)
            {
                _order.Remove(d);
                _order.Insert(Math.Clamp(target, 0, _order.Count), d);
                for (var i = 0; i < _order.Count; i++)
                {
                    _order[i].SortOrder = i + 1;
                }

                orders = [.. _order.Select(o => (o.Id, o.SortOrder))];
                Resort();
            }

            MarkChanged(d);
            item = d.ToItem();
        }

        if (orders is not null)
        {
            repository.SetSortOrders(orders);
        }

        _work.Pulse();
        await FlushAsync();
        return item;
    }

    /// <summary>Removes a download from the queue or the history. <paramref name="deleteFiles"/> also removes its incomplete folder;
    /// the final (complete) folder is never touched.</summary>
    public async Task<DownloadActionResult> DeleteAsync(int id, bool deleteFiles)
    {
        EnsureLoaded();
        ActiveDownload? active;
        lock (_lock)
        {
            _finishing.Remove(id);
            if (_items.Remove(id, out active))
            {
                active.Closed = true;
                active.Cancellation.Cancel();
                _order.Remove(active);
                _dirty.Remove(active);
            }
        }

        string nzbPath;
        string workFolder;
        if (active is not null)
        {
            // Let the workers hand back what they were fetching before the files are closed and removed.
            for (var waited = 0; waited < 200 && InFlight(active) > 0; waited++)
            {
                await Task.Delay(50);
            }

            CloseFiles(active);
            nzbPath = active.NzbPath;
            workFolder = active.WorkFolder;
        }
        else if (repository.Find(id) is { } record)
        {
            nzbPath = record.NzbPath;
            workFolder = record.WorkFolder;
        }
        else
        {
            return DownloadActionResult.NotFound;
        }

        repository.Delete(id);
        TryDelete(() => File.Delete(nzbPath));
        if (deleteFiles && Directory.Exists(workFolder))
        {
            TryDelete(() => Directory.Delete(workFolder, recursive: true));
        }

        logger.LogInformation("Removed download {Id}{Files}", id, deleteFiles ? " and its incomplete files" : string.Empty);
        _outbox.Enqueue(new DownloadChanged(id));
        _outbox.Enqueue(new OngoingChanged());
        await FlushAsync();
        return DownloadActionResult.Ok;
    }

    // ---------------------------------------------------------------- background service

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureLoaded();
        var postProcessing = Task.Run(() => PostProcessLoopAsync(stoppingToken), CancellationToken.None);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                EnsureWorkers(stoppingToken);
                Tick();
                await FlushAsync();
                try
                {
                    await Task.Delay(options.Tick, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            Task[] running;
            lock (_lock)
            {
                running = [.. _workers, postProcessing];
            }

            try
            {
                await Task.WhenAll(running);
            }
            catch (OperationCanceledException)
            {
            }

            lock (_lock)
            {
                foreach (var d in _items.Values)
                {
                    CloseFiles(d);
                }
            }

            Checkpoint();
            logger.LogInformation("Download engine stopped");
        }
    }

    /// <summary>Speeds, progress event, disk-space guard, end-of-download detection and saving. Runs every tick.</summary>
    internal void Tick()
    {
        var connections = Math.Max(0, articles.MaxConnections);
        var folder = settings.IncompleteFolder;
        var free = freeSpace.FreeBytes(folder);
        var minFree = settings.MinFreeSpaceBytes;
        var now = _clock.Elapsed.TotalSeconds;
        List<DownloadProgressItem>? progress = null;

        lock (_lock)
        {
            _maxConnections = connections;
            _freeBytes = free;

            if (free is { } bytes && bytes < minFree && !_diskPaused)
            {
                _diskPaused = true;
                _diskMessage = $"Paused: only {FormatBytes(bytes)} free in {folder}. Downloads continue when at least {FormatBytes(minFree)} are free.";
                logger.LogWarning("Download queue paused, only {Free} bytes free in {Folder}", bytes, folder);
                QueueChanged();
            }
            else if (_diskPaused && (free is null || free >= minFree + (minFree / 2)))
            {
                _diskPaused = false;
                _diskMessage = null;
                logger.LogInformation("Enough disk space again, download queue continues");
                QueueChanged();
                _work.Pulse();
            }

            foreach (var d in _order.ToList())
            {
                if (d.Status == DownloadStatus.Downloading)
                {
                    d.Speed.Sample(now, d.ReceivedBytes);
                }

                if (d.IsDownloadPhase && !d.Closed)
                {
                    d.Pending ??= BuildPendingFor(d);
                    if (d.InFlight == 0 && !d.HasWork)
                    {
                        DownloadPhaseFinished(d);
                    }
                }
            }

            if (_order.Any(d => d.Status == DownloadStatus.Downloading || d.IsPostProcessing))
            {
                progress = [.. _order.Select(d =>
                {
                    var item = d.ToItem();
                    return new DownloadProgressItem(d.Id, d.Status, item.Progress, item.SpeedBps, item.Eta?.TotalSeconds, d.StepProgress, d.StatusMessage);
                })];
            }
        }

        if (progress is not null)
        {
            _outbox.Enqueue(new DownloadProgress(progress));
        }

        Checkpoint();
    }

    private void EnsureWorkers(CancellationToken stoppingToken)
    {
        var wanted = Math.Min(MaxWorkers, Math.Max(0, articles.MaxConnections));
        lock (_lock)
        {
            while (_workers.Count < wanted)
            {
                var index = _workers.Count;
                _workers.Add(Task.Run(() => WorkerAsync(index, stoppingToken), CancellationToken.None));
            }
        }
    }

    private async Task WorkerAsync(int index, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Work? work = null;
            lock (_lock)
            {
                if (index < _maxConnections && !_userPaused && !_diskPaused && DateTime.UtcNow >= _serverRetryAt)
                {
                    work = TakeSegment();
                }
            }

            if (work is null)
            {
                try
                {
                    await _work.WaitAsync(options.Tick, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            await ProcessAsync(work, stoppingToken);
            await FlushAsync();
        }
    }

    private Work? TakeSegment()
    {
        foreach (var d in _order)
        {
            if (!d.IsDownloadPhase || d.Closed)
            {
                continue;
            }

            d.Pending ??= BuildPendingFor(d);
            while (d.Retry.Count > 0 || d.Pending.Count > 0)
            {
                var (file, segment) = d.Retry.Count > 0 ? d.Retry.Pop() : d.Pending.Dequeue();
                if (file.IsProcessed(segment) || !file.Wanted)
                {
                    continue;
                }

                d.InFlight++;
                file.InFlight++;
                if (d.Status == DownloadStatus.Queued)
                {
                    d.Status = DownloadStatus.Downloading;
                    d.StatusMessage = null;
                    d.Speed.Reset();
                    d.Speed.Sample(_clock.Elapsed.TotalSeconds, d.ReceivedBytes);
                    MarkChanged(d);
                }

                return new Work(d, file, segment, d.Cancellation.Token);
            }
        }

        return null;
    }

    private async Task ProcessAsync(Work work, CancellationToken stoppingToken)
    {
        var (d, file, index, token) = work;
        var segment = file.Nzb.Segments[index];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, token);
        DecodedArticle article;
        try
        {
            article = await articles.FetchAsync(segment.MessageId, file.Nzb.Groups, file.Nzb.Date, linked.Token);
            ServerAvailable();
            await _limiter.WaitAsync(article.Data.Length, linked.Token);
        }
        catch (ArticleNotFoundException)
        {
            ServerAvailable();
            Finish(d, file, index, SegmentResult.Missing, 0);
            return;
        }
        catch (OperationCanceledException)
        {
            GiveBack(d, file, index);
            return;
        }
        catch (UsenetServerException ex)
        {
            GiveBack(d, file, index);
            ServerUnavailable(ex);
            return;
        }
        catch (Exception ex)
        {
            // A broken article (bad yEnc, unexpected server answer): treat it like a missing one, par2 may repair it.
            logger.LogWarning("Article <{MessageId}> of '{Title}' could not be used: {Message}", segment.MessageId, d.Title, ex.Message);
            Finish(d, file, index, SegmentResult.Missing, 0);
            return;
        }

        try
        {
            Write(d, file, article);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            GiveBack(d, file, index);
            lock (_lock)
            {
                if (!d.Closed && _items.ContainsKey(d.Id))
                {
                    logger.LogError(ex, "Could not write download {Id} to {Folder}", d.Id, d.WorkFolder);
                    Fail(d, $"Could not write to {d.WorkFolder}: {ex.Message}");
                }
            }

            return;
        }

        Finish(d, file, index, article.CrcOk ? SegmentResult.Done : SegmentResult.Damaged, article.Data.Length);
    }

    private static void Write(ActiveDownload d, ActiveFile file, DecodedArticle article)
    {
        lock (file)
        {
            if (d.Closed)
            {
                return;
            }

            if (file.Handle is null)
            {
                Directory.CreateDirectory(d.WorkFolder);
                var path = Path.Combine(d.WorkFolder, file.Name);
                file.Handle = File.Exists(path)
                    ? File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
                    : File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, FileOptions.None, Math.Max(0, article.FileSize));
            }

            if (article.FileSize > file.KnownSize)
            {
                file.KnownSize = article.FileSize;
            }

            if (article.Data.Length > 0 && article.Offset >= 0)
            {
                RandomAccess.Write(file.Handle, article.Data, article.Offset);
            }
        }
    }

    private void Finish(ActiveDownload d, ActiveFile file, int index, SegmentResult result, int received)
    {
        lock (_lock)
        {
            d.InFlight--;
            file.InFlight--;
            if (d.Closed || !_items.ContainsKey(d.Id) || file.IsProcessed(index))
            {
                return;
            }

            var bytes = file.Nzb.Segments[index].Bytes;
            if (result == SegmentResult.Done)
            {
                SegmentBits.Set(file.Done, index);
            }
            else
            {
                SegmentBits.Set(file.Missing, index);
                file.MissingArticles++;
                file.MissingBytes += bytes;
            }

            file.Processed++;
            file.ProcessedBytes += bytes;
            file.Dirty = true;
            d.ReceivedBytes += received;
            _dirty.Add(d);

            if (result != SegmentResult.Done && settings.AbortHopeless && TooMuchMissing(d) is { } error)
            {
                Fail(d, error);
                return;
            }

            if (file.Complete && file.InFlight == 0)
            {
                CloseFile(file);
            }

            if (d.IsDownloadPhase && d.InFlight == 0 && !d.HasWork)
            {
                DownloadPhaseFinished(d);
            }
        }
    }

    private void GiveBack(ActiveDownload d, ActiveFile file, int index)
    {
        lock (_lock)
        {
            d.InFlight--;
            file.InFlight--;
            if (!d.Closed && !file.IsProcessed(index))
            {
                d.Retry.Push((file, index));
            }
        }

        _work.Pulse();
    }

    /// <summary>All wanted segments are written or missing: fetch par2 volumes if damage needs them, else post-process.</summary>
    private void DownloadPhaseFinished(ActiveDownload d)
    {
        CloseFiles(d);
        if (d.MissingArticles > 0)
        {
            // Recovery blocks are about as big as the data they replace; 20 % on top for block alignment.
            var recovery = d.Files.Where(f => f.Kind == NzbFileKind.Par2Recovery).ToList();
            var held = recovery.Where(f => !f.Wanted).OrderBy(f => f.Bytes).ToList();
            var available = recovery.Where(f => f.Wanted).Sum(f => f.Bytes - f.MissingBytes);
            var needed = (long)(MissingDataBytes(d) * 1.2) + 1 - available;
            if (held.Count > 0 && needed > 0)
            {
                var picked = new List<ActiveFile>();
                long sum = 0;
                foreach (var file in held)
                {
                    picked.Add(file);
                    sum += file.Bytes;
                    if (sum >= needed)
                    {
                        break;
                    }
                }

                RequestRecovery(d, picked);
                d.StatusMessage = $"{d.MissingArticles} articles missing, fetching {picked.Count} par2 recovery {(picked.Count == 1 ? "file" : "files")}";
                logger.LogInformation("Download {Id} misses {Missing} articles, fetching {Count} par2 recovery files", d.Id, d.MissingArticles, picked.Count);
                return;
            }
        }

        d.Status = DownloadStatus.Verifying;
        d.StatusMessage = "Waiting for post-processing";
        d.StepProgress = null;
        d.Speed.Reset();
        MarkChanged(d);
        _postProcess.Writer.TryWrite(d.Id);
    }

    private void RequestRecovery(ActiveDownload d, IReadOnlyList<ActiveFile> files)
    {
        foreach (var file in files)
        {
            file.Wanted = true;
            file.Dirty = true;
            d.EnqueueFile(file);
        }

        if (d.Status != DownloadStatus.Downloading)
        {
            d.Status = DownloadStatus.Queued;
        }

        MarkChanged(d);
        _work.Pulse();
    }

    /// <summary>Error text when more is missing than par2 could ever repair, else null.</summary>
    private string? TooMuchMissing(ActiveDownload d)
    {
        var dataBytes = d.Files.Where(f => f.Kind != NzbFileKind.Par2Recovery).Sum(f => f.Bytes);
        var missingData = MissingDataBytes(d);
        if (missingData == 0 || dataBytes == 0)
        {
            return null;
        }

        var recovery = d.Files.Where(f => f.Kind == NzbFileKind.Par2Recovery).ToList();
        var recoveryBytes = recovery.Sum(f => f.Bytes) - recovery.Sum(f => f.MissingBytes);
        var limit = recovery.Count > 0 ? recoveryBytes : (long)(dataBytes * options.MaxMissingRatioWithoutPar2);
        if (missingData <= limit)
        {
            return null;
        }

        var percent = (100.0 * missingData / dataBytes).ToString("0.#", CultureInfo.InvariantCulture);
        var reason = recovery.Count > 0 ? "more than the par2 recovery files can repair" : "and there are no par2 files to repair it";
        return $"{d.MissingArticles} articles are missing ({percent} % of the data), {reason}. The release is incomplete or was taken down; try another one.";
    }

    private static long MissingDataBytes(ActiveDownload d) => d.Files.Where(f => f.Kind != NzbFileKind.Par2Recovery).Sum(f => f.MissingBytes);

    private void Fail(ActiveDownload d, string error)
    {
        d.Status = DownloadStatus.Failed;
        d.Error = error;
        d.StatusMessage = null;
        d.StepProgress = null;
        d.Completed = DateTime.UtcNow;
        d.Closed = true;
        d.Cancellation.Cancel();
        d.Speed.Reset();
        CloseFiles(d);
        _items.Remove(d.Id);
        _order.Remove(d);
        _finishing[d.Id] = d;
        MarkChanged(d);
        _outbox.Enqueue(new OngoingChanged());
        _historyOutbox.Enqueue(HistoryFor(d, HistoryEventType.DownloadFailed));
        logger.LogWarning("Download {Id} '{Title}' failed: {Error}", d.Id, d.Title, error);
    }

    // ---------------------------------------------------------------- post-processing

    private async Task PostProcessLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in _postProcess.Reader.ReadAllAsync(stoppingToken))
            {
                await PostProcessAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PostProcessAsync(int id, CancellationToken stoppingToken)
    {
        ActiveDownload? d;
        PostProcessJob job;
        CancellationToken token;
        var keepFiles = settings.KeepFiles;
        lock (_lock)
        {
            if (!_items.TryGetValue(id, out d) || !d.IsPostProcessing || d.Closed)
            {
                return;
            }

            d.Status = DownloadStatus.Verifying;
            d.StatusMessage = "Verifying";
            d.StepProgress = 0;
            MarkChanged(d);
            var target = UniqueFolder(Path.Combine(settings.CompleteFolder, CategoryFolder(d.Category)), d.Title, [], onlyIfNotEmpty: true);
            job = new PostProcessJob(d.Id, d.Title, d.WorkFolder, target, d.Password, keepFiles);
            token = d.Cancellation.Token;
        }

        await FlushAsync();
        logger.LogInformation("Post-processing download {Id} '{Title}' into {Target}", id, job.Title, job.TargetFolder);

        PostProcessResult result;
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, token))
        {
            try
            {
                Directory.CreateDirectory(job.WorkFolder);
                result = await postProcessor.RunAsync(job, new ProgressSink(this, d), linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return; // shutting down (runs again after the restart) or deleted
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Post-processing of download {Id} crashed", id);
                result = new PostProcessResult(false, $"Post-processing failed: {ex.Message}", job.TargetFolder, [], false);
            }
        }

        if (result.Success)
        {
            // Clean up before the download shows as completed: whoever sees "completed" sees the final folders.
            var output = Path.GetFullPath(result.OutputFolder);
            var work = Path.GetFullPath(d.WorkFolder);
            if (!keepFiles && Directory.Exists(work) && !output.StartsWith(work, StringComparison.Ordinal) && !work.StartsWith(output, StringComparison.Ordinal))
            {
                TryDelete(() => Directory.Delete(work, recursive: true));
            }

            TryDelete(() => File.Delete(d.NzbPath));
        }

        lock (_lock)
        {
            if (d.Closed || !_items.ContainsKey(d.Id))
            {
                return;
            }

            var held = d.Files.Where(f => !f.Wanted && f.Kind == NzbFileKind.Par2Recovery).ToList();
            if (result.Success)
            {
                d.Status = DownloadStatus.Completed;
                d.OutputPath = result.OutputFolder;
                d.Completed = DateTime.UtcNow;
                d.StepProgress = null;
                d.Error = null;
                d.StatusMessage = result.Repaired ? "Repaired with par2" : null;
                d.Closed = true;
                _items.Remove(d.Id);
                _order.Remove(d);
                _finishing[d.Id] = d;
                MarkChanged(d);
                _outbox.Enqueue(new OngoingChanged());
                _historyOutbox.Enqueue(HistoryFor(d, HistoryEventType.DownloadCompleted));
                logger.LogInformation("Download {Id} '{Title}' completed: {Output}", d.Id, d.Title, result.OutputFolder);
            }
            else if (held.Count > 0 && result.Error?.StartsWith(NotEnoughRecoveryBlocks, StringComparison.OrdinalIgnoreCase) == true)
            {
                logger.LogInformation("Post-processing of download {Id} failed ({Error}), fetching all {Count} held par2 recovery files", d.Id, result.Error, held.Count);
                d.StepProgress = null;
                d.Status = DownloadStatus.Queued;
                RequestRecovery(d, held);
                d.StatusMessage = "Downloading repair blocks";
            }
            else
            {
                Fail(d, string.IsNullOrWhiteSpace(result.Error) ? "Post-processing failed." : result.Error);
            }
        }

        await FlushAsync();
    }

    private void ReportPostProcess(ActiveDownload d, PostProcessProgress progress)
    {
        lock (_lock)
        {
            if (d.Closed || !d.IsPostProcessing)
            {
                return;
            }

            var status = progress.Stage switch
            {
                PostProcessStage.Repairing => DownloadStatus.Repairing,
                PostProcessStage.Extracting or PostProcessStage.Cleaning => DownloadStatus.Extracting,
                _ => DownloadStatus.Verifying,
            };
            var changed = status != d.Status;
            d.Status = status;
            d.StepProgress = double.IsFinite(progress.Progress) ? Math.Clamp(progress.Progress, 0, 1) : null;
            d.StatusMessage = progress.Message ?? progress.Stage switch
            {
                PostProcessStage.Repairing => "Repairing",
                PostProcessStage.Extracting => "Extracting",
                PostProcessStage.Cleaning => "Cleaning up",
                _ => "Verifying",
            };
            if (changed)
            {
                MarkChanged(d);
            }
            else
            {
                _dirty.Add(d);
            }
        }
    }

    // ---------------------------------------------------------------- state helpers

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            _userPaused = string.Equals(config.Get(ConfigKeys.DownloadsPaused), "true", StringComparison.OrdinalIgnoreCase);
            foreach (var record in repository.Unfinished())
            {
                try
                {
                    var nzb = ParseNzb(File.ReadAllBytes(record.NzbPath));
                    var files = repository.Files(record.Id);
                    if (files.Count != nzb.Files.Count)
                    {
                        throw new LibraryException("The stored NZB does not match the download.", LibraryError.Invalid);
                    }

                    var status = record.Status == DownloadStatus.Downloading ? DownloadStatus.Queued : record.Status;
                    var d = Activate(record with { Status = status }, nzb, files);
                    _items[d.Id] = d;
                    if (d.IsPostProcessing)
                    {
                        d.Status = DownloadStatus.Verifying;
                        d.StatusMessage = "Waiting for post-processing";
                        _postProcess.Writer.TryWrite(d.Id);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibraryException)
                {
                    logger.LogWarning("Download {Id} '{Title}' cannot be resumed: {Message}", record.Id, record.Title, ex.Message);
                    repository.Update(record with
                    {
                        Status = DownloadStatus.Failed,
                        Error = $"Could not resume after a restart: {ex.Message}",
                        Completed = DateTime.UtcNow,
                    });
                }
            }

            Resort();
            if (_items.Count > 0)
            {
                logger.LogInformation("Download queue loaded: {Count} downloads", _items.Count);
            }
        }
    }

    private static ActiveDownload Activate(DownloadRecord record, NzbDocument nzb, IReadOnlyList<DownloadFileRecord> files)
    {
        var d = new ActiveDownload
        {
            Id = record.Id,
            Title = record.Title,
            Category = record.Category,
            Status = record.Status,
            Priority = record.Priority,
            SortOrder = record.SortOrder,
            Error = record.Error,
            StatusMessage = record.StatusMessage,
            Indexer = record.Indexer,
            Link = new MediaLink(record.MediaType, record.MovieId, record.SeriesId, record.EpisodeIds),
            Password = record.Password,
            NzbPath = record.NzbPath,
            WorkFolder = record.WorkFolder,
            OutputPath = record.OutputPath,
            Added = record.Added,
            Completed = record.Completed,
        };

        foreach (var file in files)
        {
            d.Files.Add(new ActiveFile(file, nzb.Files[file.FileIndex]));
        }

        return d;
    }

    private static Queue<(ActiveFile File, int Segment)> BuildPendingFor(ActiveDownload d)
    {
        d.BuildPending();
        return d.Pending!;
    }

    private void Resort() =>
        _order = [.. _items.Values.OrderByDescending(d => d.Priority).ThenBy(d => d.SortOrder).ThenBy(d => d.Id)];

    private void MarkChanged(ActiveDownload d)
    {
        _dirty.Add(d);
        _saveNow = true;
        _outbox.Enqueue(new DownloadChanged(d.Id));
    }

    /// <summary>Queue-wide change (pause all, disk guard): downloadChanged with id 0, and the status window.</summary>
    private void QueueChanged()
    {
        _outbox.Enqueue(new DownloadChanged(0));
        _outbox.Enqueue(new OngoingChanged());
    }

    private bool Exists(int id) => repository.Find(id) is not null;

    private int InFlight(ActiveDownload d)
    {
        lock (_lock)
        {
            return d.InFlight;
        }
    }

    private static void CancelInFlight(ActiveDownload d)
    {
        d.Cancellation.Cancel();
        d.Cancellation = new CancellationTokenSource();
    }

    private void ServerAvailable()
    {
        if (_serverMessage is null && _serverDelay == TimeSpan.Zero)
        {
            return;
        }

        lock (_lock)
        {
            if (_serverMessage is not null)
            {
                logger.LogInformation("Usenet server reachable again");
                QueueChanged();
            }

            _serverMessage = null;
            _serverDelay = TimeSpan.Zero;
            _serverRetryAt = DateTime.MinValue;
        }
    }

    private void ServerUnavailable(UsenetServerException ex)
    {
        lock (_lock)
        {
            _serverDelay = _serverDelay == TimeSpan.Zero
                ? options.ServerRetryDelay
                : TimeSpan.FromTicks(Math.Min(_serverDelay.Ticks * 2, options.MaxServerRetryDelay.Ticks));
            _serverRetryAt = DateTime.UtcNow + _serverDelay;
            var message = ex.IsAuthError
                ? $"The Usenet server refused the login: {ex.Message}"
                : $"Cannot reach the Usenet server: {ex.Message}";
            message += $" Trying again in {Math.Ceiling(_serverDelay.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s.";
            if (_serverMessage is null)
            {
                logger.LogWarning("{Message}", message);
                QueueChanged();
            }

            _serverMessage = message;
        }
    }

    private static void CloseFiles(ActiveDownload d)
    {
        foreach (var file in d.Files)
        {
            CloseFile(file);
        }
    }

    /// <summary>Closes the handle and gives the file its real size (a missing last article leaves it short otherwise).</summary>
    private static void CloseFile(ActiveFile file)
    {
        lock (file)
        {
            if (file.Handle is not { } handle)
            {
                return;
            }

            try
            {
                if (file.KnownSize > 0 && RandomAccess.GetLength(handle) != file.KnownSize)
                {
                    RandomAccess.SetLength(handle, file.KnownSize);
                }
            }
            catch (IOException)
            {
            }
            finally
            {
                handle.Dispose();
                file.Handle = null;
            }
        }
    }

    /// <summary>Saves the state of changed downloads. Snapshots are taken under the lock and written in order.</summary>
    private void Checkpoint()
    {
        lock (_checkpointLock)
        {
            var snapshot = new List<(DownloadRecord Record, List<DownloadFileRecord> Files)>();
            lock (_lock)
            {
                foreach (var d in _dirty)
                {
                    var files = new List<DownloadFileRecord>();
                    foreach (var file in d.Files.Where(f => f.Dirty))
                    {
                        file.Dirty = false;
                        files.Add(file.ToRecord(d.Id));
                    }

                    snapshot.Add((d.ToRecord(), files));
                }

                _dirty.Clear();
                _saveNow = false;
            }

            foreach (var (record, files) in snapshot)
            {
                try
                {
                    repository.Update(record);
                    if (files.Count > 0)
                    {
                        repository.UpdateFiles(files);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not save the state of download {Id}", record.Id);
                }
            }

            lock (_lock)
            {
                foreach (var (record, _) in snapshot)
                {
                    if (record.Status is DownloadStatus.Completed or DownloadStatus.Failed
                        && _finishing.TryGetValue(record.Id, out var finished) && finished.Status == record.Status && !_dirty.Contains(finished))
                    {
                        _finishing.Remove(record.Id);
                    }
                }
            }
        }
    }

    /// <summary>Saves urgent changes, writes history and publishes queued events. Never called under the lock.</summary>
    private async Task FlushAsync()
    {
        while (_historyOutbox.TryDequeue(out var entry))
        {
            try
            {
                history.Add(entry);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not write the history entry for '{Title}'", entry.SourceTitle);
            }
        }

        if (_saveNow)
        {
            Checkpoint();
        }

        while (_outbox.TryDequeue(out var appEvent))
        {
            try
            {
                switch (appEvent)
                {
                    case DownloadChanged changed:
                        await events.PublishAsync(changed);
                        break;
                    case DownloadProgress progress:
                        await events.PublishAsync(progress);
                        break;
                    case OngoingChanged ongoing:
                        await events.PublishAsync(ongoing);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A download event subscriber failed");
            }
        }
    }

    private static HistoryItem HistoryFor(ActiveDownload d, HistoryEventType eventType)
    {
        var parsed = ReleaseParser.Parse(d.Title);
        return new HistoryItem
        {
            Date = DateTime.UtcNow,
            EventType = eventType,
            MediaType = d.Link.MediaType,
            MovieId = d.Link.MovieId,
            SeriesId = d.Link.SeriesId,
            EpisodeIds = d.Link.EpisodeIds,
            SourceTitle = d.Title,
            QualityId = parsed?.Quality.Quality.Id ?? StreamZH.Core.Quality.Quality.Unknown.Id,
            LanguageIds = [.. (parsed?.Languages ?? []).Select(l => l.Id)],
            ReleaseGroup = parsed?.ReleaseGroup,
            Indexer = d.Indexer,
            Size = d.TotalBytes,
            Data = new Dictionary<string, string?>
            {
                ["downloadClient"] = BuiltInDownloadClient.ClientName,
                ["downloadId"] = d.Id.ToString(CultureInfo.InvariantCulture),
                ["outputPath"] = d.OutputPath,
                ["error"] = d.Error,
                ["missingArticles"] = d.MissingArticles.ToString(CultureInfo.InvariantCulture),
                ["category"] = d.Category.ToString(),
            },
        };
    }

    private static DownloadItem ToItem(DownloadRecord r) => new()
    {
        Id = r.Id,
        Title = r.Title,
        Category = r.Category,
        Status = r.Status,
        Priority = r.Priority,
        TotalBytes = r.TotalBytes,
        DownloadedBytes = r.DownloadedBytes,
        StatusMessage = r.StatusMessage,
        MissingArticles = r.MissingArticles,
        Error = r.Error,
        Indexer = r.Indexer,
        Link = new MediaLink(r.MediaType, r.MovieId, r.SeriesId, r.EpisodeIds),
        OutputPath = r.OutputPath,
        Added = new DateTimeOffset(r.Added, TimeSpan.Zero),
        Completed = r.Completed is { } c ? new DateTimeOffset(c, TimeSpan.Zero) : null,
    };

    private static NzbDocument ParseNzb(byte[]? content)
    {
        if (content is null || content.Length == 0)
        {
            throw new LibraryException("The NZB file is empty.", LibraryError.Invalid);
        }

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            return NzbParser.Parse(stream);
        }
        catch (Exception ex) when (ex is InvalidNzbException or XmlException)
        {
            throw new LibraryException($"This is not a valid NZB file: {ex.Message}", LibraryError.Invalid);
        }
    }

    public static string CategoryFolder(DownloadCategory category) => category switch
    {
        DownloadCategory.Movie => "movies",
        DownloadCategory.Series => "series",
        _ => "other",
    };

    /// <summary>"&lt;parent&gt;/&lt;safe title&gt;", or "… (2)" etc. when that folder exists (non-empty) or another download uses it.</summary>
    private static string UniqueFolder(string parent, string title, IEnumerable<string> taken, bool onlyIfNotEmpty = false)
    {
        var used = taken.ToHashSet(PathComparer.Instance);
        var name = NzbFileNames.SafeName(title);
        for (var attempt = 1; ; attempt++)
        {
            var path = Path.Combine(parent, attempt == 1 ? name : $"{name} ({attempt.ToString(CultureInfo.InvariantCulture)})");
            var exists = Directory.Exists(path) && (!onlyIfNotEmpty || Directory.EnumerateFileSystemEntries(path).Any());
            if ((!exists && !used.Contains(path)) || attempt >= 1000)
            {
                return path;
            }
        }
    }

    /// <summary>Safe names from the NZB subjects; duplicates get " (2)" before the extension.</summary>
    private static List<string> UniqueFileNames(IReadOnlyList<NzbFile> files)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(files.Count);
        foreach (var file in files)
        {
            var name = NzbFileNames.SafeName(file.FileName);
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            for (var attempt = 2; !used.Add(name); attempt++)
            {
                name = $"{stem} ({attempt.ToString(CultureInfo.InvariantCulture)}){extension}";
            }

            names.Add(name);
        }

        return names;
    }

    private void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not clean up download files: {Message}", ex.Message);
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{(bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture)} GB"
            : $"{(bytes / (1024.0 * 1024)).ToString("0", CultureInfo.InvariantCulture)} MB";

    public override void Dispose()
    {
        _addLock.Dispose();
        base.Dispose();
    }

    private sealed record Work(ActiveDownload Download, ActiveFile File, int Segment, CancellationToken Token);

    /// <summary>Synchronous progress (no SynchronizationContext hop), so the last report is applied before the result.</summary>
    private sealed class ProgressSink(DownloadEngine engine, ActiveDownload download) : IProgress<PostProcessProgress>
    {
        public void Report(PostProcessProgress value) => engine.ReportPostProcess(download, value);
    }
}
