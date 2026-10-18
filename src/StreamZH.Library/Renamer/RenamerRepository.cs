// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using StreamZH.Data;

namespace StreamZH.Library.Renamer;

/// <summary>The renamer's tables: runs with their items (history and Undo) and presets.</summary>
public sealed class RenamerRepository(Database database)
{
    public const int MaxRunsKept = 200;

    private const string RunColumns = "Id, Started, Finished, Folder, State, Total, Done, Failed, Error, Summary, Rules, UndoneAt";

    // ------------------------------------------------------------------ runs

    /// <summary>Stores a planned run with its items; returns the run id. Old runs beyond <see cref="MaxRunsKept"/> are removed.</summary>
    public int AddRun(string folder, string summary, IReadOnlyList<RenameRule> rules, IReadOnlyList<RenamerRunItem> items, DateTime started)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var id = connection.ExecuteScalar<int>(
            """
            INSERT INTO RenamerRuns (Started, Folder, State, Total, Done, Failed, Summary, Rules)
            VALUES (@started, @folder, @state, @total, 0, 0, @summary, @rules);
            SELECT last_insert_rowid();
            """,
            new { started, folder, state = (int)RunState.Queued, total = items.Count(i => i.Kind == RunItemKind.Rename), summary, rules = JsonSerializer.Serialize(rules, RenamerJson.Options) },
            transaction);
        InsertItems(connection, transaction, id, items);

        // Keep the newest runs (their items go with them).
        connection.Execute(
            "DELETE FROM RenamerRuns WHERE Id NOT IN (SELECT Id FROM RenamerRuns ORDER BY Id DESC LIMIT @keep)",
            new { keep = MaxRunsKept },
            transaction);
        transaction.Commit();
        return id;
    }

    /// <summary>Adds items to a run (folders the run created).</summary>
    public void AddItems(int runId, IReadOnlyList<RenamerRunItem> items)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        InsertItems(connection, transaction, runId, items);
        transaction.Commit();
    }

    private static void InsertItems(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, int runId, IReadOnlyList<RenamerRunItem> items)
    {
        foreach (var item in items)
        {
            connection.Execute(
                """
                INSERT INTO RenamerRunItems (RunId, Ord, Kind, OldPath, NewPath, IsFolder, State, Error)
                VALUES (@runId, @Order, @kind, @OldPath, @NewPath, @IsFolder, @state, @Error)
                """,
                new { runId, item.Order, kind = (int)item.Kind, item.OldPath, item.NewPath, item.IsFolder, state = (int)item.State, item.Error },
                transaction);
        }
    }

    public void SetItemState(int runId, int order, RunItemState state, string? error)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE RenamerRunItems SET State = @state, Error = @error WHERE RunId = @runId AND Ord = @order",
            new { runId, order, state = (int)state, error });
    }

    /// <summary>One more item done or failed, for the progress while the run is going.</summary>
    public void CountItem(int runId, bool success)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE RenamerRuns SET Done = Done + @done, Failed = Failed + @failed WHERE Id = @runId",
            new { runId, done = success ? 1 : 0, failed = success ? 0 : 1 });
    }

    public void UpdateRun(int id, RunState state, int done, int failed, string? error, DateTime? finished, DateTime? undoneAt = null)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE RenamerRuns SET State = @state, Done = @done, Failed = @failed, Error = @error,
                Finished = IFNULL(@finished, Finished), UndoneAt = IFNULL(@undoneAt, UndoneAt)
            WHERE Id = @id
            """,
            new { id, state = (int)state, done, failed, error, finished, undoneAt });
    }

    public IReadOnlyList<RenamerRun> Runs(int limit)
    {
        using var connection = database.Open();
        return [.. connection.Query<RunRow>($"SELECT {RunColumns} FROM RenamerRuns ORDER BY Id DESC LIMIT @limit", new { limit }).Select(r => r.ToRun())];
    }

    public RenamerRun? FindRun(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<RunRow>($"SELECT {RunColumns} FROM RenamerRuns WHERE Id = @id", new { id })?.ToRun();
    }

    public IReadOnlyList<RenamerRunItem> Items(int runId)
    {
        using var connection = database.Open();
        return [.. connection.Query<ItemRow>(
            "SELECT Ord, Kind, OldPath, NewPath, IsFolder, State, Error FROM RenamerRunItems WHERE RunId = @runId ORDER BY Ord",
            new { runId }).Select(r => r.ToItem())];
    }

    /// <summary>Runs that were running when StreamZH stopped are marked failed (their items show what happened).</summary>
    public void FailInterrupted(DateTime now)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE RenamerRuns SET State = @failed, Error = 'StreamZH stopped during the run.', Finished = @now WHERE State IN (@queued, @running, @undoing)",
            new { failed = (int)RunState.Failed, queued = (int)RunState.Queued, running = (int)RunState.Running, undoing = (int)RunState.Undoing, now });
    }

    /// <summary>A folder was renamed: the stored file details (MediaFileDetails, keyed by full path) of the files in it move too.</summary>
    public void MoveDetailsUnder(string fromFolder, string toFolder)
    {
        var from = MediaFileDetailsRepository.Key(fromFolder) + Path.DirectorySeparatorChar;
        var to = MediaFileDetailsRepository.Key(toFolder) + Path.DirectorySeparatorChar;
        using var connection = database.Open();
        connection.Execute(
            "UPDATE MediaFileDetails SET Path = @to || substr(Path, @length + 1) WHERE substr(Path, 1, @length) = @from",
            new { from, to, length = from.Length });
    }

    // ------------------------------------------------------------------ presets

    public IReadOnlyList<RenamerPreset> Presets()
    {
        using var connection = database.Open();
        return [.. connection.Query<PresetRow>("SELECT Id, Name, Content, Updated FROM RenamerPresets ORDER BY Name COLLATE NOCASE").Select(r => r.ToPreset())];
    }

    public RenamerPreset? FindPreset(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<PresetRow>("SELECT Id, Name, Content, Updated FROM RenamerPresets WHERE Id = @id", new { id })?.ToPreset();
    }

    public RenamerPreset? FindPreset(string name)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<PresetRow>(
            "SELECT Id, Name, Content, Updated FROM RenamerPresets WHERE Name = @name COLLATE NOCASE", new { name })?.ToPreset();
    }

    public int AddPreset(string name, RenamerPresetContent content, DateTime updated)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO RenamerPresets (Name, Content, Updated) VALUES (@name, @content, @updated); SELECT last_insert_rowid();",
            new { name, content = JsonSerializer.Serialize(content, RenamerJson.Options), updated });
    }

    public bool UpdatePreset(int id, string name, RenamerPresetContent content, DateTime updated)
    {
        using var connection = database.Open();
        return connection.Execute(
            "UPDATE RenamerPresets SET Name = @name, Content = @content, Updated = @updated WHERE Id = @id",
            new { id, name, content = JsonSerializer.Serialize(content, RenamerJson.Options), updated }) > 0;
    }

    public bool DeletePreset(int id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM RenamerPresets WHERE Id = @id", new { id }) > 0;
    }

    private sealed class RunRow
    {
        public long Id { get; init; }
        public DateTime Started { get; init; }
        public DateTime? Finished { get; init; }
        public string Folder { get; init; } = string.Empty;
        public long State { get; init; }
        public long Total { get; init; }
        public long Done { get; init; }
        public long Failed { get; init; }
        public string? Error { get; init; }
        public string Summary { get; init; } = string.Empty;
        public string Rules { get; init; } = "[]";
        public DateTime? UndoneAt { get; init; }

        public RenamerRun ToRun() => new(
            (int)Id,
            Utc(Started),
            Finished is { } f ? Utc(f) : null,
            Folder,
            (RunState)State,
            (int)Total,
            (int)Done,
            (int)Failed,
            Error,
            Summary,
            UndoneAt is { } u ? Utc(u) : null,
            ReadRules(Rules));
    }

    private sealed class ItemRow
    {
        public long Ord { get; init; }
        public long Kind { get; init; }
        public string OldPath { get; init; } = string.Empty;
        public string NewPath { get; init; } = string.Empty;
        public bool IsFolder { get; init; }
        public long State { get; init; }
        public string? Error { get; init; }

        public RenamerRunItem ToItem() => new((int)Ord, (RunItemKind)Kind, OldPath, NewPath, IsFolder, (RunItemState)State, Error);
    }

    private sealed class PresetRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Content { get; init; } = "{}";
        public DateTime Updated { get; init; }

        public RenamerPreset ToPreset() => new((int)Id, Name, ReadContent(Content), Utc(Updated));
    }

    private static IReadOnlyList<RenameRule> ReadRules(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<RenameRule>>(json, RenamerJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static RenamerPresetContent ReadContent(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RenamerPresetContent>(json, RenamerJson.Options) ?? new RenamerPresetContent();
        }
        catch (JsonException)
        {
            return new RenamerPresetContent();
        }
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
