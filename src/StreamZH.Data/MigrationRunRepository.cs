// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;

namespace StreamZH.Data;

/// <summary>A stored migration run; App and Report are JSON written by the migration service.</summary>
public sealed record MigrationRunRow(
    int Id, string Source, string Status, bool DryRun, DateTime Started, DateTime? Finished, string? Error, string? App, string? Report, string? UserName);

/// <summary>Table MigrationRuns (0.9): one row per preview-run or real run of the migration wizard.</summary>
public sealed class MigrationRunRepository(Database database)
{
    private const string Columns = "Id, Source, Status, DryRun, Started, Finished, Error, App, Report, UserName";

    public IReadOnlyList<MigrationRunRow> All(int limit = 50)
    {
        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM MigrationRuns ORDER BY Id DESC LIMIT @limit", new { limit }).Select(r => r.ToRow())];
    }

    public MigrationRunRow? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM MigrationRuns WHERE Id = @id", new { id })?.ToRow();
    }

    public int Add(string source, string status, bool dryRun, DateTime started, string? userName)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO MigrationRuns (Source, Status, DryRun, Started, UserName) VALUES (@source, @status, @dryRun, @started, @userName); SELECT last_insert_rowid();",
            new { source, status, dryRun, started, userName });
    }

    public void Update(int id, string status, DateTime? finished, string? error, string? app, string? report)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE MigrationRuns SET Status = @status, Finished = @finished, Error = @error, App = @app, Report = @report WHERE Id = @id",
            new { id, status, finished, error, app, report });
    }

    private sealed class Row
    {
        public int Id { get; init; }

        public string Source { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public bool DryRun { get; init; }

        public DateTime Started { get; init; }

        public DateTime? Finished { get; init; }

        public string? Error { get; init; }

        public string? App { get; init; }

        public string? Report { get; init; }

        public string? UserName { get; init; }

        public MigrationRunRow ToRow() => new(
            Id, Source, Status, DryRun, DateTime.SpecifyKind(Started, DateTimeKind.Utc),
            Finished is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null, Error, App, Report, UserName);
    }
}
