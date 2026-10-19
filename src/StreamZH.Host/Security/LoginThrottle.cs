// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;

namespace StreamZH.Host.Security;

/// <summary>
/// Counts failed sign-ins (sign-in page and Basic) and refuses further attempts for a while once too many failed within
/// <see cref="Window"/>. The per-request rate limits (AuthServiceExtensions) stop fast guessing from one address; this
/// stops slow guessing. Clients are counted by IPv4 address or IPv6 /64 (<see cref="ClientAddress"/>).
/// <list type="bullet">
/// <item>Per account and client network (IPv4 /24, IPv6 /64; <see cref="AccountLimit"/>): an attacker who guesses the
/// owner's password locks the owner out of <em>the attacker's</em> network only, not the owner at home (0.12, V-07).</item>
/// <item>Per client address, any account (<see cref="AddressLimit"/>).</item>
/// <item>Per account over all networks (<see cref="GlobalAccountLimit"/>), against guessing from many networks. It does
/// not apply to networks the account signed in from successfully before (since the start), so it cannot lock the owner
/// out of their usual network either.</item>
/// </list>
/// Memory is bounded: entries expire after <see cref="Window"/>, and when <see cref="MaxKeys"/> is reached the least
/// recently used entries go first. An account under attack is used with every attempt, so it is never the one dropped;
/// nothing is ever reset all at once (V-07).
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan KnownNetworkLifetime = TimeSpan.FromDays(30);
    public const int AccountLimit = 10;
    public const int AddressLimit = 30;
    public const int GlobalAccountLimit = 100;
    public const int MaxKeys = 20_000;
    public const int MaxKnownNetworks = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _known = new(StringComparer.Ordinal);
    private readonly Lock _pruneLock = new();
    private long _sequence;

    /// <summary>Entries held right now (for tests).</summary>
    public int Count => _failures.Count;

    /// <summary>How long the next attempt for this account from this client must wait; null = it may try now.</summary>
    public TimeSpan? RetryAfter(string? username, string client)
    {
        var now = time.GetUtcNow();
        var waits = new List<TimeSpan?>
        {
            Wait(AccountKey(username, client), AccountLimit, now),
            Wait(AddressKey(client), AddressLimit, now),
        };
        if (!IsKnown(username, client, now))
        {
            waits.Add(Wait(GlobalKey(username), GlobalAccountLimit, now));
        }

        return waits.Where(w => w is not null).DefaultIfEmpty(null).Max();
    }

    public void Failed(string? username, string client)
    {
        var now = time.GetUtcNow();
        Add(AccountKey(username, client), AccountLimit, now);
        Add(AddressKey(client), AddressLimit, now);
        Add(GlobalKey(username), GlobalAccountLimit, now);
        if (_failures.Count > MaxKeys)
        {
            Prune(now);
        }
    }

    /// <summary>
    /// A successful sign-in clears the account's failures from this network and over all networks (not the network's own
    /// count) and remembers the network as one the account uses.
    /// </summary>
    public void Succeeded(string? username, string client)
    {
        var now = time.GetUtcNow();
        _failures.TryRemove(AccountKey(username, client), out _);
        _failures.TryRemove(GlobalKey(username), out _);
        _known[KnownKey(username, client)] = now;
        if (_known.Count > MaxKnownNetworks)
        {
            foreach (var key in _known.OrderBy(k => k.Value).Take(_known.Count - (MaxKnownNetworks * 9 / 10)).Select(k => k.Key).ToList())
            {
                _known.TryRemove(key, out _);
            }
        }
    }

    private static string Name(string? username) => (username ?? string.Empty).Trim().ToUpperInvariant();

    private static string AccountKey(string? username, string client) => "u:" + Name(username) + "\n" + Network(client);

    /// <summary>The network an account lock applies to: an IPv4 /24 (the client key already is a /64 for IPv6).</summary>
    public static string Network(string client)
    {
        if (System.Net.IPAddress.TryParse(client, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            bytes[3] = 0;
            return new System.Net.IPAddress(bytes) + "/24";
        }

        return client;
    }

    private static string GlobalKey(string? username) => "g:" + Name(username);

    private static string AddressKey(string client) => "a:" + client;

    private static string KnownKey(string? username, string client) => Name(username) + "\n" + Network(client);

    private bool IsKnown(string? username, string client, DateTimeOffset now) =>
        _known.TryGetValue(KnownKey(username, client), out var since) && now - since < KnownNetworkLifetime;

    private TimeSpan? Wait(string key, int limit, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var entry))
        {
            return null;
        }

        lock (entry)
        {
            entry.Expire(now);
            return entry.Times.Count >= limit ? entry.Times.Peek() + Window - now : null;
        }
    }

    private void Add(string key, int limit, DateTimeOffset now)
    {
        var entry = _failures.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            entry.Times.Enqueue(now);
            entry.LastUsed = Interlocked.Increment(ref _sequence);
            while (entry.Times.Count > limit)
            {
                entry.Times.Dequeue();
            }
        }
    }

    // Expired entries first; then the least recently used ones, down to 90 % of the limit.
    private void Prune(DateTimeOffset now)
    {
        lock (_pruneLock)
        {
            foreach (var (key, entry) in _failures)
            {
                bool stale;
                lock (entry)
                {
                    entry.Expire(now);
                    stale = entry.Times.Count == 0;
                }

                if (stale)
                {
                    _failures.TryRemove(key, out _);
                }
            }

            var excess = _failures.Count - (MaxKeys * 9 / 10);
            if (excess > 0)
            {
                foreach (var key in _failures.OrderBy(p => p.Value.LastUsed).Take(excess).Select(p => p.Key).ToList())
                {
                    _failures.TryRemove(key, out _);
                }
            }
        }
    }

    private sealed class Entry
    {
        public Queue<DateTimeOffset> Times { get; } = new();

        /// <summary>When the entry was last counted (an increasing number, so the order is exact).</summary>
        public long LastUsed { get; set; }

        public void Expire(DateTimeOffset now)
        {
            while (Times.Count > 0 && now - Times.Peek() >= Window)
            {
                Times.Dequeue();
            }
        }
    }
}
