// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;
using StreamZH.Core.Downloads;

namespace StreamZH.Data;

/// <summary>The UsenetServers table. Passwords are stored as entered (same policy as indexer API keys).</summary>
public sealed class UsenetServerRepository(Database database)
{
    private const string Columns = "Id, Name, Host, Port, UseSsl, Username, Password, Connections, Priority, Enabled, RetentionDays";

    /// <summary>All servers ordered by priority (primary first), then name.</summary>
    public IReadOnlyList<UsenetServer> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM UsenetServers ORDER BY Priority, Name COLLATE NOCASE").Select(r => r.ToServer())];
    }

    public UsenetServer? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM UsenetServers WHERE Id = @id", new { id })?.ToServer();
    }

    /// <summary>Case-insensitive lookup by name.</summary>
    public UsenetServer? FindByName(string name)
    {
        using var connection = database.Open();
        return connection.QueryFirstOrDefault<Row>($"SELECT {Columns} FROM UsenetServers WHERE Name = @name COLLATE NOCASE", new { name })?.ToServer();
    }

    public int Add(UsenetServer server, DateTime added)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO UsenetServers (Name, Host, Port, UseSsl, Username, Password, Connections, Priority, Enabled, RetentionDays, Added)
            VALUES (@Name, @Host, @Port, @UseSsl, @Username, @Password, @Connections, @Priority, @Enabled, @RetentionDays, @added);
            SELECT last_insert_rowid();
            """,
            new
            {
                server.Name,
                server.Host,
                server.Port,
                server.UseSsl,
                server.Username,
                server.Password,
                server.Connections,
                server.Priority,
                server.Enabled,
                server.RetentionDays,
                added,
            });
    }

    public void Update(UsenetServer server)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE UsenetServers SET Name = @Name, Host = @Host, Port = @Port, UseSsl = @UseSsl, Username = @Username, Password = @Password,
                Connections = @Connections, Priority = @Priority, Enabled = @Enabled, RetentionDays = @RetentionDays
            WHERE Id = @Id
            """,
            new
            {
                server.Id,
                server.Name,
                server.Host,
                server.Port,
                server.UseSsl,
                server.Username,
                server.Password,
                server.Connections,
                server.Priority,
                server.Enabled,
                server.RetentionDays,
            });
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM UsenetServers WHERE Id = @id", new { id });
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Host { get; init; } = string.Empty;
        public long Port { get; init; }
        public bool UseSsl { get; init; }
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public long Connections { get; init; }
        public long Priority { get; init; }
        public bool Enabled { get; init; }
        public long RetentionDays { get; init; }

        public UsenetServer ToServer() => new(
            (int)Id, Name, Host, (int)Port, UseSsl, Username, Password, (int)Connections, (int)Priority, Enabled, (int)RetentionDays);
    }
}
