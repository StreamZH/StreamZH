// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Decisions;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Library.Wanted;
using static StreamZH.Tests.CustomFormats.CustomFormatTestData;
using static StreamZH.Tests.Decisions.DecisionTestData;
using Impl = StreamZH.Core.CustomFormats.CustomFormatImplementations;

namespace StreamZH.Tests.Decisions;

/// <summary>Custom formats in the decision engine: minimum score, ranking, upgrades and cutoff, like Radarr.</summary>
public sealed class CustomFormatDecisionTests
{
    // Hand-built formats: "-GOOD" +100, "-OK" +20, "HDR" +50, "-BAD" -500.
    private static readonly CustomFormat Good = Movie("Good group", 1, Title("-GOOD$"));
    private static readonly CustomFormat Ok = Movie("Ok group", 2, Title("-OK$"));
    private static readonly CustomFormat Hdr = Movie("HDR", 3, Title(@"\bHDR\b"));
    private static readonly CustomFormat Bad = Movie("Bad group", 4, Title("-BAD$"));
    private static readonly CustomFormat[] Formats = [Good, Ok, Hdr, Bad];

    private static QualityProfile Profile(int min = -10_000, int cutoffScore = 0, int minUpgrade = 1, int? cutoffQualityId = null, bool preferDual = false) =>
        GermanProfile(preferDualLanguage: preferDual, cutoffQualityId: cutoffQualityId) with
        {
            FormatItems = [new(1, 100), new(2, 20), new(3, 50), new(4, -500)],
            MinFormatScore = min,
            CutoffFormatScore = cutoffScore,
            MinUpgradeFormatScore = minUpgrade,
        };

    private static DecisionTarget Target(QualityProfile profile, QualityModel? existing = null, params CustomFormat[] existingFormats) =>
        DecisionTestData.Target(profile, existing: existing, existingLanguages: existing is null ? null : [Language.German]) with
        {
            CustomFormats = Formats,
            ExistingCustomFormats = existingFormats,
        };

    // ------------------------------------------------------------------ minimum score

    [Fact]
    public void Releases_below_the_minimum_score_are_rejected()
    {
        var target = Target(Profile(min: 100));
        var decision = EvaluateOne(target, Candidate("Dune.2024.German.1080p.WEB.HDR.x265-OK", Quality.WebDl1080p));

        var rejection = AssertRejected(decision, RejectionCodes.CustomFormatMinimumScore);
        Assert.Equal("Custom format score 70 is below the minimum 100 (HDR, Ok group)", rejection.Message);
        Assert.Equal(RejectionKind.Permanent, rejection.Kind);
        Assert.Equal(["HDR", "Ok group"], decision.CustomFormats.Select(f => f.Name));
        Assert.Equal(70, decision.CustomFormatScore);

        AssertAccepted(EvaluateOne(Target(Profile(min: 100)), Candidate("Dune.2024.German.1080p.WEB.HDR.x265-GOOD", Quality.WebDl1080p)));
    }

    [Fact]
    public void Without_formats_nothing_changes()
    {
        var plain = DecisionTestData.Target(GermanProfile() with { MinFormatScore = 0 });
        var decision = EvaluateOne(plain, Candidate("Dune.2024.German.1080p.WEB.x265-BAD", Quality.WebDl1080p));
        AssertAccepted(decision);
        Assert.Empty(decision.CustomFormats);
        Assert.Equal(0, decision.CustomFormatScore);

        // The default minimum (0) rejects negative scores, like Radarr.
        AssertRejected(EvaluateOne(Target(Profile(min: 0)), Candidate("Dune.2024.German.1080p.WEB.x265-BAD", Quality.WebDl1080p)), RejectionCodes.CustomFormatMinimumScore);
    }

    // ------------------------------------------------------------------ ranking

    [Fact]
    public void Ranking_quality_then_revision_then_custom_format_score()
    {
        var target = Target(Profile());
        var ranked = Evaluate(
            target,
            Candidate("Dune.2024.German.1080p.WEB.x265-GOOD", Quality.WebDl1080p),
            Candidate("Dune.2024.German.1080p.BluRay.x264-OK", Quality.Bluray1080p),
            Candidate("Dune.2024.German.1080p.WEB.PROPER.x265-OK", Quality.WebDl1080p, revision: new Revision(2)),
            Candidate("Dune.2024.German.1080p.WEB.HDR.x265-GOOD", Quality.WebDl1080p),
            Candidate("Dune.2024.German.1080p.WEB.x265-NONE", Quality.WebDl1080p));

        Assert.Equal(
            [
                "Dune.2024.German.1080p.BluRay.x264-OK",
                "Dune.2024.German.1080p.WEB.PROPER.x265-OK",
                "Dune.2024.German.1080p.WEB.HDR.x265-GOOD",
                "Dune.2024.German.1080p.WEB.x265-GOOD",
                "Dune.2024.German.1080p.WEB.x265-NONE",
            ],
            ranked.Select(d => d.Candidate.Release.ReleaseTitle));
    }

    [Fact]
    public void Qualities_of_one_group_rank_equally_so_the_score_decides()
    {
        var profile = Profile() with
        {
            Items =
            [
                new(Quality.WebDl1080p.Id, true, "WEB 1080p"),
                new(Quality.WebRip1080p.Id, true, "WEB 1080p"),
                new(Quality.Bluray1080p.Id, true),
            ],
            CutoffQualityId = Quality.Bluray1080p.Id,
        };

        var ranked = Evaluate(
            Target(profile),
            Candidate("Dune.2024.German.1080p.WEBRip.x265-OK", Quality.WebRip1080p),
            Candidate("Dune.2024.German.1080p.WEB.x265-GOOD", Quality.WebDl1080p),
            Candidate("Dune.2024.German.1080p.WEBRip.x265-NONE", Quality.WebRip1080p));

        Assert.Equal(
            ["Dune.2024.German.1080p.WEB.x265-GOOD", "Dune.2024.German.1080p.WEBRip.x265-OK", "Dune.2024.German.1080p.WEBRip.x265-NONE"],
            ranked.Select(d => d.Candidate.Release.ReleaseTitle));
    }

    [Fact]
    public void Custom_format_score_ranks_before_dual_language_preferred_score_priority_and_size()
    {
        var target = Target(Profile(preferDual: true));
        var ranked = Evaluate(
            target,
            Candidate("Dune.2024.German.DL.1080p.WEB.x265-NONE", Quality.WebDl1080p, languages: [Language.German, Language.English], indexerPriority: 1, sizeMb: 6000),
            Candidate("Dune.2024.German.1080p.WEB.x265-OK", Quality.WebDl1080p, indexerPriority: 50, sizeMb: 1000));

        Assert.Equal("Dune.2024.German.1080p.WEB.x265-OK", ranked[0].Candidate.Release.ReleaseTitle);
    }

    // ------------------------------------------------------------------ upgrades

    [Fact]
    public void Same_quality_upgrades_by_score_while_the_file_is_below_the_cutoff_score()
    {
        var existing = new QualityModel(Quality.WebDl1080p);
        var profile = Profile(cutoffScore: 100);

        AssertAccepted(EvaluateOne(Target(profile, existing, Ok), Candidate("Dune.2024.German.1080p.WEB.x265-GOOD", Quality.WebDl1080p)));

        var notBetter = AssertRejected(
            EvaluateOne(Target(profile, existing, Ok), Candidate("Dune.2024.German.1080p.WEB.x265-NONE", Quality.WebDl1080p)),
            RejectionCodes.DiskCustomFormatScore);
        Assert.Contains("custom format score: 20 [Ok group], this release 0", notBetter.Message, StringComparison.Ordinal);

        var cutoff = AssertRejected(
            EvaluateOne(Target(profile, existing, Good), Candidate("Dune.2024.German.1080p.WEB.HDR.x265-GOOD", Quality.WebDl1080p)),
            RejectionCodes.DiskCustomFormatCutoffMet);
        Assert.Equal("Existing file meets the custom format cutoff score 100: score 100 [Good group]", cutoff.Message);
    }

    [Fact]
    public void An_upgrade_by_score_must_reach_the_minimum_step()
    {
        var existing = new QualityModel(Quality.WebDl1080p);
        var profile = Profile(cutoffScore: 1000, minUpgrade: 60);

        var small = AssertRejected(
            EvaluateOne(Target(profile, existing, Ok), Candidate("Dune.2024.German.1080p.WEB.HDR.x265-OK", Quality.WebDl1080p)),
            RejectionCodes.DiskCustomFormatScoreIncrement);
        Assert.Equal("Custom format score 70 is less than 60 above the existing file's 20", small.Message);

        AssertAccepted(EvaluateOne(Target(profile, existing, Ok), Candidate("Dune.2024.German.1080p.WEB.x265-GOOD", Quality.WebDl1080p)));
    }

    [Fact]
    public void Quality_cutoff_and_score_cutoff_met_stops_upgrades()
    {
        var existing = new QualityModel(Quality.Bluray1080p);
        var profile = Profile(cutoffScore: 100);

        AssertRejected(
            EvaluateOne(Target(profile, existing, Good), Candidate("Dune.2024.German.1080p.BluRay.HDR.x264-GOOD", Quality.Bluray1080p)),
            RejectionCodes.DiskCutoffMet);

        // Quality cutoff met but the score cutoff not: the same quality with a better score is still wanted ...
        AssertAccepted(EvaluateOne(Target(profile, existing, Ok), Candidate("Dune.2024.German.1080p.BluRay.x264-GOOD", Quality.Bluray1080p)));

        // ... a better quality is not (Radarr: the quality cutoff is met).
        AssertRejected(
            EvaluateOne(Target(profile, existing, Ok), Candidate("Dune.2024.German.1080p.BluRay.REMUX-GOOD", Quality.Remux1080p)),
            RejectionCodes.DiskCutoffMet);
    }

    [Fact]
    public void A_better_quality_below_the_cutoff_is_an_upgrade_whatever_the_score()
    {
        var existing = new QualityModel(Quality.WebDl1080p);
        AssertAccepted(EvaluateOne(
            Target(Profile(cutoffScore: 100), existing, Good, Hdr),
            Candidate("Dune.2024.German.1080p.BluRay.x264-NONE", Quality.Bluray1080p)));
    }

    [Fact]
    public void Without_formats_same_quality_is_still_not_better()
    {
        var existing = new QualityModel(Quality.WebDl1080p);
        var target = DecisionTestData.Target(GermanProfile(), existing: existing, existingLanguages: [Language.German]);
        var rejection = AssertRejected(EvaluateOne(target, Candidate("Dune.2024.German.1080p.WEB.x265-NONE", Quality.WebDl1080p)), RejectionCodes.DiskHigherPreference);
        Assert.Equal("Existing file has the same quality: WEBDL-1080p", rejection.Message);
    }

    [Theory]
    [InlineData(0, 0, 0, 1, UpgradeVerdict.NotBetter)]
    [InlineData(0, 10, 5, 1, UpgradeVerdict.FormatScoreNotBetter)]
    [InlineData(100, 50, 60, 1, UpgradeVerdict.Upgrade)]
    [InlineData(100, 50, 60, 20, UpgradeVerdict.FormatScoreIncrement)]
    [InlineData(100, 100, 150, 1, UpgradeVerdict.FormatCutoffMet)]
    [InlineData(100, 10, 20, 20, UpgradeVerdict.FormatScoreIncrement)]
    [InlineData(100, 10, 30, 20, UpgradeVerdict.Upgrade)]
    [InlineData(100, -100, 0, 1, UpgradeVerdict.Upgrade)]
    public void Same_rank_verdicts(int cutoffScore, int existingScore, int candidateScore, int minUpgrade, UpgradeVerdict expected)
    {
        var profile = Profile(cutoffScore: cutoffScore, minUpgrade: minUpgrade);
        var quality = new QualityModel(Quality.WebDl1080p);
        var verdict = UpgradeRules.Check(profile, quality, [Language.German], quality, [Language.German], Language.English, ProperHandling.PreferAndUpgrade, existingScore, candidateScore);
        Assert.Equal(expected, verdict);
    }

    // ------------------------------------------------------------------ cutoff unmet

    [Fact]
    public void Cutoff_unmet_includes_a_score_below_the_cutoff_score()
    {
        var profile = Profile(cutoffScore: 100);
        var quality = new QualityModel(Quality.Bluray1080p);

        Assert.Equal([CutoffRules.ReasonFormatScore], CutoffRules.Unmet(profile, quality, [Language.German], Language.English, 20));
        Assert.Empty(CutoffRules.Unmet(profile, quality, [Language.German], Language.English, 100));
        Assert.Empty(CutoffRules.Unmet(profile, quality, [Language.German], Language.English));
        Assert.Empty(CutoffRules.Unmet(profile with { UpgradeAllowed = false }, quality, [Language.German], Language.English, 20));
    }
}
