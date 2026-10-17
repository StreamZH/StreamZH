// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;

namespace StreamZH.Core.Decisions;

/// <summary>Why a release is not an upgrade over the file on disk.</summary>
public enum UpgradeVerdict
{
    /// <summary>It is an upgrade (or there is no file).</summary>
    Upgrade,

    /// <summary>The file on disk has a better quality by profile rank.</summary>
    BetterQualityOnDisk,

    /// <summary>Same quality, but the file on disk has a newer revision (e.g. it is already the PROPER).</summary>
    BetterRevisionOnDisk,

    /// <summary>The profile does not allow upgrades.</summary>
    UpgradesNotAllowed,

    /// <summary>The file on disk already meets the profile cutoff.</summary>
    CutoffMet,

    /// <summary>Same quality and revision, nothing gained.</summary>
    NotBetter,
}

/// <summary>
/// Upgrade logic, modelled on Radarr's UpgradableSpecification (without custom formats). Order of checks:
/// <list type="number">
/// <item>The file has the wrong language for the profile and the release has the right one → always
/// (a German version replaces an English or subbed file, whatever the quality order).</item>
/// <item>Lower profile rank than the file → never.</item>
/// <item>Same quality with a higher revision (PROPER/REPACK/REAL) → always, even past the cutoff and with upgrades off
/// (only with <see cref="ProperHandling.PreferAndUpgrade"/>; with <see cref="ProperHandling.DoNotPrefer"/> revisions are ignored).</item>
/// <item>Same quality with a lower revision → never.</item>
/// <item>Upgrades disabled in the profile → never.</item>
/// <item>Dual-language release while the file is not dual-language (profile prefers dual language) → yes, even past the cutoff.</item>
/// <item>File already meets the cutoff → no.</item>
/// <item>Same quality → no; better quality → yes.</item>
/// </list>
/// </summary>
public static class UpgradeRules
{
    public static UpgradeVerdict Check(
        QualityProfile profile,
        QualityModel? existing,
        IReadOnlyList<Language> existingLanguages,
        QualityModel candidate,
        IReadOnlyList<Language> candidateLanguages,
        Language originalLanguage,
        ProperHandling propers = ProperHandling.PreferAndUpgrade)
    {
        if (existing is null)
        {
            return UpgradeVerdict.Upgrade;
        }

        if (IsWrongLanguageFix(profile, existingLanguages, candidateLanguages))
        {
            return UpgradeVerdict.Upgrade;
        }

        var qualityCompare = profile.RankOf(candidate.Quality.Id).CompareTo(profile.RankOf(existing.Quality.Id));

        if (qualityCompare < 0)
        {
            return UpgradeVerdict.BetterQualityOnDisk;
        }

        // "Do not prefer": revisions count as equal; "Do not upgrade": a newer revision alone replaces nothing.
        var revisionCompare = propers == ProperHandling.DoNotPrefer ? 0 : candidate.Revision.CompareTo(existing.Revision);

        // Compare the quality itself, not the rank: a PROPER WEBRip is no revision upgrade for a WEBDL.
        if (propers == ProperHandling.PreferAndUpgrade && IsRevisionUpgrade(existing, candidate))
        {
            return UpgradeVerdict.Upgrade;
        }

        if (qualityCompare == 0 && revisionCompare < 0)
        {
            return UpgradeVerdict.BetterRevisionOnDisk;
        }

        if (!profile.UpgradeAllowed)
        {
            return UpgradeVerdict.UpgradesNotAllowed;
        }

        if (IsLanguageUpgrade(profile, existingLanguages, candidateLanguages, originalLanguage))
        {
            return UpgradeVerdict.Upgrade;
        }

        if (profile.RankOf(existing.Quality.Id) >= CutoffRank(profile))
        {
            return UpgradeVerdict.CutoffMet;
        }

        return qualityCompare == 0 ? UpgradeVerdict.NotBetter : UpgradeVerdict.Upgrade;
    }

    /// <summary>The profile wants a specific language, the file on disk does not have it and the release does.</summary>
    public static bool IsWrongLanguageFix(QualityProfile profile, IReadOnlyList<Language> existingLanguages, IReadOnlyList<Language> candidateLanguages)
    {
        var wanted = Language.FindById(profile.LanguageId);
        return LanguageRules.IsReal(wanted) && existingLanguages.Count > 0 && !existingLanguages.Contains(wanted) && candidateLanguages.Contains(wanted);
    }

    /// <summary>Same quality, higher revision (REAL first, then version).</summary>
    public static bool IsRevisionUpgrade(QualityModel existing, QualityModel candidate) =>
        candidate.Quality == existing.Quality && candidate.Revision.CompareTo(existing.Revision) > 0;

    /// <summary>The profile prefers dual language, the candidate has it and the file on disk does not.</summary>
    public static bool IsLanguageUpgrade(
        QualityProfile profile,
        IReadOnlyList<Language> existingLanguages,
        IReadOnlyList<Language> candidateLanguages,
        Language originalLanguage) =>
        profile.PreferDualLanguage
        && LanguageRules.IsDualLanguage(candidateLanguages, profile, originalLanguage)
        && !LanguageRules.IsDualLanguage(existingLanguages, profile, originalLanguage);

    /// <summary>Profile rank of the cutoff; a cutoff that is not in the profile means "keep upgrading to the best".</summary>
    public static int CutoffRank(QualityProfile profile)
    {
        var rank = profile.RankOf(profile.CutoffQualityId);
        return rank >= 0 ? rank : profile.Items.Count - 1;
    }
}
