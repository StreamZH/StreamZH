// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;

namespace StreamZH.Core.Requests;

/// <summary>Where a request is in its life: pending → approved → processing (in the library, searching/downloading) → available; or declined/failed.</summary>
public enum RequestStatus
{
    Pending = 1,
    Approved = 2,
    Declined = 3,
    Processing = 4,
    PartiallyAvailable = 5,
    Available = 6,
    Failed = 7,
}

public enum RequestSource
{
    Web = 1,
    Discord = 2,
    Api = 3,
}

/// <summary>A movie or series somebody asked for (0.10). <see cref="Seasons"/> null = all seasons (or a movie).</summary>
public sealed record MediaRequest
{
    public int Id { get; init; }

    public MediaType MediaType { get; init; }

    public int TmdbId { get; init; }

    public string Title { get; init; } = string.Empty;

    public int? Year { get; init; }

    public string? PosterPath { get; init; }

    public IReadOnlyList<int>? Seasons { get; init; }

    public RequestStatus Status { get; init; } = RequestStatus.Pending;

    public RequestSource Source { get; init; } = RequestSource.Web;

    /// <summary>The StreamZH user; null for a Discord user without a linked account (guest request).</summary>
    public int? RequestedBy { get; init; }

    public string? DiscordUserId { get; init; }

    public string? DiscordName { get; init; }

    public int? QualityProfileId { get; init; }

    public int? RootFolderId { get; init; }

    /// <summary>The movie/series id in the library once it was added.</summary>
    public int? ItemId { get; init; }

    public string? Note { get; init; }

    /// <summary>Why it was declined or failed.</summary>
    public string? Reason { get; init; }

    public int? DecidedBy { get; init; }

    public DateTime Created { get; init; }

    public DateTime Updated { get; init; }

    public DateTime? Decided { get; init; }

    public DateTime? Available { get; init; }
}

/// <summary>An invite link; only the SHA-256 of its token is stored.</summary>
public sealed record Invite(
    int Id, string TokenHash, Users.UserRole Role, string? Note, int CreatedBy, DateTime Created, DateTime? Expires, int MaxUses, int Uses, bool Revoked)
{
    public bool IsUsable(DateTime now) => !Revoked && Uses < MaxUses && (Expires is null || Expires > now);
}

public sealed record DiscordLink(int Id, int UserId, string DiscordUserId, string? DiscordName, DateTime Linked);
