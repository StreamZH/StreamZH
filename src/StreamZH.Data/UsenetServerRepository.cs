// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;
using StreamZH.Core.Downloads;

namespace StreamZH.Data;

/// <summary>The UsenetServers table. Passwords are stored as entered (same policy as indexer API keys).</summary>
public sealed class UsenetServerRepository(Database database)
{
    private const string Columns =
        "Id, Name, Host, Port, UseSsl, Username, Password, Connections, Priority, Enabled, RetentionDays, "
        + "TimeoutSeconds, CertificateCheck, ArticlesPerRequest, OnFailure, ExpiresOn, QuotaBytes, QuotaUsedBytes, QuotaSince, Notes";

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

    /// <summary>Adds the server; its quota counter (if a quota is set) starts at zero from <paramref name="added"/>.</summary>
    public int Add(UsenetServer server, DateTime added)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO UsenetServers (Name, Host, Port, UseSsl, Username, Password, Connections, Priority, Enabled, RetentionDays, Added,
                TimeoutSeconds, CertificateCheck, ArticlesPerRequest, OnFailure, ExpiresOn, QuotaBytes, QuotaUsedBytes, QuotaSince, Notes)
            VALUES (@Name, @Host, @Port, @UseSsl, @Username, @Password, @Connections, @Priority, @Enabled, @RetentionDays, @added,
                @TimeoutSeconds, @CertificateCheck, @ArticlesPerRequest, @OnFailure, @ExpiresOn, @QuotaBytes, 0, @QuotaSince, @Notes);
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
                server.TimeoutSeconds,
                CertificateCheck = (int)server.CertificateCheck,
                server.ArticlesPerRequest,
                OnFailure = (int)server.OnFailure,
                server.ExpiresOn,
                server.QuotaBytes,
                QuotaSince = server.QuotaBytes is null ? (DateTime?)null : added,
                server.Notes,
            });
    }

    /// <summary>Saves the settings. The quota counter (<see cref="UsenetServer.QuotaUsedBytes"/>, <see cref="UsenetServer.QuotaSince"/>) is not touched.</summary>
    public void Update(UsenetServer server)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE UsenetServers SET Name = @Name, Host = @Host, Port = @Port, UseSsl = @UseSsl, Username = @Username, Password = @Password,
                Connections = @Connections, Priority = @Priority, Enabled = @Enabled, RetentionDays = @RetentionDays,
                TimeoutSeconds = @TimeoutSeconds, CertificateCheck = @CertificateCheck, ArticlesPerRequest = @ArticlesPerRequest,
                OnFailure = @OnFailure, ExpiresOn = @ExpiresOn, QuotaBytes = @QuotaBytes, Notes = @Notes
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
                server.TimeoutSeconds,
                CertificateCheck = (int)server.CertificateCheck,
                server.ArticlesPerRequest,
                OnFailure = (int)server.OnFailure,
                server.ExpiresOn,
                server.QuotaBytes,
                server.Notes,
            });
    }

    /// <summary>Sets the quota counter to zero, counting from <paramref name="since"/> (null: no quota, no counter).</summary>
    public void ResetQuota(int id, DateTime? since)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE UsenetServers SET QuotaUsedBytes = 0, QuotaSince = @since WHERE Id = @id", new { id, since });
    }

    /// <summary>Adds downloaded bytes to the quota counters (server id → bytes) of servers that have a quota.</summary>
    public void AddQuotaUsage(IReadOnlyDictionary<int, long> bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Count == 0)
        {
            return;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (id, added) in bytes)
        {
            connection.Execute(
                "UPDATE UsenetServers SET QuotaUsedBytes = QuotaUsedBytes + @added WHERE Id = @id AND QuotaBytes IS NOT NULL",
                new { id, added },
                transaction);
        }

        transaction.Commit();
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
        public long TimeoutSeconds { get; init; }
        public long CertificateCheck { get; init; }
        public long ArticlesPerRequest { get; init; }
        public long OnFailure { get; init; }
        public string? ExpiresOn { get; init; }
        public long? QuotaBytes { get; init; }
        public long QuotaUsedBytes { get; init; }
        public DateTime? QuotaSince { get; init; }
        public string? Notes { get; init; }

        public UsenetServer ToServer() => new(
            (int)Id,
            Name,
            Host,
            (int)Port,
            UseSsl,
            Username,
            Password,
            (int)Connections,
            (int)Priority,
            Enabled,
            (int)RetentionDays,
            (int)TimeoutSeconds,
            Enum.IsDefined((CertificateCheck)CertificateCheck) ? (CertificateCheck)CertificateCheck : Core.Downloads.CertificateCheck.Strict,
            (int)ArticlesPerRequest,
            Enum.IsDefined((ServerFailureAction)OnFailure) ? (ServerFailureAction)OnFailure : ServerFailureAction.Skip,
            ExpiresOn,
            QuotaBytes,
            QuotaUsedBytes,
            QuotaSince is { } since ? DateTime.SpecifyKind(since, DateTimeKind.Utc) : null,
            Notes ?? string.Empty);
    }
}
