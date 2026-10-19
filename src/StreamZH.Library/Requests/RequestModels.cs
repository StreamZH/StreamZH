// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Library.Requests;

/// <summary>Who acts on a request: a signed-in user, the API key, or a Discord user without a linked account.</summary>
/// <param name="UserId">The StreamZH user; null for the API key, a local-network login without account and Discord guests.</param>
/// <param name="Role">Role of the user (Owner for the API key); <see cref="UserRole.Requester"/> for Discord guests.</param>
public sealed record RequestActor(int? UserId, UserRole Role, RequestSource Source = RequestSource.Web, string? DiscordUserId = null, string? DiscordName = null)
{
    public bool IsAdmin => Role is UserRole.Owner or UserRole.Admin;

    /// <summary>A Discord user without a linked account: requests always wait for an admin, no limits.</summary>
    public bool IsGuest => UserId is null && Source == RequestSource.Discord;

    /// <summary>A Discord user without a linked StreamZH account.</summary>
    public static RequestActor DiscordGuest(string discordUserId, string? discordName) =>
        new(null, UserRole.Requester, RequestSource.Discord, discordUserId, discordName);
}

/// <param name="Seasons">Series: the season numbers; null = all seasons. Must be null for movies.</param>
public sealed record CreateRequestInput(MediaType MediaType, int TmdbId, IReadOnlyList<int>? Seasons = null, string? Note = null);

/// <summary>Optional overrides when an admin approves a request.</summary>
public sealed record ApproveRequestInput(int? QualityProfileId = null, int? RootFolderId = null, IReadOnlyList<int>? Seasons = null);

public enum RequestError
{
    NotFound,
    Invalid,

    /// <summary>Requests are switched off in Settings → Requests.</summary>
    Disabled,

    /// <summary>The user may not do this (disabled account, not their request, not an admin).</summary>
    Forbidden,

    /// <summary>The request limit of the user is used up.</summary>
    LimitReached,

    /// <summary>An open request for the title (and seasons) exists; <see cref="RequestException.Existing"/> is it.</summary>
    AlreadyRequested,

    /// <summary>Already in the library with files.</summary>
    AlreadyAvailable,

    /// <summary>The request is not in a state that allows this (e.g. approving a declined one).</summary>
    Conflict,
}

/// <param name="Field">The API field the message belongs to (validation errors, "limit").</param>
public sealed class RequestException(string message, RequestError error, MediaRequest? existing = null, string? field = null) : Exception(message)
{
    public RequestError Error { get; } = error;

    public MediaRequest? Existing { get; } = existing;

    public string? Field { get; } = field;
}

public enum RequestActivityKind
{
    /// <summary>A new request (pending, or approved at once when the user approves themselves).</summary>
    Created,

    /// <summary>An admin approved a pending request.</summary>
    Approved,

    Declined,

    /// <summary>Everything that was requested has files.</summary>
    Available,
}

/// <summary>
/// What happened to a request, for notifications (Connect). <see cref="RequestChanged"/> is published on every change
/// as well; this one only for the four moments people get told about.
/// </summary>
public sealed record RequestActivity(RequestActivityKind Kind, int RequestId) : IAppEvent;

/// <summary>Display names of users for request lists and messages.</summary>
public static class RequestUserNames
{
    /// <summary>Display name (or user name) per id; unknown ids are left out.</summary>
    public static IReadOnlyDictionary<int, string> Of(UserRepository users, IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.ToHashSet();
        return wanted.Count == 0
            ? new Dictionary<int, string>()
            : users.All().Where(u => wanted.Contains(u.Id)).ToDictionary(u => u.Id, u => u.Name);
    }
}
