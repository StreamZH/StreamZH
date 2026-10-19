// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;

namespace StreamZH.Host.Security;

/// <summary>
/// Counts failed sign-ins (sign-in page and Basic) per account and per client (IPv6 per /64, see <see cref="ClientAddress"/>)
/// and refuses further attempts for a while once too many failed within <see cref="Window"/>. The per-request rate limits
/// (AuthServiceExtensions) stop fast guessing from one address; this stops slow guessing of one account from many
/// addresses and guessing behind a reverse proxy that is not trusted (all clients share its address).
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public const int AccountLimit = 10;
    public const int AddressLimit = 30;
    private const int MaxKeys = 10_000;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);

    /// <summary>How long the next attempt for this account or from this client must wait; null = it may try now.</summary>
    public TimeSpan? RetryAfter(string? username, string client)
    {
        var now = time.GetUtcNow();
        var account = Wait(AccountKey(username), AccountLimit, now);
        var address = Wait(AddressKey(client), AddressLimit, now);
        return account is null ? address : address is null ? account : account > address ? account : address;
    }

    public void Failed(string? username, string client)
    {
        var now = time.GetUtcNow();
        if (_failures.Count >= MaxKeys)
        {
            Prune(now);
        }

        Add(AccountKey(username), AccountLimit, now);
        Add(AddressKey(client), AddressLimit, now);
    }

    /// <summary>A successful sign-in clears the account's failures (not the client's).</summary>
    public void Succeeded(string? username) => _failures.TryRemove(AccountKey(username), out _);

    private static string AccountKey(string? username) => "u:" + (username ?? string.Empty).Trim().ToUpperInvariant();

    private static string AddressKey(string client) => "a:" + client;

    private TimeSpan? Wait(string key, int limit, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var queue))
        {
            return null;
        }

        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= Window)
            {
                queue.Dequeue();
            }

            return queue.Count >= limit ? queue.Peek() + Window - now : null;
        }
    }

    private void Add(string key, int limit, DateTimeOffset now)
    {
        var queue = _failures.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            queue.Enqueue(now);
            while (queue.Count > limit)
            {
                queue.Dequeue();
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, queue) in _failures)
        {
            bool stale;
            lock (queue)
            {
                stale = queue.Count == 0 || now - queue.Last() >= Window;
            }

            if (stale)
            {
                _failures.TryRemove(key, out _);
            }
        }

        if (_failures.Count >= MaxKeys)
        {
            _failures.Clear();
        }
    }
}
