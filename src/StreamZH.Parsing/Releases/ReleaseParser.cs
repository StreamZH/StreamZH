// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Quality;
using static StreamZH.Parsing.Releases.EpisodeTitleParser;

namespace StreamZH.Parsing.Releases;

/// <summary>Parses release names into a <see cref="ParsedRelease"/>.</summary>
public static class ReleaseParser
{
    /// <summary>Movie release, e.g. "Dune.Part.Two.2024.German.DL.2160p.WEB.H265-GROUP". Null if it is not a movie name.</summary>
    /// <remarks>Names with clear season/episode info ("S01E02", "S02", "4x07", "2026.10.16") are episodes and return null.</remarks>
    public static ParsedRelease? ParseMovie(string releaseTitle)
    {
        if (string.IsNullOrWhiteSpace(releaseTitle))
        {
            return null;
        }

        if (EpisodeTitleParser.Parse(releaseTitle, out var confidence) is not null && confidence == EpisodeConfidence.Strong)
        {
            return null;
        }

        return MovieTitleParser.Parse(releaseTitle);
    }

    /// <summary>Series release, e.g. "Show.S01E02.1080p.WEB.H264-GROUP". Null if no season/episode can be found.</summary>
    public static ParsedRelease? ParseEpisode(string releaseTitle) =>
        string.IsNullOrWhiteSpace(releaseTitle) ? null : EpisodeTitleParser.Parse(releaseTitle, out _);

    /// <summary>
    /// Tries series first (S01E02 is unambiguous), then movie. Episode matches from ambiguous patterns lose against
    /// a movie parse: "Blade.Runner.2049.2017" looks like 1103-style S20E17, "[Group] Title 1993 (1993)" like episode 1993.
    /// </summary>
    public static ParsedRelease? Parse(string releaseTitle)
    {
        if (string.IsNullOrWhiteSpace(releaseTitle))
        {
            return null;
        }

        var episode = EpisodeTitleParser.Parse(releaseTitle, out var confidence);
        if (episode is not null && confidence == EpisodeConfidence.Strong)
        {
            return episode;
        }

        var movie = MovieTitleParser.Parse(releaseTitle);
        if (movie is null || episode is null)
        {
            return movie ?? episode;
        }

        // "series.2009.416.hdtv": the year sits in the series title in front of the episode number, so it is not a movie year.
        if (episode.Year > 0 && episode.Year == movie.Year)
        {
            return episode;
        }

        // "[Group] Title 1993 (1993)", "[BD]Title.2008.2023.1080p": an absolute "episode" that looks like a year is a year.
        if (confidence == EpisodeConfidence.AbsoluteOnly)
        {
            return movie.Year > 0 && episode.AbsoluteEpisodeNumbers.Any(n => n == movie.Year || n is >= 1900 and <= 2099) ? movie : episode;
        }

        return movie;
    }
}
