// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;

namespace StreamZH.Data;

/// <summary>A sign-in (0.12): the cookie carries <see cref="Id"/>; signing out deletes the row, which ends the cookie.</summary>
public sealed record UserSession(string Id, int UserId, DateTime Created, DateTime LastSeen, bool Persistent);

public sealed class SessionRepository(Database database)
{
    private const string Columns = "Id, UserId, Created, LastSeen, Persistent";

    public void Create(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var connection = database.Open();
        connection.Execute($"INSERT INTO Sessions ({Columns}) VALUES (@Id, @UserId, @Created, @LastSeen, @Persistent)", session);
    }

    public UserSession? Find(string id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<SessionRow>($"SELECT {Columns} FROM Sessions WHERE Id = @id", new { id })?.ToSession();
    }

    public void Touch(string id, DateTime when)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Sessions SET LastSeen = @when WHERE Id = @id", new { id, when });
    }

    public bool Delete(string id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM Sessions WHERE Id = @id", new { id }) > 0;
    }

    public int DeleteForUser(int userId)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM Sessions WHERE UserId = @userId", new { userId });
    }

    /// <summary>Removes sessions not used since <paramref name="before"/> and sessions of accounts that no longer exist.</summary>
    public int DeleteStale(DateTime before)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM Sessions WHERE LastSeen < @before OR UserId NOT IN (SELECT Id FROM Users)", new { before });
    }

    public int Count(int userId)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Sessions WHERE UserId = @userId", new { userId });
    }

    private sealed class SessionRow
    {
        public string Id { get; init; } = string.Empty;
        public long UserId { get; init; }
        public DateTime Created { get; init; }
        public DateTime LastSeen { get; init; }
        public bool Persistent { get; init; }

        public UserSession ToSession() =>
            new(Id, (int)UserId, DateTime.SpecifyKind(Created, DateTimeKind.Utc), DateTime.SpecifyKind(LastSeen, DateTimeKind.Utc), Persistent);
    }
}
