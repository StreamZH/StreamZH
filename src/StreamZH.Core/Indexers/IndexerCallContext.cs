// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace StreamZH.Core.Indexers;

/// <summary>Who caused a call to an indexer; shown in the indexer history.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndexerCallSource>))]
public enum IndexerCallSource
{
    /// <summary>Manual search in the UI for a movie or series.</summary>
    [JsonStringEnumMemberName("interactive")]
    Interactive,

    /// <summary>Search started by StreamZH (missing items, upgrades).</summary>
    [JsonStringEnumMemberName("automatic")]
    Automatic,

    [JsonStringEnumMemberName("rss")]
    Rss,

    /// <summary>The search page (search all indexers without a library item).</summary>
    [JsonStringEnumMemberName("search")]
    Search,

    /// <summary>The "Test" button and "Test all".</summary>
    [JsonStringEnumMemberName("test")]
    Test,
}

/// <summary>
/// The source of the indexer calls made in the current async flow. Callers set it with <see cref="Begin(IndexerCallSource)"/>
/// (Dispose restores the previous value); the tracking client reads it when it records a call.
/// </summary>
public static class IndexerCallContext
{
    private static readonly AsyncLocal<IndexerCallSource?> CurrentSource = new();

    public static IndexerCallSource? Current => CurrentSource.Value;

    public static IDisposable Begin(IndexerCallSource source)
    {
        var previous = CurrentSource.Value;
        CurrentSource.Value = source;
        return new Restore(previous);
    }

    /// <summary>The source that belongs to a search use (interactive, automatic or RSS).</summary>
    public static IDisposable Begin(IndexerUse use) => Begin(use switch
    {
        IndexerUse.AutomaticSearch => IndexerCallSource.Automatic,
        IndexerUse.Rss => IndexerCallSource.Rss,
        _ => IndexerCallSource.Interactive,
    });

    private sealed class Restore(IndexerCallSource? previous) : IDisposable
    {
        public void Dispose() => CurrentSource.Value = previous;
    }
}
