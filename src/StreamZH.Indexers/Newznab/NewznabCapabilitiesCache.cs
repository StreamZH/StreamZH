// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StreamZH.Core.Indexers;

namespace StreamZH.Indexers.Newznab;

/// <summary>
/// In-memory cache of t=caps per indexer (key: hash of base URL, API path and API key), like Radarr's 7-day cache.
/// Register as a singleton; the Newznab client itself is transient.
/// </summary>
public sealed class NewznabCapabilitiesCache(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>How long the default caps stand in for an indexer whose caps could not be read.</summary>
    public static readonly TimeSpan FallbackLifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public TimeProvider Time { get; } = timeProvider ?? TimeProvider.System;

    public NewznabCaps? Get(IndexerDefinition indexer)
    {
        var key = KeyOf(indexer);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (entry.Expires > Time.GetUtcNow())
        {
            return entry.Caps;
        }

        _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        return null;
    }

    public void Set(IndexerDefinition indexer, NewznabCaps caps, TimeSpan lifetime) =>
        _entries[KeyOf(indexer)] = new Entry(caps, Time.GetUtcNow() + lifetime);

    public void Remove(IndexerDefinition indexer) => _entries.TryRemove(KeyOf(indexer), out _);

    public static string KeyOf(IndexerDefinition indexer)
    {
        var text = $"{indexer.BaseUrl.Trim().TrimEnd('/')}\n{indexer.ApiPath.Trim().TrimEnd('/')}\n{indexer.ApiKey.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private sealed record Entry(NewznabCaps Caps, DateTimeOffset Expires);
}
