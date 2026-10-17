// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Decisions;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Search;

/// <summary>
/// Does a search result belong to the movie or series that was searched, and to the requested episodes?
/// Ids the indexer sends count as a match even when the title differs (localized titles), like Radarr and Sonarr.
/// </summary>
public static class ReleaseMatching
{
    public static ReleaseMatch MatchMovie(ParsedRelease parsed, Movie movie, ReleaseInfo? info = null)
    {
        if (info is not null && MovieIdsMatch(info, movie))
        {
            return ReleaseMatch.Match;
        }

        return ReleaseMatcher.Match(parsed, isSeries: false, [movie.Title, movie.OriginalTitle], movie.Year, movie.ImdbId, movie.TmdbId);
    }

    public static ReleaseMatch MatchSeries(ParsedRelease parsed, Series series, ReleaseInfo? info = null)
    {
        if (info is not null && SeriesIdsMatch(info, series))
        {
            return ReleaseMatch.Match;
        }

        return ReleaseMatcher.Match(parsed, isSeries: true, [series.Title, series.OriginalTitle], series.Year);
    }

    /// <summary>A readable <see cref="RejectionCodes.WrongMedia"/> rejection, or null for a match.</summary>
    public static Rejection? MismatchRejection(ReleaseMatch match, ParsedRelease parsed, string targetName)
    {
        var named = parsed.Year > 0 ? $"{parsed.Title} ({parsed.Year})" : parsed.Title;
        var message = match switch
        {
            ReleaseMatch.WrongType => parsed.IsEpisode ? $"This is a series release, {targetName} is a movie" : $"This is a movie release, {targetName} is a series",
            ReleaseMatch.WrongTitle => $"The release is for {named}, not {targetName}",
            ReleaseMatch.WrongYear => $"Same title, but the release year {parsed.Year} does not fit {targetName}",
            _ => null,
        };

        return message is null ? null : new Rejection(RejectionCodes.WrongMedia, message);
    }

    /// <summary>
    /// The library episodes a series release covers: one or more episodes, a season pack or a daily episode.
    /// Returns a rejection when the release cannot be mapped (multi-season pack, unknown episode).
    /// </summary>
    public static (IReadOnlyList<Episode> Episodes, Rejection? Rejection) MapEpisodes(ParsedRelease parsed, IReadOnlyList<Episode> library)
    {
        if (parsed.MultiSeason)
        {
            return ([], new Rejection(RejectionCodes.MultiSeason, "Multi-season pack"));
        }

        if (parsed.AirDate is { } airDate)
        {
            var daily = library.Where(e => e.AirDate == airDate).ToList();
            return daily.Count > 0 ? (daily, null) : ([], new Rejection(RejectionCodes.UnknownEpisode, $"No episode aired on {airDate}"));
        }

        if (parsed.SeasonNumber is not { } season)
        {
            return ([], new Rejection(RejectionCodes.UnknownEpisode, "Absolute episode numbers are not supported yet"));
        }

        if (parsed.FullSeason)
        {
            var pack = library.Where(e => e.SeasonNumber == season).ToList();
            return pack.Count > 0 ? (pack, null) : ([], new Rejection(RejectionCodes.UnknownEpisode, $"Season {season} is not in the library"));
        }

        var episodes = library.Where(e => e.SeasonNumber == season && parsed.EpisodeNumbers.Contains(e.EpisodeNumber)).ToList();
        return episodes.Count > 0
            ? (episodes, null)
            : ([], new Rejection(RejectionCodes.UnknownEpisode, $"{Label(season, parsed.EpisodeNumbers)} is not in the library"));
    }

    /// <summary>
    /// Sonarr's SeasonMatch, SingleEpisodeSearchMatch and EpisodeRequested specifications in one check:
    /// the release must be for the searched season; an episode search takes no season packs and needs at least one requested episode.
    /// </summary>
    public static Rejection? CheckRequested(
        ParsedRelease parsed, IReadOnlyList<Episode> covered, int seasonNumber, IReadOnlySet<int> requestedIds, bool seasonSearch)
    {
        if (!covered.Any(e => e.SeasonNumber == seasonNumber))
        {
            var found = covered.Select(e => e.SeasonNumber).Distinct().Order().Select(s => $"S{s:00}");
            return new Rejection(RejectionCodes.WrongSeason, $"Wrong season ({string.Join(", ", found)}, searched S{seasonNumber:00})");
        }

        if (seasonSearch)
        {
            return null;
        }

        if (parsed.FullSeason)
        {
            return new Rejection(RejectionCodes.FullSeason, "Full season pack");
        }

        if (!covered.Any(e => requestedIds.Contains(e.Id)))
        {
            var numbers = covered.Where(e => e.SeasonNumber == seasonNumber).Select(e => e.EpisodeNumber).Order().ToList();
            return new Rejection(RejectionCodes.WrongEpisode, $"Episode was not requested: {Label(seasonNumber, numbers)}");
        }

        return null;
    }

    /// <summary>"S01E02", "S01E02-E03".</summary>
    public static string Label(int season, IReadOnlyList<int> episodes) => episodes.Count switch
    {
        0 => $"S{season:00}",
        1 => $"S{season:00}E{episodes[0]:00}",
        _ => $"S{season:00}E{episodes.Min():00}-E{episodes.Max():00}",
    };

    private static bool MovieIdsMatch(ReleaseInfo info, Movie movie) =>
        (info.TmdbId > 0 && info.TmdbId == movie.TmdbId) || ImdbEquals(info.ImdbId, movie.ImdbId);

    private static bool SeriesIdsMatch(ReleaseInfo info, Series series) =>
        (info.TvdbId > 0 && info.TvdbId == series.TvdbId)
        || (info.TmdbId > 0 && info.TmdbId == series.TmdbId)
        || ImdbEquals(info.ImdbId, series.ImdbId);

    /// <summary>Newznab sends IMDb ids with or without "tt" and leading zeros ("tt0111161" = "111161").</summary>
    private static bool ImdbEquals(string? a, string? b)
    {
        var x = ImdbNumber(a);
        return x > 0 && x == ImdbNumber(b);
    }

    private static long ImdbNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var digits = value.Trim();
        if (digits.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }
}
