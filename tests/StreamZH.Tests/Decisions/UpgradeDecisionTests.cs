// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Quality;
using static StreamZH.Tests.Decisions.DecisionTestData;

namespace StreamZH.Tests.Decisions;

public class UpgradeDecisionTests
{
    private static readonly Revision Proper = new(Version: 2);
    private static readonly Revision Real = new(Version: 1, Real: 1);

    private static DecisionTarget WithFile(Quality quality, Revision? revision = null, Language[]? languages = null, bool upgradeAllowed = true, bool preferDualLanguage = true) =>
        Target(
            GermanProfile(upgradeAllowed, preferDualLanguage),
            existing: new QualityModel(quality, revision ?? new Revision()),
            existingLanguages: languages ?? [Language.German]);

    [Fact]
    public void Better_quality_below_cutoff_is_an_upgrade()
    {
        AssertAccepted(EvaluateOne(WithFile(Quality.WebDl1080p), Candidate("Dune.German.1080p.BluRay", Quality.Bluray1080p)));
    }

    [Fact]
    public void Better_quality_past_the_cutoff_is_still_an_upgrade_when_the_file_is_below_it()
    {
        AssertAccepted(EvaluateOne(WithFile(Quality.WebDl1080p), Candidate("Dune.German.1080p.Remux", Quality.Remux1080p, sizeMb: 10_000)));
    }

    [Fact]
    public void Lower_quality_is_rejected()
    {
        var rejection = AssertRejected(
            EvaluateOne(WithFile(Quality.Bluray1080p), Candidate("Dune.German.1080p.WEB", Quality.WebDl1080p)),
            RejectionCodes.DiskHigherPreference);

        Assert.Equal("Existing file has a better quality: Bluray-1080p", rejection.Message);
    }

    [Fact]
    public void Same_quality_below_cutoff_is_rejected()
    {
        var rejection = AssertRejected(
            EvaluateOne(WithFile(Quality.WebDl1080p), Candidate("Dune.German.1080p.WEB", Quality.WebDl1080p)),
            RejectionCodes.DiskHigherPreference);

        Assert.Equal("Existing file has the same quality: WEBDL-1080p", rejection.Message);
    }

    [Fact]
    public void Existing_file_at_cutoff_rejects_better_quality()
    {
        var rejection = AssertRejected(
            EvaluateOne(WithFile(Quality.Bluray1080p), Candidate("Dune.German.1080p.Remux", Quality.Remux1080p, sizeMb: 10_000)),
            RejectionCodes.DiskCutoffMet);

        Assert.Equal("Existing file meets the cutoff: Bluray-1080p", rejection.Message);
    }

    [Fact]
    public void Existing_file_at_cutoff_rejects_same_quality()
    {
        AssertRejected(EvaluateOne(WithFile(Quality.Bluray1080p), Candidate("Dune.German.1080p.BluRay", Quality.Bluray1080p)), RejectionCodes.DiskCutoffMet);
    }

    [Fact]
    public void Proper_of_the_same_quality_is_an_upgrade_even_past_the_cutoff()
    {
        AssertAccepted(EvaluateOne(WithFile(Quality.Bluray1080p), Candidate("Dune.German.1080p.BluRay.PROPER", Quality.Bluray1080p, revision: Proper)));
    }

    [Fact]
    public void Real_beats_a_higher_version()
    {
        AssertAccepted(EvaluateOne(
            WithFile(Quality.WebDl1080p, revision: new Revision(Version: 2)),
            Candidate("Dune.German.1080p.WEB.REAL", Quality.WebDl1080p, revision: Real)));
    }

    [Fact]
    public void Proper_of_a_different_quality_is_no_revision_upgrade()
    {
        // WEBRip is ranked above WEBDL in this profile, so it is a normal quality upgrade below the cutoff ...
        AssertAccepted(EvaluateOne(WithFile(Quality.WebDl1080p), Candidate("Dune.German.1080p.WEBRip.PROPER", Quality.WebRip1080p, revision: Proper)));

        // ... but at the cutoff only a proper of the very same quality gets through.
        AssertRejected(
            EvaluateOne(
                Target(GermanProfile(cutoffQualityId: Quality.WebDl1080p.Id), existing: new QualityModel(Quality.WebDl1080p), existingLanguages: [Language.German]),
                Candidate("Dune.German.1080p.WEBRip.PROPER", Quality.WebRip1080p, revision: Proper)),
            RejectionCodes.DiskCutoffMet);
    }

    [Fact]
    public void Older_revision_of_the_same_quality_is_rejected()
    {
        var rejection = AssertRejected(
            EvaluateOne(WithFile(Quality.WebDl1080p, revision: Proper), Candidate("Dune.German.1080p.WEB", Quality.WebDl1080p)),
            RejectionCodes.DiskHigherRevision);

        Assert.Equal("Existing file has a newer revision: WEBDL-1080p v2", rejection.Message);
    }

    [Fact]
    public void Upgrades_disabled_rejects_better_quality()
    {
        var rejection = AssertRejected(
            EvaluateOne(WithFile(Quality.WebDl1080p, upgradeAllowed: false), Candidate("Dune.German.1080p.BluRay", Quality.Bluray1080p)),
            RejectionCodes.DiskUpgradesNotAllowed);

        Assert.Equal("A file exists and profile Deutsch 1080p does not allow upgrades", rejection.Message);
    }

    [Fact]
    public void Upgrades_disabled_still_accepts_a_proper_of_the_same_quality()
    {
        AssertAccepted(EvaluateOne(
            WithFile(Quality.WebDl1080p, upgradeAllowed: false),
            Candidate("Dune.German.1080p.WEB.PROPER", Quality.WebDl1080p, revision: Proper)));
    }

    [Fact]
    public void Upgrades_disabled_without_a_file_accepts()
    {
        AssertAccepted(EvaluateOne(Target(GermanProfile(upgradeAllowed: false)), Candidate("Dune.German.1080p.WEB", Quality.WebDl1080p)));
    }

    // Dual language

    [Fact]
    public void Dual_language_release_upgrades_a_single_language_file_of_the_same_quality()
    {
        AssertAccepted(EvaluateOne(
            WithFile(Quality.WebDl1080p, languages: [Language.German]),
            Candidate("Dune.German.DL.1080p.WEB", Quality.WebDl1080p, languages: [Language.German, Language.English])));
    }

    [Fact]
    public void Dual_language_upgrade_also_works_past_the_cutoff()
    {
        AssertAccepted(EvaluateOne(
            WithFile(Quality.Bluray1080p, languages: [Language.German]),
            Candidate("Dune.German.DL.1080p.BluRay", Quality.Bluray1080p, languages: [Language.German, Language.English])));
    }

    [Fact]
    public void Dual_language_release_does_not_upgrade_a_dual_language_file()
    {
        AssertRejected(
            EvaluateOne(
                WithFile(Quality.WebDl1080p, languages: [Language.German, Language.English]),
                Candidate("Dune.German.DL.1080p.WEB", Quality.WebDl1080p, languages: [Language.German, Language.English])),
            RejectionCodes.DiskHigherPreference);
    }

    [Fact]
    public void Dual_language_is_no_upgrade_without_the_profile_preference()
    {
        AssertRejected(
            EvaluateOne(
                WithFile(Quality.WebDl1080p, languages: [Language.German], preferDualLanguage: false),
                Candidate("Dune.German.DL.1080p.WEB", Quality.WebDl1080p, languages: [Language.German, Language.English])),
            RejectionCodes.DiskHigherPreference);
    }

    [Fact]
    public void Dual_language_does_not_beat_better_quality_on_disk()
    {
        AssertRejected(
            EvaluateOne(
                WithFile(Quality.Bluray1080p, languages: [Language.German]),
                Candidate("Dune.German.DL.1080p.WEB", Quality.WebDl1080p, languages: [Language.German, Language.English])),
            RejectionCodes.DiskHigherPreference);
    }

    [Fact]
    public void Dual_language_does_not_beat_a_newer_revision_on_disk()
    {
        AssertRejected(
            EvaluateOne(
                WithFile(Quality.WebDl1080p, revision: Proper, languages: [Language.German]),
                Candidate("Dune.German.DL.1080p.WEB", Quality.WebDl1080p, languages: [Language.German, Language.English])),
            RejectionCodes.DiskHigherRevision);
    }

    [Fact]
    public void Dual_language_is_no_upgrade_when_upgrades_are_disabled()
    {
        AssertRejected(
            EvaluateOne(
                WithFile(Quality.WebDl1080p, languages: [Language.German], upgradeAllowed: false),
                Candidate("Dune.German.DL.1080p.WEB", Quality.WebDl1080p, languages: [Language.German, Language.English])),
            RejectionCodes.DiskUpgradesNotAllowed);
    }

    [Fact]
    public void Dual_language_counts_the_original_language_as_second_language()
    {
        // Anime: German profile, Japanese original. German + Japanese is dual language too.
        var target = Target(
            GermanProfile(),
            originalLanguage: Language.Japanese,
            existing: new QualityModel(Quality.WebDl1080p),
            existingLanguages: [Language.German]);

        AssertAccepted(EvaluateOne(target, Candidate("Anime.German.DL.1080p.WEB", Quality.WebDl1080p, languages: [Language.German, Language.Japanese])));
    }

    [Fact]
    public void Dual_language_rules()
    {
        var profile = GermanProfile();

        Assert.True(LanguageRules.IsDualLanguage([Language.German, Language.English], profile, Language.English));
        Assert.True(LanguageRules.IsDualLanguage([Language.German, Language.English], profile, Language.Japanese));
        Assert.True(LanguageRules.IsDualLanguage([Language.German, Language.Japanese], profile, Language.Japanese));
        Assert.True(LanguageRules.IsDualLanguage([Language.German, Language.English], profile, Language.German));
        Assert.False(LanguageRules.IsDualLanguage([Language.German], profile, Language.English));
        Assert.False(LanguageRules.IsDualLanguage([Language.German, Language.French], profile, Language.English));
        Assert.False(LanguageRules.IsDualLanguage([Language.English, Language.French], profile, Language.French));
        Assert.False(LanguageRules.IsDualLanguage([Language.German, Language.English], ProfileWithLanguage(Language.Any), Language.English));
        Assert.False(LanguageRules.IsDualLanguage([Language.German, Language.English], ProfileWithLanguage(Language.Original), Language.English));
    }
}
