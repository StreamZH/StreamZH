// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Indexers;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Indexers;

namespace StreamZH.Tests.Library;

public sealed class IndexerServiceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero));
    private readonly IndexerService _service;

    public IndexerServiceTests()
    {
        var database = new Database(_file);
        database.Migrate();
        _service = new IndexerService(new IndexerRepository(database), new IndexerStatusRepository(database), _time, NullLogger<IndexerService>.Instance);
    }

    [Fact]
    public void Create_normalizes_and_fills_defaults()
    {
        var created = _service.Create(Valid("  Usenet A ") with
        {
            BaseUrl = " http://prowlarr:9696/1/ ",
            ApiPath = "",
            ApiKey = " key ",
            Categories = [2040, 2000, 2040],
            Implementation = "",
        });

        Assert.True(created.Id > 0);
        Assert.Equal("Usenet A", created.Name);
        Assert.Equal("http://prowlarr:9696/1", created.BaseUrl);
        Assert.Equal("/api", created.ApiPath);
        Assert.Equal("key", created.ApiKey);
        Assert.Equal([2000, 2040], created.Categories);
        Assert.Equal(IndexerDefinition.Newznab, created.Implementation);

        Assert.Equal("/newznab/api", _service.Create(Valid("B") with { ApiPath = "newznab/api/" }).ApiPath);
    }

    [Fact]
    public void Validation_reports_every_problem()
    {
        var errors = Errors(() => _service.Create(Valid(" ") with
        {
            BaseUrl = "ftp://indexer.example",
            ApiPath = "/api?t=caps",
            Priority = 0,
            Categories = [],
            TvCategories = [],
            Implementation = "Cardigann",
        }));
        Assert.Equal(["apiPath", "baseUrl", "categories", "implementation", "name", "priority"], errors);

        Assert.Equal(["baseUrl"], Errors(() => _service.Create(Valid("A") with { BaseUrl = "indexer.example" })));
        Assert.Equal(["baseUrl"], Errors(() => _service.Create(Valid("A") with { BaseUrl = "https://indexer.example/?apikey=1" })));
        Assert.Equal(["priority"], Errors(() => _service.Create(Valid("A") with { Priority = 51 })));
        Assert.Equal(["categories"], Errors(() => _service.Create(Valid("A") with { Categories = [-5] })));
        Assert.Equal(["name"], Errors(() => _service.Create(Valid(new string('x', 101)))));
        Assert.Equal(["apiKey"], Errors(() => _service.Create(Valid("A") with { ApiKey = IndexerService.MaskedApiKey })));

        // Only TV categories is fine; priority bounds are inclusive.
        _service.Create(Valid("TV only") with { Categories = [], Priority = 1 });
        _service.Create(Valid("Last") with { Priority = 50 });
    }

    [Fact]
    public void Names_are_unique_ignoring_case()
    {
        var first = _service.Create(Valid("Usenet"));
        Assert.Equal(["name"], Errors(() => _service.Create(Valid("USENET"))));

        // Keeping its own name is fine.
        _service.Update(first.Id, first with { Priority = 10 });
        var second = _service.Create(Valid("Other"));
        Assert.Equal(["name"], Errors(() => _service.Update(second.Id, second with { Name = "usenet" })));
    }

    [Fact]
    public void Update_keeps_the_stored_key_when_the_masked_value_comes_back()
    {
        var created = _service.Create(Valid("A") with { ApiKey = "original" });

        var updated = _service.Update(created.Id, created with { ApiKey = IndexerService.MaskedApiKey, Name = "B" });
        Assert.Equal("original", updated.ApiKey);
        Assert.Equal("B", updated.Name);

        Assert.Equal("changed", _service.Update(created.Id, created with { ApiKey = "changed" }).ApiKey);

        var missing = Assert.Throws<LibraryException>(() => _service.Update(999, created));
        Assert.Equal(LibraryError.NotFound, missing.Error);
        Assert.Equal(LibraryError.NotFound, Assert.Throws<LibraryException>(() => _service.Delete(999)).Error);

        _service.Delete(created.Id);
        Assert.Null(_service.Find(created.Id));
    }

    [Fact]
    public void Failures_escalate_the_back_off_and_success_resets_it()
    {
        var indexer = _service.Create(Valid("A"));
        var expected = new[] { 5, 15, 30, 60, 180, 360, 720, 1440, 1440 };

        foreach (var minutes in expected)
        {
            _service.RecordFailure(indexer.Id, new IndexerException("Timeout"));
            var health = _service.Health(indexer.Id);
            Assert.False(health.Healthy);
            Assert.Equal(_time.Now.AddMinutes(minutes), health.DisabledTill);
            Assert.Equal("Timeout", health.LastError);
            Assert.DoesNotContain(indexer.Id, _service.Active(IndexerUse.AutomaticSearch).Select(i => i.Id));

            _time.Advance(TimeSpan.FromMinutes(minutes));
            Assert.True(_service.Health(indexer.Id).Healthy);
            Assert.Contains(indexer.Id, _service.Active(IndexerUse.AutomaticSearch).Select(i => i.Id));
        }

        Assert.Equal(IndexerBackOff.MaxLevel, _service.Health(indexer.Id).EscalationLevel);

        _service.RecordSuccess(indexer.Id);
        Assert.Equal(IndexerHealth.Ok(indexer.Id), _service.Health(indexer.Id));
        Assert.Empty(_service.Health());

        // Starts again from the first step.
        _service.RecordFailure(indexer.Id, new HttpRequestException("Connection refused"));
        Assert.Equal(_time.Now.AddMinutes(5), _service.Health(indexer.Id).DisabledTill);
        Assert.Equal(1, _service.Health(indexer.Id).EscalationLevel);
    }

    [Fact]
    public void A_longer_retry_after_wins_over_the_back_off()
    {
        var indexer = _service.Create(Valid("A"));

        _service.RecordFailure(indexer.Id, new IndexerException("Request limit reached", 500, TimeSpan.FromHours(2)));
        Assert.Equal(_time.Now.AddHours(2), _service.Health(indexer.Id).DisabledTill);

        _time.Advance(TimeSpan.FromHours(2));
        _service.RecordFailure(indexer.Id, new IndexerException("Request limit reached", 500, TimeSpan.FromMinutes(1)));
        Assert.Equal(_time.Now.AddMinutes(15), _service.Health(indexer.Id).DisabledTill);
    }

    [Fact]
    public void Failures_for_unknown_indexers_are_ignored()
    {
        _service.RecordFailure(0, new IndexerException("x"));
        _service.RecordFailure(42, new IndexerException("x"));
        _service.RecordSuccess(42);
        Assert.Empty(_service.Health());
    }

    [Fact]
    public void Active_filters_by_use_and_orders_by_priority_then_name()
    {
        var b = _service.Create(Valid("b") with { Priority = 10 });
        var a = _service.Create(Valid("A") with { Priority = 10 });
        var low = _service.Create(Valid("Low") with { Priority = 40 });
        var top = _service.Create(Valid("Top") with { Priority = 1, EnableRss = false });
        var manual = _service.Create(Valid("Manual") with { EnableAutomaticSearch = false, EnableRss = false });

        Assert.Equal([top.Id, a.Id, b.Id, manual.Id, low.Id], _service.Active(IndexerUse.InteractiveSearch).Select(i => i.Id));
        Assert.Equal([top.Id, a.Id, b.Id, low.Id], _service.Active(IndexerUse.AutomaticSearch).Select(i => i.Id));
        Assert.Equal([a.Id, b.Id, low.Id], _service.Active(IndexerUse.Rss).Select(i => i.Id));

        _service.RecordFailure(a.Id, new IndexerException("down"));
        Assert.Equal([b.Id, low.Id], _service.Active(IndexerUse.Rss).Select(i => i.Id));
        Assert.Equal(a.Id, _service.Find(a.Id)!.Id);
    }

    [Fact]
    public async Task Test_returns_capabilities_or_a_readable_error()
    {
        var client = new StubIndexerClient();

        var ok = await _service.TestAsync(Valid("A") with { BaseUrl = "https://indexer.example/" }, client, CancellationToken.None);
        Assert.True(ok.Success);
        Assert.Same(StubIndexerClient.SampleCaps, ok.Capabilities);
        Assert.Equal("https://indexer.example", client.LastIndexer!.BaseUrl);
        Assert.Empty(_service.All());

        client.Error = new IndexerException("Incorrect user credentials", 100);
        var auth = await _service.TestAsync(Valid("A"), client, CancellationToken.None);
        Assert.False(auth.Success);
        Assert.Equal("Incorrect user credentials Check the API key.", auth.Error);

        client.Error = new HttpRequestException("Connection refused");
        Assert.Equal("Could not connect to indexer.example: Connection refused", (await _service.TestAsync(Valid("A"), client, CancellationToken.None)).Error);

        client.Error = new TaskCanceledException();
        Assert.Equal("indexer.example did not answer in time.", (await _service.TestAsync(Valid("A"), client, CancellationToken.None)).Error);

        // Name and categories do not matter for a test, the address does.
        await Assert.ThrowsAsync<ValidationFailedException>(() => _service.TestAsync(Valid("") with { BaseUrl = "nope" }, client, CancellationToken.None));
    }

    [Fact]
    public async Task Test_with_a_masked_key_uses_the_stored_key()
    {
        var client = new StubIndexerClient();
        var saved = _service.Create(Valid("A") with { ApiKey = "stored-key" });

        await _service.TestAsync(saved with { ApiKey = IndexerService.MaskedApiKey }, client, CancellationToken.None);
        Assert.Equal("stored-key", client.LastIndexer!.ApiKey);

        var error = await Assert.ThrowsAsync<ValidationFailedException>(
            () => _service.TestAsync(saved with { Id = 0, ApiKey = IndexerService.MaskedApiKey }, client, CancellationToken.None));
        Assert.Equal(["apiKey"], error.Errors.Keys);
    }

    [Fact]
    public async Task A_successful_test_of_the_saved_indexer_ends_its_waiting_time()
    {
        var client = new StubIndexerClient();
        var saved = _service.Create(Valid("A") with { ApiKey = "stored-key" });
        _service.RecordFailure(saved.Id, new IndexerException("Timeout"));

        // A draft with another address does not count for the saved indexer.
        await _service.TestAsync(saved with { BaseUrl = "https://other.example", ApiKey = "k" }, client, CancellationToken.None);
        Assert.False(_service.Health(saved.Id).Healthy);

        client.Error = new IndexerException("Incorrect user credentials", 100);
        await _service.TestAsync(saved with { ApiKey = IndexerService.MaskedApiKey }, client, CancellationToken.None);
        Assert.False(_service.Health(saved.Id).Healthy);

        client.Error = null;
        await _service.TestAsync(saved with { ApiKey = IndexerService.MaskedApiKey }, client, CancellationToken.None);
        Assert.Equal(IndexerHealth.Ok(saved.Id), _service.Health(saved.Id));
    }

    [Fact]
    public void Schema_has_the_radarr_and_sonarr_defaults()
    {
        var schema = IndexerService.Schema();
        Assert.Equal("/api", schema.ApiPath);
        Assert.Equal(25, schema.Priority);
        Assert.True(schema.EnableRss && schema.EnableAutomaticSearch && schema.EnableInteractiveSearch);
        Assert.Equal([2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060], schema.Categories);
        Assert.Equal([5030, 5040], schema.TvCategories);
    }

    private static IndexerDefinition Valid(string name) => new(
        0, name, IndexerDefinition.Newznab, "https://indexer.example", "/api", "key", [2000], [5000], 25, true, true, true);

    private static string[] Errors(Action action) =>
        [.. Assert.Throws<ValidationFailedException>(action).Errors.Keys.Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(file);
        }
    }
}
