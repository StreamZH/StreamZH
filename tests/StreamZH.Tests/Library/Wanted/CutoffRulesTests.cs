// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Wanted;

namespace StreamZH.Tests.Library.Wanted;

public sealed class CutoffRulesTests
{
    private static readonly QualityProfile Hd = QualityDefaults.Profiles.Single(p => p.Name == "HD-1080p");
    private static readonly QualityProfile German = QualityDefaults.Profiles.Single(p => p.Name == "Deutsch 1080p");

    private static QualityModel Q(Quality quality) => new(quality);

    [Fact]
    public void A_quality_below_the_cutoff_is_unmet_and_the_cutoff_itself_is_met()
    {
        Assert.Equal([CutoffRules.ReasonQuality], CutoffRules.Unmet(Hd, Q(Quality.WebDl1080p), [], Language.English));
        Assert.Empty(CutoffRules.Unmet(Hd, Q(Quality.Bluray1080p), [], Language.English));
        Assert.Empty(CutoffRules.Unmet(Hd, Q(Quality.Remux1080p), [], Language.English));

        // A quality that is not part of the profile at all counts as below the cutoff.
        Assert.Equal([CutoffRules.ReasonQuality], CutoffRules.Unmet(Hd, Q(Quality.Unknown), [], Language.English));
    }

    [Fact]
    public void Without_upgrades_only_the_wrong_language_counts()
    {
        var noUpgrades = German with { UpgradeAllowed = false };

        Assert.Empty(CutoffRules.Unmet(Hd with { UpgradeAllowed = false }, Q(Quality.WebDl1080p), [], Language.English));
        Assert.Empty(CutoffRules.Unmet(noUpgrades, Q(Quality.WebDl720p), [Language.German], Language.English));
        Assert.Equal([CutoffRules.ReasonLanguage], CutoffRules.Unmet(noUpgrades, Q(Quality.Bluray1080p), [Language.English], Language.English));
    }

    [Fact]
    public void The_language_rule_follows_the_profile_and_the_original_language()
    {
        // German profile, English movie: an English file is wrong (and below the cutoff).
        Assert.Equal(
            [CutoffRules.ReasonLanguage, CutoffRules.ReasonQuality],
            CutoffRules.Unmet(German, Q(Quality.WebDl1080p), [Language.English], Language.English));

        // A file without a language tag counts as the original language, like the decision engine does.
        Assert.Equal([CutoffRules.ReasonLanguage], CutoffRules.Unmet(German, Q(Quality.Bluray1080p), [], Language.English));

        // German.DL at the cutoff meets everything; German only misses the preferred dual-language track.
        Assert.Empty(CutoffRules.Unmet(German, Q(Quality.Bluray1080p), [Language.German, Language.English], Language.English));
        Assert.Equal([CutoffRules.ReasonDualLanguage], CutoffRules.Unmet(German, Q(Quality.Bluray1080p), [Language.German], Language.English));
        Assert.Empty(CutoffRules.Unmet(German with { PreferDualLanguage = false }, Q(Quality.Bluray1080p), [Language.German], Language.English));

        // "Original" and "Any" profiles have no language rule.
        Assert.Empty(CutoffRules.Unmet(Hd, Q(Quality.Bluray1080p), [Language.French], Language.English));
    }

    [Fact]
    public void The_cutoff_quality_is_named_and_falls_back_to_the_best_one()
    {
        Assert.Equal(Quality.Bluray1080p, CutoffRules.CutoffQuality(Hd));
        var missingCutoff = Hd with { CutoffQualityId = Quality.Remux2160p.Id, Items = [.. Hd.Items.Where(i => i.QualityId != Quality.Remux2160p.Id)] };
        Assert.Equal(Quality.FindById(missingCutoff.Items[^1].QualityId), CutoffRules.CutoffQuality(missingCutoff));
        Assert.Equal([CutoffRules.ReasonQuality], CutoffRules.Unmet(missingCutoff, Q(Quality.Remux1080p), [], Language.English));
    }
}
