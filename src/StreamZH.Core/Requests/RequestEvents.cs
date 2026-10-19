// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Events;
using StreamZH.Core.Users;

namespace StreamZH.Core.Requests;

/// <summary>
/// A request was created, decided, changed state or was deleted (0.10). Listeners re-read the request; after a delete it
/// is gone and <see cref="Status"/> is the last state it had. Published on every change (the Discord bot and the web UI
/// react to it).
/// </summary>
public sealed record RequestChanged(int RequestId, RequestStatus Status) : IAppEvent;

/// <summary>
/// The defaults that apply to a user whose own columns are empty (Users.AutoApprove, MovieRequestLimit,
/// SeriesRequestLimit, RequestLimitDays are null). Settings → Requests provides them; without those settings the role
/// defaults apply (Owner, Admin and Friend approve themselves, Requester waits, no limits, 7 days).
/// </summary>
public interface IRequestDefaults
{
    /// <summary>Whether requests of a user with this role are approved at once when the user has no own setting.</summary>
    bool AutoApprove(UserRole role);

    /// <summary>Movies per <see cref="LimitDays"/>; null = no limit.</summary>
    int? MovieLimit { get; }

    /// <summary>Series requests per <see cref="LimitDays"/>; null = no limit.</summary>
    int? SeriesLimit { get; }

    /// <summary>The window of the limits in days.</summary>
    int LimitDays { get; }
}
