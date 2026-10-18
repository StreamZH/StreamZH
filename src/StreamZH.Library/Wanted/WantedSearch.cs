// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Library.Wanted;

/// <summary>
/// The search buttons of Wanted ("Search selected", "Search all missing", "Search all cutoff unmet") as background jobs
/// of the job queue. Every search goes through <see cref="AutomaticSearchService"/>, so items in the download queue are
/// skipped and the decision engine decides (upgrades only for cutoff unmet). Job keys match the search commands, so the
/// same search queued twice runs once.
/// </summary>
public sealed class WantedSearch(JobQueue jobs, MovieRepository movies, SeriesRepository series, WantedQueries queries)
{
    public const string MissingMoviesKey = "search-missing-movies";
    public const string MissingEpisodesKey = "search-missing-episodes";
    public const string CutoffMoviesKey = "search-cutoff-movies";
    public const string CutoffEpisodesKey = "search-cutoff-episodes";

    /// <summary>Searches the movies and episodes (unknown ids are ignored). Returns how many jobs were queued.</summary>
    public int Selected(IReadOnlyCollection<int> movieIds, IReadOnlyCollection<int> episodeIds)
    {
        var queued = 0;
        var found = movieIds.Distinct().Order().Select(movies.Find).OfType<Core.Media.Movie>().ToList();
        if (found.Count > 0)
        {
            var ids = found.Select(m => m.Id).ToList();
            queued += jobs.Enqueue(new Job(
                $"search-movies:{string.Join(',', ids)}",
                found.Count == 1 ? $"Search {found[0].Title}" : $"Search {found.Count} movies",
                (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchMoviesAsync(ids, ct))) ? 1 : 0;
        }

        var wanted = episodeIds.ToHashSet();
        if (wanted.Count > 0)
        {
            foreach (var group in queries.AllEpisodes().Where(e => wanted.Contains(e.Id)).GroupBy(e => e.SeriesId))
            {
                var seriesId = group.Key;
                var ids = group.Select(e => e.Id).Order().ToList();
                var title = series.Find(seriesId)?.Title ?? "series";
                queued += jobs.Enqueue(new Job(
                    $"search-episodes:{seriesId}:{string.Join(',', ids)}",
                    ids.Count == 1 ? $"Search {title} episode" : $"Search {ids.Count} episodes of {title}",
                    (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchEpisodesAsync(seriesId, ids, ct))) ? 1 : 0;
            }
        }

        return queued;
    }

    /// <summary>"Search all missing": the list is worked out when the job runs, with the default filters.</summary>
    public int AllMissing(WantedItemType? type)
    {
        var queued = 0;
        if (type is null or WantedItemType.Movies)
        {
            queued += jobs.Enqueue(new Job(
                MissingMoviesKey, "Search missing movies",
                (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchMissingMoviesAsync(ct))) ? 1 : 0;
        }

        if (type is null or WantedItemType.Episodes)
        {
            queued += jobs.Enqueue(new Job(MissingEpisodesKey, "Search missing episodes", SearchMissingEpisodesAsync)) ? 1 : 0;
        }

        return queued;
    }

    /// <summary>"Search all cutoff unmet": movies and episodes whose file is below the profile, searched for an upgrade.</summary>
    public int AllCutoffUnmet(WantedItemType? type)
    {
        var queued = 0;
        if (type is null or WantedItemType.Movies)
        {
            queued += jobs.Enqueue(new Job(CutoffMoviesKey, "Search cutoff unmet movies", async (sp, ct) =>
            {
                var ids = sp.GetRequiredService<WantedService>().AllCutoffUnmet(WantedItemType.Movies).Select(r => r.Id).ToList();
                if (ids.Count > 0)
                {
                    await sp.GetRequiredService<AutomaticSearchService>().SearchMoviesAsync(ids, ct);
                }
            })) ? 1 : 0;
        }

        if (type is null or WantedItemType.Episodes)
        {
            queued += jobs.Enqueue(new Job(CutoffEpisodesKey, "Search cutoff unmet episodes", async (sp, ct) =>
            {
                var search = sp.GetRequiredService<AutomaticSearchService>();
                foreach (var group in sp.GetRequiredService<WantedService>().AllCutoffUnmet(WantedItemType.Episodes).GroupBy(r => r.SeriesId!.Value))
                {
                    ct.ThrowIfCancellationRequested();
                    await search.SearchEpisodesAsync(group.Key, [.. group.Select(r => r.Id)], ct);
                }
            })) ? 1 : 0;
        }

        return queued;
    }

    /// <summary>
    /// Like the scheduled missing search: per monitored series a season with several gaps is searched as a season,
    /// single episodes on their own; monitored specials are searched by episode.
    /// </summary>
    private static async Task SearchMissingEpisodesAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var search = services.GetRequiredService<AutomaticSearchService>();
        var missing = services.GetRequiredService<WantedService>().AllMissing(WantedItemType.Episodes);
        foreach (var group in missing.GroupBy(r => r.SeriesId!.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Any(r => r.SeasonNumber > 0))
            {
                await search.SearchMissingEpisodesAsync(group.Key, cancellationToken);
            }

            var specials = group.Where(r => r.SeasonNumber == 0).Select(r => r.Id).ToList();
            if (specials.Count > 0)
            {
                await search.SearchEpisodesAsync(group.Key, specials, cancellationToken);
            }
        }
    }
}
