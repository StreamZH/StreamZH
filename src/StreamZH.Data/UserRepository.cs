// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;
using StreamZH.Core.Users;

namespace StreamZH.Data;

/// <summary>What sign-in needs: the password hash, the role and (0.10) whether the account may sign in and its session version.</summary>
public sealed record User(int Id, string Username, string PasswordHash, UserRole Role, DateTime CreatedAt, bool Enabled = true, int SessionVersion = 0);

public sealed class UserRepository(Database database)
{
    private const string AuthColumns = "Id, Username, PasswordHash, Role, CreatedAt, Enabled, SessionVersion";

    private const string AccountColumns =
        "Id, Username, Role, CreatedAt, DisplayName, Email, Enabled, LastLogin, SessionVersion, AutoApprove, MovieRequestLimit, SeriesRequestLimit, RequestLimitDays";

    public int Count()
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Users");
    }

    public User? FindByUsername(string username)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<UserRow>(
            $"SELECT {AuthColumns} FROM Users WHERE Username = @username COLLATE NOCASE",
            new { username })?.ToUser();
    }

    public User? FindById(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<UserRow>($"SELECT {AuthColumns} FROM Users WHERE Id = @id", new { id })?.ToUser();
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

    /// <summary>Stores a new hash without ending sessions (rehash after an algorithm upgrade).</summary>
    public void UpdatePasswordHash(int id, string passwordHash)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Users SET PasswordHash = @passwordHash WHERE Id = @id", new { id, passwordHash });
    }

    // ---- 0.10: user management ----

    public IReadOnlyList<UserAccount> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<AccountRow>($"SELECT {AccountColumns} FROM Users ORDER BY Role, Username COLLATE NOCASE").Select(r => r.ToAccount())];
    }

    public UserAccount? FindAccount(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<AccountRow>($"SELECT {AccountColumns} FROM Users WHERE Id = @id", new { id })?.ToAccount();
    }

    public UserAccount? FindAccountByUsername(string username)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<AccountRow>(
            $"SELECT {AccountColumns} FROM Users WHERE Username = @username COLLATE NOCASE", new { username })?.ToAccount();
    }

    /// <summary>Creates an account with all its settings; returns the new id.</summary>
    public int Create(UserAccount account, string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO Users (Username, PasswordHash, Role, CreatedAt, DisplayName, Email, Enabled, SessionVersion, AutoApprove,
                               MovieRequestLimit, SeriesRequestLimit, RequestLimitDays)
            VALUES (@Username, @passwordHash, @Role, @createdAt, @DisplayName, @Email, @Enabled, 0, @AutoApprove,
                    @MovieRequestLimit, @SeriesRequestLimit, @RequestLimitDays);
            SELECT last_insert_rowid();
            """,
            new
            {
                account.Username,
                passwordHash,
                Role = (int)account.Role,
                createdAt = DateTime.UtcNow,
                account.DisplayName,
                account.Email,
                account.Enabled,
                account.AutoApprove,
                account.MovieRequestLimit,
                account.SeriesRequestLimit,
                account.RequestLimitDays,
            });
    }

    /// <summary>Saves role, profile, state and request settings (not the username, password or session version).</summary>
    public void Update(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Users SET Role = @Role, DisplayName = @DisplayName, Email = @Email, Enabled = @Enabled, AutoApprove = @AutoApprove,
                MovieRequestLimit = @MovieRequestLimit, SeriesRequestLimit = @SeriesRequestLimit, RequestLimitDays = @RequestLimitDays
            WHERE Id = @Id
            """,
            new
            {
                account.Id,
                Role = (int)account.Role,
                account.DisplayName,
                account.Email,
                account.Enabled,
                account.AutoApprove,
                account.MovieRequestLimit,
                account.SeriesRequestLimit,
                account.RequestLimitDays,
            });
    }

    public void UpdateProfile(int id, string? displayName, string? email)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Users SET DisplayName = @displayName, Email = @email WHERE Id = @id", new { id, displayName, email });
    }

    /// <summary>A new password: also ends every session of the user.</summary>
    public void ChangePassword(int id, string passwordHash)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Users SET PasswordHash = @passwordHash, SessionVersion = SessionVersion + 1 WHERE Id = @id", new { id, passwordHash });
    }

    /// <summary>Ends every session of the user (their cookies carry the old version).</summary>
    public void BumpSessionVersion(int id)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Users SET SessionVersion = SessionVersion + 1 WHERE Id = @id", new { id });
    }

    public void SetLastLogin(int id, DateTime when)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Users SET LastLogin = @when WHERE Id = @id", new { id, when });
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM Users WHERE Id = @id", new { id });
    }

    /// <summary>Enabled owners: the last one can be neither lowered, disabled nor deleted.</summary>
    public int CountEnabledOwners()
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Users WHERE Role = @role AND Enabled = 1", new { role = (int)UserRole.Owner });
    }

    /// <summary>Number of requests per user (all time), for the user list.</summary>
    public IReadOnlyDictionary<int, int> RequestCounts()
    {
        using var connection = database.Open();
        return connection.Query<(long UserId, long Count)>(
                "SELECT RequestedBy, COUNT(*) FROM Requests WHERE RequestedBy IS NOT NULL GROUP BY RequestedBy")
            .ToDictionary(r => (int)r.UserId, r => (int)r.Count);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed class UserRow
    {
        public long Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public long Role { get; init; }
        public DateTime CreatedAt { get; init; }
        public bool Enabled { get; init; } = true;
        public long SessionVersion { get; init; }

        public User ToUser() => new((int)Id, Username, PasswordHash, (UserRole)Role, Utc(CreatedAt), Enabled, (int)SessionVersion);
    }

    private sealed class AccountRow
    {
        public long Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public long Role { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? DisplayName { get; init; }
        public string? Email { get; init; }
        public bool Enabled { get; init; } = true;
        public DateTime? LastLogin { get; init; }
        public long SessionVersion { get; init; }
        public bool? AutoApprove { get; init; }
        public long? MovieRequestLimit { get; init; }
        public long? SeriesRequestLimit { get; init; }
        public long? RequestLimitDays { get; init; }

        public UserAccount ToAccount() => new()
        {
            Id = (int)Id,
            Username = Username,
            Role = (UserRole)Role,
            CreatedAt = Utc(CreatedAt),
            DisplayName = DisplayName,
            Email = Email,
            Enabled = Enabled,
            LastLogin = LastLogin is { } last ? Utc(last) : null,
            SessionVersion = (int)SessionVersion,
            AutoApprove = AutoApprove,
            MovieRequestLimit = (int?)MovieRequestLimit,
            SeriesRequestLimit = (int?)SeriesRequestLimit,
            RequestLimitDays = (int?)RequestLimitDays,
        };
    }
}
