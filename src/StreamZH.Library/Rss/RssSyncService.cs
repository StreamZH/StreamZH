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
public sealed record RssSyncResult(int Indexers, int FailedIndexers, int Releases, int Matched, int Accepted, IReadOnlyList<string> Grabbed, int SkippedPending)
{
    public static readonly RssSyncResult Empty = new(0, 0, 0, 0, 0, [], 0);
}

/// <summary>
/// RSS sync like Radarr/Sonarr's RssSyncService: fetches the newest releases of every indexer enabled for RSS, matches
/// them to the library, decides with the release rules (monitored and wanted: missing, or an upgrade below the cutoff;
/// minimum availability for movies) and grabs the best release per movie and per set of episodes. Items already in the
/// download queue (or grabbed recently, blackhole folder) are skipped; query and grab limits are respected by the
/// tracked indexer client.
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
    AutomationHold? automationHold = null)
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

        var now = _time.GetUtcNow();
        var defs = definitions.All().ToDictionary(d => d.QualityId);
        var rules = options.Rules();
        var pending = new PendingGrabs(history, downloadSettings, queue, _time, pendingImports);
        var index = new LibraryIndex(
            movies.All(), series.All(), alternativeTitles is null ? null : new AlternativeTitleLookup([.. alternativeTitles.All(MediaType.Movie), .. alternativeTitles.All(MediaType.Series)]));
        var run = new Run();

        var movieReleases = new Dictionary<int, (Movie Movie, List<(ReleaseInfo Info, ParsedRelease Parsed)> Items)>();
        var seriesReleases = new Dictionary<int, (Series Show, List<(ReleaseInfo Info, ParsedRelease Parsed)> Items)>();
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

        var result = new RssSyncResult(active.Count, failed, releases.Count, run.Matched, run.Accepted, run.Grabbed, run.SkippedPending);
        logger.LogInformation(
            "RSS sync: {Releases} releases from {Indexers} indexers ({Failed} failed), {Matched} for the library, {Accepted} accepted, "
            + "{Grabbed} grabbed, {Pending} skipped (already {Where})",
            result.Releases, result.Indexers, result.FailedIndexers, result.Matched, result.Accepted, result.Grabbed.Count, result.SkippedPending,
            pending.UsesQueue ? "in the download queue" : "grabbed recently");
        return result;
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
            target = targets.ForMovie(movie) with { AvailableFrom = options.AvailableFrom(movie, now) };
        }
        catch (LibraryException ex)
        {
            logger.LogWarning("RSS sync: '{Title}' skipped: {Message}", movie.Title, ex.Message);
            return;
        }

        var candidates = items.Select(i => (i.Info, Candidate: ReleaseSearchService.Candidate(i.Info, i.Parsed))).ToList();
        var accepted = DecisionEngine.Evaluate(candidates.Select(c => c.Candidate), target, defs, now, rules).Where(d => d.Accepted).ToList();
        run.Accepted += accepted.Count;
        if (accepted.Count == 0)
        {
            return;
        }

        if (pending.MovieSkipReason(movie.Id) is not null)
        {
            logger.LogInformation("RSS sync: '{Title}' is already on its way, not grabbed again", movie.Title);
            run.SkippedPending++;
            return;
        }

        var subject = new SearchSubject(MediaType.Movie, movie.Id, null, movie.Title);
        var failedIndexers = new HashSet<int>();
        foreach (var decision in accepted)
        {
            var info = candidates.First(c => ReferenceEquals(c.Candidate, decision.Candidate)).Info;
            if (failedIndexers.Contains(info.IndexerId))
            {
                continue;
            }

            if (await TryGrabAsync(new ReleaseDecision(info, decision.Candidate.Release, decision, subject, []), cancellationToken))
            {
                run.Grabbed.Add(info.Title);
                return;
            }

            failedIndexers.Add(info.IndexerId);
        }
    }

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

            group.Candidates.Add((info, ReleaseSearchService.Candidate(info, parsed)));
        }

        var accepted = new List<ReleaseDecision>();
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
            foreach (var decision in DecisionEngine.Evaluate(candidates.Select(c => c.Candidate), target, defs, now, rules).Where(d => d.Accepted))
            {
                var info = candidates.First(c => ReferenceEquals(c.Candidate, decision.Candidate)).Info;
                accepted.Add(new ReleaseDecision(info, decision.Candidate.Release, decision, subject, ids));
            }
        }

        run.Accepted += accepted.Count;
        if (accepted.Count == 0)
        {
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
        public List<string> Grabbed { get; } = [];
        public HashSet<int> GrabbedEpisodes { get; } = [];
    }
}
