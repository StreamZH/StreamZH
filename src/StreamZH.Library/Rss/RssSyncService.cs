// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Decisions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Search;
using StreamZH.Library.Titles;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Rss;

/// <summary>What one RSS sync did.</summary>
/// <param name="Indexers">Indexers asked (enabled for RSS and not in back-off).</param>
/// <param name="FailedIndexers">Indexers that failed or hit their query limit.</param>
/// <param name="Releases">Releases received (without duplicates).</param>
/// <param name="Matched">Releases that belong to a movie or episodes in the library.</param>
/// <param name="Accepted">Of those, releases the decision engine accepted.</param>
/// <param name="Grabbed">Titles grabbed.</param>
/// <param name="SkippedPending">Library items with an accepted release that were skipped because they are already on their way.</param>
/// <param name="Delayed">0.11: releases kept as pending because their protocol delay has not passed yet.</param>
public sealed record RssSyncResult(int Indexers, int FailedIndexers, int Releases, int Matched, int Accepted, IReadOnlyList<string> Grabbed, int SkippedPending, int Delayed = 0)
{
    public static readonly RssSyncResult Empty = new(0, 0, 0, 0, 0, [], 0);
}

/// <summary>
/// RSS sync like Radarr/Sonarr's RssSyncService: fetches the newest releases of every indexer enabled for RSS, matches
/// them to the library, decides with the release rules (monitored and wanted: missing, or an upgrade below the cutoff;
/// minimum availability for movies) and grabs the best release per movie and per set of episodes. Items already in the
/// download queue (or grabbed recently, blackhole folder) are skipped; query and grab limits are respected by the
/// tracked indexer client. 0.11: releases held back by the protocol delay are kept as pending releases and decided again
/// with every sync (and by the "Process pending releases" task) until the best one is grabbed, like Radarr.
/// </summary>
public sealed class RssSyncService(
    IIndexerProvider indexers,
    IIndexerClient client,
    MovieRepository movies,
    SeriesRepository series,
    DecisionTargetFactory targets,
    QualityDefinitionRepository definitions,
    GrabService grab,
    HistoryRepository history,
    DownloadSettings downloadSettings,
    IndexerOptions options,
    ILogger<RssSyncService> logger,
    IDownloadQueue? queue = null,
    TimeProvider? timeProvider = null,
    PendingImports? pendingImports = null,
    SceneNumbering.EpisodeMapper? episodeMapper = null,
    AlternativeTitleRepository? alternativeTitles = null,
    AutomationHold? automationHold = null,
    PendingReleaseService? pendingReleases = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>How long one indexer may take; the others are not held up.</summary>
    public TimeSpan IndexerTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public async Task<RssSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        // Side-by-side mode: the old apps grab; asking the indexers would only use up their API limits.
        if (automationHold?.IsOn == true)
        {
            logger.LogInformation("RSS sync skipped: side-by-side mode is on");
            return RssSyncResult.Empty;
        }

        using var source = IndexerCallContext.Begin(IndexerCallSource.Rss);
        var active = indexers.Active(IndexerUse.Rss);
        if (active.Count == 0)
        {
            logger.LogInformation("RSS sync: no indexer is enabled for RSS");
            return RssSyncResult.Empty;
        }

        var answers = await Task.WhenAll(active.Select(indexer => FetchAsync(indexer, cancellationToken)));
        var seen = new HashSet<(int, string)>();
        var releases = answers.SelectMany(a => a.Releases).Where(r => seen.Add((r.IndexerId, r.Guid))).ToList();
        var failed = answers.Count(a => a.Failed);
        return await ProcessAsync(releases, active.Count, failed, cancellationToken);
    }

    /// <summary>
    /// Decides the pending releases again without asking the indexers (the "Process pending releases" task): the ones
    /// whose delay has passed are grabbed. Nothing happens without pending releases or in side-by-side mode.
    /// </summary>
    public async Task<RssSyncResult> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        if (pendingReleases?.HasAny != true || automationHold?.IsOn == true)
        {
            return RssSyncResult.Empty;
        }

        using var source = IndexerCallContext.Begin(IndexerCallSource.Rss);
        return await ProcessAsync([], 0, 0, cancellationToken);
    }

    private async Task<RssSyncResult> ProcessAsync(List<ReleaseInfo> releases, int indexerCount, int failed, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var defs = definitions.All().ToDictionary(d => d.QualityId);
        var rules = options.Rules();
        var pending = new PendingGrabs(history, downloadSettings, queue, _time, pendingImports);
        var index = new LibraryIndex(
            movies.All(), series.All(), alternativeTitles is null ? null : new AlternativeTitleLookup([.. alternativeTitles.All(MediaType.Movie), .. alternativeTitles.All(MediaType.Series)]));
        var run = new Run();

        var movieReleases = new Dictionary<int, (Movie Movie, List<(ReleaseInfo Info, ParsedRelease Parsed)> Items)>();
        var seriesReleases = new Dictionary<int, (Series Show, List<(ReleaseInfo Info, ParsedRelease Parsed)> Items)>();

        // Pending releases go to the item they wait for (the fresh copy from this sync wins: newer seeders).
        var waiting = pendingReleases?.All() ?? [];
        var routed = RoutePending(waiting, releases, movieReleases, seriesReleases);
        var fresh = releases.Select(r => (r.IndexerId, r.Guid)).ToHashSet();
        releases = [.. releases.Where(r => !routed.Contains((r.IndexerId, r.Guid)))];

        foreach (var info in releases)
        {
            var parsed = ReleaseParser.Parse(info.Title);
            if (parsed is null)
            {
                continue;
            }

            if (parsed.IsEpisode)
            {
                if (index.FindSeries(parsed, info) is { } show)
                {
                    (seriesReleases.TryGetValue(show.Id, out var entry) ? entry : seriesReleases[show.Id] = (show, [])).Items.Add((info, parsed));
                }
            }
            else if (index.FindMovie(parsed, info) is { } movie)
            {
                (movieReleases.TryGetValue(movie.Id, out var entry) ? entry : movieReleases[movie.Id] = (movie, [])).Items.Add((info, parsed));
            }
            else if (ReleaseParser.ParseEpisode(info.Title) is { } episode && index.FindSeries(episode, info) is { } episodeShow)
            {
                // Names a movie parse wins ("Title.E12.German.1080p", anime with a year) but that belong to a series.
                (seriesReleases.TryGetValue(episodeShow.Id, out var entry) ? entry : seriesReleases[episodeShow.Id] = (episodeShow, [])).Items.Add((info, episode));
            }
        }

        foreach (var (movie, items) in movieReleases.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessMovieAsync(movie, items, defs, rules, now, pending, run, cancellationToken);
        }

        foreach (var (show, items) in seriesReleases.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessSeriesAsync(show, items, defs, rules, now, pending, run, cancellationToken);
        }

        var received = releases.Count + routed.Count(fresh.Contains);
        var result = new RssSyncResult(indexerCount, failed, received, run.Matched, run.Accepted, run.Grabbed, run.SkippedPending, run.Delayed);
        logger.LogInformation(
            "RSS sync: {Releases} releases from {Indexers} indexers ({Failed} failed), {Matched} for the library, {Accepted} accepted, "
            + "{Grabbed} grabbed, {Pending} skipped (already {Where}), {Delayed} waiting for the protocol delay",
            result.Releases, result.Indexers, result.FailedIndexers, result.Matched, result.Accepted, result.Grabbed.Count, result.SkippedPending,
            pending.UsesQueue ? "in the download queue" : "grabbed recently", result.Delayed);
        return result;
    }

    /// <summary>
    /// Adds the pending releases to the items they wait for; a pending release that came again in this sync takes the
    /// fresh copy. Entries of items that are gone are dropped. Returns the (indexer, guid) keys that were routed.
    /// </summary>
    private HashSet<(int, string)> RoutePending(
        IReadOnlyList<PendingRelease> waiting,
        List<ReleaseInfo> releases,
        Dictionary<int, (Movie Movie, List<(ReleaseInfo Info, ParsedRelease Parsed)> Items)> movieReleases,
        Dictionary<int, (Series Show, List<(ReleaseInfo Info, ParsedRelease Parsed)> Items)> seriesReleases)
    {
        var routed = new HashSet<(int, string)>();
        if (waiting.Count == 0)
        {
            return routed;
        }

        var current = releases.GroupBy(r => (r.IndexerId, r.Guid)).ToDictionary(g => g.Key, g => g.First());
        foreach (var entry in waiting)
        {
            var key = (entry.Release.IndexerId, entry.Release.Guid);
            if (!routed.Add(key))
            {
                continue;
            }

            var info = current.GetValueOrDefault(key) ?? entry.Release;
            if (entry.MediaType == MediaType.Movie && entry.MovieId is { } movieId)
            {
                if (movies.Find(movieId) is not { } movie)
                {
                    pendingReleases!.RemoveMovie(movieId);
                    continue;
                }

                if ((ReleaseParser.ParseMovie(info.Title) ?? ReleaseParser.Parse(info.Title)) is { } parsed)
                {
                    (movieReleases.TryGetValue(movie.Id, out var item) ? item : movieReleases[movie.Id] = (movie, [])).Items.Add((info, parsed));
                }
            }
            else if (entry.SeriesId is { } seriesId)
            {
                if (series.Find(seriesId) is not { } show)
                {
                    pendingReleases!.RemoveEpisodes(seriesId, entry.EpisodeIds);
                    continue;
                }

                if (ReleaseParser.ParseEpisode(info.Title) is { } parsed)
                {
                    (seriesReleases.TryGetValue(show.Id, out var item) ? item : seriesReleases[show.Id] = (show, [])).Items.Add((info, parsed));
                }
            }
        }

        return routed;
    }

    private async Task ProcessMovieAsync(
        Movie movie,
        List<(ReleaseInfo Info, ParsedRelease Parsed)> items,
        IReadOnlyDictionary<int, QualityDefinition> defs,
        ReleaseRules rules,
        DateTimeOffset now,
        PendingGrabs pending,
        Run run,
        CancellationToken cancellationToken)
    {
        run.Matched += items.Count;
        DecisionTarget target;
        try
        {
            target = targets.ForMovie(movie) with
            {
                AvailableFrom = options.AvailableFrom(movie, now),
                PendingSince = pendingReleases?.OldestForMovie(movie.Id),
            };
        }
        catch (LibraryException ex)
        {
            logger.LogWarning("RSS sync: '{Title}' skipped: {Message}", movie.Title, ex.Message);
            return;
        }

        var subject = new SearchSubject(MediaType.Movie, movie.Id, null, movie.Title);
        var seeders = MinimumSeeders(items.Select(i => i.Info));
        var candidates = items.Select(i => (i.Info, Candidate: ReleaseSearchService.Candidate(i.Info, i.Parsed, seeders.GetValueOrDefault(i.Info.IndexerId)))).ToList();
        var decisions = DecisionEngine.Evaluate(candidates.Select(c => c.Candidate), target, defs, now, rules)
            .Select(d => new ReleaseDecision(candidates.First(c => ReferenceEquals(c.Candidate, d.Candidate)).Info, d.Candidate.Release, d, subject, []))
            .ToList();
        var accepted = decisions.Where(d => d.Accepted).ToList();
        run.Accepted += accepted.Count;

        if (accepted.Count > 0 && pending.MovieSkipReason(movie.Id) is not null)
        {
            logger.LogInformation("RSS sync: '{Title}' is already on its way, not grabbed again", movie.Title);
            run.SkippedPending++;
            pendingReleases?.RemoveMovie(movie.Id);
            return;
        }

        var failedIndexers = new HashSet<int>();
        foreach (var decision in accepted)
        {
            if (failedIndexers.Contains(decision.Release.IndexerId))
            {
                continue;
            }

            if (await TryGrabAsync(decision, cancellationToken))
            {
                run.Grabbed.Add(decision.Release.Title);
                pendingReleases?.RemoveMovie(movie.Id);
                return;
            }

            failedIndexers.Add(decision.Release.IndexerId);
        }

        // Nothing grabbed: what only waits for the protocol delay stays pending (and replaces older entries of the movie).
        if (pendingReleases is not null)
        {
            var delayed = PendingReleaseService.DelayedOnly(decisions);
            run.Delayed += delayed.Count;
            pendingReleases.ReplaceMovie(movie.Id, delayed);
        }
    }

    /// <summary>"Minimum seeders" of the torrent indexers among the releases.</summary>
    private Dictionary<int, int> MinimumSeeders(IEnumerable<ReleaseInfo> releases) =>
        releases.Where(r => r.Protocol == DownloadProtocol.Torrent).Select(r => r.IndexerId).Distinct()
            .ToDictionary(id => id, id => indexers.Find(id)?.MinimumSeeders ?? 1);

    private async Task ProcessSeriesAsync(
        Series show,
        List<(ReleaseInfo Info, ParsedRelease Parsed)> items,
        IReadOnlyDictionary<int, QualityDefinition> defs,
        ReleaseRules rules,
        DateTimeOffset now,
        PendingGrabs pending,
        Run run,
        CancellationToken cancellationToken)
    {
        var library = series.Episodes(show.Id);
        var subject = new SearchSubject(MediaType.Series, null, show.Id, show.Title);
        var groups = new Dictionary<string, (IReadOnlyList<Episode> Episodes, List<(ReleaseInfo Info, ReleaseCandidate Candidate)> Candidates)>();
        var seeders = MinimumSeeders(items.Select(i => i.Info));

        foreach (var (info, parsed) in items)
        {
            var (covered, rejection) = episodeMapper.MapEpisodes(parsed, show, library);
            if (rejection is not null || covered.Count == 0)
            {
                continue;
            }

            run.Matched++;
            var key = string.Join(',', covered.Select(e => e.Id).Order());
            if (!groups.TryGetValue(key, out var group))
            {
                group = (covered, []);
                groups[key] = group;
            }

            group.Candidates.Add((info, ReleaseSearchService.Candidate(info, parsed, seeders.GetValueOrDefault(info.IndexerId))));
        }

        var accepted = new List<ReleaseDecision>();
        var delayed = new List<ReleaseDecision>();
        var evaluated = new HashSet<int>();
        DecisionTarget? rankingTarget = null;
        foreach (var (episodes, candidates) in groups.Values)
        {
            DecisionTarget target;
            try
            {
                target = targets.ForEpisodes(show, episodes);
            }
            catch (LibraryException ex)
            {
                logger.LogWarning("RSS sync: '{Title}' skipped: {Message}", show.Title, ex.Message);
                return;
            }

            rankingTarget ??= target;
            var ids = episodes.Select(e => e.Id).ToList();
            evaluated.UnionWith(ids);
            target = target with { PendingSince = pendingReleases?.OldestForEpisodes(show.Id, ids) };
            var decided = DecisionEngine.Evaluate(candidates.Select(c => c.Candidate), target, defs, now, rules)
                .Select(d => new ReleaseDecision(candidates.First(c => ReferenceEquals(c.Candidate, d.Candidate)).Info, d.Candidate.Release, d, subject, ids))
                .ToList();
            accepted.AddRange(decided.Where(d => d.Accepted));
            delayed.AddRange(PendingReleaseService.DelayedOnly(decided));
        }

        run.Accepted += accepted.Count;
        if (accepted.Count == 0)
        {
            KeepDelayed(show, evaluated, delayed, new HashSet<int>(), run);
            return;
        }

        // Best first over all episode sets (same series, same profile); a release must not touch episodes already taken.
        var comparer = new DecisionComparer(new DecisionContext(rankingTarget!, defs, now) { Rules = rules });
        var taken = new HashSet<int>(pending.BlockedEpisodes(show.Id));
        var failedIndexers = new HashSet<int>();
        var skipped = false;
        foreach (var decision in accepted.OrderByDescending(d => d.Decision.Candidate, comparer))
        {
            if (decision.EpisodeIds.Any(taken.Contains))
            {
                skipped |= !run.GrabbedEpisodes.Overlaps(decision.EpisodeIds);
                continue;
            }

            if (failedIndexers.Contains(decision.Release.IndexerId))
            {
                continue;
            }

            if (await TryGrabAsync(decision, cancellationToken))
            {
                run.Grabbed.Add(decision.Release.Title);
                taken.UnionWith(decision.EpisodeIds);
                run.GrabbedEpisodes.UnionWith(decision.EpisodeIds);
            }
            else
            {
                failedIndexers.Add(decision.Release.IndexerId);
            }
        }

        if (skipped)
        {
            logger.LogInformation("RSS sync: episodes of '{Title}' are already on their way, not grabbed again", show.Title);
            run.SkippedPending++;
        }

        KeepDelayed(show, evaluated, delayed, taken, run);
    }

    /// <summary>The delayed releases of the evaluated episodes replace their pending entries, except those for episodes taken now.</summary>
    private void KeepDelayed(Series show, HashSet<int> evaluated, List<ReleaseDecision> delayed, IReadOnlySet<int> taken, Run run)
    {
        if (pendingReleases is null || evaluated.Count == 0)
        {
            return;
        }

        var keep = delayed.Where(d => !d.EpisodeIds.Any(taken.Contains)).ToList();
        run.Delayed += keep.Count;
        pendingReleases.ReplaceEpisodes(show.Id, evaluated, keep);
    }

    private async Task<bool> TryGrabAsync(ReleaseDecision decision, CancellationToken cancellationToken)
    {
        using var source = IndexerCallContext.Begin(IndexerCallSource.Rss);
        try
        {
            await grab.GrabAsync(decision, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IndexerException or LibraryException)
        {
            logger.LogWarning("RSS sync: grabbing '{Title}' failed: {Message}", decision.Release.Title, ex.Message);
            return false;
        }
    }

    private async Task<(IReadOnlyList<ReleaseInfo> Releases, bool Failed)> FetchAsync(IndexerDefinition indexer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IndexerTimeout);
        try
        {
            var found = await client.FetchRecentAsync(indexer, timeout.Token).WaitAsync(timeout.Token);
            indexers.RecordSuccess(indexer.Id);
            return ([.. found.Select(r => r with { IndexerId = indexer.Id, Indexer = indexer.Name, IndexerPriority = indexer.Priority })], false);
        }
        catch (IndexerException ex) when (ex.IsLimitReached)
        {
            logger.LogInformation("RSS sync: {Indexer} skipped, {Message}", indexer.Name, ex.Message);
            return ([], true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var error = new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"No answer within {IndexerTimeout.TotalSeconds:0} seconds"));
            indexers.RecordFailure(indexer.Id, error);
            logger.LogWarning("RSS sync: {Indexer} failed: {Message}", indexer.Name, error.Message);
            return ([], true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            indexers.RecordFailure(indexer.Id, ex);
            logger.LogWarning("RSS sync: {Indexer} failed: {Message}", indexer.Name, ex.Message);
            return ([], true);
        }
    }

    private sealed class Run
    {
        public int Matched { get; set; }
        public int Accepted { get; set; }
        public int SkippedPending { get; set; }
        public int Delayed { get; set; }
        public List<string> Grabbed { get; } = [];
        public HashSet<int> GrabbedEpisodes { get; } = [];
    }
}
