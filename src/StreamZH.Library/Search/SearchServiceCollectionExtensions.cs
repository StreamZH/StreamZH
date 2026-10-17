// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Indexers;
using StreamZH.Data;

namespace StreamZH.Library.Search;

public static class SearchServiceCollectionExtensions
{
    /// <summary>
    /// Release search, grabbing and history. Uses <see cref="IIndexerProvider"/> and <see cref="IIndexerClient"/> from the
    /// indexer module; until that is registered, a stand-in without indexers keeps the app working (TryAdd, so the real
    /// registrations win whatever their order, as long as they use Add).
    /// Search and grab are scoped, so a typed HttpClient behind <see cref="IIndexerClient"/> is not held forever; the cache is shared.
    /// </summary>
    public static IServiceCollection AddReleaseSearch(this IServiceCollection services)
    {
        services.TryAddSingleton<IIndexerProvider>(NoIndexers.Instance);
        services.TryAddSingleton<IIndexerClient>(NoIndexers.Instance);
        services.AddSingleton<HistoryRepository>();
        services.AddSingleton<EpisodeLookup>();
        services.AddSingleton<DecisionTargetFactory>();
        services.AddSingleton<ReleaseCache>();
        services.AddSingleton<DownloadSettings>();
        services.AddScoped<ReleaseSearchService>();
        services.AddScoped<GrabService>();
        services.AddScoped<AutomaticSearchService>();
        return services;
    }

    /// <summary>No indexers configured: searches find nothing.</summary>
    private sealed class NoIndexers : IIndexerProvider, IIndexerClient
    {
        public static readonly NoIndexers Instance = new();

        public IReadOnlyList<IndexerDefinition> Active(IndexerUse use) => [];

        public IndexerDefinition? Find(int id) => null;

        public void RecordSuccess(int indexerId)
        {
        }

        public void RecordFailure(int indexerId, Exception error)
        {
        }

        public Task<IndexerCapabilities> GetCapabilitiesAsync(IndexerDefinition indexer, CancellationToken cancellationToken) =>
            throw new IndexerException("Indexer support is not available.");

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(IndexerDefinition indexer, SearchCriteria criteria, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReleaseInfo>>([]);

        public Task<IReadOnlyList<ReleaseInfo>> FetchRecentAsync(IndexerDefinition indexer, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReleaseInfo>>([]);

        public Task<byte[]> DownloadNzbAsync(IndexerDefinition indexer, ReleaseInfo release, CancellationToken cancellationToken) =>
            throw new IndexerException("Indexer support is not available.");
    }
}
