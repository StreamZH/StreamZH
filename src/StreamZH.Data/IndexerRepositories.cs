// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Dapper;
using StreamZH.Core.Indexers;

namespace StreamZH.Data;

/// <summary>Health of one indexer after a failure. Times are UTC. No row means healthy.</summary>
public sealed record IndexerStatus(
    int IndexerId,
    DateTime? InitialFailure,
    DateTime? MostRecentFailure,
    int EscalationLevel,
    DateTime? DisabledTill,
    string? LastError);

/// <summary>Category lists are stored as comma-separated ids.</summary>
public static class IndexerCategoryList
{
    public static string Serialize(IEnumerable<int> categories) =>
        string.Join(',', categories.Select(c => c.ToString(CultureInfo.InvariantCulture)));

    public static IReadOnlyList<int> Deserialize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null)
                .OfType<int>()];
}

public sealed class IndexerRepository(Database database)
{
    private const string Columns = """
        Id, Name, Implementation, BaseUrl, ApiPath, ApiKey, Categories, TvCategories, Priority,
        EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Added
        """;

    /// <summary>All indexers ordered by priority, then name.</summary>
    public IReadOnlyList<IndexerDefinition> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<IndexerRow>($"SELECT {Columns} FROM Indexers ORDER BY Priority, Name COLLATE NOCASE").Select(r => r.ToDefinition())];
    }

    public IndexerDefinition? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<IndexerRow>($"SELECT {Columns} FROM Indexers WHERE Id = @id", new { id })?.ToDefinition();
    }

    /// <summary>Case-insensitive lookup by name.</summary>
    public IndexerDefinition? FindByName(string name)
    {
        using var connection = database.Open();
        return connection.QueryFirstOrDefault<IndexerRow>($"SELECT {Columns} FROM Indexers WHERE Name = @name COLLATE NOCASE", new { name })?.ToDefinition();
    }

    public int Add(IndexerDefinition indexer, DateTime added)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO Indexers (Name, Implementation, BaseUrl, ApiPath, ApiKey, Categories, TvCategories, Priority,
                EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Added)
            VALUES (@Name, @Implementation, @BaseUrl, @ApiPath, @ApiKey, @Categories, @TvCategories, @Priority,
                @EnableRss, @EnableAutomaticSearch, @EnableInteractiveSearch, @Added);
            SELECT last_insert_rowid();
            """,
            IndexerRow.From(indexer, added));
    }

    public void Update(IndexerDefinition indexer)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Indexers SET Name = @Name, Implementation = @Implementation, BaseUrl = @BaseUrl, ApiPath = @ApiPath, ApiKey = @ApiKey,
                Categories = @Categories, TvCategories = @TvCategories, Priority = @Priority, EnableRss = @EnableRss,
                EnableAutomaticSearch = @EnableAutomaticSearch, EnableInteractiveSearch = @EnableInteractiveSearch
            WHERE Id = @Id
            """,
            IndexerRow.From(indexer, default));
    }

    /// <summary>Deletes the indexer; its status row goes with it (cascade).</summary>
    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM Indexers WHERE Id = @id", new { id });
    }

    private sealed class IndexerRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Implementation { get; init; } = IndexerDefinition.Newznab;
        public string BaseUrl { get; init; } = string.Empty;
        public string ApiPath { get; init; } = string.Empty;
        public string ApiKey { get; init; } = string.Empty;
        public string Categories { get; init; } = string.Empty;
        public string TvCategories { get; init; } = string.Empty;
        public long Priority { get; init; }
        public bool EnableRss { get; init; }
        public bool EnableAutomaticSearch { get; init; }
        public bool EnableInteractiveSearch { get; init; }
        public DateTime Added { get; init; }

        public static IndexerRow From(IndexerDefinition i, DateTime added) => new()
        {
            Id = i.Id,
            Name = i.Name,
            Implementation = i.Implementation,
            BaseUrl = i.BaseUrl,
            ApiPath = i.ApiPath,
            ApiKey = i.ApiKey,
            Categories = IndexerCategoryList.Serialize(i.Categories),
            TvCategories = IndexerCategoryList.Serialize(i.TvCategories),
            Priority = i.Priority,
            EnableRss = i.EnableRss,
            EnableAutomaticSearch = i.EnableAutomaticSearch,
            EnableInteractiveSearch = i.EnableInteractiveSearch,
            Added = added,
        };

        public IndexerDefinition ToDefinition() => new(
            (int)Id,
            Name,
            Implementation,
            BaseUrl,
            ApiPath,
            ApiKey,
            IndexerCategoryList.Deserialize(Categories),
            IndexerCategoryList.Deserialize(TvCategories),
            (int)Priority,
            EnableRss,
            EnableAutomaticSearch,
            EnableInteractiveSearch);
    }
}

public sealed class IndexerStatusRepository(Database database)
{
    private const string Columns = "IndexerId, InitialFailure, MostRecentFailure, EscalationLevel, DisabledTill, LastError";

    public IReadOnlyList<IndexerStatus> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<StatusRow>($"SELECT {Columns} FROM IndexerStatus").Select(r => r.ToStatus())];
    }

    public IndexerStatus? Find(int indexerId)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<StatusRow>($"SELECT {Columns} FROM IndexerStatus WHERE IndexerId = @indexerId", new { indexerId })?.ToStatus();
    }

    public void Upsert(IndexerStatus status)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO IndexerStatus (IndexerId, InitialFailure, MostRecentFailure, EscalationLevel, DisabledTill, LastError)
            VALUES (@IndexerId, @InitialFailure, @MostRecentFailure, @EscalationLevel, @DisabledTill, @LastError)
            ON CONFLICT(IndexerId) DO UPDATE SET InitialFailure = excluded.InitialFailure, MostRecentFailure = excluded.MostRecentFailure,
                EscalationLevel = excluded.EscalationLevel, DisabledTill = excluded.DisabledTill, LastError = excluded.LastError
            """,
            new { status.IndexerId, status.InitialFailure, status.MostRecentFailure, status.EscalationLevel, status.DisabledTill, status.LastError });
    }

    public void Delete(int indexerId)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM IndexerStatus WHERE IndexerId = @indexerId", new { indexerId });
    }

    private sealed class StatusRow
    {
        public long IndexerId { get; init; }
        public DateTime? InitialFailure { get; init; }
        public DateTime? MostRecentFailure { get; init; }
        public long EscalationLevel { get; init; }
        public DateTime? DisabledTill { get; init; }
        public string? LastError { get; init; }

        public IndexerStatus ToStatus() => new((int)IndexerId, Utc(InitialFailure), Utc(MostRecentFailure), (int)EscalationLevel, Utc(DisabledTill), LastError);

        private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
    }
}
