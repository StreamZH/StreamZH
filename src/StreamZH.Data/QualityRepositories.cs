// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;

namespace StreamZH.Data;

/// <summary>Profile items are stored as one JSON array: [{"qualityId":7,"allowed":true}, ...].</summary>
public static class QualityProfileItemsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<QualityProfileItem> items) => JsonSerializer.Serialize(items, Options);

    public static IReadOnlyList<QualityProfileItem> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<QualityProfileItem>>(json, Options) ?? [];

    public static string SerializeFormats(IReadOnlyList<ProfileFormatItem> items) => JsonSerializer.Serialize(items, Options);

    public static IReadOnlyList<ProfileFormatItem> DeserializeFormats(string json) =>
        JsonSerializer.Deserialize<List<ProfileFormatItem>>(json, Options) ?? [];
}

public sealed class QualityProfileRepository(Database database)
{
    private const string Columns =
        "Id, Name, Items, CutoffQualityId, UpgradeAllowed, LanguageId, PreferDualLanguage, FormatItems, MinFormatScore, CutoffFormatScore, " +
        "MinUpgradeFormatScore, TrashMovieTemplate, TrashSeriesTemplate";

    public IReadOnlyList<QualityProfile> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<ProfileRow>($"SELECT {Columns} FROM QualityProfiles ORDER BY Name COLLATE NOCASE").Select(r => r.ToProfile())];
    }

    public QualityProfile? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<ProfileRow>($"SELECT {Columns} FROM QualityProfiles WHERE Id = @id", new { id })?.ToProfile();
    }

    public QualityProfile? FindByName(string name)
    {
        using var connection = database.Open();
        return connection.QueryFirstOrDefault<ProfileRow>(
            $"SELECT {Columns} FROM QualityProfiles WHERE Name = @name COLLATE NOCASE", new { name })?.ToProfile();
    }

    public bool Exists(int id)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM QualityProfiles WHERE Id = @id", new { id }) > 0;
    }

    public int Count()
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM QualityProfiles");
    }

    /// <summary>Lowest profile id, used as a fallback default.</summary>
    public int? FirstId()
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int?>("SELECT MIN(Id) FROM QualityProfiles");
    }

    public int Add(QualityProfile profile)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO QualityProfiles (Name, Items, CutoffQualityId, UpgradeAllowed, LanguageId, PreferDualLanguage, FormatItems,
                MinFormatScore, CutoffFormatScore, MinUpgradeFormatScore, TrashMovieTemplate, TrashSeriesTemplate)
            VALUES (@Name, @Items, @CutoffQualityId, @UpgradeAllowed, @LanguageId, @PreferDualLanguage, @FormatItems,
                @MinFormatScore, @CutoffFormatScore, @MinUpgradeFormatScore, @TrashMovieTemplate, @TrashSeriesTemplate);
            SELECT last_insert_rowid();
            """,
            ProfileRow.From(profile));
    }

    public void Update(QualityProfile profile)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE QualityProfiles SET Name = @Name, Items = @Items, CutoffQualityId = @CutoffQualityId, UpgradeAllowed = @UpgradeAllowed,
                LanguageId = @LanguageId, PreferDualLanguage = @PreferDualLanguage, FormatItems = @FormatItems,
                MinFormatScore = @MinFormatScore, CutoffFormatScore = @CutoffFormatScore, MinUpgradeFormatScore = @MinUpgradeFormatScore,
                TrashMovieTemplate = @TrashMovieTemplate, TrashSeriesTemplate = @TrashSeriesTemplate
            WHERE Id = @Id
            """,
            ProfileRow.From(profile));
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM QualityProfiles WHERE Id = @id", new { id });
    }

    /// <summary>Number of movies and series that use the profile.</summary>
    public int UsageCount(int id)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "SELECT (SELECT COUNT(*) FROM Movies WHERE QualityProfileId = @id) + (SELECT COUNT(*) FROM Series WHERE QualityProfileId = @id)",
            new { id });
    }

    private sealed class ProfileRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Items { get; init; } = "[]";
        public long CutoffQualityId { get; init; }
        public bool UpgradeAllowed { get; init; }
        public long LanguageId { get; init; }
        public bool PreferDualLanguage { get; init; }
        public string FormatItems { get; init; } = "[]";
        public long MinFormatScore { get; init; }
        public long CutoffFormatScore { get; init; }
        public long MinUpgradeFormatScore { get; init; } = 1;
        public string? TrashMovieTemplate { get; init; }
        public string? TrashSeriesTemplate { get; init; }

        public static ProfileRow From(QualityProfile p) => new()
        {
            Id = p.Id,
            Name = p.Name,
            Items = QualityProfileItemsJson.Serialize(p.Items),
            CutoffQualityId = p.CutoffQualityId,
            UpgradeAllowed = p.UpgradeAllowed,
            LanguageId = p.LanguageId,
            PreferDualLanguage = p.PreferDualLanguage,
            FormatItems = QualityProfileItemsJson.SerializeFormats(p.FormatItems),
            MinFormatScore = p.MinFormatScore,
            CutoffFormatScore = p.CutoffFormatScore,
            MinUpgradeFormatScore = p.MinUpgradeFormatScore,
            TrashMovieTemplate = p.TrashMovieTemplate,
            TrashSeriesTemplate = p.TrashSeriesTemplate,
        };

        public QualityProfile ToProfile() => new()
        {
            Id = (int)Id,
            Name = Name,
            Items = QualityProfileItemsJson.Deserialize(Items),
            CutoffQualityId = (int)CutoffQualityId,
            UpgradeAllowed = UpgradeAllowed,
            LanguageId = (int)LanguageId,
            PreferDualLanguage = PreferDualLanguage,
            FormatItems = QualityProfileItemsJson.DeserializeFormats(FormatItems),
            MinFormatScore = (int)MinFormatScore,
            CutoffFormatScore = (int)CutoffFormatScore,
            MinUpgradeFormatScore = (int)MinUpgradeFormatScore,
            TrashMovieTemplate = TrashMovieTemplate,
            TrashSeriesTemplate = TrashSeriesTemplate,
        };
    }
}

public sealed class QualityDefinitionRepository(Database database)
{
    private static readonly Dictionary<int, int> Order = Quality.All.Select((q, i) => (q.Id, i)).ToDictionary(x => x.Id, x => x.i);

    // The size columns hold whole numbers as INTEGER and fractions as REAL; Dapper reads every row with the types of the
    // first one, so a table with 0 and 12.5 failed. CAST gives every row the same type.
    private const string DefinitionColumns =
        "QualityId, Title, CAST(MinSize AS REAL) AS MinSize, CAST(MaxSize AS REAL) AS MaxSize, CAST(PreferredSize AS REAL) AS PreferredSize";

    /// <summary>All definitions from worst to best quality.</summary>
    public IReadOnlyList<QualityDefinition> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<DefinitionRow>($"SELECT {DefinitionColumns} FROM QualityDefinitions")
            .Select(r => r.ToDefinition())
            .OrderBy(d => Order.GetValueOrDefault(d.QualityId, int.MaxValue))];
    }

    public QualityDefinition? Find(int qualityId)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<DefinitionRow>(
            $"SELECT {DefinitionColumns} FROM QualityDefinitions WHERE QualityId = @qualityId",
            new { qualityId })?.ToDefinition();
    }

    /// <summary>Inserts or replaces the given definitions in one transaction; others stay as they are.</summary>
    public void Save(IEnumerable<QualityDefinition> definitions)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var d in definitions)
        {
            connection.Execute(
                """
                INSERT INTO QualityDefinitions (QualityId, Title, MinSize, MaxSize, PreferredSize)
                VALUES (@QualityId, @Title, @MinSize, @MaxSize, @PreferredSize)
                ON CONFLICT(QualityId) DO UPDATE SET Title = excluded.Title, MinSize = excluded.MinSize,
                    MaxSize = excluded.MaxSize, PreferredSize = excluded.PreferredSize
                """,
                new { d.QualityId, d.Title, d.MinSize, d.MaxSize, d.PreferredSize },
                transaction);
        }

        transaction.Commit();
    }

    private sealed class DefinitionRow
    {
        public long QualityId { get; init; }
        public string Title { get; init; } = string.Empty;
        public double MinSize { get; init; }
        public double? MaxSize { get; init; }
        public double? PreferredSize { get; init; }

        public QualityDefinition ToDefinition() => new((int)QualityId, Title, MinSize, MaxSize, PreferredSize);
    }
}
