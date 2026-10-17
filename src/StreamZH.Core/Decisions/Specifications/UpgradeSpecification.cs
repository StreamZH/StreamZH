// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>
/// When a file exists, only real upgrades are accepted (see <see cref="UpgradeRules"/>). Release profiles add one way
/// (Sonarr v3): the same quality and revision with a higher preferred score than the file on disk is an upgrade, when
/// the profile allows upgrades, even past the cutoff. The file's score comes from its file name.
/// </summary>
public sealed class UpgradeSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        var target = context.Target;
        var existing = target.ExistingQuality;

        if (existing is null)
        {
            return null;
        }

        var profile = context.Profile;
        var verdict = UpgradeRules.Check(
            profile,
            existing,
            LanguageRules.Normalize(target.ExistingLanguages, target.OriginalLanguage),
            candidate.Release.Quality,
            LanguageRules.EffectiveLanguages(candidate.Release, target.OriginalLanguage),
            target.OriginalLanguage);

        if (verdict is (UpgradeVerdict.NotBetter or UpgradeVerdict.CutoffMet) && IsPreferredScoreUpgrade(candidate, context))
        {
            return null;
        }

        return verdict switch
        {
            UpgradeVerdict.Upgrade => null,
            UpgradeVerdict.BetterQualityOnDisk => new Rejection(
                RejectionCodes.DiskHigherPreference, $"Existing file has a better quality: {existing}"),
            UpgradeVerdict.BetterRevisionOnDisk => new Rejection(
                RejectionCodes.DiskHigherRevision, $"Existing file has a newer revision: {existing}"),
            UpgradeVerdict.UpgradesNotAllowed => new Rejection(
                RejectionCodes.DiskUpgradesNotAllowed, $"A file exists and profile {profile.Name} does not allow upgrades"),
            UpgradeVerdict.CutoffMet => new Rejection(
                RejectionCodes.DiskCutoffMet, $"Existing file meets the cutoff: {existing}"),
            _ => new Rejection(
                RejectionCodes.DiskHigherPreference, $"Existing file has the same quality: {existing}"),
        };
    }

    /// <summary>Same quality and revision as the file, upgrades allowed, and a higher preferred score than the file name.</summary>
    public static bool IsPreferredScoreUpgrade(ReleaseCandidate candidate, DecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);
        var target = context.Target;
        if (!context.Profile.UpgradeAllowed
            || target.ExistingQuality is not { } existing
            || existing.Quality != candidate.Release.Quality.Quality
            || existing.Revision.CompareTo(candidate.Release.Quality.Revision) != 0
            || !ReleaseProfileRules.HasPreferred(context.Rules, candidate.IndexerId, context.Profile.Id))
        {
            return false;
        }

        // The file on disk came from some indexer: score it with the profiles for all indexers only.
        var existingScore = ReleaseProfileRules.Score(context.Rules, 0, context.Profile.Id, target.ExistingFileName ?? string.Empty);
        return context.PreferredScore(candidate) > existingScore;
    }
}
