// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>
/// Matches Sonarr's episodes (TheTVDB numbering) to StreamZH's (TMDB numbering). Standard series: season and episode
/// number, then the air date when it is unique on both sides. Anime: the absolute number first (TheTVDB and TMDB often
/// split anime seasons differently), then season/episode, then air date. Daily shows: the air date first. Each StreamZH
/// episode is used once.
/// </summary>
public static class EpisodeMatcher
{
    /// <summary>Sonarr episode id → StreamZH episode.</summary>
    public static IReadOnlyDictionary<int, Episode> Match(SeriesType seriesType, IEnumerable<ArrEpisode> sonarr, IReadOnlyList<Episode> local)
    {
        ArgumentNullException.ThrowIfNull(sonarr);
        ArgumentNullException.ThrowIfNull(local);
        var bySeasonEpisode = local.GroupBy(e => (e.SeasonNumber, e.EpisodeNumber)).ToDictionary(g => g.Key, g => g.First());
        var byAbsolute = local.Where(e => e.SeasonNumber > 0 && EpisodeNumbering.SearchAbsolute(e) is not null)
            .GroupBy(e => EpisodeNumbering.SearchAbsolute(e)!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var byAbsoluteCounted = local.Where(e => e.SeasonNumber > 0 && e.AbsoluteEpisodeNumber is not null)
            .GroupBy(e => e.AbsoluteEpisodeNumber!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var byAirDate = UniqueByAirDate(local.Where(e => e.SeasonNumber > 0));

        var sonarrList = sonarr.ToList();
        var sonarrAirDates = sonarrList.Where(e => e.SeasonNumber > 0 && !string.IsNullOrEmpty(e.AirDate))
            .GroupBy(e => e.AirDate!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var used = new HashSet<int>();
        var result = new Dictionary<int, Episode>();

        Episode? BySeasonEpisode(ArrEpisode e) => bySeasonEpisode.GetValueOrDefault((e.SeasonNumber, e.EpisodeNumber));

        Episode? ByAbsolute(ArrEpisode e) =>
            e.SeasonNumber > 0 && e.AbsoluteEpisodeNumber is { } absolute
                ? byAbsoluteCounted.GetValueOrDefault(absolute) ?? byAbsolute.GetValueOrDefault(absolute)
                : null;

        Episode? ByAirDate(ArrEpisode e) =>
            e.SeasonNumber > 0 && e.AirDate is { Length: > 0 } date && sonarrAirDates.Contains(date) ? byAirDate.GetValueOrDefault(date) : null;

        var strategies = seriesType switch
        {
            SeriesType.Anime => new Func<ArrEpisode, Episode?>[] { ByAbsolute, BySeasonEpisode, ByAirDate },
            SeriesType.Daily => [ByAirDate, BySeasonEpisode],
            _ => [BySeasonEpisode, ByAirDate],
        };

        // Specials only match by season 0 and number; everything else in the order of the series type.
        foreach (var episode in sonarrList.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber))
        {
            foreach (var strategy in episode.SeasonNumber == 0 ? [BySeasonEpisode] : strategies)
            {
                if (strategy(episode) is { } match && used.Add(match.Id))
                {
                    result[episode.Id] = match;
                    break;
                }
            }
        }

        return result;
    }

    private static Dictionary<string, Episode> UniqueByAirDate(IEnumerable<Episode> episodes) =>
        episodes.Where(e => !string.IsNullOrEmpty(e.AirDate))
            .GroupBy(e => e.AirDate!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
}
