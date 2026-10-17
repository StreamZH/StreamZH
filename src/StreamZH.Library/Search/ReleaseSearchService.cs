// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.Decisions;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Search;

/// <summary>
/// Searches all active indexers for one movie or a set of episodes and decides about every result,
/// modelled on Radarr/Sonarr's ReleaseSearchService and DownloadDecisionMaker.
/// Every result comes back (rejected ones with their reasons), so the interactive table can show all of them.
/// </summary>
public sealed class ReleaseSearchService(
    IIndexerProvider indexers,
    IIndexerClient client,
    MovieRepository movies,
    SeriesRepository series,
    DecisionTargetFactory targets,
    QualityDefinitionRepository definitions,
    ReleaseCache cache,
    ILogger<ReleaseSearchService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>How long one indexer may take before it counts as failed; the others are not held up.</summary>
    public TimeSpan IndexerTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public async Task<ReleaseSearchResult> SearchMovieAsync(int movieId, IndexerUse use, CancellationToken cancellationToken = default)
    {
        var movie = movies.Find(movieId) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound);

        // An explicit search ignores the monitored flag (Radarr does the same for user-invoked searches).
        var target = targets.ForMovie(movie) with { Monitored = true };
        var subject = new SearchSubject(MediaType.Movie, movie.Id, null, movie.Title);
        var targetName = movie.Year > 0 ? $"{movie.Title} ({movie.Year})" : movie.Title;
        var criteria = new MovieSearchCriteria
        {
            Titles = Titles(movie.Title, movie.OriginalTitle),
            Year = movie.Year,
            TmdbId = movie.TmdbId,
            ImdbId = movie.ImdbId,
        };

        var (releases, indexerCount, failures) = await QueryAsync(use, criteria, cancellationToken);
        var now = _time.GetUtcNow();
        var defs = Definitions();
        var results = new List<Ranked>();
        var candidates = new List<(ReleaseInfo Info, ReleaseCandidate Candidate)>();

        foreach (var info in releases)
        {
            var parsed = ReleaseParser.ParseMovie(info.Title);
            if (parsed is null)
            {
                results.Add(Unparsable(info, subject));
                continue;
            }

            if (ReleaseMatching.MismatchRejection(ReleaseMatching.MatchMovie(parsed, movie, info), parsed, targetName) is { } mismatch)
            {
                results.Add(new Ranked(Decide(info, parsed, [mismatch], subject, []), Tier.NotForTarget));
                continue;
            }

            candidates.Add((info, Candidate(info, parsed)));
        }

        foreach (var decision in DecisionEngine.Evaluate(candidates.Select(c => c.Candidate), target, defs, now))
        {
            var info = candidates.First(c => ReferenceEquals(c.Candidate, decision.Candidate)).Info;
            results.Add(new Ranked(new ReleaseDecision(info, decision.Candidate.Release, decision, subject, []), Tier.Evaluated));
        }

        return Finish(results, target, defs, now, indexerCount, failures);
    }

    /// <summary>
    /// Searches a season (<paramref name="episodeIds"/> null: season packs and single episodes of that season)
    /// or specific episodes of one season (no season packs).
    /// </summary>
    public async Task<ReleaseSearchResult> SearchEpisodesAsync(
        int seriesId, int seasonNumber, IReadOnlyCollection<int>? episodeIds, IndexerUse use, CancellationToken cancellationToken = default)
    {
        var show = series.Find(seriesId) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
        var library = series.Episodes(seriesId);
        var seasonEpisodes = library.Where(e => e.SeasonNumber == seasonNumber).ToList();
        var requested = episodeIds is null ? seasonEpisodes : seasonEpisodes.Where(e => episodeIds.Contains(e.Id)).ToList();
        if (requested.Count == 0)
        {
            throw new LibraryException(
                episodeIds is null ? $"Season {seasonNumber} has no episodes in the library." : "Episode not found in this season.",
                LibraryError.NotFound);
        }

        var seasonSearch = episodeIds is null;
        var requestedIds = requested.Select(e => e.Id).ToHashSet();
        var subject = new SearchSubject(MediaType.Series, null, show.Id, show.Title);
        var targetName = show.Year > 0 ? $"{show.Title} ({show.Year})" : show.Title;
        var criteria = new EpisodeSearchCriteria
        {
            Titles = Titles(show.Title, show.OriginalTitle),
            Year = show.Year,
            TvdbId = show.TvdbId ?? 0,
            TmdbId = show.TmdbId,
            ImdbId = show.ImdbId,
            SeasonNumber = seasonNumber,
            EpisodeNumbers = seasonSearch ? [] : [.. requested.Select(e => e.EpisodeNumber).Order()],
        };

        var (releases, indexerCount, failures) = await QueryAsync(use, criteria, cancellationToken);
        var now = _time.GetUtcNow();
        var defs = Definitions();
        var results = new List<Ranked>();
        var groups = new Dictionary<string, (IReadOnlyList<Episode> Episodes, List<(ReleaseInfo Info, ReleaseCandidate Candidate)> Candidates)>();

        foreach (var info in releases)
        {
            var parsed = ReleaseParser.ParseEpisode(info.Title);
            if (parsed is null)
            {
                results.Add(Unparsable(info, subject));
                continue;
            }

            if (ReleaseMatching.MismatchRejection(ReleaseMatching.MatchSeries(parsed, show, info), parsed, targetName) is { } mismatch)
            {
                results.Add(new Ranked(Decide(info, parsed, [mismatch], subject, []), Tier.NotForTarget));
                continue;
            }

            var (covered, unmapped) = ReleaseMatching.MapEpisodes(parsed, library);
            var coveredIds = covered.Select(e => e.Id).ToList();
            if ((unmapped ?? ReleaseMatching.CheckRequested(parsed, covered, seasonNumber, requestedIds, seasonSearch)) is { } rejection)
            {
                results.Add(new Ranked(Decide(info, parsed, [rejection], subject, coveredIds), Tier.NotForTarget));
                continue;
            }

            // The target depends on the episodes a release covers (count, files on disk), so decide per episode set.
            var key = string.Join(',', coveredIds.Order());
            if (!groups.TryGetValue(key, out var group))
            {
                group = (covered, []);
                groups[key] = group;
            }

            group.Candidates.Add((info, Candidate(info, parsed)));
        }

        foreach (var (episodes, candidates) in groups.Values)
        {
            var target = targets.ForEpisodes(show, episodes) with { Monitored = true };
            var ids = episodes.Select(e => e.Id).ToList();
            foreach (var decision in DecisionEngine.Evaluate(candidates.Select(c => c.Candidate), target, defs, now))
            {
                var info = candidates.First(c => ReferenceEquals(c.Candidate, decision.Candidate)).Info;
                results.Add(new Ranked(new ReleaseDecision(info, decision.Candidate.Release, decision, subject, ids), Tier.Evaluated));
            }
        }

        var rankingTarget = targets.ForEpisodes(show, requested) with { Monitored = true };
        return Finish(results, rankingTarget, defs, now, indexerCount, failures);
    }

    /// <summary>Asks every active indexer in parallel; a slow or failing indexer is recorded and skipped.</summary>
    private async Task<(IReadOnlyList<ReleaseInfo> Releases, int IndexerCount, IReadOnlyList<IndexerSearchFailure> Failures)> QueryAsync(
        IndexerUse use, SearchCriteria criteria, CancellationToken cancellationToken)
    {
        using var source = IndexerCallContext.Begin(use);
        var active = indexers.Active(use);
        if (active.Count == 0)
        {
            logger.LogInformation("No indexer is enabled for {Use}; nothing to search", use);
            return ([], 0, []);
        }

        var answers = await Task.WhenAll(active.Select(indexer => QueryOneAsync(indexer, criteria, cancellationToken)));

        var seen = new HashSet<(int, string)>();
        var releases = answers
            .SelectMany(a => a.Releases)
            .Where(r => seen.Add((r.IndexerId, r.Guid)))
            .ToList();
        var failures = answers.Where(a => a.Failure is not null).Select(a => a.Failure!).ToList();

        logger.LogInformation(
            "Search for '{Title}' found {Count} releases on {Indexers} indexers ({Failed} failed)",
            criteria.Titles.FirstOrDefault(), releases.Count, active.Count, failures.Count);
        return (releases, active.Count, failures);
    }

    private async Task<(IReadOnlyList<ReleaseInfo> Releases, IndexerSearchFailure? Failure)> QueryOneAsync(
        IndexerDefinition indexer, SearchCriteria criteria, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IndexerTimeout);
        try
        {
            // WaitAsync: a client that ignores the token still cannot hold up the whole search.
            var found = await client.SearchAsync(indexer, criteria, timeout.Token).WaitAsync(timeout.Token);
            indexers.RecordSuccess(indexer.Id);

            // The definition is the truth for id, name and priority, whatever the client filled in.
            return ([.. found.Select(r => r with { IndexerId = indexer.Id, Indexer = indexer.Name, IndexerPriority = indexer.Priority })], null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var error = new TimeoutException($"No answer within {IndexerTimeout.TotalSeconds:0} seconds");
            return Failed(indexer, error);
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

    private ReleaseSearchResult Finish(
        List<Ranked> results, DecisionTarget rankingTarget, IReadOnlyDictionary<int, QualityDefinition> defs, DateTimeOffset now,
        int indexerCount, IReadOnlyList<IndexerSearchFailure> failures)
    {
        var comparer = new DecisionComparer(new DecisionContext(rankingTarget, defs, now));
        var ranked = results
            .OrderByDescending(r => r.Decision.Accepted)
            .ThenBy(r => r.Tier)
            .ThenByDescending(r => r.Decision.Decision.Candidate, comparer)
            .Select(r => r.Decision)
            .ToList();

        cache.Store(ranked);
        return new ReleaseSearchResult(ranked, indexerCount, failures);
    }

    private IReadOnlyDictionary<int, QualityDefinition> Definitions() => definitions.All().ToDictionary(d => d.QualityId);

    private static IReadOnlyList<string> Titles(string title, string originalTitle) =>
        [.. new[] { title, originalTitle }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static ReleaseCandidate Candidate(ReleaseInfo info, ParsedRelease parsed) =>
        new(parsed, info.Size, info.Indexer, info.IndexerPriority, info.PublishDate, info.DownloadUrl, info.Guid);

    private static ReleaseDecision Decide(ReleaseInfo info, ParsedRelease parsed, IReadOnlyList<Rejection> rejections, SearchSubject subject, IReadOnlyList<int> episodeIds) =>
        new(info, parsed, new Decision(Candidate(info, parsed), rejections), subject, episodeIds);

    private static Ranked Unparsable(ReleaseInfo info, SearchSubject subject)
    {
        var placeholder = new ParsedRelease { ReleaseTitle = info.Title, Title = info.Title, CleanTitle = string.Empty };
        var decision = new Decision(Candidate(info, placeholder), [new Rejection(RejectionCodes.UnableToParse, "Unable to parse release")]);
        return new Ranked(new ReleaseDecision(info, null, decision, subject, []), Tier.Unparsable);
    }

    /// <summary>Order of rejected results: decided by the engine, then not for this item, then unreadable names.</summary>
    private enum Tier
    {
        Evaluated,
        NotForTarget,
        Unparsable,
    }

    private sealed record Ranked(ReleaseDecision Decision, Tier Tier);
}
