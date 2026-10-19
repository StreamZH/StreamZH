// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Host.Auth;

/// <summary>What a session is checked against: the account as it is now.</summary>
public sealed record SessionState(int Id, string Username, UserRole Role, bool Enabled, int SessionVersion);

/// <summary>How a session compares with its account.</summary>
public enum SessionCheck
{
    /// <summary>Account exists, enabled, same session version and role.</summary>
    Valid,

    /// <summary>Same session, but the role or name changed without a version bump: issue fresh claims.</summary>
    Refresh,

    /// <summary>Account gone or disabled, or the session was ended (password change, role change, revoke).</summary>
    Rejected,
}

/// <summary>
/// 0.10 session hardening: every cookie (and remembered Basic sign-in) is checked against its account on each request.
/// The account state is cached for <see cref="Lifetime"/> so a request does not hit the database every time; user
/// management calls <see cref="Forget"/> on every change, so changes made in StreamZH apply at once and only changes
/// made behind StreamZH's back take up to <see cref="Lifetime"/>.
/// </summary>
public sealed class UserSessionCache(UserRepository users, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private const int MaxEntries = 1000;

    private readonly ConcurrentDictionary<int, (SessionState? State, DateTimeOffset Until)> _entries = new();

    public SessionState? Get(int userId)
    {
        var now = time.GetUtcNow();
        if (_entries.TryGetValue(userId, out var entry) && entry.Until > now)
        {
            return entry.State;
        }

        var user = users.FindById(userId);
        var state = user is null ? null : new SessionState(user.Id, user.Username, user.Role, user.Enabled, user.SessionVersion);
        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }

        _entries[userId] = (state, now + Lifetime);
        return state;
    }

    public void Forget(int userId) => _entries.TryRemove(userId, out _);

    public void Clear() => _entries.Clear();

    public SessionCheck Check(ClaimsPrincipal principal, out SessionState? current)
    {
        ArgumentNullException.ThrowIfNull(principal);
        current = null;
        var id = UserClaims.UserId(principal);
        if (id <= 0)
        {
            return SessionCheck.Rejected;
        }

        current = Get(id);
        if (current is null || !current.Enabled || current.SessionVersion != UserClaims.Version(principal))
        {
            return SessionCheck.Rejected;
        }

        return UserClaims.Role(principal) == current.Role && principal.Identity?.Name == current.Username
            ? SessionCheck.Valid
            : SessionCheck.Refresh;
    }

    /// <summary>CookieAuthenticationEvents.OnValidatePrincipal.</summary>
    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal is not { } principal)
        {
            return;
        }

        var cache = context.HttpContext.RequestServices.GetRequiredService<UserSessionCache>();
        switch (cache.Check(principal, out var current))
        {
            case SessionCheck.Valid:
                return;
            case SessionCheck.Refresh:
                context.ReplacePrincipal(UserClaims.Principal(current!.Id, current.Username, current.Role, current.SessionVersion, AuthSchemes.Cookie));
                context.ShouldRenew = true;
                return;
            default:
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(AuthSchemes.Cookie);
                return;
        }
    }
}
