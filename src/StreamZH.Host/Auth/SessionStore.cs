// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Security.Cryptography;
using StreamZH.Data;

namespace StreamZH.Host.Auth;

/// <summary>
/// Server-side sign-in sessions (0.12, security review S1-10). Every sign-in cookie names a session row; signing out
/// deletes it, so a copied cookie stops working at once. Whether a session still exists is cached for
/// <see cref="Lifetime"/>; <see cref="End"/> drops the cache entry, so a sign-out applies immediately.
/// </summary>
public sealed class SessionStore(SessionRepository sessions, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    /// <summary>Sessions unused for this long are removed (the longest cookie lives 30 days, sliding).</summary>
    public static readonly TimeSpan Unused = TimeSpan.FromDays(31);

    private static readonly TimeSpan TouchEvery = TimeSpan.FromHours(1);
    private const int MaxEntries = 2000;

    /// <summary>How long a cookie from before 0.12 keeps working after it was replaced (requests already on their way).</summary>
    public static readonly TimeSpan LegacyGrace = TimeSpan.FromSeconds(60);

    private const string LegacyPrefix = "legacy-";

    private readonly ConcurrentDictionary<string, (bool Active, int UserId, DateTimeOffset Until)> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string SessionId, DateTimeOffset Until)> _legacy = new(StringComparer.Ordinal);
    private DateTimeOffset _nextCleanup;

    /// <summary>
    /// 0.12 (V-09): a cookie from before 0.12 has no session. The first time it is used it gets one and is replaced by a
    /// cookie that names it; the old cookie is remembered (by a hash of its account, issue and expiry time) and works
    /// only for <see cref="LegacyGrace"/> more, so a copy of it stops working like any signed-out cookie. Returns the
    /// session to put into the new cookie, or null when this old cookie was already replaced.
    /// </summary>
    public string? ReplaceLegacy(int userId, DateTimeOffset? issued, DateTimeOffset? expires, int version, bool persistent)
    {
        var now = time.GetUtcNow();
        var key = LegacyPrefix + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{userId}|{issued?.UtcTicks}|{expires?.UtcTicks}|{version}")));
        lock (_legacy)
        {
            if (_legacy.TryGetValue(key, out var replaced))
            {
                return replaced.Until > now ? replaced.SessionId : null;
            }

            if (sessions.Find(key) is not null)
            {
                // Replaced before (also before a restart): the old cookie is done.
                return null;
            }

            var id = Create(userId, persistent);

            // The marker is a session row nobody's cookie can name (it would need the sign-in keys), cleaned up like one.
            sessions.Create(new UserSession(key, userId, now.UtcDateTime, now.UtcDateTime, false));
            _legacy[key] = (id, now + LegacyGrace);
            foreach (var (old, value) in _legacy)
            {
                if (value.Until <= now)
                {
                    _legacy.TryRemove(old, out _);
                }
            }

            return id;
        }
    }

    /// <summary>Starts a session for the account and returns its id (256 random bits, hex).</summary>
    public string Create(int userId, bool persistent)
    {
        var now = time.GetUtcNow();
        CleanUp(now);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        sessions.Create(new UserSession(id, userId, now.UtcDateTime, now.UtcDateTime, persistent));
        _entries[id] = (true, userId, now + Lifetime);
        return id;
    }

    /// <summary>The session exists and belongs to the account.</summary>
    public bool IsActive(string? id, int userId)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var now = time.GetUtcNow();
        if (_entries.TryGetValue(id, out var entry) && entry.Until > now)
        {
            return entry.Active && entry.UserId == userId;
        }

        var session = sessions.Find(id);
        if (session is not null && now.UtcDateTime - session.LastSeen > TouchEvery)
        {
            sessions.Touch(id, now.UtcDateTime);
        }

        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }

        _entries[id] = (session is not null, session?.UserId ?? 0, now + Lifetime);
        return session is not null && session.UserId == userId;
    }

    /// <summary>Signs this session out (logout).</summary>
    public void End(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        sessions.Delete(id);
        _entries[id] = (false, 0, time.GetUtcNow() + Lifetime);
    }

    /// <summary>Ends every session of the account (the session version bump already refuses their cookies).</summary>
    public void EndAll(int userId)
    {
        sessions.DeleteForUser(userId);
        foreach (var (key, value) in _entries)
        {
            if (value.UserId == userId)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private void CleanUp(DateTimeOffset now)
    {
        if (now < _nextCleanup)
        {
            return;
        }

        _nextCleanup = now + TimeSpan.FromHours(6);
        sessions.DeleteStale((now - Unused).UtcDateTime);
    }
}
