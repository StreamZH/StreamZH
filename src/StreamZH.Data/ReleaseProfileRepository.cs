// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using StreamZH.Core.Decisions;

namespace StreamZH.Data;

/// <summary>Release profiles (Settings → Indexers). Term and id lists are stored as JSON.</summary>
public sealed class ReleaseProfileRepository(Database database)
{
    private const string Columns = "Id, Name, Enabled, Required, Ignored, Preferred, IndexerIds, QualityProfileIds";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<ReleaseProfile> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM ReleaseProfiles ORDER BY Name COLLATE NOCASE, Id").Select(r => r.ToProfile())];
    }

    public ReleaseProfile? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM ReleaseProfiles WHERE Id = @id", new { id })?.ToProfile();
    }

    public int Add(ReleaseProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO ReleaseProfiles (Name, Enabled, Required, Ignored, Preferred, IndexerIds, QualityProfileIds)
            VALUES (@Name, @Enabled, @Required, @Ignored, @Preferred, @IndexerIds, @QualityProfileIds);
            SELECT last_insert_rowid();
            """,
            Row.From(profile));
    }

    /// <summary>False when the profile does not exist.</summary>
    public bool Update(ReleaseProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var connection = database.Open();
        return connection.Execute(
            """
            UPDATE ReleaseProfiles SET Name = @Name, Enabled = @Enabled, Required = @Required, Ignored = @Ignored, Preferred = @Preferred,
                IndexerIds = @IndexerIds, QualityProfileIds = @QualityProfileIds
            WHERE Id = @Id
            """,
            Row.From(profile)) > 0;
    }

    public bool Delete(int id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM ReleaseProfiles WHERE Id = @id", new { id }) > 0;
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool Enabled { get; init; }
        public string Required { get; init; } = "[]";
        public string Ignored { get; init; } = "[]";
        public string Preferred { get; init; } = "[]";
        public string IndexerIds { get; init; } = "[]";
        public string QualityProfileIds { get; init; } = "[]";

        public static Row From(ReleaseProfile p) => new()
        {
            Id = p.Id,
            Name = p.Name,
            Enabled = p.Enabled,
            Required = JsonSerializer.Serialize(p.Required, Json),
            Ignored = JsonSerializer.Serialize(p.Ignored, Json),
            Preferred = JsonSerializer.Serialize(p.Preferred, Json),
            IndexerIds = JsonSerializer.Serialize(p.IndexerIds, Json),
            QualityProfileIds = JsonSerializer.Serialize(p.QualityProfileIds, Json),
        };

        public ReleaseProfile ToProfile() => new()
        {
            Id = (int)Id,
            Name = Name,
            Enabled = Enabled,
            Required = Read<List<string>>(Required) ?? [],
            Ignored = Read<List<string>>(Ignored) ?? [],
            Preferred = Read<List<PreferredTerm>>(Preferred) ?? [],
            IndexerIds = Read<List<int>>(IndexerIds) ?? [],
            QualityProfileIds = Read<List<int>>(QualityProfileIds) ?? [],
        };

        private static T? Read<T>(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, ReleaseProfileRepository.Json);
            }
            catch (JsonException)
            {
                return default;
            }
        }
    }
}
