// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;

namespace StreamZH.Library.Wanted;

/// <summary>
/// "Cutoff unmet" (Radarr/Sonarr Wanted): a file the decision engine would still replace with a better release. Uses the
/// same rules as <see cref="UpgradeRules"/>: the file's languages are normalised like the engine does (unknown = the
/// original language), a file in the wrong language for the profile is always unmet (the engine fixes it even with
/// upgrades off), and with upgrades allowed a quality below the cutoff, a custom format score below the profile's cutoff
/// score (Radarr's CustomFormatCutoffNotMet) or a missing dual-language track is unmet.
/// </summary>
public static class CutoffRules
{
    public const string ReasonQuality = "quality";
    public const string ReasonLanguage = "language";
    public const string ReasonDualLanguage = "dualLanguage";
    public const string ReasonFormatScore = "formatScore";

    /// <summary>Why the file does not meet the profile; empty when it does.</summary>
    /// <param name="formatScore">Custom format score of the file in the profile; null skips the custom format cutoff.</param>
    public static IReadOnlyList<string> Unmet(
        QualityProfile profile, QualityModel quality, IReadOnlyList<Language> fileLanguages, Language originalLanguage, int? formatScore = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(fileLanguages);

        var languages = LanguageRules.Normalize(fileLanguages, originalLanguage);
        var wanted = Language.FindById(profile.LanguageId);
        var reasons = new List<string>(2);

        var wrongLanguage = LanguageRules.IsReal(wanted) && !languages.Contains(wanted);
        if (wrongLanguage)
        {
            reasons.Add(ReasonLanguage);
        }

        if (!profile.UpgradeAllowed)
        {
            return reasons;
        }

        if (profile.RankOf(quality.Quality.Id) < UpgradeRules.CutoffRank(profile))
        {
            reasons.Add(ReasonQuality);
        }

        if (formatScore is { } score && UpgradeRules.IsFormatCutoffUnmet(profile, score))
        {
            reasons.Add(ReasonFormatScore);
        }

        if (!wrongLanguage && profile.PreferDualLanguage && LanguageRules.IsReal(wanted)
            && !LanguageRules.IsDualLanguage(languages, profile, originalLanguage))
        {
            reasons.Add(ReasonDualLanguage);
        }

        return reasons;
    }

    /// <summary>The quality the profile stops upgrading at (the best one when the cutoff is not in the profile).</summary>
    public static Quality CutoffQuality(QualityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var rank = UpgradeRules.CutoffRank(profile);
        return rank >= 0 && rank < profile.Items.Count
            ? Quality.FindById(profile.Items[rank].QualityId)
            : Quality.FindById(profile.CutoffQualityId);
    }
}
