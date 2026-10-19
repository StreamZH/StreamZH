// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;

namespace StreamZH.Data;

/// <summary>Filter for the request list; all parts optional.</summary>
public sealed record RequestQuery(
    IReadOnlyCollection<RequestStatus>? Statuses = null,
    int? RequestedBy = null,
    MediaType? MediaType = null,
    int Page = 1,
    int PageSize = 50);

public sealed record RequestPage(IReadOnlyList<MediaRequest> Records, int TotalRecords, int Page, int PageSize);

/// <summary>Table Requests (0.10).</summary>
public sealed class RequestRepository(Database database)
{
    private const string Columns =
        "Id, MediaType, TmdbId, Title, Year, PosterPath, Seasons, Status, Source, RequestedBy, DiscordUserId, DiscordName, QualityProfileId, RootFolderId, ItemId, Note, Reason, DecidedBy, Created, Updated, Decided, Available";

    public RequestPage Query(RequestQuery query)
    {
        var (where, args) = Where(query);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var page = Math.Max(1, query.Page);
        args.Add("limit", pageSize);
        args.Add("offset", (page - 1) * pageSize);
        using var connection = database.Open();
        var total = connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM Requests {where}", args);
        var rows = connection.Query<Row>($"SELECT {Columns} FROM Requests {where} ORDER BY Created DESC, Id DESC LIMIT @limit OFFSET @offset", args);
        return new RequestPage([.. rows.Select(r => r.ToRequest())], total, page, pageSize);
    }

    public MediaRequest? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM Requests WHERE Id = @id", new { id })?.ToRequest();
    }

    /// <summary>Requests for one title that are not finished (pending, approved, processing, partially available).</summary>
    public IReadOnlyList<MediaRequest> Open(MediaType mediaType, int tmdbId)
    {
        using var connection = database.Open();
        return
        [
            .. connection.Query<Row>(
                $"SELECT {Columns} FROM Requests WHERE MediaType = @type AND TmdbId = @tmdbId AND Status IN (1, 2, 4, 5) ORDER BY Id",
                new { type = (int)mediaType, tmdbId }).Select(r => r.ToRequest()),
        ];
    }

    /// <summary>All requests for one title, newest first.</summary>
    public IReadOnlyList<MediaRequest> ForTitle(MediaType mediaType, int tmdbId)
    {
        using var connection = database.Open();
        return
        [
            .. connection.Query<Row>(
                $"SELECT {Columns} FROM Requests WHERE MediaType = @type AND TmdbId = @tmdbId ORDER BY Id DESC",
                new { type = (int)mediaType, tmdbId }).Select(r => r.ToRequest()),
        ];
    }

    /// <summary>Requests in the given states, oldest first (for the availability check).</summary>
    public IReadOnlyList<MediaRequest> InStates(params RequestStatus[] statuses)
    {
        using var connection = database.Open();
        return
        [
            .. connection.Query<Row>($"SELECT {Columns} FROM Requests WHERE Status IN @statuses ORDER BY Id", new { statuses = statuses.Select(s => (int)s).ToArray() })
                .Select(r => r.ToRequest()),
        ];
    }

    /// <summary>How many requests of a type a user made since a time (declined ones do not count), for request limits.</summary>
    public int CountSince(int userId, MediaType mediaType, DateTime since)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Requests WHERE RequestedBy = @userId AND MediaType = @type AND Created >= @since AND Status <> 3",
            new { userId, type = (int)mediaType, since });
    }

    /// <summary>
    /// The newest requests of one person for the Discord /requests command: those of the StreamZH user (any source) and
    /// those made from the Discord account (also as a guest before the account was linked). Additive (0.10, Discord bot).
    /// </summary>
    public IReadOnlyList<MediaRequest> ForRequester(int? userId, string? discordUserId, int limit)
    {
        if (userId is null && string.IsNullOrEmpty(discordUserId))
        {
            return [];
        }

        using var connection = database.Open();
        return
        [
            .. connection.Query<Row>(
                $"SELECT {Columns} FROM Requests WHERE (RequestedBy = @userId OR DiscordUserId = @discordUserId) ORDER BY Created DESC, Id DESC LIMIT @limit",
                new { userId, discordUserId, limit = Math.Clamp(limit, 1, 100) }).Select(r => r.ToRequest()),
        ];
    }

    public int Count(RequestStatus status)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Requests WHERE Status = @status", new { status = (int)status });
    }

    public int Add(MediaRequest request)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO Requests (MediaType, TmdbId, Title, Year, PosterPath, Seasons, Status, Source, RequestedBy, DiscordUserId, DiscordName,
                                  QualityProfileId, RootFolderId, ItemId, Note, Reason, DecidedBy, Created, Updated, Decided, Available)
            VALUES (@MediaType, @TmdbId, @Title, @Year, @PosterPath, @Seasons, @Status, @Source, @RequestedBy, @DiscordUserId, @DiscordName,
                    @QualityProfileId, @RootFolderId, @ItemId, @Note, @Reason, @DecidedBy, @Created, @Updated, @Decided, @Available);
            SELECT last_insert_rowid();
            """,
            Parameters(request));
    }

    public void Update(MediaRequest request)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Requests SET MediaType = @MediaType, TmdbId = @TmdbId, Title = @Title, Year = @Year, PosterPath = @PosterPath, Seasons = @Seasons,
                Status = @Status, Source = @Source, RequestedBy = @RequestedBy, DiscordUserId = @DiscordUserId, DiscordName = @DiscordName,
                QualityProfileId = @QualityProfileId, RootFolderId = @RootFolderId, ItemId = @ItemId, Note = @Note, Reason = @Reason,
                DecidedBy = @DecidedBy, Created = @Created, Updated = @Updated, Decided = @Decided, Available = @Available
            WHERE Id = @Id
            """,
            Parameters(request));
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM Requests WHERE Id = @id", new { id });
    }

    /// <summary>When a user is deleted their requests stay, without the user.</summary>
    public void DetachUser(int userId)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Requests SET RequestedBy = NULL WHERE RequestedBy = @userId", new { userId });
    }

    private static (string Where, DynamicParameters Args) Where(RequestQuery query)
    {
        var parts = new List<string>();
        var args = new DynamicParameters();
        if (query.Statuses is { Count: > 0 } statuses)
        {
            parts.Add("Status IN @statuses");
            args.Add("statuses", statuses.Select(s => (int)s).ToArray());
        }

        if (query.RequestedBy is { } user)
        {
            parts.Add("RequestedBy = @user");
            args.Add("user", user);
        }

        if (query.MediaType is { } type)
        {
            parts.Add("MediaType = @type");
            args.Add("type", (int)type);
        }

        return (parts.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", parts), args);
    }

    private static object Parameters(MediaRequest r) => new
    {
        r.Id,
        MediaType = (int)r.MediaType,
        r.TmdbId,
        r.Title,
        r.Year,
        r.PosterPath,
        Seasons = r.Seasons is null ? null : JsonSerializer.Serialize(r.Seasons),
        Status = (int)r.Status,
        Source = (int)r.Source,
        r.RequestedBy,
        r.DiscordUserId,
        r.DiscordName,
        r.QualityProfileId,
        r.RootFolderId,
        r.ItemId,
        r.Note,
        r.Reason,
        r.DecidedBy,
        r.Created,
        r.Updated,
        r.Decided,
        r.Available,
    };

    private sealed class Row
    {
        public long Id { get; init; }

        public long MediaType { get; init; }

        public long TmdbId { get; init; }

        public string Title { get; init; } = string.Empty;

        public long? Year { get; init; }

        public string? PosterPath { get; init; }

        public string? Seasons { get; init; }

        public long Status { get; init; }

        public long Source { get; init; }

        public long? RequestedBy { get; init; }

        public string? DiscordUserId { get; init; }

        public string? DiscordName { get; init; }

        public long? QualityProfileId { get; init; }

        public long? RootFolderId { get; init; }

        public long? ItemId { get; init; }

        public string? Note { get; init; }

        public string? Reason { get; init; }

        public long? DecidedBy { get; init; }

        public DateTime Created { get; init; }

        public DateTime Updated { get; init; }

        public DateTime? Decided { get; init; }

        public DateTime? Available { get; init; }

        public MediaRequest ToRequest() => new()
        {
            Id = (int)Id,
            MediaType = (MediaType)MediaType,
            TmdbId = (int)TmdbId,
            Title = Title,
            Year = (int?)Year,
            PosterPath = PosterPath,
            Seasons = Seasons is null ? null : JsonSerializer.Deserialize<int[]>(Seasons),
            Status = (RequestStatus)Status,
            Source = (RequestSource)Source,
            RequestedBy = (int?)RequestedBy,
            DiscordUserId = DiscordUserId,
            DiscordName = DiscordName,
            QualityProfileId = (int?)QualityProfileId,
            RootFolderId = (int?)RootFolderId,
            ItemId = (int?)ItemId,
            Note = Note,
            Reason = Reason,
            DecidedBy = (int?)DecidedBy,
            Created = Utc(Created),
            Updated = Utc(Updated),
            Decided = Decided is { } d ? Utc(d) : null,
            Available = Available is { } a ? Utc(a) : null,
        };
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>Table Invites (0.10). Tokens are stored as SHA-256 only.</summary>
public sealed class InviteRepository(Database database)
{
    private const string Columns = "Id, TokenHash, Role, Note, CreatedBy, Created, Expires, MaxUses, Uses, Revoked";

    public IReadOnlyList<Invite> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM Invites ORDER BY Id DESC").Select(r => r.ToInvite())];
    }

    public Invite? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM Invites WHERE Id = @id", new { id })?.ToInvite();
    }

    public Invite? FindByHash(string tokenHash)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM Invites WHERE TokenHash = @tokenHash", new { tokenHash })?.ToInvite();
    }

    public int Add(string tokenHash, UserRole role, string? note, int createdBy, DateTime? expires, int maxUses)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO Invites (TokenHash, Role, Note, CreatedBy, Created, Expires, MaxUses) VALUES (@tokenHash, @role, @note, @createdBy, @created, @expires, @maxUses); SELECT last_insert_rowid();",
            new { tokenHash, role = (int)role, note, createdBy, created = DateTime.UtcNow, expires, maxUses });
    }

    /// <summary>Counts one use if the invite is still usable; false when it is used up, expired or revoked (atomic).</summary>
    public bool TryUse(int id, DateTime now)
    {
        using var connection = database.Open();
        return connection.Execute(
            "UPDATE Invites SET Uses = Uses + 1 WHERE Id = @id AND Revoked = 0 AND Uses < MaxUses AND (Expires IS NULL OR Expires > @now)",
            new { id, now }) == 1;
    }

    public void Revoke(int id)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Invites SET Revoked = 1 WHERE Id = @id", new { id });
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM Invites WHERE Id = @id", new { id });
    }

    private sealed class Row
    {
        public long Id { get; init; }

        public string TokenHash { get; init; } = string.Empty;

        public long Role { get; init; }

        public string? Note { get; init; }

        public long CreatedBy { get; init; }

        public DateTime Created { get; init; }

        public DateTime? Expires { get; init; }

        public long MaxUses { get; init; }

        public long Uses { get; init; }

        public bool Revoked { get; init; }

        public Invite ToInvite() => new(
            (int)Id, TokenHash, (UserRole)Role, Note, (int)CreatedBy, DateTime.SpecifyKind(Created, DateTimeKind.Utc),
            Expires is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null, (int)MaxUses, (int)Uses, Revoked);
    }
}

/// <summary>Table DiscordLinks (0.10): one Discord account per StreamZH user.</summary>
public sealed class DiscordLinkRepository(Database database)
{
    private const string Columns = "Id, UserId, DiscordUserId, DiscordName, Linked";

    public IReadOnlyList<DiscordLink> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM DiscordLinks ORDER BY Id").Select(r => r.ToLink())];
    }

    public DiscordLink? ForUser(int userId)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM DiscordLinks WHERE UserId = @userId", new { userId })?.ToLink();
    }

    public DiscordLink? ForDiscord(string discordUserId)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM DiscordLinks WHERE DiscordUserId = @discordUserId", new { discordUserId })?.ToLink();
    }

    /// <summary>Links (or re-links) a user; an existing link of the Discord account to another user is removed.</summary>
    public void Set(int userId, string discordUserId, string? discordName)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute("DELETE FROM DiscordLinks WHERE UserId = @userId OR DiscordUserId = @discordUserId", new { userId, discordUserId }, transaction);
        connection.Execute(
            "INSERT INTO DiscordLinks (UserId, DiscordUserId, DiscordName, Linked) VALUES (@userId, @discordUserId, @discordName, @linked)",
            new { userId, discordUserId, discordName, linked = DateTime.UtcNow }, transaction);
        transaction.Commit();
    }

    public void RemoveForUser(int userId)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM DiscordLinks WHERE UserId = @userId", new { userId });
    }

    private sealed class Row
    {
        public long Id { get; init; }

        public long UserId { get; init; }

        public string DiscordUserId { get; init; } = string.Empty;

        public string? DiscordName { get; init; }

        public DateTime Linked { get; init; }

        public DiscordLink ToLink() => new((int)Id, (int)UserId, DiscordUserId, DiscordName, DateTime.SpecifyKind(Linked, DateTimeKind.Utc));
    }
}
