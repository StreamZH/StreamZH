// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Quality;

namespace StreamZH.Parsing.Releases;

/// <summary>Result of checking a parsed release against one library item.</summary>
public enum ReleaseMatch
{
    Match,

    /// <summary>A series release for a movie or the other way round.</summary>
    WrongType,

    /// <summary>The title (clean title) matches none of the item's titles.</summary>
    WrongTitle,

    /// <summary>The title matches, the year does not.</summary>
    WrongYear,
}

/// <summary>
/// Does a release belong to a movie or series? Ids in the name (IMDb, TMDB) decide first; otherwise the clean title
/// must match the title or the original title, and a year in the name must be within one year
/// (Radarr does the same: German and US releases often carry different years).
/// </summary>
public static class ReleaseMatcher
{
    public static ReleaseMatch Match(
        ParsedRelease release,
        bool isSeries,
        IEnumerable<string> titles,
        int year,
        string? imdbId = null,
        int? tmdbId = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(titles);

        if (release.IsEpisode != isSeries)
        {
            return ReleaseMatch.WrongType;
        }

        if (!isSeries)
        {
            if (tmdbId is > 0 && release.TmdbId is > 0)
            {
                return release.TmdbId == tmdbId ? ReleaseMatch.Match : ReleaseMatch.WrongTitle;
            }

            if (!string.IsNullOrEmpty(imdbId) && !string.IsNullOrEmpty(release.ImdbId))
            {
                return string.Equals(release.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase) ? ReleaseMatch.Match : ReleaseMatch.WrongTitle;
            }
        }

        var clean = release.CleanTitle;
        var titleMatches = titles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(TitleNormalizer.Clean)
            .Any(t => t.Length > 0 && (t == clean || (isSeries && year > 0 && t + year == clean)));

        if (!titleMatches)
        {
            return ReleaseMatch.WrongTitle;
        }

        // Series names carry a year only to tell remakes apart ("Doctor.Who.2005"); then it has to be exact.
        if (release.Year > 0 && year > 0)
        {
            var tolerance = isSeries ? 0 : 1;
            if (Math.Abs(release.Year - year) > tolerance)
            {
                return ReleaseMatch.WrongYear;
            }
        }

        return ReleaseMatch.Match;
    }
}
