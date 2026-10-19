// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using StreamZH.Core.Downloads;

namespace StreamZH.Data;

/// <summary>Table DownloadClients (0.11): torrent clients (external or built in).</summary>
public sealed class DownloadClientRepository(Database database)
{
    private const string Columns = "Id, Name, Implementation, Protocol, Enable, Priority, RemoveCompleted, Settings";

    public IReadOnlyList<DownloadClientDefinition> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM DownloadClients ORDER BY Priority, Name COLLATE NOCASE").Select(Read)];
    }

    public DownloadClientDefinition? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM DownloadClients WHERE Id = @id", new { id }) is { } row ? Read(row) : null;
    }

    public DownloadClientDefinition? FindByName(string name)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM DownloadClients WHERE Name = @name COLLATE NOCASE", new { name }) is { } row ? Read(row) : null;
    }

    public const string SecretKind = "Download client";

    // 0.12: passwords in the settings are stored encrypted; an unreadable one reads as not set.
    private DownloadClientDefinition Read(Row row)
    {
        var client = row.ToDefinition();
        return client with { Settings = database.Secrets.DecryptSettings(client.Settings, SecretKind, client.Name) };
    }

    public int Add(DownloadClientDefinition client)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO DownloadClients (Name, Implementation, Protocol, Enable, Priority, RemoveCompleted, Settings, Added)
            VALUES (@Name, @Implementation, @Protocol, @Enable, @Priority, @RemoveCompleted, @Settings, @added);
            SELECT last_insert_rowid();
            """,
            Parameters(client, DateTime.UtcNow));
    }

    public void Update(DownloadClientDefinition client)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE DownloadClients SET Name = @Name, Implementation = @Implementation, Protocol = @Protocol, Enable = @Enable,
                Priority = @Priority, RemoveCompleted = @RemoveCompleted, Settings = @Settings
            WHERE Id = @Id
            """,
            Parameters(client, DateTime.UtcNow));
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM DownloadClients WHERE Id = @id", new { id });
    }

    private object Parameters(DownloadClientDefinition c, DateTime added)
    {
        database.Secrets.Forget(SecretKind, c.Name);
        return new
        {
            c.Id,
            c.Name,
            c.Implementation,
            Protocol = (int)c.Protocol,
            c.Enable,
            c.Priority,
            c.RemoveCompleted,
            Settings = JsonSerializer.Serialize(database.Secrets.EncryptSettings(c.Settings)),
            added,
        };
    }

    private sealed class Row
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Implementation { get; init; } = string.Empty;

        public long Protocol { get; init; }

        public bool Enable { get; init; }

        public long Priority { get; init; }

        public bool RemoveCompleted { get; init; }

        public string Settings { get; init; } = "{}";

        public DownloadClientDefinition ToDefinition() => new(
            (int)Id, Name, Implementation, (DownloadProtocol)Protocol, Enable, (int)Priority,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Settings) ?? [])
        {
            RemoveCompleted = RemoveCompleted,
        };
    }
}

/// <summary>A path as a download client on another host sees it, and the same folder for StreamZH.</summary>
public sealed record RemotePathMapping(int Id, string Host, string RemotePath, string LocalPath);

/// <summary>Table RemotePathMappings (0.11), like Radarr's Settings → Download Clients → Remote Path Mappings.</summary>
public sealed class RemotePathMappingRepository(Database database)
{
    public IReadOnlyList<RemotePathMapping> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<(long Id, string Host, string RemotePath, string LocalPath)>(
            "SELECT Id, Host, RemotePath, LocalPath FROM RemotePathMappings ORDER BY Host, RemotePath").Select(r => new RemotePathMapping((int)r.Id, r.Host, r.RemotePath, r.LocalPath))];
    }

    public int Add(string host, string remotePath, string localPath)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO RemotePathMappings (Host, RemotePath, LocalPath) VALUES (@host, @remotePath, @localPath); SELECT last_insert_rowid();",
            new { host, remotePath, localPath });
    }

    public void Update(RemotePathMapping mapping)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE RemotePathMappings SET Host = @Host, RemotePath = @RemotePath, LocalPath = @LocalPath WHERE Id = @Id", mapping);
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM RemotePathMappings WHERE Id = @id", new { id });
    }
}
