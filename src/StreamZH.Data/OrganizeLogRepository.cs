// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using StreamZH.Core.Media;

namespace StreamZH.Data;

/// <summary>One renamed file of an organize run, relative to the old and to the new item folder.</summary>
/// <param name="IsVideo">The library's video file (its record and stored details follow it).</param>
public sealed record OrganizeFileLog(string OldPath, string NewPath, bool IsVideo);

/// <summary>One movie or series an organize run renamed: its folder before and after and the renamed files.</summary>
public sealed record OrganizeItemLog(int ItemId, string Title, string OldFolder, string NewFolder, IReadOnlyList<OrganizeFileLog> Files);

/// <summary>One organize run (several movies or series of one media type).</summary>
public sealed record OrganizeRun(int Id, MediaType MediaType, DateTime Started, DateTime? Finished, int Renamed, int Failed, DateTime? UndoneAt);

/// <summary>The rename log of "Organize", for "Undo last organize".</summary>
public sealed class OrganizeLogRepository(Database database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public int StartRun(MediaType mediaType, DateTime started)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO OrganizeRuns (MediaType, Started) VALUES (@mediaType, @started); SELECT last_insert_rowid();",
            new { mediaType = (int)mediaType, started });
    }

    public void AddItem(int runId, OrganizeItemLog item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var connection = database.Open();
        connection.Execute(
            "INSERT INTO OrganizeRunItems (RunId, ItemId, Title, OldFolder, NewFolder, Files) VALUES (@runId, @ItemId, @Title, @OldFolder, @NewFolder, @files)",
            new { runId, item.ItemId, item.Title, item.OldFolder, item.NewFolder, files = JsonSerializer.Serialize(item.Files, Json) });
    }

    public void FinishRun(int runId, DateTime finished, int renamed, int failed)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE OrganizeRuns SET Finished = @finished, Renamed = @renamed, Failed = @failed WHERE Id = @runId",
            new { runId, finished, renamed, failed });
    }

    public void MarkUndone(int runId, DateTime at)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE OrganizeRuns SET UndoneAt = @at WHERE Id = @runId", new { runId, at });
    }

    /// <summary>The newest finished run that renamed something (undone or not).</summary>
    public OrganizeRun? Last()
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<RunRow>(
            "SELECT Id, MediaType, Started, Finished, Renamed, Failed, UndoneAt FROM OrganizeRuns WHERE Finished IS NOT NULL AND Renamed > 0 ORDER BY Id DESC LIMIT 1")
            ?.ToRun();
    }

    public OrganizeRun? Find(int runId)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<RunRow>(
            "SELECT Id, MediaType, Started, Finished, Renamed, Failed, UndoneAt FROM OrganizeRuns WHERE Id = @runId", new { runId })?.ToRun();
    }

    public IReadOnlyList<OrganizeItemLog> Items(int runId)
    {
        using var connection = database.Open();
        return [.. connection.Query<ItemRow>(
            "SELECT ItemId, Title, OldFolder, NewFolder, Files FROM OrganizeRunItems WHERE RunId = @runId ORDER BY Id", new { runId })
            .Select(r => new OrganizeItemLog(
                (int)r.ItemId, r.Title, r.OldFolder, r.NewFolder, JsonSerializer.Deserialize<List<OrganizeFileLog>>(r.Files, Json) ?? []))];
    }

    /// <summary>Removes runs started before <paramref name="cutoff"/> (with their items); returns how many.</summary>
    public int DeleteOlderThan(DateTime cutoff)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM OrganizeRunItems WHERE RunId IN (SELECT Id FROM OrganizeRuns WHERE Started < @cutoff)", new { cutoff });
        return connection.Execute("DELETE FROM OrganizeRuns WHERE Started < @cutoff", new { cutoff });
    }

    private sealed class RunRow
    {
        public long Id { get; init; }
        public long MediaType { get; init; }
        public DateTime Started { get; init; }
        public DateTime? Finished { get; init; }
        public long Renamed { get; init; }
        public long Failed { get; init; }
        public DateTime? UndoneAt { get; init; }

        public OrganizeRun ToRun() => new(
            (int)Id, (MediaType)MediaType, Utc(Started), Finished is { } f ? Utc(f) : null, (int)Renamed, (int)Failed, UndoneAt is { } u ? Utc(u) : null);

        private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private sealed class ItemRow
    {
        public long ItemId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string OldFolder { get; init; } = string.Empty;
        public string NewFolder { get; init; } = string.Empty;
        public string Files { get; init; } = "[]";
    }
}
