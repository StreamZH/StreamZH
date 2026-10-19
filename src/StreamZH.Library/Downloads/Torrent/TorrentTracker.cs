// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Data;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>Polling intervals of the torrent tracker (tests make them short).</summary>
public sealed class TorrentTrackerOptions
{
    /// <summary>While a torrent downloads or waits for its seed goal.</summary>
    public TimeSpan ActiveInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>When nothing is tracked or every client fails.</summary>
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A torrent missing from its client this many polls in a row (and older than <see cref="MissingGrace"/>) is gone.</summary>
    public int MissesBeforeGone { get; init; } = 3;

    public TimeSpan MissingGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Seeding torrents are saved at most this often when only ratio and upload change.</summary>
    public TimeSpan SeedingSaveInterval { get; init; } = TimeSpan.FromSeconds(60);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>Whether StreamZH can talk to a download client; <see cref="Error"/> since <see cref="Since"/> while it cannot.</summary>
public sealed record TorrentClientHealth(int ClientId, string Name, bool Ok, string? Error, string? Field, DateTimeOffset? Since, DateTimeOffset? LastContact);

/// <summary>
/// Follows the torrents StreamZH handed to download clients (rows of the Downloads table with protocol torrent): adds
/// them, polls every enabled client (every few seconds while something is active, every minute otherwise), maps the
/// client's state to the download (queued, downloading, paused, completed + seeding, ratio, peers, ETA), publishes
/// DownloadChanged/DownloadProgress and DownloadFinished exactly once when a torrent is done. After the import, the seed
/// goal (the indexer's ratio/time, or the client stopping the torrent at its own limits) decides when the torrent is
/// removed from the client, if the client has "Remove completed" on (Radarr's completed download handling).
/// Client errors become a health notice and never touch the downloads.
/// </summary>
public sealed class TorrentTracker(
    DownloadRepository repository,
    DownloadClientService clients,
    RemotePathMappingService mappings,
    DownloadImportRepository imports,
    HistoryRepository history,
    IEventBus events,
    ILogger<TorrentTracker> logger,
    TorrentTrackerOptions? options = null) : BackgroundService
{
    private readonly TorrentTrackerOptions _options = options ?? new TorrentTrackerOptions();
    private readonly Lock _lock = new();
    private readonly Dictionary<int, DownloadRecord> _records = [];
    private readonly Dictionary<int, Live> _live = [];
    private readonly ConcurrentDictionary<int, TorrentClientHealth> _health = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly AsyncWake _wake = new();
    private bool _loaded;

    private TimeProvider Time => _options.Time;

    /// <summary>Torrents that are not finished yet, in queue order.</summary>
    public IReadOnlyList<DownloadItem> Active()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return [.. _records.Values
                .Where(r => r.Status is not (DownloadStatus.Completed or DownloadStatus.Failed))
                .OrderByDescending(r => r.Priority).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
                .Select(ToItemLocked)];
        }
    }

    /// <summary>A tracked torrent with its live values (speed, ETA); null when it is not tracked (finished and out of the client).</summary>
    public DownloadItem? Find(int id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _records.TryGetValue(id, out var record) ? ToItemLocked(record) : null;
        }
    }

    /// <summary>Connection state per client that was polled or used since the start.</summary>
    public IReadOnlyList<TorrentClientHealth> Health() => [.. _health.Values.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)];

    public TorrentClientHealth? Health(int clientId) => _health.GetValueOrDefault(clientId);

    /// <summary>Whether a grab could go to a torrent client (the given one, or any enabled one).</summary>
    public bool HasClient(int? clientId = null) => clients.ForGrab(clientId) is not null;

    public string? ClientName(int? clientId) => clientId is { } id ? clients.Find(id)?.Name : null;

    /// <summary>
    /// Hands a torrent to the download client (the request's, or the default one) and starts following it.
    /// The category comes from the client's settings (the series category for series).
    /// </summary>
    /// <exception cref="LibraryException">No usable client, or the client did not take the torrent (message for the user).</exception>
    public async Task<DownloadItem> AddAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TorrentFile is not { Length: > 0 } && TorrentMetadata.ParseMagnet(request.MagnetUrl) is null)
        {
            throw new LibraryException("The release has neither a .torrent file nor a valid magnet link.", LibraryError.Invalid);
        }

        var client = clients.ForGrab(request.ClientId) ?? throw new LibraryException(
            request.ClientId is null
                ? "No torrent client is set up. Add one in Settings → Download clients."
                : "The chosen download client does not exist or is switched off.",
            LibraryError.Invalid);
        var implementation = clients.Client(client.Implementation)!;
        // The built-in engine saves into the folder of StreamZH's download category ("movies", "series" or a custom one).
        var category = string.Equals(client.Implementation, DownloadClientDefinition.BuiltInTorrent, StringComparison.OrdinalIgnoreCase)
            ? request.CategoryName ?? DownloadCategoryService.DefaultName(request.Category)
            : request.Category == DownloadCategory.Series
                ? TorrentClientHttp.Setting(client, DownloadClientCatalog.TvCategory) ?? TorrentClientHttp.Setting(client, DownloadClientCatalog.Category)
                : TorrentClientHttp.Setting(client, DownloadClientCatalog.Category);

        string hash;
        try
        {
            hash = await implementation.AddAsync(
                client,
                new TorrentAddRequest(request.Title, request.TorrentFile, request.TorrentFile is { Length: > 0 } ? null : request.MagnetUrl, category,
                    SeedRatio: request.SeedRatio, SeedTimeMinutes: request.SeedTimeMinutes),
                cancellationToken);
            Healthy(client);
        }
        catch (DownloadClientException ex)
        {
            await FailedAsync(client, ex);
            logger.LogWarning("{Client} did not take '{Title}': {Message}", client.Name, request.Title, ex.Message);
            throw new LibraryException($"{client.Name}: {ex.Message}", LibraryError.Invalid);
        }

        if (repository.FindTorrent(client.Id, hash) is { } existing && (existing.Status is not (DownloadStatus.Completed or DownloadStatus.Failed) || existing.Seeding))
        {
            logger.LogInformation("'{Title}' is already in {Client} (download {Id}); following the existing one", request.Title, client.Name, existing.Id);
            Remember(existing);
            return Find(existing.Id) ?? ToItem(existing, null);
        }

        var record = repository.Add(
            new DownloadRecord
            {
                Title = request.Title,
                Category = request.Category,
                Status = DownloadStatus.Queued,
                Priority = request.Priority ?? 0,
                Indexer = request.Indexer,
                MediaType = request.Link.MediaType,
                MovieId = request.Link.MovieId,
                SeriesId = request.Link.SeriesId,
                EpisodeIds = request.Link.EpisodeIds,
                NzbPath = string.Empty,
                WorkFolder = string.Empty,
                Added = Time.GetUtcNow().UtcDateTime,
                CategoryName = request.CategoryName ?? DownloadCategoryService.DefaultName(request.Category),
                StatusMessage = $"Added to {client.Name}",
                Protocol = DownloadProtocol.Torrent,
                ClientId = client.Id,
                ExternalId = hash,
                SeedRatio = request.SeedRatio,
                SeedTimeMinutes = request.SeedTimeMinutes,
            },
            []);
        Remember(record);
        logger.LogInformation("Added torrent '{Title}' ({Hash}) to {Client} as download {Id}", request.Title, hash, client.Name, record.Id);
        await PublishAsync(new DownloadChanged(record.Id));
        await PublishAsync(new OngoingChanged());
        _wake.Set();
        return Find(record.Id)!;
    }

    /// <summary>
    /// Stops following a torrent: removes it from its client when <paramref name="removeFromClient"/> (with its data when
    /// <paramref name="deleteData"/>) and deletes the download row. False when the download is not a torrent StreamZH knows.
    /// </summary>
    /// <exception cref="LibraryException">The client could not remove it (the row is kept).</exception>
    public async Task<bool> RemoveAsync(int id, bool removeFromClient, bool deleteData, CancellationToken cancellationToken = default)
    {
        var record = repository.Find(id);
        if (record is not { Protocol: DownloadProtocol.Torrent })
        {
            return false;
        }

        if (removeFromClient && record is { ExternalId: { } hash, ClientId: { } clientId }
            && clients.Find(clientId) is { } client && clients.Client(client.Implementation) is { } implementation)
        {
            try
            {
                await implementation.RemoveAsync(client, hash, deleteData, cancellationToken);
                Healthy(client);
            }
            catch (DownloadClientException ex)
            {
                // Some clients (Deluge) answer an error for a torrent they no longer have: it is gone, which is what was asked.
                if (!await GoneFromClientAsync(client, implementation, hash, cancellationToken))
                {
                    await FailedAsync(client, ex);
                    throw new LibraryException($"{client.Name}: {ex.Message}", LibraryError.Conflict);
                }

                logger.LogDebug("{Client} no longer has torrent {Hash} ({Message})", client.Name, hash, ex.Message);
            }
        }

        repository.Delete(id);
        lock (_lock)
        {
            _records.Remove(id);
            _live.Remove(id);
        }

        logger.LogInformation("Removed torrent download {Id} '{Title}'{Client}", id, record.Title, removeFromClient ? " from its client" : string.Empty);
        await PublishAsync(new DownloadChanged(id));
        await PublishAsync(new OngoingChanged());
        return true;
    }

    /// <summary>True when the client answers and does not list the torrent (any more).</summary>
    private static async Task<bool> GoneFromClientAsync(DownloadClientDefinition client, ITorrentClient implementation, string hash, CancellationToken cancellationToken)
    {
        try
        {
            var torrents = await implementation.GetTorrentsAsync(client, null, cancellationToken);
            return !torrents.Any(t => string.Equals(t.Hash, hash, StringComparison.OrdinalIgnoreCase));
        }
        catch (DownloadClientException)
        {
            return false;
        }
    }

    /// <summary>Polls now (between the regular polls), e.g. after a client was changed.</summary>
    public void PollSoon() => _wake.Set();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        clients.Changed += PollSoon;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var active = false;
                try
                {
                    active = await PollAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Torrent tracking failed");
                }

                await _wake.WaitAsync(active ? _options.ActiveInterval : _options.IdleInterval, Time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            clients.Changed -= PollSoon;
        }
    }

    /// <summary>One round over all clients. Returns whether something is still active (poll again soon).</summary>
    public async Task<bool> PollAsync(CancellationToken cancellationToken = default)
    {
        await _pollLock.WaitAsync(cancellationToken);
        try
        {
            var records = repository.TrackedTorrents();
            lock (_lock)
            {
                var ids = records.Select(r => r.Id).ToHashSet();
                foreach (var gone in _records.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    _records.Remove(gone);
                    _live.Remove(gone);
                }

                foreach (var record in records)
                {
                    _records[record.Id] = record;
                }

                _loaded = true;
            }

            var definitions = clients.All().ToDictionary(c => c.Id);
            var active = false;
            foreach (var group in records.GroupBy(r => r.ClientId ?? 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!definitions.TryGetValue(group.Key, out var client))
                {
                    foreach (var record in group)
                    {
                        await GoneAsync(record, "the download client was removed from StreamZH");
                    }

                    continue;
                }

                if (!client.Enable || clients.Client(client.Implementation) is not { } implementation)
                {
                    continue;
                }

                active |= await PollClientAsync(client, implementation, [.. group], cancellationToken);
            }

            await PublishProgressAsync();
            return active;
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private async Task<bool> PollClientAsync(DownloadClientDefinition client, ITorrentClient implementation, List<DownloadRecord> records, CancellationToken cancellationToken)
    {
        Dictionary<string, TorrentStatus> torrents;
        try
        {
            torrents = await TorrentsAsync(client, implementation, records, cancellationToken);
            Healthy(client);
        }
        catch (DownloadClientException ex)
        {
            await FailedAsync(client, ex);
            return false;
        }

        var active = false;
        foreach (var record in records)
        {
            var status = record.ExternalId is { } hash ? torrents.GetValueOrDefault(hash) : null;
            active |= await ApplyAsync(client, implementation, record, status, cancellationToken);
        }

        return active;
    }

    /// <summary>The client's torrents by hash: its categories first; one unfiltered list when some are missing (the category was changed).</summary>
    private static async Task<Dictionary<string, TorrentStatus>> TorrentsAsync(
        DownloadClientDefinition client, ITorrentClient implementation, List<DownloadRecord> records, CancellationToken cancellationToken)
    {
        var torrents = new Dictionary<string, TorrentStatus>(StringComparer.OrdinalIgnoreCase);
        var categories = new[] { TorrentClientHttp.Setting(client, DownloadClientCatalog.Category), TorrentClientHttp.Setting(client, DownloadClientCatalog.TvCategory) }
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (var category in categories.Count == 0 ? [null] : categories.Cast<string?>())
        {
            foreach (var torrent in await implementation.GetTorrentsAsync(client, category, cancellationToken))
            {
                torrents[torrent.Hash] = torrent;
            }
        }

        if (categories.Count > 0 && records.Any(r => r.ExternalId is { } h && !torrents.ContainsKey(h)))
        {
            foreach (var torrent in await implementation.GetTorrentsAsync(client, null, cancellationToken))
            {
                torrents.TryAdd(torrent.Hash, torrent);
            }
        }

        return torrents;
    }

    /// <summary>Maps one torrent onto its download. Returns whether it still needs fast polling.</summary>
    private async Task<bool> ApplyAsync(DownloadClientDefinition client, ITorrentClient implementation, DownloadRecord record, TorrentStatus? status, CancellationToken cancellationToken)
    {
        var live = LiveFor(record.Id);
        var now = Time.GetUtcNow();
        if (status is null)
        {
            live.DownloadSpeed = 0;
            live.UploadSpeed = 0;
            live.Eta = null;
            live.Misses++;
            if (live.Misses >= _options.MissesBeforeGone && now - new DateTimeOffset(record.Added, TimeSpan.Zero) >= _options.MissingGrace)
            {
                await GoneAsync(record, $"it is no longer in {client.Name}");
                return false;
            }

            return true;
        }

        live.Misses = 0;
        live.DownloadSpeed = status.DownloadSpeed;
        live.UploadSpeed = status.UploadSpeed;
        live.Eta = status.IsDone ? null : status.Eta;

        var updated = Map(client, record, status, now);
        var justCompleted = record.Status != DownloadStatus.Completed && updated.Status == DownloadStatus.Completed;
        var visible = updated.Status != record.Status || updated.StatusMessage != record.StatusMessage || updated.Seeding != record.Seeding
            || updated.OutputPath != record.OutputPath;
        var progressed = updated.DownloadedBytes != record.DownloadedBytes || updated.TotalBytes != record.TotalBytes
            || updated.Seeders != record.Seeders || updated.Peers != record.Peers;
        var seedingOnly = updated.Status == DownloadStatus.Completed && !visible;
        var saveDue = !seedingOnly || now - live.Saved >= _options.SeedingSaveInterval;
        if ((visible || progressed || updated.Ratio != record.Ratio || updated.UploadedBytes != record.UploadedBytes) && saveDue)
        {
            repository.UpdateTorrent(updated);
            live.Saved = now;
            Remember(updated);
            if (visible)
            {
                await PublishAsync(new DownloadChanged(record.Id));
            }
        }
        else
        {
            // Shown at once; saved with the next due save.
            Remember(updated);
        }

        if (justCompleted)
        {
            logger.LogInformation("Torrent download {Id} '{Title}' completed in {Client}: {Output}", record.Id, record.Title, client.Name, updated.OutputPath);
            history.Add(HistoryFor(updated, client, HistoryEventType.DownloadCompleted));
            if (updated.OutputPath is { } output && !File.Exists(output) && !Directory.Exists(output))
            {
                logger.LogWarning(
                    "StreamZH cannot see {Path} reported by {Client}. If the client runs on another machine or in a container, add a remote path mapping",
                    output, client.Name);
            }

            await PublishAsync(new OngoingChanged());
            await PublishAsync(new DownloadFinished(ToItem(updated, live)));
        }

        if (updated is { Status: DownloadStatus.Completed, Seeding: true })
        {
            await SeedGoalAsync(client, implementation, updated, status, cancellationToken);
            return true;
        }

        return updated.Status is not (DownloadStatus.Completed or DownloadStatus.Failed);
    }

    /// <summary>The download as the torrent's state says (title, links, seed goals and the added date stay).</summary>
    private DownloadRecord Map(DownloadClientDefinition client, DownloadRecord record, TorrentStatus status, DateTimeOffset now)
    {
        var updated = record with
        {
            TotalBytes = status.Size > 0 ? status.Size : record.TotalBytes,
            DownloadedBytes = status.Size > 0 ? status.Downloaded : record.DownloadedBytes,
            Ratio = Math.Round(status.Ratio, 3),
            UploadedBytes = status.Uploaded,
            Seeders = status.Seeders,
            Peers = status.Peers,
            Error = null,
        };

        if (status.IsDone || record.Status == DownloadStatus.Completed)
        {
            // Once done, a torrent stays completed (a re-check in the client does not bring it back into the queue).
            var remote = status.ContentPath ?? (status.SavePath is { } save ? TorrentClientHttp.Combine(save, status.Name) : null);
            return updated with
            {
                Status = DownloadStatus.Completed,
                Seeding = true,
                OutputPath = remote is null ? record.OutputPath : mappings.RemoteToLocal(client.Text(DownloadClientCatalog.Host), remote),
                Completed = record.Completed ?? status.CompletedOn?.UtcDateTime ?? now.UtcDateTime,
                DownloadedBytes = updated.TotalBytes,
                StatusMessage = status.State switch
                {
                    TorrentState.Seeding => $"Seeding in {client.Name}",
                    TorrentState.Error => $"Error in {client.Name}: {status.Error}",
                    _ => $"Seeding stopped in {client.Name}",
                },
            };
        }

        return status.State switch
        {
            TorrentState.Queued => updated with { Status = DownloadStatus.Queued, StatusMessage = $"Queued in {client.Name}" },
            TorrentState.Paused => updated with { Status = DownloadStatus.Paused, StatusMessage = $"Paused in {client.Name}" },
            TorrentState.Checking => updated with { Status = DownloadStatus.Downloading, StatusMessage = "Checking files" },
            TorrentState.Stalled => updated with { Status = DownloadStatus.Downloading, StatusMessage = "Stalled: no connection to peers" },
            TorrentState.Error => updated with { Status = DownloadStatus.Downloading, StatusMessage = $"Error in {client.Name}: {status.Error}" },
            _ => updated with { Status = DownloadStatus.Downloading, StatusMessage = status.Size <= 0 ? "Downloading metadata" : null },
        };
    }

    /// <summary>
    /// Radarr's "Remove completed": once the download is imported and the seed goal is met (ratio or seed time from the
    /// indexer, or the client stopped the torrent at its own limits), the torrent and its data are removed from the client.
    /// </summary>
    private async Task SeedGoalAsync(DownloadClientDefinition client, ITorrentClient implementation, DownloadRecord record, TorrentStatus status, CancellationToken cancellationToken)
    {
        if (!client.RemoveCompleted || record.ExternalId is not { } hash || imports.Find(record.Id)?.State != ImportState.Imported)
        {
            return;
        }

        var reached = status.State == TorrentState.Completed
            || (record.SeedRatio is { } ratio && status.Ratio >= ratio)
            || (record.SeedTimeMinutes is { } minutes && status.SeedingTime >= TimeSpan.FromMinutes(minutes));
        if (!reached)
        {
            return;
        }

        try
        {
            await implementation.RemoveAsync(client, hash, deleteData: true, cancellationToken);
        }
        catch (DownloadClientException ex)
        {
            await FailedAsync(client, ex);
            return;
        }

        var removed = record with
        {
            Seeding = false,
            StatusMessage = string.Create(CultureInfo.InvariantCulture, $"Removed from {client.Name} after seeding (ratio {status.Ratio:0.00})"),
        };
        repository.UpdateTorrent(removed);
        Forget(record.Id);
        logger.LogInformation("Seed goal of '{Title}' reached (ratio {Ratio:0.00}, seeded {Hours:0.#} h): removed from {Client}",
            record.Title, status.Ratio, status.SeedingTime.TotalHours, client.Name);
        await PublishAsync(new DownloadChanged(record.Id));
    }

    /// <summary>
    /// The torrent left its client (removed by hand, or its client was deleted). An unfinished one fails without failed
    /// download handling (like Radarr, nothing is blocklisted); a finished one simply stops seeding.
    /// </summary>
    private async Task GoneAsync(DownloadRecord record, string reason)
    {
        var updated = record.Status is DownloadStatus.Completed
            ? record with { Seeding = false, StatusMessage = $"Not seeding any more: {reason}" }
            : record with
            {
                Status = DownloadStatus.Failed,
                Error = $"Removed before it finished: {reason}.",
                StatusMessage = null,
                Completed = Time.GetUtcNow().UtcDateTime,
            };
        repository.UpdateTorrent(updated);
        Forget(record.Id);
        logger.LogInformation("Torrent download {Id} '{Title}': {Reason}", record.Id, record.Title, reason);
        await PublishAsync(new DownloadChanged(record.Id));
        await PublishAsync(new OngoingChanged());
    }

    private void Healthy(DownloadClientDefinition client)
    {
        var now = Time.GetUtcNow();
        var previous = _health.GetValueOrDefault(client.Id);
        _health[client.Id] = new TorrentClientHealth(client.Id, client.Name, true, null, null, null, now);
        if (previous is { Ok: false })
        {
            logger.LogInformation("{Client} is reachable again", client.Name);
            _ = PublishAsync(new OngoingChanged());
        }
    }

    private async Task FailedAsync(DownloadClientDefinition client, DownloadClientException error)
    {
        var previous = _health.GetValueOrDefault(client.Id);
        var changed = previous is not { Ok: false } || previous.Error != error.Message;
        _health[client.Id] = new TorrentClientHealth(
            client.Id, client.Name, false, error.Message, error.Field, previous is { Ok: false } ? previous.Since : Time.GetUtcNow(), previous?.LastContact);
        if (changed)
        {
            logger.LogWarning("Download client {Client}: {Message}", client.Name, error.Message);
            await PublishAsync(new OngoingChanged());
        }
    }

    private async Task PublishProgressAsync()
    {
        List<DownloadProgressItem> items;
        lock (_lock)
        {
            items = [.. _records.Values
                .Where(r => r.Status is not (DownloadStatus.Completed or DownloadStatus.Failed))
                .Select(r => ToItemLocked(r))
                .Select(i => new DownloadProgressItem(i.Id, i.Status, i.Progress, i.SpeedBps, i.Eta?.TotalSeconds, null, i.StatusMessage))];
        }

        if (items.Count > 0)
        {
            await PublishAsync(new DownloadProgress(items));
        }
    }

    private async Task PublishAsync<T>(T appEvent)
        where T : IAppEvent
    {
        try
        {
            await events.PublishAsync(appEvent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A download event subscriber failed");
        }
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded)
            {
                return;
            }

            foreach (var record in repository.TrackedTorrents())
            {
                _records[record.Id] = record;
            }

            _loaded = true;
        }
    }

    private void Remember(DownloadRecord record)
    {
        lock (_lock)
        {
            _records[record.Id] = record;
        }
    }

    private void Forget(int id)
    {
        lock (_lock)
        {
            _records.Remove(id);
            _live.Remove(id);
        }
    }

    private Live LiveFor(int id)
    {
        lock (_lock)
        {
            if (!_live.TryGetValue(id, out var live))
            {
                live = new Live();
                _live[id] = live;
            }

            return live;
        }
    }

    private DownloadItem ToItemLocked(DownloadRecord record) => ToItem(record, _live.GetValueOrDefault(record.Id));

    private static DownloadItem ToItem(DownloadRecord r, Live? live) => new()
    {
        Id = r.Id,
        Title = r.Title,
        Category = r.Category,
        Status = r.Status,
        Priority = r.Priority,
        TotalBytes = r.TotalBytes,
        DownloadedBytes = r.DownloadedBytes,
        SpeedBps = r.Status == DownloadStatus.Downloading ? live?.DownloadSpeed ?? 0 : 0,
        Eta = r.Status == DownloadStatus.Downloading ? live?.Eta : null,
        StatusMessage = r.StatusMessage,
        Error = r.Error,
        Indexer = r.Indexer,
        Link = new MediaLink(r.MediaType, r.MovieId, r.SeriesId, r.EpisodeIds),
        OutputPath = r.OutputPath,
        Added = new DateTimeOffset(r.Added, TimeSpan.Zero),
        Completed = r.Completed is { } c ? new DateTimeOffset(c, TimeSpan.Zero) : null,
        CategoryName = r.CategoryName ?? DownloadCategoryService.DefaultName(r.Category),
        Protocol = DownloadProtocol.Torrent,
        ClientId = r.ClientId,
        ExternalId = r.ExternalId,
        Seeding = r.Seeding,
        Ratio = r.Ratio,
        UploadedBytes = r.UploadedBytes,
        UploadSpeedBps = live?.UploadSpeed ?? 0,
        Seeders = r.Seeders,
        Peers = r.Peers,
    };

    private static HistoryItem HistoryFor(DownloadRecord r, DownloadClientDefinition client, HistoryEventType eventType)
    {
        var parsed = ReleaseParser.Parse(r.Title);
        return new HistoryItem
        {
            Date = DateTime.UtcNow,
            EventType = eventType,
            MediaType = r.MediaType,
            MovieId = r.MovieId,
            SeriesId = r.SeriesId,
            EpisodeIds = r.EpisodeIds,
            SourceTitle = r.Title,
            QualityId = parsed?.Quality.Quality.Id ?? Core.Quality.Quality.Unknown.Id,
            LanguageIds = [.. (parsed?.Languages ?? []).Select(l => l.Id)],
            ReleaseGroup = parsed?.ReleaseGroup,
            Indexer = r.Indexer,
            Size = r.TotalBytes,
            Data = new Dictionary<string, string?>
            {
                ["downloadClient"] = client.Name,
                ["downloadClientId"] = client.Id.ToString(CultureInfo.InvariantCulture),
                ["downloadId"] = r.Id.ToString(CultureInfo.InvariantCulture),
                ["protocol"] = "torrent",
                ["infoHash"] = r.ExternalId,
                ["outputPath"] = r.OutputPath,
                ["category"] = r.Category.ToString(),
                ["categoryName"] = r.CategoryName,
            },
        };
    }

    private sealed class Live
    {
        public long DownloadSpeed { get; set; }

        public long UploadSpeed { get; set; }

        public TimeSpan? Eta { get; set; }

        public int Misses { get; set; }

        public DateTimeOffset Saved { get; set; } = DateTimeOffset.MinValue;
    }

    /// <summary>A wait that ends early when <see cref="Set"/> is called.</summary>
    private sealed class AsyncWake
    {
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Set() => _signal.TrySetResult();

        public async Task WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
        {
            var signal = _signal;
            try
            {
                await signal.Task.WaitAsync(timeout, time, cancellationToken);
            }
            catch (TimeoutException)
            {
            }

            Interlocked.CompareExchange(ref _signal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), signal);
        }
    }
}
