// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;

namespace StreamZH.Data;

/// <summary>A download category as stored. <see cref="Priority"/> null = default; -2 = paused. <see cref="PostProcessing"/> 0 none … 3 full.</summary>
public sealed record DownloadCategoryRecord(int Id, string Name, string Folder, int? Priority, int PostProcessing);

/// <summary>A scheduler rule as stored. <see cref="Minute"/> after local midnight; <see cref="Days"/> bit 0 = Monday … bit 6 = Sunday.</summary>
public sealed record DownloadScheduleRecord(int Id, bool Enabled, int Minute, int Days, string Action, string Argument);

public sealed class DownloadCategoryRepository(Database database)
{
    private const string Columns = "Id, Name, Folder, Priority, PostProcessing";

    /// <summary>All categories by id (the three defaults first).</summary>
    public IReadOnlyList<DownloadCategoryRecord> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM DownloadCategories ORDER BY Id").Select(r => r.ToRecord())];
    }

    public DownloadCategoryRecord? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM DownloadCategories WHERE Id = @id", new { id })?.ToRecord();
    }

    public int Add(DownloadCategoryRecord category)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO DownloadCategories (Name, Folder, Priority, PostProcessing) VALUES (@Name, @Folder, @Priority, @PostProcessing); SELECT last_insert_rowid();",
            category);
    }

    public void Update(DownloadCategoryRecord category)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE DownloadCategories SET Name = @Name, Folder = @Folder, Priority = @Priority, PostProcessing = @PostProcessing WHERE Id = @Id", category);
    }

    public bool Delete(int id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM DownloadCategories WHERE Id = @id", new { id }) > 0;
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Folder { get; init; } = string.Empty;
        public long? Priority { get; init; }
        public long PostProcessing { get; init; }

        public DownloadCategoryRecord ToRecord() => new((int)Id, Name, Folder, Priority is { } p ? (int)p : null, (int)PostProcessing);
    }
}

public sealed class DownloadScheduleRepository(Database database)
{
    private const string Columns = "Id, Enabled, Minute, Days, Action, Argument";

    /// <summary>All rules by time of day, then id.</summary>
    public IReadOnlyList<DownloadScheduleRecord> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM DownloadSchedules ORDER BY Minute, Id").Select(r => r.ToRecord())];
    }

    public DownloadScheduleRecord? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM DownloadSchedules WHERE Id = @id", new { id })?.ToRecord();
    }

    public int Add(DownloadScheduleRecord rule)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO DownloadSchedules (Enabled, Minute, Days, Action, Argument) VALUES (@Enabled, @Minute, @Days, @Action, @Argument); SELECT last_insert_rowid();",
            rule);
    }

    public void Update(DownloadScheduleRecord rule)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE DownloadSchedules SET Enabled = @Enabled, Minute = @Minute, Days = @Days, Action = @Action, Argument = @Argument WHERE Id = @Id", rule);
    }

    public bool Delete(int id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM DownloadSchedules WHERE Id = @id", new { id }) > 0;
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public bool Enabled { get; init; }
        public long Minute { get; init; }
        public long Days { get; init; }
        public string Action { get; init; } = string.Empty;
        public string Argument { get; init; } = string.Empty;

        public DownloadScheduleRecord ToRecord() => new((int)Id, Enabled, (int)Minute, (int)Days, Action, Argument);
    }
}
