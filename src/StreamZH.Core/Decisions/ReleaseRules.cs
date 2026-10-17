// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions;

/// <summary>
/// Release rules for all indexers (Settings → Indexers, like Radarr's "Indexers → Options" plus Sonarr's release
/// profiles). <see cref="None"/> switches every rule off; the app reads the real values from the settings.
/// </summary>
public sealed record ReleaseRules
{
    /// <summary>No rules: every option off or unlimited.</summary>
    public static ReleaseRules None { get; } = new();

    /// <summary>Usenet propagation: releases younger than this many minutes are rejected for now (0 = off).</summary>
    public int MinimumAgeMinutes { get; init; }

    /// <summary>Releases older than this many days are rejected (0 = unlimited), like the provider's retention.</summary>
    public int RetentionDays { get; init; }

    /// <summary>Releases larger than this many MB are rejected (0 = unlimited), on top of the quality size limits.</summary>
    public int MaximumSizeMb { get; init; }

    /// <summary>False: releases with hardcoded subtitles (HC, KORSUB …) are rejected unless the tag is whitelisted.</summary>
    public bool AllowHardcodedSubs { get; init; } = true;

    /// <summary>Subtitle tags that are not counted as hardcoded (e.g. "DKSUBS"); case-insensitive.</summary>
    public IReadOnlyList<string> WhitelistedHardcodedSubs { get; init; } = [];

    /// <summary>Must contain, must not contain and preferred terms (only enabled profiles are used).</summary>
    public IReadOnlyList<ReleaseProfile> ReleaseProfiles { get; init; } = [];
}
