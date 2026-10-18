// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Decisions;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Parsing;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Search;

/// <summary>What the Search page asks for. Empty <see cref="IndexerIds"/> means every indexer enabled for interactive search.</summary>
public sealed record FreeSearchRequest(string? Query, TextSearchType Type, IReadOnlyList<int> Categories, IReadOnlyList<int> IndexerIds);

/// <summary>One result of a free search, with quality and languages read from the name (display only, nothing is decided).</summary>
public sealed record FreeSearchRelease(ReleaseInfo Release, Quality Quality, IReadOnlyList<Language> Languages)
{
    /// <summary>Custom formats of the media type the name looks like (episode → series formats, else movie formats).</summary>
    public IReadOnlyList<CustomFormat> CustomFormats { get; init; } = [];
}

/// <summary>Results newest first, how many indexers were asked and which of them failed.</summary>
public sealed record FreeSearchResult(IReadOnlyList<FreeSearchRelease> Releases, int IndexerCount, IReadOnlyList<IndexerSearchFailure> Failures);

/// <summary>
/// Searches indexers by free text without a library item (like Prowlarr's Search page). Results go into the
/// <see cref="ReleaseCache"/> without a media type, so <see cref="GrabService"/> can grab them by (indexer id, guid)
/// and records the grab in the history with no movie or series.
/// </summary>
public sealed class FreeSearchService(
    IIndexerProvider indexers,
    IIndexerClient client,
    ReleaseCache cache,
    GrabService grab,
    ILogger<FreeSearchService> logger,
    CustomFormatRepository? customFormats = null)
{
    /// <summary>The longest query passed on to the indexers.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>How long one indexer may take before it counts as failed; the others are not held up.</summary>
    public TimeSpan IndexerTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public async Task<FreeSearchResult> SearchAsync(FreeSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var source = IndexerCallContext.Begin(IndexerCallSource.Search);
        var query = (request.Query ?? string.Empty).Trim();
        if (query.Length > MaxQueryLength)
        {
            throw new LibraryException($"The search text may be at most {MaxQueryLength} characters long.", LibraryError.Invalid);
        }

        var wanted = request.IndexerIds.ToHashSet();
        var active = indexers.Active(IndexerUse.InteractiveSearch)
            .Where(i => wanted.Count == 0 || wanted.Contains(i.Id))
            .ToList();
        if (active.Count == 0)
        {
            logger.LogInformation("No indexer is enabled for interactive search (or none of the chosen ones); nothing to search");
            return new FreeSearchResult([], 0, []);
        }

        var criteria = new TextSearchCriteria
        {
            Titles = query.Length > 0 ? [query] : [],
            Query = query,
            Type = request.Type,
            Categories = [.. request.Categories.Where(c => c > 0).Distinct()],
        };

        var answers = await Task.WhenAll(active.Select(indexer => QueryOneAsync(indexer, criteria, cancellationToken)));

        var seen = new HashSet<(int, string)>();
        var releases = answers
            .SelectMany(a => a.Releases)
            .Where(r => seen.Add((r.IndexerId, r.Guid)))
            .OrderByDescending(r => r.PublishDate)
            .ThenBy(r => r.IndexerPriority)
            .ToList();
        var failures = answers.Where(a => a.Failure is not null).Select(a => a.Failure!).ToList();

        var subject = new SearchSubject(null, null, null, query.Length > 0 ? $"search \"{query}\"" : "search (newest releases)");
        var results = new List<FreeSearchRelease>(releases.Count);
        var decisions = new List<ReleaseDecision>(releases.Count);
        var formats = new Dictionary<MediaType, IReadOnlyList<CustomFormat>>();
        foreach (var info in releases)
        {
            var parsed = Parse(info, request.Type);
            var quality = parsed?.Quality.Quality ?? QualityParser.ParseQuality(info.Title).Quality;
            var languages = parsed?.Languages ?? LanguageParser.ParseLanguages(info.Title);
            results.Add(new FreeSearchRelease(info, quality, [.. languages.Where(l => l != Language.Unknown)])
            {
                CustomFormats = FormatsOf(info, parsed, formats),
            });

            var candidate = new ReleaseCandidate(
                parsed ?? new ParsedRelease { ReleaseTitle = info.Title, Title = info.Title, CleanTitle = string.Empty },
                info.Size, info.Indexer, info.IndexerPriority, info.PublishDate, info.DownloadUrl, info.Guid);
            decisions.Add(new ReleaseDecision(info, parsed, new Decision(candidate, []), subject, []));
        }

        cache.Store(decisions);
        logger.LogInformation(
            "Free search for '{Query}' ({Type}) found {Count} releases on {Indexers} indexers ({Failed} failed)",
            query, request.Type, results.Count, active.Count, failures.Count);
        return new FreeSearchResult(results, active.Count, failures);
    }

    /// <summary>Grabs a release from the latest free search results (see <see cref="GrabService.GrabAsync(int, string, CancellationToken)"/>).</summary>
    /// <param name="categoryName">Download category for the built-in engine; null = "other".</param>
    public async Task<GrabResult> GrabAsync(int indexerId, string guid, CancellationToken cancellationToken = default, string? categoryName = null)
    {
        using var source = IndexerCallContext.Begin(IndexerCallSource.Search);
        return await grab.GrabAsync(indexerId, guid, categoryName, cancellationToken);
    }

    private IReadOnlyList<CustomFormat> FormatsOf(ReleaseInfo info, ParsedRelease? parsed, Dictionary<MediaType, IReadOnlyList<CustomFormat>> cache)
    {
        if (customFormats is null || parsed is null)
        {
            return [];
        }

        var type = parsed.IsEpisode ? MediaType.Series : MediaType.Movie;
        if (!cache.TryGetValue(type, out var all))
        {
            all = customFormats.ForMediaType(type);
            cache[type] = all;
        }

        // No library item: the original language is unknown, so "Original" conditions only match a tagged original track.
        return all.Count == 0 ? [] : CustomFormatCalculator.Matching(all, CustomFormatInput.ForRelease(parsed, info.Size, type, Language.Unknown));
    }

    /// <summary>TV categories or a TV search read the name as an episode first, everything else as a movie first.</summary>
    internal static ParsedRelease? Parse(ReleaseInfo info, TextSearchType type)
    {
        var tv = type == TextSearchType.Tv
            || (type == TextSearchType.Search && info.Categories.Count > 0 && info.Categories.All(c => c is >= 5000 and < 6000));
        if (tv)
        {
            return ReleaseParser.ParseEpisode(info.Title) ?? ReleaseParser.Parse(info.Title);
        }

        return type == TextSearchType.Movie
            ? ReleaseParser.ParseMovie(info.Title) ?? ReleaseParser.Parse(info.Title)
            : ReleaseParser.Parse(info.Title);
    }

    private async Task<(IReadOnlyList<ReleaseInfo> Releases, IndexerSearchFailure? Failure)> QueryOneAsync(
        IndexerDefinition indexer, TextSearchCriteria criteria, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IndexerTimeout);
        try
        {
            // WaitAsync: a client that ignores the token still cannot hold up the whole search.
            var found = await client.SearchAsync(indexer, criteria, timeout.Token).WaitAsync(timeout.Token);
            indexers.RecordSuccess(indexer.Id);
            return ([.. found.Select(r => r with { IndexerId = indexer.Id, Indexer = indexer.Name, IndexerPriority = indexer.Priority })], null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(indexer, new TimeoutException($"No answer within {IndexerTimeout.TotalSeconds:0} seconds"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(indexer, ex);
        }
    }

    private (IReadOnlyList<ReleaseInfo>, IndexerSearchFailure) Failed(IndexerDefinition indexer, Exception error)
    {
        indexers.RecordFailure(indexer.Id, error);
        logger.LogWarning("Indexer {Indexer} failed: {Message}", indexer.Name, error.Message);
        return ([], new IndexerSearchFailure(indexer.Id, indexer.Name, error.Message));
    }
}
