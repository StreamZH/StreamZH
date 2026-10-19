// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;

namespace StreamZH.Data;

/// <summary>When a connection is used (Settings → Connect, like Radarr's notification triggers).</summary>
/// <param name="OnImport">A file was imported that is not an upgrade ("On Import" / "On Download").</param>
/// <param name="OnUpgrade">A file was imported that replaced an existing one.</param>
/// <param name="OnHealthIssue">A notice appeared in the status window.</param>
/// <param name="OnHealthRestored">A notice disappeared again.</param>
/// <param name="IncludeHealthWarnings">Health triggers also fire for warnings, not only for errors.</param>
/// <param name="OnRequestCreated">A movie or series was requested (0.10).</param>
/// <param name="OnRequestApproved">An admin approved a request (0.10).</param>
/// <param name="OnRequestDeclined">An admin declined a request (0.10).</param>
/// <param name="OnRequestAvailable">Everything a request asked for has files (0.10).</param>
public sealed record ConnectionTriggers(
    bool OnGrab = false,
    bool OnImport = false,
    bool OnUpgrade = false,
    bool OnRename = false,
    bool OnDownloadFailure = false,
    bool OnHealthIssue = false,
    bool OnHealthRestored = false,
    bool IncludeHealthWarnings = false,
    bool OnRequestCreated = false,
    bool OnRequestApproved = false,
    bool OnRequestDeclined = false,
    bool OnRequestAvailable = false);

/// <summary>Result of the last delivery; times are UTC. Failing while <see cref="LastFailure"/> is newer than <see cref="LastSuccess"/>.</summary>
public sealed record ConnectionStatus(DateTime? LastSuccess, DateTime? LastFailure, string? LastError)
{
    public static ConnectionStatus None { get; } = new(null, null, null);

    public bool Failing => LastFailure is { } failure && (LastSuccess is not { } success || failure > success);
}

/// <summary>One configured media server or notification service.</summary>
/// <param name="Implementation">Catalog key, e.g. "plex", "discord" or "webhook".</param>
/// <param name="Settings">Type-specific settings as stored (JSON values; secrets in plain text).</param>
public sealed record ConnectionDefinition(
    int Id,
    string Name,
    string Implementation,
    bool Enable,
    ConnectionTriggers Triggers,
    IReadOnlyDictionary<string, JsonElement> Settings)
{
    public ConnectionStatus Status { get; init; } = ConnectionStatus.None;
}

public sealed class ConnectionRepository(Database database)
{
    private const string Columns = """
        Id, Name, Implementation, Enable, OnGrab, OnImport, OnUpgrade, OnRename, OnDownloadFailure, OnHealthIssue, OnHealthRestored,
        IncludeHealthWarnings, OnRequestCreated, OnRequestApproved, OnRequestDeclined, OnRequestAvailable, Settings, LastSuccess, LastFailure, LastError
        """;

    public IReadOnlyList<ConnectionDefinition> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<ConnectionRow>($"SELECT {Columns} FROM Connections ORDER BY Name COLLATE NOCASE").Select(r => r.ToDefinition())];
    }

    public ConnectionDefinition? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<ConnectionRow>($"SELECT {Columns} FROM Connections WHERE Id = @id", new { id })?.ToDefinition();
    }

    public ConnectionDefinition? FindByName(string name)
    {
        using var connection = database.Open();
        return connection.QueryFirstOrDefault<ConnectionRow>($"SELECT {Columns} FROM Connections WHERE Name = @name COLLATE NOCASE", new { name })?.ToDefinition();
    }

    public int Add(ConnectionDefinition definition, DateTime added)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO Connections (Name, Implementation, Enable, OnGrab, OnImport, OnUpgrade, OnRename, OnDownloadFailure,
                OnHealthIssue, OnHealthRestored, IncludeHealthWarnings, OnRequestCreated, OnRequestApproved, OnRequestDeclined, OnRequestAvailable,
                Settings, Added)
            VALUES (@Name, @Implementation, @Enable, @OnGrab, @OnImport, @OnUpgrade, @OnRename, @OnDownloadFailure,
                @OnHealthIssue, @OnHealthRestored, @IncludeHealthWarnings, @OnRequestCreated, @OnRequestApproved, @OnRequestDeclined, @OnRequestAvailable,
                @Settings, @Added);
            SELECT last_insert_rowid();
            """,
            Parameters(definition, added));
    }

    /// <summary>Saves the definition and clears the delivery status (the settings may have fixed the problem).</summary>
    public void Update(ConnectionDefinition definition)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Connections SET Name = @Name, Implementation = @Implementation, Enable = @Enable, OnGrab = @OnGrab, OnImport = @OnImport,
                OnUpgrade = @OnUpgrade, OnRename = @OnRename, OnDownloadFailure = @OnDownloadFailure, OnHealthIssue = @OnHealthIssue,
                OnHealthRestored = @OnHealthRestored, IncludeHealthWarnings = @IncludeHealthWarnings, OnRequestCreated = @OnRequestCreated,
                OnRequestApproved = @OnRequestApproved, OnRequestDeclined = @OnRequestDeclined, OnRequestAvailable = @OnRequestAvailable, Settings = @Settings,
                LastSuccess = NULL, LastFailure = NULL, LastError = NULL
            WHERE Id = @Id
            """,
            Parameters(definition, default));
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM Connections WHERE Id = @id", new { id });
    }

    public void SetSuccess(int id, DateTime at)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Connections SET LastSuccess = @at WHERE Id = @id", new { id, at });
    }

    public void SetFailure(int id, DateTime at, string error)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Connections SET LastFailure = @at, LastError = @error WHERE Id = @id", new { id, at, error = Core.Security.SecretUrls.Redact(error) });
    }

    public static string SerializeSettings(IReadOnlyDictionary<string, JsonElement> settings) => JsonSerializer.Serialize(settings);

    public static IReadOnlyDictionary<string, JsonElement> DeserializeSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonElement>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    private static object Parameters(ConnectionDefinition d, DateTime added) => new
    {
        d.Id,
        d.Name,
        d.Implementation,
        d.Enable,
        d.Triggers.OnGrab,
        d.Triggers.OnImport,
        d.Triggers.OnUpgrade,
        d.Triggers.OnRename,
        d.Triggers.OnDownloadFailure,
        d.Triggers.OnHealthIssue,
        d.Triggers.OnHealthRestored,
        d.Triggers.IncludeHealthWarnings,
        d.Triggers.OnRequestCreated,
        d.Triggers.OnRequestApproved,
        d.Triggers.OnRequestDeclined,
        d.Triggers.OnRequestAvailable,
        Settings = SerializeSettings(d.Settings),
        Added = added,
    };

    private sealed class ConnectionRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Implementation { get; init; } = string.Empty;
        public bool Enable { get; init; }
        public bool OnGrab { get; init; }
        public bool OnImport { get; init; }
        public bool OnUpgrade { get; init; }
        public bool OnRename { get; init; }
        public bool OnDownloadFailure { get; init; }
        public bool OnHealthIssue { get; init; }
        public bool OnHealthRestored { get; init; }
        public bool IncludeHealthWarnings { get; init; }
        public bool OnRequestCreated { get; init; }
        public bool OnRequestApproved { get; init; }
        public bool OnRequestDeclined { get; init; }
        public bool OnRequestAvailable { get; init; }
        public string Settings { get; init; } = "{}";
        public DateTime? LastSuccess { get; init; }
        public DateTime? LastFailure { get; init; }
        public string? LastError { get; init; }

        public ConnectionDefinition ToDefinition() => new(
            (int)Id,
            Name,
            Implementation,
            Enable,
            new ConnectionTriggers(
                OnGrab, OnImport, OnUpgrade, OnRename, OnDownloadFailure, OnHealthIssue, OnHealthRestored, IncludeHealthWarnings,
                OnRequestCreated, OnRequestApproved, OnRequestDeclined, OnRequestAvailable),
            DeserializeSettings(Settings))
        {
            Status = new ConnectionStatus(Utc(LastSuccess), Utc(LastFailure), LastError),
        };

        private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
    }
}
