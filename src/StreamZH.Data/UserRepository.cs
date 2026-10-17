// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;
using StreamZH.Core.Users;

namespace StreamZH.Data;

public sealed record User(int Id, string Username, string PasswordHash, UserRole Role, DateTime CreatedAt);

public sealed class UserRepository(Database database)
{
    public int Count()
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Users");
    }

    public User? FindByUsername(string username)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<UserRow>(
            "SELECT Id, Username, PasswordHash, Role, CreatedAt FROM Users WHERE Username = @username COLLATE NOCASE",
            new { username })?.ToUser();
    }

    public User? FindById(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<UserRow>(
            "SELECT Id, Username, PasswordHash, Role, CreatedAt FROM Users WHERE Id = @id",
            new { id })?.ToUser();
    }

    public int Create(string username, string passwordHash, UserRole role)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO Users (Username, PasswordHash, Role, CreatedAt) VALUES (@username, @passwordHash, @role, @createdAt); SELECT last_insert_rowid();",
            new { username, passwordHash, role = (int)role, createdAt = DateTime.UtcNow });
    }

    /// <summary>Creates the Owner only if no user exists yet. Returns null if setup was already done.</summary>
    public int? CreateOwnerIfNone(string username, string passwordHash)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Users", transaction: transaction) > 0)
        {
            return null;
        }

        var id = connection.ExecuteScalar<int>(
            "INSERT INTO Users (Username, PasswordHash, Role, CreatedAt) VALUES (@username, @passwordHash, @role, @createdAt); SELECT last_insert_rowid();",
            new { username, passwordHash, role = (int)UserRole.Owner, createdAt = DateTime.UtcNow },
            transaction);

        transaction.Commit();
        return id;
    }

    public void UpdatePasswordHash(int id, string passwordHash)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Users SET PasswordHash = @passwordHash WHERE Id = @id", new { id, passwordHash });
    }

    private sealed class UserRow
    {
        public long Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public long Role { get; init; }
        public DateTime CreatedAt { get; init; }

        public User ToUser() => new((int)Id, Username, PasswordHash, (UserRole)Role, DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc));
    }
}