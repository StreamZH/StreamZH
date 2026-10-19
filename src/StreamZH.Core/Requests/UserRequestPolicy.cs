// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Users;

namespace StreamZH.Core.Requests;

/// <summary>
/// The built-in <see cref="IRequestDefaults"/>: Owner, Admin and Friend approve themselves, Requester waits for an admin,
/// no limits, a 7-day window. Settings → Requests registers its own implementation, which replaces this one.
/// </summary>
public sealed class DefaultRequestDefaults : IRequestDefaults
{
    public bool AutoApprove(UserRole role) => role is UserRole.Owner or UserRole.Admin or UserRole.Friend;

    public int? MovieLimit => null;

    public int? SeriesLimit => null;

    public int LimitDays => 7;
}

/// <summary>What applies to one user's requests: auto-approve and the limits per <see cref="LimitDays"/> (null = no limit).</summary>
public sealed record RequestPolicy(bool AutoApprove, int? MovieLimit, int? SeriesLimit, int LimitDays)
{
    public int? LimitFor(MediaType mediaType) => mediaType == MediaType.Movie ? MovieLimit : SeriesLimit;
}

/// <summary>
/// The effective request settings of a user: the user's own values where set, else the defaults. Auto-approve falls back
/// to <see cref="IRequestDefaults.AutoApprove"/> for the role. Owners and admins have no limits unless custom limits are
/// set; friends and requesters follow the global limits.
/// </summary>
public static class UserRequestPolicy
{
    public static RequestPolicy For(UserAccount user, IRequestDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(defaults);

        var autoApprove = user.AutoApprove ?? defaults.AutoApprove(user.Role);
        if (user.RequestLimitDays is { } days)
        {
            return new RequestPolicy(autoApprove, user.MovieRequestLimit, user.SeriesRequestLimit, days);
        }

        return user.Role is UserRole.Owner or UserRole.Admin
            ? new RequestPolicy(autoApprove, null, null, defaults.LimitDays)
            : new RequestPolicy(autoApprove, defaults.MovieLimit, defaults.SeriesLimit, defaults.LimitDays);
    }
}
