// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Media;

/// <summary>Where an alternative title comes from.</summary>
public static class AlternativeTitleSources
{
    /// <summary>TMDB alternative titles.</summary>
    public const string Tmdb = "tmdb";

    /// <summary>TMDB translations (e.g. the German title).</summary>
    public const string Translation = "translation";

    /// <summary>Scene names from TheXEM (mostly anime), optionally per scene season.</summary>
    public const string Xem = "xem";

    /// <summary>Added by the user; never removed by a refresh.</summary>
    public const string User = "user";
}

/// <summary>
/// Another name a movie or series is known by, used to match release names and as an extra search term
/// (Radarr's alternative titles, Sonarr's scene mappings).
/// </summary>
public sealed record AlternativeTitle
{
    public int Id { get; init; }

    public required MediaType MediaType { get; init; }

    public required int ItemId { get; init; }

    public required string Title { get; init; }

    /// <summary>Normalised for matching (see the parser's clean title).</summary>
    public required string CleanTitle { get; init; }

    public required string Source { get; init; }

    public int? LanguageId { get; init; }

    /// <summary>Series: the title only applies to this TMDB season (null = all seasons).</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Series: the scene season this name stands for (TheXEM), null = unknown.</summary>
    public int? SceneSeasonNumber { get; init; }
}
