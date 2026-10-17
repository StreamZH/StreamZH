// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions;

/// <summary>Stable rejection codes. Names follow Radarr's DownloadRejectionReason where one exists.</summary>
public static class RejectionCodes
{
    public const string NotMonitored = "NotMonitored";
    public const string UnknownQuality = "UnknownQuality";
    public const string QualityNotWanted = "QualityNotWanted";
    public const string WantedLanguage = "WantedLanguage";
    public const string BelowMinimumSize = "BelowMinimumSize";
    public const string AboveMaximumSize = "AboveMaximumSize";
    public const string Sample = "Sample";
    public const string Blocklisted = "Blocklisted";
    public const string DiskHigherPreference = "DiskHigherPreference";
    public const string DiskHigherRevision = "DiskHigherRevision";
    public const string DiskCutoffMet = "DiskCutoffMet";
    public const string DiskUpgradesNotAllowed = "DiskUpgradesNotAllowed";
    public const string DecisionError = "DecisionError";

    /// <summary>The release is for another movie or series (title, year or movie/series mix-up).</summary>
    public const string WrongMedia = "WrongMedia";

    // Search results (Sonarr names): checked before the decision engine; a release with one of these gets only that reason.

    /// <summary>The release name could not be read.</summary>
    public const string UnableToParse = "UnableToParse";

    /// <summary>The episodes the release names are not in the library.</summary>
    public const string UnknownEpisode = "UnknownEpisode";

    /// <summary>The release is for another season than the one searched.</summary>
    public const string WrongSeason = "WrongSeason";

    /// <summary>The release is for episodes that were not searched.</summary>
    public const string WrongEpisode = "WrongEpisode";

    /// <summary>A season pack in a search for single episodes.</summary>
    public const string FullSeason = "FullSeason";

    /// <summary>A pack of several seasons (S01-S03).</summary>
    public const string MultiSeason = "MultiSeason";
}
