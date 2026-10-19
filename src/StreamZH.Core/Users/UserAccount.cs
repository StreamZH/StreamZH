// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Users;

/// <summary>
/// A StreamZH account as user management sees it (0.10): profile, state and request settings, without the password
/// hash. <see cref="AutoApprove"/> null = the role's default. Request limits are custom when
/// <see cref="RequestLimitDays"/> is set (then a null limit means "no limit" for that type); otherwise the role's
/// defaults apply.
/// </summary>
public sealed record UserAccount
{
    public int Id { get; init; }

    public string Username { get; init; } = string.Empty;

    public UserRole Role { get; init; } = UserRole.Friend;

    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTime CreatedAt { get; init; }

    public DateTime? LastLogin { get; init; }

    /// <summary>Bumped on password change, role change, disable and "sign out everywhere"; older cookies stop working.</summary>
    public int SessionVersion { get; init; }

    public bool? AutoApprove { get; init; }

    public int? MovieRequestLimit { get; init; }

    public int? SeriesRequestLimit { get; init; }

    public int? RequestLimitDays { get; init; }

    /// <summary>Display name if set, else the username.</summary>
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

    public bool HasCustomLimits => RequestLimitDays is not null;

    public bool IsAdmin => Role is UserRole.Owner or UserRole.Admin;

    /// <summary>Owner, Admin and Friend may change the library; a Requester only requests.</summary>
    public bool CanManageLibrary => Role is UserRole.Owner or UserRole.Admin or UserRole.Friend;
}
