// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Indexers;

namespace StreamZH.Tests.Library;

/// <summary>A clock that only moves when the test says so.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Answers t=caps with fixed capabilities or throws the configured error; remembers the last definition it got.</summary>
public sealed class StubIndexerClient : IIndexerClient
{
    public static readonly IndexerCapabilities SampleCaps = new(
        100,
        100,
        true,
        ["q", "imdbid", "tmdbid"],
        ["q", "tvdbid", "season", "ep"],
        [
            new IndexerCategory(2000, "Movies", [new IndexerCategory(2040, "Movies/HD", []), new IndexerCategory(2045, "Movies/UHD", [])]),
            new IndexerCategory(5000, "TV", [new IndexerCategory(5040, "TV/HD", [])]),
        ]);

    public Exception? Error { get; set; }

    public IndexerDefinition? LastIndexer { get; private set; }

    public Task<IndexerCapabilities> GetCapabilitiesAsync(IndexerDefinition indexer, CancellationToken cancellationToken)
    {
        LastIndexer = indexer;
        return Error is null ? Task.FromResult(SampleCaps) : Task.FromException<IndexerCapabilities>(Error);
    }

    public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(IndexerDefinition indexer, SearchCriteria criteria, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReleaseInfo>>([]);

    public Task<IReadOnlyList<ReleaseInfo>> FetchRecentAsync(IndexerDefinition indexer, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReleaseInfo>>([]);

    public Task<byte[]> DownloadNzbAsync(IndexerDefinition indexer, ReleaseInfo release, CancellationToken cancellationToken) =>
        Task.FromResult(Array.Empty<byte>());
}
