// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) its contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;

namespace StreamZH.Library.Search;

/// <summary>
/// The indexer searches for episodes of one series by its type, like Sonarr's SearchDaily, SearchAnime and scene season
/// handling: daily episodes by air date (one search per episode, a season search per air year), anime episodes by season
/// and episode plus their absolute numbers, and series with TheXEM scene numbering by the scene season and episode.
/// </summary>
public static class SeriesSearchCriteria
{
    /// <param name="template">Titles, ids and the TMDB season/episodes; returned as it is when nothing needs changing.</param>
    public static IReadOnlyList<EpisodeSearchCriteria> For(
        Series series, EpisodeSearchCriteria template, IReadOnlyList<Episode> requested, bool seasonSearch)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(requested);

        if (series.SeriesType == SeriesType.Daily && Daily(template, requested, seasonSearch) is { Count: > 0 } daily)
        {
            return daily;
        }

        var anime = series.SeriesType == SeriesType.Anime;
        if (!anime && !EpisodeNumbering.HasSceneNumbering(requested))
        {
            return [template];
        }

        // One search per scene season: TMDB's season 1 may be scene seasons 1 and 2.
        return
        [
            .. requested
                .GroupBy(EpisodeNumbering.SearchSeason)
                .OrderBy(g => g.Key)
                .Select(g => template with
                {
                    SeasonNumber = g.Key,
                    EpisodeNumbers = seasonSearch ? [] : [.. g.Select(EpisodeNumbering.SearchEpisode).Distinct().Order()],
                    AbsoluteEpisodeNumbers = !anime || seasonSearch || g.Key == 0
                        ? []
                        : [.. g.Select(EpisodeNumbering.SearchAbsolute).OfType<int>().Distinct().Order()],
                }),
        ];
    }

    /// <summary>
    /// Sonarr's SearchDaily and SearchDailySeason: each episode by its air date; a season search asks for a whole air year
    /// (season=yyyy) when it has several episodes of that year. Episodes without an air date are left out.
    /// </summary>
    private static List<EpisodeSearchCriteria> Daily(EpisodeSearchCriteria template, IReadOnlyList<Episode> requested, bool seasonSearch)
    {
        var dated = requested
            .Where(e => e.SeasonNumber > 0)
            .Select(e => (Episode: e, Date: ParseDate(e.AirDate)))
            .Where(x => x.Date is not null)
            .ToList();
        var result = new List<EpisodeSearchCriteria>();
        foreach (var year in dated.GroupBy(x => x.Date!.Value.Year).OrderBy(g => g.Key))
        {
            if (seasonSearch && year.Count() > 1)
            {
                result.Add(template with { SeasonNumber = year.Key, EpisodeNumbers = [], AirDate = null });
                continue;
            }

            result.AddRange(year.OrderBy(x => x.Date).Select(x => template with
            {
                SeasonNumber = x.Episode.SeasonNumber,
                EpisodeNumbers = [x.Episode.EpisodeNumber],
                AirDate = x.Date!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }));
        }

        return result;
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
