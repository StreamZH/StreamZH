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
        EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Added,
        Enable, DefinitionKey, AdditionalParameters, VipExpiration, QueryLimit, GrabLimit, LimitsUnit,
        MinimumSeeders, SeedRatio, SeedTimeMinutes, SeasonPackSeedTimeMinutes
        """;

    /// <summary>All indexers ordered by priority, then name.</summary>
    public IReadOnlyList<IndexerDefinition> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<IndexerRow>($"SELECT {Columns} FROM Indexers ORDER BY Priority, Name COLLATE NOCASE").Select(Read)];
    }

    public IndexerDefinition? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<IndexerRow>($"SELECT {Columns} FROM Indexers WHERE Id = @id", new { id }) is { } row ? Read(row) : null;
    }

    public const string SecretKind = "Indexer";

    // 0.12: the API key is stored encrypted; an unreadable one reads as empty (the indexer asks for it again).
    private IndexerDefinition Read(IndexerRow row)
    {
        var definition = row.ToDefinition();
        return definition with { ApiKey = database.Secrets.Decrypt(definition.ApiKey, new SecretLocation(SecretKind, definition.Name, "API key")) ?? string.Empty };
    }

    private IndexerRow Write(IndexerDefinition indexer, DateTime added)
    {
        database.Secrets.Forget(SecretKind, indexer.Name);
        return IndexerRow.From(indexer with { ApiKey = database.Secrets.Encrypt(indexer.ApiKey) ?? string.Empty }, added);
    }

    /// <summary>Case-insensitive lookup by name.</summary>
    public IndexerDefinition? FindByName(string name)
    {
        using var connection = database.Open();
        return connection.QueryFirstOrDefault<IndexerRow>($"SELECT {Columns} FROM Indexers WHERE Name = @name COLLATE NOCASE", new { name }) is { } row ? Read(row) : null;
    }

    public int Add(IndexerDefinition indexer, DateTime added)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO Indexers (Name, Implementation, BaseUrl, ApiPath, ApiKey, Categories, TvCategories, Priority,
                EnableRss, EnableAutomaticSearch, EnableInteractiveSearch, Added,
                Enable, DefinitionKey, AdditionalParameters, VipExpiration, QueryLimit, GrabLimit, LimitsUnit,
                MinimumSeeders, SeedRatio, SeedTimeMinutes, SeasonPackSeedTimeMinutes)
            VALUES (@Name, @Implementation, @BaseUrl, @ApiPath, @ApiKey, @Categories, @TvCategories, @Priority,
                @EnableRss, @EnableAutomaticSearch, @EnableInteractiveSearch, @Added,
                @Enable, @DefinitionKey, @AdditionalParameters, @VipExpiration, @QueryLimit, @GrabLimit, @LimitsUnit,
                @MinimumSeeders, @SeedRatio, @SeedTimeMinutes, @SeasonPackSeedTimeMinutes);
            SELECT last_insert_rowid();
            """,
            Write(indexer, added));
    }

    public void Update(IndexerDefinition indexer)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Indexers SET Name = @Name, Implementation = @Implementation, BaseUrl = @BaseUrl, ApiPath = @ApiPath, ApiKey = @ApiKey,
                Categories = @Categories, TvCategories = @TvCategories, Priority = @Priority, EnableRss = @EnableRss,
                EnableAutomaticSearch = @EnableAutomaticSearch, EnableInteractiveSearch = @EnableInteractiveSearch,
                Enable = @Enable, DefinitionKey = @DefinitionKey, AdditionalParameters = @AdditionalParameters, VipExpiration = @VipExpiration,
                QueryLimit = @QueryLimit, GrabLimit = @GrabLimit, LimitsUnit = @LimitsUnit,
                MinimumSeeders = @MinimumSeeders, SeedRatio = @SeedRatio, SeedTimeMinutes = @SeedTimeMinutes,
                SeasonPackSeedTimeMinutes = @SeasonPackSeedTimeMinutes
            WHERE Id = @Id
            """,
            Write(indexer, default));
    }

    /// <summary>Stores the capabilities (JSON) read from the indexer and when they were read; nulls clear them.</summary>
    public void SaveCapabilities(int id, string? capabilitiesJson, DateTime? checkedAt)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE Indexers SET Capabilities = @capabilitiesJson, CapabilitiesCheckedAt = @checkedAt WHERE Id = @id",
            new { id, capabilitiesJson, checkedAt });
    }

    /// <summary>The stored capabilities (JSON) and when they were read; both null when never read.</summary>
    public (string? Json, DateTime? CheckedAt) FindCapabilities(int id)
    {
        using var connection = database.Open();
        var row = connection.QuerySingleOrDefault<CapabilitiesRow>("SELECT Capabilities, CapabilitiesCheckedAt FROM Indexers WHERE Id = @id", new { id });
        return row is null
            ? (null, null)
            : (row.Capabilities, row.CapabilitiesCheckedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null);
    }

    private sealed class CapabilitiesRow
    {
        public string? Capabilities { get; init; }
        public DateTime? CapabilitiesCheckedAt { get; init; }
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
        public bool Enable { get; init; } = true;
        public string? DefinitionKey { get; init; }
        public string AdditionalParameters { get; init; } = string.Empty;
        public string? VipExpiration { get; init; }
        public long? QueryLimit { get; init; }
        public long? GrabLimit { get; init; }
        public string LimitsUnit { get; init; } = "day";
        public long MinimumSeeders { get; init; } = 1;
        public double? SeedRatio { get; init; }
        public long? SeedTimeMinutes { get; init; }
        public long? SeasonPackSeedTimeMinutes { get; init; }

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
            Enable = i.Enable,
            DefinitionKey = i.DefinitionKey,
            AdditionalParameters = i.AdditionalParameters ?? string.Empty,
            VipExpiration = i.VipExpiration,
            QueryLimit = i.QueryLimit,
            GrabLimit = i.GrabLimit,
            LimitsUnit = i.LimitsUnit == IndexerLimitsUnit.Hour ? "hour" : "day",
            MinimumSeeders = i.MinimumSeeders,
            SeedRatio = i.SeedRatio,
            SeedTimeMinutes = i.SeedTimeMinutes,
            SeasonPackSeedTimeMinutes = i.SeasonPackSeedTimeMinutes,
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
            EnableInteractiveSearch)
        {
            Enable = Enable,
            DefinitionKey = DefinitionKey,
            AdditionalParameters = AdditionalParameters ?? string.Empty,
            VipExpiration = VipExpiration,
            QueryLimit = QueryLimit is { } q ? (int)q : null,
            GrabLimit = GrabLimit is { } g ? (int)g : null,
            LimitsUnit = string.Equals(LimitsUnit, "hour", StringComparison.OrdinalIgnoreCase) ? IndexerLimitsUnit.Hour : IndexerLimitsUnit.Day,
            MinimumSeeders = (int)MinimumSeeders,
            SeedRatio = SeedRatio,
            SeedTimeMinutes = SeedTimeMinutes is { } t ? (int)t : null,
            SeasonPackSeedTimeMinutes = SeasonPackSeedTimeMinutes is { } p ? (int)p : null,
        };
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
