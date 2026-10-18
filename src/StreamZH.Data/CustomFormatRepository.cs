// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;

namespace StreamZH.Data;

/// <summary>Custom formats (table CustomFormats); the conditions are stored as JSON in the Radarr/Sonarr shape.</summary>
public sealed class CustomFormatRepository(Database database)
{
    private const string Columns = "Id, Name, MediaType, IncludeInRename, Specifications, TrashId";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<CustomFormat> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM CustomFormats ORDER BY MediaType, Name COLLATE NOCASE").Select(r => r.ToFormat())];
    }

    public IReadOnlyList<CustomFormat> ForMediaType(MediaType mediaType)
    {
        using var connection = database.Open();
        return
        [
            .. connection.Query<Row>($"SELECT {Columns} FROM CustomFormats WHERE MediaType = @type ORDER BY Name COLLATE NOCASE", new { type = (int)mediaType })
                .Select(r => r.ToFormat()),
        ];
    }

    public CustomFormat? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM CustomFormats WHERE Id = @id", new { id })?.ToFormat();
    }

    public CustomFormat? FindByName(string name, MediaType mediaType)
    {
        using var connection = database.Open();
        return connection.QueryFirstOrDefault<Row>(
            $"SELECT {Columns} FROM CustomFormats WHERE Name = @name COLLATE NOCASE AND MediaType = @type", new { name, type = (int)mediaType })?.ToFormat();
    }

    public CustomFormat? FindByTrashId(string trashId, MediaType mediaType)
    {
        using var connection = database.Open();
        return connection.QueryFirstOrDefault<Row>(
            $"SELECT {Columns} FROM CustomFormats WHERE TrashId = @trashId AND MediaType = @type", new { trashId, type = (int)mediaType })?.ToFormat();
    }

    public int Add(CustomFormat format)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO CustomFormats (Name, MediaType, IncludeInRename, Specifications, TrashId)
            VALUES (@Name, @MediaType, @IncludeInRename, @Specifications, @TrashId);
            SELECT last_insert_rowid();
            """,
            Row.From(format));
    }

    public void Update(CustomFormat format)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE CustomFormats SET Name = @Name, MediaType = @MediaType, IncludeInRename = @IncludeInRename,
                Specifications = @Specifications, TrashId = @TrashId
            WHERE Id = @Id
            """,
            Row.From(format));
    }

    /// <summary>Deletes the format; profiles drop its score when they are read next (see QualityProfileRepository).</summary>
    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM CustomFormats WHERE Id = @id", new { id });
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public long MediaType { get; init; }
        public bool IncludeInRename { get; init; }
        public string Specifications { get; init; } = "[]";
        public string? TrashId { get; init; }

        public static Row From(CustomFormat f) => new()
        {
            Id = f.Id,
            Name = f.Name,
            MediaType = (int)f.MediaType,
            IncludeInRename = f.IncludeInRename,
            Specifications = JsonSerializer.Serialize(f.Specifications, Json),
            TrashId = f.TrashId,
        };

        public CustomFormat ToFormat() => new()
        {
            Id = (int)Id,
            Name = Name,
            MediaType = (MediaType)MediaType,
            IncludeInRename = IncludeInRename,
            Specifications = JsonSerializer.Deserialize<List<CustomFormatSpecification>>(Specifications, Json) ?? [],
            TrashId = TrashId,
        };
    }
}
