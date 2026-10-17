// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Indexers;

/// <summary>Talks to one kind of indexer (Newznab). Stateless; the definition is passed in on every call.</summary>
public interface IIndexerClient
{
    /// <summary>Reads t=caps. Throws <see cref="IndexerException"/> on errors (used by the "Test" button).</summary>
    Task<IndexerCapabilities> GetCapabilitiesAsync(IndexerDefinition indexer, CancellationToken cancellationToken);

    /// <summary>Runs a search with the best parameters the indexer supports; results carry the indexer id, name and priority.</summary>
    Task<IReadOnlyList<ReleaseInfo>> SearchAsync(IndexerDefinition indexer, SearchCriteria criteria, CancellationToken cancellationToken);

    /// <summary>Newest releases in the indexer's movie and TV categories (RSS, no query).</summary>
    Task<IReadOnlyList<ReleaseInfo>> FetchRecentAsync(IndexerDefinition indexer, CancellationToken cancellationToken);

    /// <summary>Downloads the NZB file of a release (follows the indexer's download link, adds the API key if needed).</summary>
    Task<byte[]> DownloadNzbAsync(IndexerDefinition indexer, ReleaseInfo release, CancellationToken cancellationToken);
}

/// <summary>Configured indexers plus their health. Failing indexers are skipped for a while (back-off like Prowlarr).</summary>
public interface IIndexerProvider
{
    /// <summary>Indexers enabled for this use and not currently in back-off.</summary>
    IReadOnlyList<IndexerDefinition> Active(IndexerUse use);

    IndexerDefinition? Find(int id);

    void RecordSuccess(int indexerId);

    void RecordFailure(int indexerId, Exception error);
}
