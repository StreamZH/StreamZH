// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>When a file exists, only real upgrades are accepted (see <see cref="UpgradeRules"/>).</summary>
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
}
