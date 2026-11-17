// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;

namespace StreamZH.Indexers;

/// <summary>
/// 1.0.0-rc.7: the brake per indexer. Every request to an indexer host (search pages, RSS, caps, NZB downloads, tests)
/// takes the next free slot, so requests from searches running side by side are spaced out instead of arriving in a
/// burst. Many private indexers allow a few requests per second per account and block the API access of accounts that
/// keep going over it (seen as "Incorrect user credentials (code 100)" until a new key is made).
/// After a request limit answer (HTTP 429, Newznab 500/501) the host is held for the Retry-After time, so the searches
/// still running do not send more requests into the limit.
/// </summary>
public sealed class IndexerRequestThrottle(TimeProvider? time = null)
{
    /// <summary>No brake and no hold, for a client built without dependency injection (tests, tools).</summary>
    public static readonly IndexerRequestThrottle Off = new() { _off = true };

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _off;
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Waits for the next free slot of <paramref name="key"/> and takes it. Returns the remaining hold time instead when
    /// the host is held after a request limit (nothing may be sent then).
    /// </summary>
    public async Task<TimeSpan?> AcquireAsync(string key, TimeSpan? interval, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_off)
        {
            return null;
        }

        var slot = _slots.GetOrAdd(key, _ => new Slot());
        TimeSpan wait;
        lock (slot)
        {
            var now = _time.GetUtcNow();
            if (slot.HeldUntil > now)
            {
                return slot.HeldUntil - now;
            }

            if (interval is not { } gap || gap <= TimeSpan.Zero)
            {
                return null;
            }

            var start = slot.Next > now ? slot.Next : now;
            slot.Next = start + gap;
            wait = start - now;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, cancellationToken);
        }

        return null;
    }

    /// <summary>Holds <paramref name="key"/> for <paramref name="duration"/> (request limit reached); a longer hold stays.</summary>
    public void Hold(string key, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_off)
        {
            return;
        }

        var slot = _slots.GetOrAdd(key, _ => new Slot());
        lock (slot)
        {
            var until = _time.GetUtcNow() + duration;
            if (until > slot.HeldUntil)
            {
                slot.HeldUntil = until;
            }
        }
    }

    private sealed class Slot
    {
        public DateTimeOffset Next { get; set; }

        public DateTimeOffset HeldUntil { get; set; }
    }
}
