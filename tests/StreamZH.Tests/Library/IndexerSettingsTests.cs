// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Indexers;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Indexers;

namespace StreamZH.Tests.Library;

public sealed class IndexerSettingsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero));
    private readonly Database _database;
    private readonly IndexerRepository _repository;
    private readonly IndexerHistoryRepository _history;
    private readonly IndexerService _service;

    public IndexerSettingsTests()
    {
        _database = new Database(_file);
        _database.Migrate();
        _repository = new IndexerRepository(_database);
        _history = new IndexerHistoryRepository(_database);
        _service = new IndexerService(_repository, new IndexerStatusRepository(_database), _time, NullLogger<IndexerService>.Instance, _history);
    }

    [Fact]
    public void Stores_and_reads_the_new_settings()
    {
        var created = _service.Create(Valid("A") with
        {
            Enable = false,
            DefinitionKey = " NZBgeek ",
            AdditionalParameters = " &attrs=poster ",
            VipExpiration = "2027-01-31T00:00:00Z",
            QueryLimit = 100,
            GrabLimit = 0,
            LimitsUnit = IndexerLimitsUnit.Hour,
        });

        var stored = _service.Find(created.Id)!;
        Assert.False(stored.Enable);
        Assert.Equal("nzbgeek", stored.DefinitionKey);
        Assert.Equal("&attrs=poster", stored.AdditionalParameters);
        Assert.Equal("2027-01-31", stored.VipExpiration);
        Assert.Equal(100, stored.QueryLimit);
        Assert.Equal(0, stored.GrabLimit);
        Assert.Equal(IndexerLimitsUnit.Hour, stored.LimitsUnit);

        var updated = _service.Update(created.Id, stored with { Enable = true, VipExpiration = null, QueryLimit = null, LimitsUnit = IndexerLimitsUnit.Day });
        Assert.True(updated.Enable);
        Assert.Null(updated.VipExpiration);
        Assert.Null(updated.QueryLimit);
        Assert.Equal(IndexerLimitsUnit.Day, updated.LimitsUnit);
    }

    [Fact]
    public void Migration_gives_existing_indexers_the_defaults()
    {
        using (var connection = _database.Open())
        {
            connection.Execute(
                """
                INSERT INTO Indexers (Name, Implementation, BaseUrl, ApiPath, ApiKey, Categories, TvCategories, Priority,
                    EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Added)
                VALUES ('Old', 'Newznab', 'https://old.example', '/api', 'k', '2000', '5040', 25, 1, 1, 1, '2026-01-01 00:00:00')
                """);
        }

        var old = _service.All().Single();
        Assert.True(old.Enable);
        Assert.Null(old.DefinitionKey);
        Assert.Equal(string.Empty, old.AdditionalParameters);
        Assert.Null(old.VipExpiration);
        Assert.Null(old.QueryLimit);
        Assert.Null(old.GrabLimit);
        Assert.Equal(IndexerLimitsUnit.Day, old.LimitsUnit);
        Assert.Equal(IndexerCatalog.GenericNewznabKey, _service.Details(old.Id)!.Definition!.Key);

        using var check = _database.Open();
        var columns = check.Query<string>("SELECT name FROM pragma_table_info('IndexerHistory')").ToList();
        Assert.Equal(["Id", "Date", "IndexerId", "EventType", "Successful", "ElapsedMs", "Source", "Query", "Results", "Title", "Error"], columns);
        Assert.Contains("IX_IndexerHistory_IndexerId_Date", check.Query<string>("SELECT name FROM pragma_index_list('IndexerHistory')"));
    }

    [Fact]
    public void Validates_the_new_settings()
    {
        var ex = Assert.Throws<ValidationFailedException>(() => _service.Create(Valid("A") with
        {
            DefinitionKey = "unknown",
            AdditionalParameters = "&a=b c#",
            VipExpiration = "next year",
            QueryLimit = -1,
            GrabLimit = -5,
        }));

        Assert.Equal(
            ["additionalParameters", "definitionKey", "grabLimit", "queryLimit", "vipExpiration"],
            ex.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Disabled_indexers_are_never_active()
    {
        var on = _service.Create(Valid("On"));
        _service.Create(Valid("Off") with { Enable = false });

        foreach (var use in Enum.GetValues<IndexerUse>())
        {
            Assert.Equal([on.Id], _service.Active(use).Select(i => i.Id));
        }
    }

    [Fact]
    public async Task Testing_a_saved_indexer_stores_its_capabilities_for_the_details()
    {
        var created = _service.Create(Valid("A"));
        Assert.Null(_service.Details(created.Id)!.Capabilities);

        var client = new StubIndexerClient();
        var result = await _service.TestAsync(created with { ApiKey = IndexerService.MaskedApiKey }, client, CancellationToken.None);
        Assert.True(result.Success);

        var details = _service.Details(created.Id)!;
        Assert.Equal(StubIndexerClient.SampleCaps.Categories.Select(c => c.Id), details.Capabilities!.Categories.Select(c => c.Id));
        Assert.Equal("Movies/HD", details.Capabilities.Categories[0].SubCategories[0].Name);
        Assert.Equal(["q", "imdbid", "tmdbid"], details.Capabilities.MovieSearchParams);
        Assert.Equal(_time.Now, details.CapabilitiesCheckedAt);
        Assert.Equal(IndexerCatalog.GenericNewznabKey, details.Definition!.Key);

        // A test of other settings (the form being edited) does not overwrite the stored capabilities.
        _time.Advance(TimeSpan.FromMinutes(5));
        await _service.TestAsync(created with { BaseUrl = "https://other.example" }, client, CancellationToken.None);
        Assert.Equal(_time.Now - TimeSpan.FromMinutes(5), _service.Details(created.Id)!.CapabilitiesCheckedAt);
    }

    [Fact]
    public async Task Saving_right_after_a_successful_test_stores_the_capabilities()
    {
        var client = new StubIndexerClient();
        await _service.TestAsync(Valid("A"), client, CancellationToken.None);
        var created = _service.Create(Valid("A") with { DefinitionKey = "nzbgeek" });

        var details = _service.Details(created.Id)!;
        Assert.NotNull(details.Capabilities);
        Assert.Equal("nzbgeek", details.Definition!.Key);

        // Another URL without a test: the old capabilities no longer apply.
        _service.Update(created.Id, created with { BaseUrl = "https://moved.example", ApiKey = IndexerService.MaskedApiKey });
        Assert.Null(_service.Details(created.Id)!.Capabilities);
        Assert.Null(_service.Details(created.Id)!.CapabilitiesCheckedAt);

        // Too long ago: not used.
        await _service.TestAsync(Valid("B"), client, CancellationToken.None);
        _time.Advance(IndexerService.RecentTestLifetime + TimeSpan.FromMinutes(1));
        Assert.Null(_service.Details(_service.Create(Valid("B")).Id)!.Capabilities);
    }

    [Fact]
    public async Task Test_all_tests_enabled_indexers_and_records_their_health()
    {
        var good = _service.Create(Valid("Good"));
        var bad = _service.Create(Valid("Bad") with { BaseUrl = "https://bad.example" });
        _service.Create(Valid("Off") with { Enable = false });
        var client = new FailingForHostClient("bad.example");

        var results = await _service.TestAllAsync(client, CancellationToken.None);

        Assert.Equal(["Bad", "Good"], results.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.True(results.Single(r => r.Id == good.Id).Ok);
        Assert.Null(results.Single(r => r.Id == good.Id).Message);
        var failed = results.Single(r => r.Id == bad.Id);
        Assert.False(failed.Ok);
        Assert.Equal("Invalid API key", failed.Message);
        Assert.Equal("Invalid API key", _service.Health(bad.Id).LastError);
        Assert.False(_service.Health(bad.Id).Healthy);
        Assert.True(_service.Health(good.Id).Healthy);
        Assert.NotNull(_service.Details(good.Id)!.Capabilities);
    }

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

    private sealed class FailingForHostClient(string host) : IIndexerClient
    {
        public Task<IndexerCapabilities> GetCapabilitiesAsync(IndexerDefinition indexer, CancellationToken cancellationToken) =>
            indexer.BaseUrl.Contains(host, StringComparison.Ordinal)
                ? Task.FromException<IndexerCapabilities>(new IndexerException("Invalid API key", 100))
                : Task.FromResult(StubIndexerClient.SampleCaps);

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(IndexerDefinition indexer, SearchCriteria criteria, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReleaseInfo>> FetchRecentAsync(IndexerDefinition indexer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> DownloadNzbAsync(IndexerDefinition indexer, ReleaseInfo release, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
