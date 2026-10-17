// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Indexers;

namespace StreamZH.Tests.Library;

/// <summary>Counts calls per kind; searches return <see cref="SearchResults"/> releases, or throw <see cref="Error"/>.</summary>
public sealed class CountingIndexerClient : IIndexerClient
{
    public int Caps { get; private set; }
    public int Searches { get; private set; }
    public int Recent { get; private set; }
    public int Downloads { get; private set; }
    public int SearchResults { get; set; } = 3;
    public Exception? Error { get; set; }

    public Task<IndexerCapabilities> GetCapabilitiesAsync(IndexerDefinition indexer, CancellationToken cancellationToken)
    {
        Caps++;
        return Error is null ? Task.FromResult(StubIndexerClient.SampleCaps) : Task.FromException<IndexerCapabilities>(Error);
    }

    public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(IndexerDefinition indexer, SearchCriteria criteria, CancellationToken cancellationToken)
    {
        Searches++;
        return Error is null ? Task.FromResult(Releases(SearchResults)) : Task.FromException<IReadOnlyList<ReleaseInfo>>(Error);
    }

    public Task<IReadOnlyList<ReleaseInfo>> FetchRecentAsync(IndexerDefinition indexer, CancellationToken cancellationToken)
    {
        Recent++;
        return Task.FromResult(Releases(5));
    }

    public Task<byte[]> DownloadNzbAsync(IndexerDefinition indexer, ReleaseInfo release, CancellationToken cancellationToken)
    {
        Downloads++;
        return Error is null ? Task.FromResult("<nzb/>"u8.ToArray()) : Task.FromException<byte[]>(Error);
    }

    private static IReadOnlyList<ReleaseInfo> Releases(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new ReleaseInfo { Guid = $"g{i}", Title = $"Release {i}", DownloadUrl = $"https://x.example/{i}" })];
}

public sealed class IndexerTrackingTests : IDisposable
{
    private static readonly MovieSearchCriteria Dune = new() { Titles = ["Dune: Part Two"], Year = 2024, TmdbId = 693134 };

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero));
    private readonly IndexerHistoryRepository _history;
    private readonly IndexerService _service;
    private readonly CountingIndexerClient _inner = new();
    private readonly TrackedIndexerClient _client;
    private readonly InProcessEventBus _events = new();
    private int _changes;

    public IndexerTrackingTests()
    {
        var database = new Database(_file);
        database.Migrate();
        _history = new IndexerHistoryRepository(database);
        _service = new IndexerService(new IndexerRepository(database), new IndexerStatusRepository(database), _time, NullLogger<IndexerService>.Instance, _history, _events);
        _client = new TrackedIndexerClient(_inner, _history, _time, NullLogger<TrackedIndexerClient>.Instance, _service);
        _events.Subscribe<IndexersChanged>((_, _) =>
        {
            Interlocked.Increment(ref _changes);
            return ValueTask.CompletedTask;
        });
    }

    [Fact]
    public async Task Records_searches_rss_grabs_and_tests_with_their_source()
    {
        var indexer = Create("A");

        using (IndexerCallContext.Begin(IndexerUse.AutomaticSearch))
        {
            await _client.SearchAsync(indexer, Dune, CancellationToken.None);
        }

        await _client.SearchAsync(indexer, new EpisodeSearchCriteria { Titles = ["The Office"], SeasonNumber = 2, EpisodeNumbers = [5], TvdbId = 73244 }, CancellationToken.None);
        await _client.FetchRecentAsync(indexer, CancellationToken.None);
        using (IndexerCallContext.Begin(IndexerCallSource.Search))
        {
            await _client.DownloadNzbAsync(indexer, new ReleaseInfo { Guid = "g", Title = "Dune.Part.Two.2024.1080p", DownloadUrl = "https://x.example/g" }, CancellationToken.None);
        }

        await _service.TestAsync(indexer with { ApiKey = IndexerService.MaskedApiKey }, _client, CancellationToken.None);

        _inner.Error = new IndexerException("Request limit reached", 500);
        await Assert.ThrowsAsync<IndexerException>(() => _client.SearchAsync(indexer, Dune, CancellationToken.None));

        var (records, total) = _history.Page(1, 50, indexer.Id, null);
        Assert.Equal(6, total);
        var rows = records.Reverse().ToList();

        Assert.Equal((IndexerHistoryEventType.Query, IndexerCallSource.Automatic, true, 3), (rows[0].EventType, rows[0].Source, rows[0].Successful, rows[0].Results!.Value));
        Assert.Equal("Dune: Part Two (2024) tmdbid=693134", rows[0].Query);
        Assert.Equal("A", rows[0].Indexer);

        Assert.Equal((IndexerHistoryEventType.Query, IndexerCallSource.Interactive), (rows[1].EventType, rows[1].Source));
        Assert.Equal("The Office S02E05 tvdbid=73244", rows[1].Query);

        Assert.Equal((IndexerHistoryEventType.Rss, IndexerCallSource.Rss, 5), (rows[2].EventType, rows[2].Source, rows[2].Results!.Value));
        Assert.Null(rows[2].Query);

        Assert.Equal((IndexerHistoryEventType.Grab, IndexerCallSource.Search, "Dune.Part.Two.2024.1080p"), (rows[3].EventType, rows[3].Source, rows[3].Title));
        Assert.Null(rows[3].Results);

        Assert.Equal((IndexerHistoryEventType.Caps, IndexerCallSource.Test, true), (rows[4].EventType, rows[4].Source, rows[4].Successful));

        Assert.False(rows[5].Successful);
        Assert.Equal("Request limit reached", rows[5].Error);
        Assert.Null(rows[5].Results);
        Assert.All(rows, r => Assert.True(r.ElapsedMs >= 0));
        Assert.Null(IndexerCallContext.Current);
    }

    [Fact]
    public async Task Unsaved_indexers_are_not_recorded()
    {
        var unsaved = Valid("New") with { Id = 0 };
        await _service.TestAsync(unsaved, _client, CancellationToken.None);
        Assert.Equal(1, _inner.Caps);
        Assert.Equal(0, _history.Page(1, 50, null, null).Total);
    }

    [Fact]
    public async Task Query_limit_blocks_without_request_history_row_or_back_off()
    {
        var indexer = Create("A", queryLimit: 2);
        await _client.SearchAsync(indexer, Dune, CancellationToken.None);
        await _client.FetchRecentAsync(indexer, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<IndexerException>(() => _client.SearchAsync(indexer, Dune, CancellationToken.None));
        Assert.True(ex.IsLimitReached);
        Assert.Equal("Query limit reached (2 per day)", ex.Message);
        Assert.Equal(TimeSpan.FromHours(24), ex.RetryAfter);
        Assert.Equal(1, _inner.Searches);
        Assert.Equal(2, _history.Page(1, 50, indexer.Id, null).Total);

        _service.RecordFailure(indexer.Id, ex);
        Assert.True(_service.Health(indexer.Id).Healthy);
        Assert.Single(_service.Active(IndexerUse.InteractiveSearch));

        var status = _service.LimitStatus(indexer, _service.Usage().GetValueOrDefault(indexer.Id));
        Assert.Equal((2, 0, IndexerLimitKind.Query), (status.QueriesUsed, status.GrabsUsed, status.LimitReached));

        // Grabs have their own limit.
        await _client.DownloadNzbAsync(indexer, new ReleaseInfo { Guid = "g", Title = "T", DownloadUrl = "https://x.example/g" }, CancellationToken.None);
        Assert.Equal(1, _inner.Downloads);
    }

    [Fact]
    public async Task Limit_errors_never_create_a_status_row()
    {
        var indexer = Create("A", queryLimit: 1);
        await _client.SearchAsync(indexer, Dune, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<IndexerException>(() => _client.SearchAsync(indexer, Dune, CancellationToken.None));
        _service.RecordFailure(indexer.Id, ex);
        Assert.Null(_service.Health().GetValueOrDefault(indexer.Id));
    }

    [Fact]
    public async Task Grab_limit_uses_a_rolling_hour()
    {
        var indexer = Create("A", grabLimit: 1, unit: IndexerLimitsUnit.Hour);
        var release = new ReleaseInfo { Guid = "g", Title = "T", DownloadUrl = "https://x.example/g" };
        await _client.DownloadNzbAsync(indexer, release, CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(59));
        var ex = await Assert.ThrowsAsync<IndexerException>(() => _client.DownloadNzbAsync(indexer, release, CancellationToken.None));
        Assert.Equal("Grab limit reached (1 per hour)", ex.Message);
        Assert.Equal(TimeSpan.FromMinutes(1), ex.RetryAfter);
        Assert.Equal(IndexerLimitKind.Grab, _service.LimitStatus(indexer, _service.Usage().GetValueOrDefault(indexer.Id)).LimitReached);

        _time.Advance(TimeSpan.FromMinutes(2));
        await _client.DownloadNzbAsync(indexer, release, CancellationToken.None);
        Assert.Equal(2, _inner.Downloads);

        // Searches are not limited by the grab limit.
        await _client.SearchAsync(indexer, Dune, CancellationToken.None);
        await _client.SearchAsync(indexer, Dune, CancellationToken.None);
        Assert.Equal(2, _inner.Searches);
    }

    [Fact]
    public async Task Query_limit_per_day_counts_the_last_24_hours()
    {
        var indexer = Create("A", queryLimit: 1);
        await _client.SearchAsync(indexer, Dune, CancellationToken.None);

        _time.Advance(TimeSpan.FromHours(23));
        await Assert.ThrowsAsync<IndexerException>(() => _client.SearchAsync(indexer, Dune, CancellationToken.None));
        Assert.Equal(1, _service.LimitStatus(indexer, _service.Usage().GetValueOrDefault(indexer.Id)).QueriesUsed);

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await _client.SearchAsync(indexer, Dune, CancellationToken.None);
        Assert.Equal(2, _inner.Searches);
    }

    [Fact]
    public async Task Zero_or_empty_limits_are_unlimited()
    {
        var indexer = Create("A", queryLimit: 0);
        for (var i = 0; i < 5; i++)
        {
            await _client.SearchAsync(indexer, Dune, CancellationToken.None);
        }

        Assert.Equal(5, _inner.Searches);
        Assert.Null(_service.LimitStatus(indexer, _service.Usage().GetValueOrDefault(indexer.Id)).LimitReached);
    }

    [Fact]
    public async Task Reaching_a_limit_notifies_the_ui()
    {
        var indexer = Create("A", queryLimit: 2);
        var before = Volatile.Read(ref _changes);
        await _client.SearchAsync(indexer, Dune, CancellationToken.None);
        Assert.Equal(before, Volatile.Read(ref _changes));
        await _client.SearchAsync(indexer, Dune, CancellationToken.None);
        Assert.Equal(before + 1, Volatile.Read(ref _changes));
    }

    [Fact]
    public void Vip_expiration_in_days()
    {
        var today = _service.LimitStatus(Valid("A") with { VipExpiration = "2026-10-17" }, null);
        Assert.Equal(0, today.VipExpiresInDays);
        Assert.Equal(76, _service.LimitStatus(Valid("A") with { VipExpiration = "2027-01-01" }, null).VipExpiresInDays);
        Assert.Equal(-17, _service.LimitStatus(Valid("A") with { VipExpiration = "2026-09-30" }, null).VipExpiresInDays);
        Assert.Null(_service.LimitStatus(Valid("A"), null).VipExpiresInDays);
    }

    [Fact]
    public async Task Old_history_is_pruned_after_90_days()
    {
        var indexer = Create("A");
        _history.Add(new IndexerHistoryEntry { Date = _time.Now.UtcDateTime.AddDays(-91), IndexerId = indexer.Id, EventType = IndexerHistoryEventType.Query, Successful = true });
        _history.Add(new IndexerHistoryEntry { Date = _time.Now.UtcDateTime.AddDays(-89), IndexerId = indexer.Id, EventType = IndexerHistoryEventType.Query, Successful = true });

        await _client.SearchAsync(indexer, Dune, CancellationToken.None);

        Assert.Equal(2, _history.Page(1, 50, indexer.Id, null).Total);
    }

    [Fact]
    public void Describes_criteria_for_the_history()
    {
        Assert.Equal("The Office S02", TrackedIndexerClient.Describe(new EpisodeSearchCriteria { Titles = ["The Office"], SeasonNumber = 2 }));
        Assert.Equal("Show S01E01E02", TrackedIndexerClient.Describe(new EpisodeSearchCriteria { Titles = ["Show"], SeasonNumber = 1, EpisodeNumbers = [2, 1] }));
        Assert.Equal("Daily 2024-03-01", TrackedIndexerClient.Describe(new EpisodeSearchCriteria { Titles = ["Daily"], AirDate = "2024-03-01" }));
        Assert.Equal("Alien (1979) imdbid=tt0078748", TrackedIndexerClient.Describe(new MovieSearchCriteria { Titles = ["Alien"], Year = 1979, ImdbId = "tt0078748" }));
    }

    private IndexerDefinition Create(string name, int? queryLimit = null, int? grabLimit = null, IndexerLimitsUnit unit = IndexerLimitsUnit.Day) =>
        _service.Create(Valid(name) with { QueryLimit = queryLimit, GrabLimit = grabLimit, LimitsUnit = unit });

    private static IndexerDefinition Valid(string name) =>
        new(0, name, IndexerDefinition.Newznab, "https://indexer.example", "/api", "key", [2000], [5040], 25, true, true, true);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(file);
        }
    }
}
