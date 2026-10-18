// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;

namespace StreamZH.Core.Profiles;

/// <summary>
/// A custom format group (TRaSH "cf-groups") added to a profile on top of its template, like Recyclarr's
/// <c>custom_format_groups.add</c>: required formats always, default formats minus <see cref="Exclude"/>, plus
/// <see cref="Select"/> (or every format with <see cref="SelectAll"/>).
/// </summary>
public sealed record TrashGroupSelection
{
    public required string TrashId { get; init; }

    public IReadOnlyList<string> Select { get; init; } = [];

    public IReadOnlyList<string> Exclude { get; init; } = [];

    public bool SelectAll { get; init; }
}

/// <summary>A guide format added to a profile explicitly (Recyclarr <c>custom_formats</c>); null score = the guide's score.</summary>
public sealed record TrashExtraFormat(string TrashId, MediaType MediaType, int? Score);

/// <summary>
/// How a quality profile follows its TRaSH templates (<see cref="QualityProfile.TrashMovieTemplate"/>,
/// <see cref="QualityProfile.TrashSeriesTemplate"/>): extra groups and formats, the score set, and the profile fields
/// the user set by hand (null = from the template). Stored per profile by the TRaSH sync.
/// </summary>
public sealed record TrashProfileOptions
{
    /// <summary>Score set to use instead of the template's <c>trash_score_set</c> (Recyclarr <c>score_set</c>).</summary>
    public string? ScoreSet { get; init; }

    public IReadOnlyList<TrashGroupSelection> Groups { get; init; } = [];

    /// <summary>Default groups not to add (Recyclarr <c>custom_format_groups.skip</c>).</summary>
    public IReadOnlyList<string> SkipGroups { get; init; } = [];

    public IReadOnlyList<TrashExtraFormat> ExtraFormats { get; init; } = [];

    /// <summary>Scores of formats the sync did not set are removed (Recyclarr <c>reset_unmatched_scores</c>), except these names.</summary>
    public bool ResetUnmatched { get; init; }

    public IReadOnlyList<string> ResetExcept { get; init; } = [];

    /// <summary>Formats the last sync scored; when one drops out of the template its score is removed again.</summary>
    public IReadOnlyList<int> AppliedFormatIds { get; init; } = [];

    // Profile fields set by hand (Recyclarr YAML or the profile editor); null = from the template.
    public IReadOnlyList<QualityProfileItem>? Items { get; init; }

    public int? CutoffQualityId { get; init; }

    public bool? UpgradeAllowed { get; init; }

    public int? MinFormatScore { get; init; }

    public int? CutoffFormatScore { get; init; }

    public int? MinUpgradeFormatScore { get; init; }

    public int? LanguageId { get; init; }
}
