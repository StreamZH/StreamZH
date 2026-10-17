// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Quality;

/// <summary>Everything StreamZH can read from a release name like "Dune.Part.Two.2024.German.DL.2160p.WEB.H265-GROUP".</summary>
public sealed record ParsedRelease
{
    public required string ReleaseTitle { get; init; }

    /// <summary>Movie or series title as written in the release (spaces, no dots).</summary>
    public required string Title { get; init; }

    /// <summary>Lower-case title without punctuation and articles, for matching against the library.</summary>
    public required string CleanTitle { get; init; }

    /// <summary>Movie year, or series year if the release names one. 0 when unknown.</summary>
    public int Year { get; init; }

    // Series only
    public int? SeasonNumber { get; init; }
    public IReadOnlyList<int> EpisodeNumbers { get; init; } = [];
    public IReadOnlyList<int> AbsoluteEpisodeNumbers { get; init; } = [];

    /// <summary>Daily shows: air date as yyyy-MM-dd.</summary>
    public string? AirDate { get; init; }

    /// <summary>Season pack: the whole season, no episode numbers.</summary>
    public bool FullSeason { get; init; }

    /// <summary>Several seasons in one release, e.g. S01-S03.</summary>
    public bool MultiSeason { get; init; }

    public bool IsSpecial { get; init; }

    public bool IsEpisode => SeasonNumber is not null || AirDate is not null || AbsoluteEpisodeNumbers.Count > 0;

    // Shared
    public QualityModel Quality { get; init; } = QualityModel.Unknown;
    public IReadOnlyList<Language> Languages { get; init; } = [];
    public string? ReleaseGroup { get; init; }

    /// <summary>Edition such as "Director's Cut", "Extended", "IMAX".</summary>
    public string? Edition { get; init; }

    public HdrFormat Hdr { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? ImdbId { get; init; }
    public int? TmdbId { get; init; }
    public string? ReleaseHash { get; init; }
}