// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Downloads;
using StreamZH.Library.Downloads.Torrent;
using StreamZH.Tests.Library.Downloads;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Torrents;

/// <summary>An in-memory torrent client: tests set each torrent's status by hand.</summary>
public sealed class ScriptedTorrentClient(string implementation = DownloadClientDefinition.QBittorrent) : ITorrentClient
{
    public ConcurrentDictionary<string, TorrentStatus> Torrents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<TorrentAddRequest> Added { get; } = [];

    public List<(string Hash, bool DeleteData)> Removed { get; } = [];

    /// <summary>Every call fails with this while set.</summary>
    public DownloadClientException? Error { get; set; }

    public string Implementation => implementation;

    public Task<TorrentClientInfo> TestAsync(DownloadClientDefinition client, CancellationToken cancellationToken) =>
        Error is { } e ? throw e : Task.FromResult(new TorrentClientInfo("Scripted", "1.0", []));

    public Task<string> AddAsync(DownloadClientDefinition client, TorrentAddRequest request, CancellationToken cancellationToken)
    {
        if (Error is { } e)
        {
            throw e;
        }

        var hash = TorrentRequests.Hash(request);
        Added.Add(request);
        Torrents[hash] = Status(hash, request.Title, TorrentState.Queued, 0) with { Category = request.Category };
        return Task.FromResult(hash);
    }

    public Task<IReadOnlyList<TorrentStatus>> GetTorrentsAsync(DownloadClientDefinition client, string? category, CancellationToken cancellationToken) =>
        Error is { } e ? throw e : Task.FromResult<IReadOnlyList<TorrentStatus>>([.. Torrents.Values.Where(t => category is null || t.Category == category)]);

    public Task RemoveAsync(DownloadClientDefinition client, string hash, bool deleteData, CancellationToken cancellationToken)
    {
        if (Error is { } e)
        {
            throw e;
        }

        // Like Deluge: removing a torrent the client does not have is an error.
        if (!Torrents.TryRemove(hash, out _))
        {
            throw new DownloadClientException("Torrent ID not found in session.");
        }

        Removed.Add((hash, deleteData));
        return Task.CompletedTask;
    }

    public void Set(string hash, TorrentState state, double progress = 1, double ratio = 0, TimeSpan seeding = default, string? contentPath = null) =>
        Torrents[hash] = Torrents[hash] with
        {
            State = state,
            Downloaded = (long)(Torrents[hash].Size * progress),
            Ratio = ratio,
            SeedingTime = seeding,
            ContentPath = contentPath ?? Torrents[hash].ContentPath,
            DownloadSpeed = state == TorrentState.Downloading ? 1000 : 0,
            Eta = state == TorrentState.Downloading ? TimeSpan.FromMinutes(2) : null,
        };

    private static TorrentStatus Status(string hash, string name, TorrentState state, double progress) => new(
        hash, name, state, 1000, (long)(1000 * progress), 0, 0, 0, 0, null, 3, 1, null, "/remote/downloads", $"/remote/downloads/{name}", null, null, null, TimeSpan.Zero);
}

/// <summary>The tracker with a scripted client "qBittorrent" (id 1, category streamzh) on a fresh database.</summary>
public sealed class TrackerBed : IDisposable
{
    public TrackerBed(bool removeCompleted = true, string implementation = DownloadClientDefinition.QBittorrent)
    {
        Client = new ScriptedTorrentClient(implementation);
        Downloads = new DownloadRepository(Bed.Database);
        ClientRows = new DownloadClientRepository(Bed.Database);
        Mappings = new RemotePathMappingRepository(Bed.Database);
        Imports = new DownloadImportRepository(Bed.Database);
        Clients = new DownloadClientService(ClientRows, [Client], NullLogger<DownloadClientService>.Instance);
        ClientId = ClientRows.Add(new DownloadClientDefinition(0, "qBittorrent", implementation, DownloadProtocol.Torrent, true, 1,
            new Dictionary<string, JsonElement>
            {
                [DownloadClientCatalog.Host] = JsonSerializer.SerializeToElement("127.0.0.1"),
                [DownloadClientCatalog.Port] = JsonSerializer.SerializeToElement(8080),
                [DownloadClientCatalog.Category] = JsonSerializer.SerializeToElement("streamzh"),
                [DownloadClientCatalog.TvCategory] = JsonSerializer.SerializeToElement("streamzh-tv"),
            })
        { RemoveCompleted = removeCompleted });
        Local = Path.Combine(Bed.DataDirectory, "torrents");
        Directory.CreateDirectory(Local);
        Mappings.Add("127.0.0.1", "/remote/downloads/", Local + Path.DirectorySeparatorChar);
        Bed.Events.Subscribe<DownloadFinished>((e, _) =>
        {
            Finished.Enqueue(e.Item);
            return ValueTask.CompletedTask;
        });
        Tracker = NewTracker();
    }

    public SearchTestBed Bed { get; } = new();

    public ScriptedTorrentClient Client { get; }

    public DownloadRepository Downloads { get; }

    public DownloadClientRepository ClientRows { get; }

    public RemotePathMappingRepository Mappings { get; }

    public DownloadImportRepository Imports { get; }

    public DownloadClientService Clients { get; }

    public int ClientId { get; }

    public string Local { get; }

    public TorrentTracker Tracker { get; private set; }

    public ConcurrentQueue<DownloadItem> Finished { get; } = new();

    /// <summary>A new tracker on the same database, like after a restart.</summary>
    public TorrentTracker NewTracker() => Tracker = new TorrentTracker(
        Downloads, Clients, new RemotePathMappingService(Mappings), Imports, Bed.History, Bed.Events, NullLogger<TorrentTracker>.Instance,
        new TorrentTrackerOptions { MissingGrace = TimeSpan.Zero, SeedingSaveInterval = TimeSpan.Zero, Time = Bed.Time });

    public static DownloadRequest Request(string name, DownloadCategory category = DownloadCategory.Movie, double? ratio = null, int? minutes = null) =>
        new([], name, category, new MediaLink(MediaType.Movie, 1, null, []), Indexer: "Torznab Test")
        {
            Protocol = DownloadProtocol.Torrent,
            TorrentFile = TorrentFixtures.Create(name),
            SeedRatio = ratio,
            SeedTimeMinutes = minutes,
        };

    public void Dispose() => Bed.Dispose();
}

public sealed class TorrentTrackerTests : IDisposable
{
    private readonly TrackerBed _bed = new();

    [Fact]
    public async Task Add_poll_and_complete_publish_finished_once()
    {
        var item = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Movie.2024.1080p.WEB-GRP", ratio: 1.5, minutes: 600));
        var hash = item.ExternalId!;
        Assert.Equal((DownloadStatus.Queued, DownloadProtocol.Torrent, _bed.ClientId), (item.Status, item.Protocol, item.ClientId));
        var added = Assert.Single(_bed.Client.Added);
        Assert.Equal(("streamzh", 1.5, 600), (added.Category, added.SeedRatio!.Value, added.SeedTimeMinutes!.Value));
        Assert.Equal(item.Id, Assert.Single(_bed.Tracker.Active()).Id);

        _bed.Client.Set(hash, TorrentState.Downloading, progress: 0.5);
        Assert.True(await _bed.Tracker.PollAsync());
        var downloading = _bed.Tracker.Find(item.Id)!;
        Assert.Equal((DownloadStatus.Downloading, 0.5, 1000L), (downloading.Status, downloading.Progress, downloading.SpeedBps));
        Assert.Equal(TimeSpan.FromMinutes(2), downloading.Eta);

        _bed.Client.Set(hash, TorrentState.Stalled, progress: 0.6);
        await _bed.Tracker.PollAsync();
        Assert.StartsWith("Stalled", _bed.Tracker.Find(item.Id)!.StatusMessage, StringComparison.Ordinal);

        _bed.Client.Set(hash, TorrentState.Seeding, ratio: 0.1);
        await _bed.Tracker.PollAsync();
        var done = Assert.Single(_bed.Finished);
        Assert.Equal((DownloadStatus.Completed, true), (done.Status, done.Seeding));
        Assert.Equal(Path.Combine(_bed.Local, "Generated.Movie.2024.1080p.WEB-GRP"), done.OutputPath);
        Assert.Empty(_bed.Tracker.Active());
        Assert.Contains(_bed.Bed.History.Page(1, 10).Records, h => h.EventType == HistoryEventType.DownloadCompleted);

        // Polls after that, and a restart, never publish it again.
        await _bed.Tracker.PollAsync();
        _bed.NewTracker();
        await _bed.Tracker.PollAsync();
        Assert.Single(_bed.Finished);
        Assert.True(_bed.Tracker.Find(item.Id)!.Seeding);

        // Re-adding the same torrent follows the existing download.
        var again = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Movie.2024.1080p.WEB-GRP"));
        Assert.Equal(item.Id, again.Id);
    }

    [Fact]
    public async Task Series_use_the_series_category_and_no_client_is_an_error()
    {
        var item = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Show.S01E01.1080p", DownloadCategory.Series));
        Assert.Equal("streamzh-tv", _bed.Client.Added[0].Category);
        Assert.Equal("series", item.CategoryName);

        var missing = await Assert.ThrowsAsync<LibraryException>(() => _bed.Tracker.AddAsync(TrackerBed.Request("x") with { ClientId = 99 }));
        Assert.Contains("does not exist", missing.Message, StringComparison.Ordinal);

        _bed.ClientRows.Update(_bed.ClientRows.Find(_bed.ClientId)! with { Enable = false });
        var none = await Assert.ThrowsAsync<LibraryException>(() => _bed.Tracker.AddAsync(TrackerBed.Request("y")));
        Assert.Contains("Settings → Download clients", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_built_in_engine_gets_the_download_category_name()
    {
        using var builtIn = new TrackerBed(implementation: DownloadClientDefinition.BuiltInTorrent);
        await builtIn.Tracker.AddAsync(TrackerBed.Request("Generated.Show.S02E01", DownloadCategory.Series));
        await builtIn.Tracker.AddAsync(TrackerBed.Request("Generated.Anime.S01E01", DownloadCategory.Series) with { CategoryName = "anime" });
        Assert.Equal(["series", "anime"], builtIn.Client.Added.Select(a => a.Category));
    }

    [Fact]
    public async Task Seed_goal_removes_the_torrent_only_after_the_import()
    {
        var item = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Movie.2022.720p", ratio: 1.0));
        var hash = item.ExternalId!;
        _bed.Client.Set(hash, TorrentState.Seeding, ratio: 1.4);
        await _bed.Tracker.PollAsync();
        await _bed.Tracker.PollAsync();
        Assert.Empty(_bed.Client.Removed);

        _bed.Imports.Save(new DownloadImport(item.Id, ImportState.Imported, null, DateTime.UtcNow, item.Title, MediaType.Movie, 1, null, [], ["/media/x.mkv"]));
        await _bed.Tracker.PollAsync();
        Assert.Equal((hash, true), Assert.Single(_bed.Client.Removed));
        var row = _bed.Downloads.Find(item.Id)!;
        Assert.Equal((DownloadStatus.Completed, false), (row.Status, row.Seeding));
        Assert.StartsWith("Removed from qBittorrent after seeding", row.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(_bed.Downloads.TrackedTorrents());
    }

    [Fact]
    public async Task Seed_time_or_a_stopped_torrent_reach_the_goal_unless_remove_completed_is_off()
    {
        var timed = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.A", minutes: 60));
        var stopped = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.B"));
        var forever = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.C"));
        foreach (var item in new[] { timed, stopped, forever })
        {
            _bed.Imports.Save(new DownloadImport(item.Id, ImportState.Imported, null, DateTime.UtcNow, item.Title, MediaType.Movie, 1, null, [], []));
        }

        _bed.Client.Set(timed.ExternalId!, TorrentState.Seeding, seeding: TimeSpan.FromMinutes(59));
        _bed.Client.Set(stopped.ExternalId!, TorrentState.Completed, ratio: 0.3);
        _bed.Client.Set(forever.ExternalId!, TorrentState.Seeding, ratio: 9, seeding: TimeSpan.FromDays(30));
        await _bed.Tracker.PollAsync();
        Assert.Equal([stopped.ExternalId], _bed.Client.Removed.Select(r => r.Hash));

        _bed.Client.Set(timed.ExternalId!, TorrentState.Seeding, seeding: TimeSpan.FromMinutes(61));
        await _bed.Tracker.PollAsync();
        Assert.Equal(2, _bed.Client.Removed.Count);
        Assert.True(_bed.Downloads.Find(forever.Id)!.Seeding);

        using var keep = new TrackerBed(removeCompleted: false);
        var kept = await keep.Tracker.AddAsync(TrackerBed.Request("Generated.D", ratio: 1));
        keep.Imports.Save(new DownloadImport(kept.Id, ImportState.Imported, null, DateTime.UtcNow, kept.Title, MediaType.Movie, 1, null, [], []));
        keep.Client.Set(kept.ExternalId!, TorrentState.Completed, ratio: 5);
        await keep.Tracker.PollAsync();
        Assert.Empty(keep.Client.Removed);
    }

    [Fact]
    public async Task A_torrent_removed_in_the_client_fails_without_failed_download_handling()
    {
        var item = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Gone"));
        _bed.Client.Torrents.Clear();
        await _bed.Tracker.PollAsync();
        await _bed.Tracker.PollAsync();
        Assert.Equal(DownloadStatus.Queued, _bed.Downloads.Find(item.Id)!.Status);

        await _bed.Tracker.PollAsync();
        var row = _bed.Downloads.Find(item.Id)!;
        Assert.Equal(DownloadStatus.Failed, row.Status);
        Assert.Contains("no longer in qBittorrent", row.Error, StringComparison.Ordinal);
        Assert.Empty(_bed.Finished);
        Assert.Null(_bed.Tracker.Find(item.Id));
    }

    [Fact]
    public async Task Client_errors_become_health_and_leave_the_downloads_alone()
    {
        var item = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Health"));
        _bed.Client.Error = new DownloadClientException("Could not connect to qBittorrent: connection refused", DownloadClientCatalog.Host);
        Assert.False(await _bed.Tracker.PollAsync());
        var health = _bed.Tracker.Health(_bed.ClientId)!;
        Assert.Equal((false, DownloadClientCatalog.Host), (health.Ok, health.Field));
        Assert.Contains("connection refused", health.Error, StringComparison.Ordinal);
        Assert.Equal(DownloadStatus.Queued, _bed.Tracker.Find(item.Id)!.Status);

        var refused = await Assert.ThrowsAsync<LibraryException>(() => _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Other")));
        Assert.StartsWith("qBittorrent: Could not connect", refused.Message, StringComparison.Ordinal);

        _bed.Client.Error = null;
        await _bed.Tracker.PollAsync();
        Assert.True(_bed.Tracker.Health(_bed.ClientId)!.Ok);
    }

    [Fact]
    public async Task Grabs_go_to_the_enabled_client_with_the_lowest_priority_and_a_deleted_client_ends_its_downloads()
    {
        var second = _bed.ClientRows.Add(_bed.ClientRows.Find(_bed.ClientId)! with { Id = 0, Name = "Second", Priority = 0 });
        Assert.Equal(second, _bed.Clients.ForGrab(null)!.Id);
        Assert.Equal(_bed.ClientId, _bed.Clients.ForGrab(_bed.ClientId)!.Id);
        _bed.ClientRows.Update(_bed.ClientRows.Find(second)! with { Enable = false });
        Assert.Equal(_bed.ClientId, _bed.Clients.ForGrab(null)!.Id);
        Assert.Null(_bed.Clients.ForGrab(second));

        var running = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Running"));
        var seeding = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Seeding"));
        _bed.Client.Set(seeding.ExternalId!, TorrentState.Seeding);
        await _bed.Tracker.PollAsync();

        _bed.Clients.Delete(_bed.ClientId);
        await _bed.Tracker.PollAsync();
        Assert.Equal(DownloadStatus.Failed, _bed.Downloads.Find(running.Id)!.Status);
        Assert.Equal((DownloadStatus.Completed, false), (_bed.Downloads.Find(seeding.Id)!.Status, _bed.Downloads.Find(seeding.Id)!.Seeding));
        Assert.Empty(_bed.Downloads.TrackedTorrents());
    }

    [Fact]
    public async Task Remove_forgets_and_optionally_removes_from_the_client()
    {
        var first = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.One"));
        var second = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Two"));
        Assert.True(await _bed.Tracker.RemoveAsync(first.Id, removeFromClient: true, deleteData: true));
        Assert.True(await _bed.Tracker.RemoveAsync(second.Id, removeFromClient: false, deleteData: false));
        Assert.Equal([(first.ExternalId!, true)], _bed.Client.Removed);
        Assert.Null(_bed.Downloads.Find(first.Id));
        Assert.Single(_bed.Client.Torrents);
        Assert.False(await _bed.Tracker.RemoveAsync(12345, true, true));

        // Already gone from the client: removing still succeeds. An unreachable client keeps the download.
        var gone = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Gone.Already"));
        _bed.Client.Torrents.TryRemove(gone.ExternalId!, out _);
        Assert.True(await _bed.Tracker.RemoveAsync(gone.Id, removeFromClient: true, deleteData: true));
        Assert.Null(_bed.Downloads.Find(gone.Id));

        var kept = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Kept"));
        _bed.Client.Error = new DownloadClientException("Could not connect to qBittorrent: connection refused", DownloadClientCatalog.Host);
        await Assert.ThrowsAsync<LibraryException>(() => _bed.Tracker.RemoveAsync(kept.Id, removeFromClient: true, deleteData: false));
        Assert.NotNull(_bed.Downloads.Find(kept.Id));
    }

    [Fact]
    public async Task The_usenet_engine_ignores_torrent_rows_and_the_queue_combines_both()
    {
        var item = await _bed.Tracker.AddAsync(TrackerBed.Request("Generated.Combined"));
        using var engine = new DownloadEngine(
            _bed.Downloads, _bed.Bed.History, _bed.Bed.Downloads, _bed.Bed.Config, new FakeArticleSource { MaxConnections = 0 }, new FakePostProcessor(),
            _bed.Bed.Events, new FakeFreeSpace(), new DownloadEngineOptions(), NullLogger<DownloadEngine>.Instance);
        Assert.Empty(engine.Active());
        Assert.Equal(DownloadStatus.Queued, _bed.Downloads.Find(item.Id)!.Status);
        Assert.Equal(DownloadProtocol.Torrent, engine.Find(item.Id)!.Protocol);
        Assert.Equal(DownloadActionResult.Invalid, await engine.RetryAsync(item.Id));

        var queue = new CombinedDownloadQueue(engine, _bed.Tracker);
        Assert.Equal(item.Id, Assert.Single(queue.Active()).Id);
        Assert.Equal(item.ExternalId, queue.Find(item.Id)!.ExternalId);

        // Deleting a torrent row through the engine (history cleanup) works without NZB or work folder.
        Assert.Equal(DownloadActionResult.Ok, await engine.DeleteAsync(item.Id, deleteFiles: true));
    }

    public void Dispose() => _bed.Dispose();
}
