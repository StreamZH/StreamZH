// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Quality;

namespace StreamZH.Parsing.Releases;

/// <summary>Parses release names into a <see cref="ParsedRelease"/>.</summary>
public static class ReleaseParser
{
    /// <summary>Movie release, e.g. "Dune.Part.Two.2024.German.DL.2160p.WEB.H265-GROUP". Null if it is not a movie name.</summary>
    public static ParsedRelease? ParseMovie(string releaseTitle) => null;

    /// <summary>Series release, e.g. "Show.S01E02.1080p.WEB.H264-GROUP". Null if no season/episode can be found.</summary>
    public static ParsedRelease? ParseEpisode(string releaseTitle) => null;

    /// <summary>Tries series first (S01E02 is unambiguous), then movie.</summary>
    public static ParsedRelease? Parse(string releaseTitle) => ParseEpisode(releaseTitle) ?? ParseMovie(releaseTitle);
}