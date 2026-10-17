// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Indexers;
using StreamZH.Data;

namespace StreamZH.Tests.Data;

public sealed class IndexerRepositoryTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void Stores_updates_and_deletes_indexers()
    {
        var database = Migrated();
        var repository = new IndexerRepository(database);
        var added = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        var id = repository.Add(Indexer("Usenet A", 30, [2000, 2040], [5030, 5040]), added);
        var second = repository.Add(Indexer("usenet b", 10, [2000], []) with { EnableRss = false }, added);

        var stored = repository.Find(id)!;
        Assert.Equal(Indexer("Usenet A", 30, [2000, 2040], [5030, 5040]) with { Id = id }, stored, new DefinitionComparer());
        Assert.Equal("secret", stored.ApiKey);
        Assert.Equal([], repository.Find(second)!.TvCategories);
        Assert.False(repository.Find(second)!.EnableRss);

        // Ordered by priority, then name.
        Assert.Equal(["usenet b", "Usenet A"], repository.All().Select(i => i.Name));
        Assert.Equal(second, repository.FindByName("USENET B")!.Id);
        Assert.Null(repository.FindByName("other"));

        repository.Update(stored with { Name = "Renamed", ApiPath = "/newznab/api", Categories = [2045], Priority = 1, EnableInteractiveSearch = false });
        var updated = repository.Find(id)!;
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("/newznab/api", updated.ApiPath);
        Assert.Equal([2045], updated.Categories);
        Assert.Equal(1, updated.Priority);
        Assert.False(updated.EnableInteractiveSearch);

        repository.Delete(id);
        Assert.Null(repository.Find(id));
        Assert.Single(repository.All());
    }

    [Fact]
    public void Status_is_upserted_and_removed_with_the_indexer()
    {
        var database = Migrated();
        var indexers = new IndexerRepository(database);
        var statuses = new IndexerStatusRepository(database);
        var id = indexers.Add(Indexer("A", 25, [2000], []), DateTime.UtcNow);
        var now = new DateTime(2026, 10, 17, 15, 30, 0, DateTimeKind.Utc);

        statuses.Upsert(new IndexerStatus(id, now, now, 1, now.AddMinutes(5), "Request limit reached"));
        statuses.Upsert(new IndexerStatus(id, now, now.AddMinutes(6), 2, now.AddMinutes(21), "Timeout"));

        var status = statuses.Find(id)!;
        Assert.Equal(new IndexerStatus(id, now, now.AddMinutes(6), 2, now.AddMinutes(21), "Timeout"), status);
        Assert.Equal(DateTimeKind.Utc, status.DisabledTill!.Value.Kind);
        Assert.Single(statuses.All());

        indexers.Delete(id);
        Assert.Null(statuses.Find(id));
        Assert.Empty(statuses.All());
    }

    [Fact]
    public void Category_lists_round_trip()
    {
        Assert.Equal("2000,2040", IndexerCategoryList.Serialize([2000, 2040]));
        Assert.Equal([2000, 2040], IndexerCategoryList.Deserialize(" 2000, 2040 ,x,"));
        Assert.Empty(IndexerCategoryList.Deserialize(""));
    }

    private static IndexerDefinition Indexer(string name, int priority, int[] categories, int[] tvCategories) => new(
        0, name, IndexerDefinition.Newznab, "https://indexer.example", "/api", "secret", categories, tvCategories, priority, true, true, true);

    private Database Migrated()
    {
        var database = new Database(_file);
        database.Migrate();
        return database;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(file);
        }
    }

    /// <summary>Records compare lists by reference; compare them by content.</summary>
    private sealed class DefinitionComparer : IEqualityComparer<IndexerDefinition>
    {
        public bool Equals(IndexerDefinition? x, IndexerDefinition? y) =>
            x is not null && y is not null
            && x with { Categories = Array.Empty<int>(), TvCategories = Array.Empty<int>() } == y with { Categories = Array.Empty<int>(), TvCategories = Array.Empty<int>() }
            && x.Categories.SequenceEqual(y.Categories)
            && x.TvCategories.SequenceEqual(y.TvCategories);

        public int GetHashCode(IndexerDefinition obj) => obj.Id;
    }
}
