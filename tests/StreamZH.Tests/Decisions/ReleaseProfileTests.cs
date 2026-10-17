// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Quality;
using static StreamZH.Tests.Decisions.DecisionTestData;

namespace StreamZH.Tests.Decisions;

/// <summary>Sonarr-style release profiles: must contain, must not contain, preferred scores, regex terms, limits.</summary>
public sealed class ReleaseProfileTests
{
    private static ReleaseRules Rules(params ReleaseProfile[] profiles) => new() { ReleaseProfiles = profiles };

    private static IReadOnlyList<Decision> Evaluate(ReleaseRules rules, DecisionTarget target, params ReleaseCandidate[] candidates) =>
        DecisionEngine.Evaluate(candidates, target, Definitions, Now, rules);

    private static Decision One(ReleaseRules rules, ReleaseCandidate candidate, DecisionTarget? target = null) =>
        Assert.Single(Evaluate(rules, target ?? Target(), candidate));

    [Theory]
    [InlineData("hdcam", "Dune.Part.Two.2024.German.HDCAM.1080p-GRP", true)]
    [InlineData("HDCAM", "Dune.Part.Two.2024.German.hdcam.1080p-GRP", true)]
    [InlineData("/\\bDL\\b/", "Dune.Part.Two.2024.German.DL.1080p-GRP", true)]
    [InlineData("/\\bdl\\b/", "Dune.Part.Two.2024.German.DL.1080p-GRP", false)]
    [InlineData("/\\bdl\\b/i", "Dune.Part.Two.2024.German.DL.1080p-GRP", true)]
    [InlineData("/-(GRP|OTHER)$/", "Dune.Part.Two.2024.German.DL.1080p-GRP", true)]
    [InlineData("x265", "Dune.Part.Two.2024.German.DL.1080p.x264-GRP", false)]
    public void Terms_match_as_text_or_regex(string term, string title, bool expected) =>
        Assert.Equal(expected, ReleaseTerms.Matches(term, title));

    [Theory]
    [InlineData("German", null)]
    [InlineData("/German/i", null)]
    [InlineData("a/b", null)]
    [InlineData("", "Empty terms are not allowed.")]
    [InlineData("//", "'//' is an empty regular expression.")]
    [InlineData("/x/g", "'/x/g': only the flag i (ignore case) is supported.")]
    public void Terms_are_validated(string term, string? expected) => Assert.Equal(expected, ReleaseTerms.Validate(term));

    [Fact]
    public void An_invalid_regex_has_a_readable_error()
    {
        var error = ReleaseTerms.Validate("/(unclosed/");
        Assert.NotNull(error);
        Assert.StartsWith("'/(unclosed/' is not a valid regular expression:", error, StringComparison.Ordinal);
        Assert.False(ReleaseTerms.Matches("/(unclosed/", "(unclosed"));
    }

    [Fact]
    public void Must_not_contain_rejects_with_the_profile_name()
    {
        var rules = Rules(new ReleaseProfile { Id = 1, Name = "No cams", Ignored = ["HDCAM", "/\\bTS\\b/"] });

        var cam = One(rules, Candidate("Dune.Part.Two.2024.German.HDCAM.TS.1080p.WEB-GRP", Quality.WebDl1080p));
        Assert.Equal("Release profile 'No cams': contains 'HDCAM' and '/\\bTS\\b/'", AssertRejected(cam, RejectionCodes.MustNotContainPresent).Message);
        AssertAccepted(One(rules, Candidate("Dune.Part.Two.2024.German.DL.1080p.WEB-GRP", Quality.WebDl1080p)));
    }

    [Fact]
    public void Must_contain_needs_one_of_the_terms()
    {
        var rules = Rules(new ReleaseProfile { Id = 1, Name = "Dual", Required = ["German.DL", "/\\bDL\\b/"] });

        var english = One(rules, Candidate("Dune.Part.Two.2024.1080p.WEB-GRP", Quality.WebDl1080p));
        Assert.Equal(
            "Release profile 'Dual': does not contain 'German.DL' or '/\\bDL\\b/'",
            AssertRejected(english, RejectionCodes.MustContainMissing).Message);
        AssertAccepted(One(rules, Candidate("Dune.Part.Two.2024.German.DL.1080p.WEB-GRP", Quality.WebDl1080p)));
    }

    [Fact]
    public void Disabled_and_unrelated_profiles_do_not_apply()
    {
        var cam = Candidate("Dune.Part.Two.2024.German.HDCAM.1080p.WEB-GRP", Quality.WebDl1080p) with { IndexerId = 1 };

        AssertAccepted(One(Rules(new ReleaseProfile { Name = "Off", Enabled = false, Ignored = ["HDCAM"] }), cam));
        AssertAccepted(One(Rules(new ReleaseProfile { Name = "Indexer 2", Ignored = ["HDCAM"], IndexerIds = [2] }), cam));
        AssertAccepted(One(Rules(new ReleaseProfile { Name = "Other profile", Ignored = ["HDCAM"], QualityProfileIds = [99] }), cam));
        AssertRejected(One(Rules(new ReleaseProfile { Name = "Indexer 1", Ignored = ["HDCAM"], IndexerIds = [1], QualityProfileIds = [1] }), cam), RejectionCodes.MustNotContainPresent);
    }

    [Fact]
    public void Preferred_terms_rank_after_quality_and_before_indexer_priority()
    {
        var rules = Rules(
            new ReleaseProfile { Name = "Groups", Preferred = [new("-GOOD", 50), new("x265", 10)] },
            new ReleaseProfile { Name = "Avoid", Preferred = [new("/\\bLD\\b/", -100)] });
        var plain = Candidate("Dune.Part.Two.2024.German.DL.1080p.WEB.x264-OTHER", Quality.WebDl1080p, indexerPriority: 1);
        var good = Candidate("Dune.Part.Two.2024.German.DL.1080p.WEB.x265-GOOD", Quality.WebDl1080p, indexerPriority: 50);
        var line = Candidate("Dune.Part.Two.2024.German.DL.LD.1080p.WEB.x265-GOOD", Quality.WebDl1080p);
        var bluray = Candidate("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-OTHER", Quality.Bluray1080p);

        var ranked = Evaluate(rules, Target(), plain, good, line, bluray);

        Assert.Equal([bluray, good, plain, line], ranked.Select(d => d.Candidate));
        Assert.Equal([0, 60, 0, -40], ranked.Select(d => d.PreferredScore));
        Assert.All(ranked, AssertAccepted);
        Assert.Equal(0, Assert.Single(Evaluate(ReleaseRules.None, Target(), good)).PreferredScore);
    }

    [Fact]
    public void A_higher_preferred_score_upgrades_a_file_of_the_same_quality_when_upgrades_are_allowed()
    {
        var rules = Rules(new ReleaseProfile { Name = "Groups", Preferred = [new("-GOOD", 50)] });
        var existing = new QualityModel(Quality.Bluray1080p, new Revision());
        var onDisk = Target(existing: existing, existingLanguages: [Language.German, Language.English]) with
        {
            ExistingFileName = "Dune Part Two (2024) German DL 1080p BluRay x264-OTHER.mkv",
        };
        var good = Candidate("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-GOOD", Quality.Bluray1080p, languages: [Language.German, Language.English]);

        // Past the cutoff (Bluray-1080p), like Sonarr v3's preferred words.
        AssertAccepted(One(rules, good, onDisk));

        // Same score as the file, or upgrades off: no.
        AssertRejected(One(rules, good, onDisk with { ExistingFileName = "Dune Part Two (2024) German DL 1080p BluRay x264-GOOD.mkv" }), RejectionCodes.DiskCutoffMet);
        var noUpgrades = Target(GermanProfile(upgradeAllowed: false), existing: existing, existingLanguages: [Language.German, Language.English]) with
        {
            ExistingFileName = "Dune Part Two (2024) German DL 1080p BluRay x264-OTHER.mkv",
        };
        AssertRejected(One(rules, good, noUpgrades), RejectionCodes.DiskUpgradesNotAllowed);

        // A lower quality with a better score is still no upgrade.
        var web = Candidate("Dune.Part.Two.2024.German.DL.1080p.WEB.x264-GOOD", Quality.WebDl1080p, languages: [Language.German, Language.English]);
        AssertRejected(One(rules, web, onDisk), RejectionCodes.DiskHigherPreference);

        // Without release profiles nothing changes.
        AssertRejected(One(ReleaseRules.None, good, onDisk), RejectionCodes.DiskCutoffMet);
    }

    [Fact]
    public void Explain_lists_what_matched_per_profile()
    {
        var rules = Rules(
            new ReleaseProfile { Id = 1, Name = "Dual", Required = ["German.DL"], Preferred = [new("x265", 10)] },
            new ReleaseProfile { Id = 2, Name = "No cams", Ignored = ["HDCAM"] },
            new ReleaseProfile { Id = 3, Name = "Groups", Preferred = [new("-GRP", 5), new("-BAD", -5)] });

        var matches = ReleaseProfileRules.Explain(rules, 0, 1, "Dune.Part.Two.2024.German.DL.1080p.WEB.x265-GRP");

        Assert.Equal(["Dual", "Groups"], matches.Select(m => m.Profile.Name));
        Assert.Equal((false, 10), (matches[0].Rejects, matches[0].Score));
        Assert.Equal(["German.DL"], matches[0].RequiredMatched);
        Assert.Equal(5, matches[1].Score);
        Assert.Equal(15, ReleaseProfileRules.Score(rules, 0, 1, "Dune.Part.Two.2024.German.DL.1080p.WEB.x265-GRP"));
    }
}
