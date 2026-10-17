// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Quality;
using static StreamZH.Tests.Decisions.DecisionTestData;

namespace StreamZH.Tests.Decisions;

/// <summary>Settings → Indexers in the decision engine: minimum age, retention, maximum size, hardcoded subs, availability.</summary>
public sealed class ReleaseRulesTests
{
    private const string Title = "Dune.Part.Two.2024.German.DL.1080p.WEB.H264-GRP";

    private static Decision One(ReleaseRules rules, ReleaseCandidate candidate, DecisionTarget? target = null) =>
        Assert.Single(DecisionEngine.Evaluate([candidate], target ?? Target(), Definitions, Now, rules));

    [Fact]
    public void No_rules_change_nothing()
    {
        AssertAccepted(One(ReleaseRules.None, Candidate("Dune.Part.Two.2024.German.DL.KORSUB.1080p.WEB.H264-GRP", Quality.WebDl1080p, published: Now)));
    }

    [Fact]
    public void Minimum_age_rejects_young_releases_temporarily()
    {
        var rules = new ReleaseRules { MinimumAgeMinutes = 30 };

        var young = One(rules, Candidate(Title, Quality.WebDl1080p, published: Now.AddMinutes(-10)));
        var rejection = AssertRejected(young, RejectionCodes.MinimumAge);
        Assert.Equal(RejectionKind.Temporary, rejection.Kind);
        Assert.Equal("Only 10 minutes old, the minimum age is 30 minutes", rejection.Message);
        Assert.True(young.TemporarilyRejected);

        AssertAccepted(One(rules, Candidate(Title, Quality.WebDl1080p, published: Now.AddMinutes(-30))));
        AssertAccepted(One(rules, Candidate(Title, Quality.WebDl1080p, published: default(DateTimeOffset))));
    }

    [Fact]
    public void Retention_rejects_releases_older_than_the_provider_keeps()
    {
        var rules = new ReleaseRules { RetentionDays = 1000 };

        var old = AssertRejected(One(rules, Candidate(Title, Quality.WebDl1080p, published: Now.AddDays(-1200))), RejectionCodes.MaximumAge);
        Assert.Equal("1200 days old, older than the retention of 1000 days", old.Message);
        Assert.Equal(RejectionKind.Permanent, old.Kind);
        AssertAccepted(One(rules, Candidate(Title, Quality.WebDl1080p, published: Now.AddDays(-999))));
        AssertAccepted(One(ReleaseRules.None, Candidate(Title, Quality.WebDl1080p, published: Now.AddDays(-5000))));
    }

    [Fact]
    public void Maximum_size_applies_to_every_quality()
    {
        var rules = new ReleaseRules { MaximumSizeMb = 5000 };

        var big = AssertRejected(One(rules, Candidate(Title, Quality.WebDl1080p, sizeMb: 6000)), RejectionCodes.MaximumSizeExceeded);
        Assert.Equal("5.9 GB is larger than the maximum size of 4.9 GB for all releases", big.Message);
        AssertAccepted(One(rules, Candidate(Title, Quality.WebDl1080p, sizeMb: 5000)));
        AssertAccepted(One(rules, Candidate(Title, Quality.WebDl1080p, sizeMb: 0)));
    }

    [Theory]
    [InlineData("Dune.Part.Two.2024.German.DL.KORSUB.1080p.WEB.H264-GRP", "KORSUB")]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.HC.WEB.H264-GRP", HardcodedSubs.Generic)]
    [InlineData("Dune.Part.Two.2024.German.Subbed.1080p.WEB.H264-GRP", HardcodedSubs.Generic)]
    [InlineData("Dune.Part.Two.2024.1080p.HARDSUB.WEB.H264-GRP", "HARDSUB")]
    [InlineData("Dune.Part.Two.2024.1080p.NLSUBS.WEB.H264-GRP", "NLSUBS")]
    public void Hardcoded_subs_are_found(string title, string expected) => Assert.Equal(expected, HardcodedSubs.Find(title));

    [Theory]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.WEB.H264-GRP")]
    [InlineData("Dune.Part.Two.2024.MULTiSUBS.1080p.WEB.H264-GRP")]
    [InlineData("Dune.Part.Two.2024.SOFTSUBS.1080p.WEB.H264-GRP")]
    [InlineData("Dune.Part.Two.2024.1080p.HEVC-GRP")]
    public void Soft_and_multi_subs_are_not_hardcoded(string title) => Assert.Null(HardcodedSubs.Find(title));

    [Fact]
    public void Hardcoded_subs_are_rejected_unless_allowed_or_whitelisted()
    {
        var korsub = Candidate("Dune.Part.Two.2024.German.DL.KORSUB.1080p.WEB.H264-GRP", Quality.WebDl1080p);
        var rules = new ReleaseRules { AllowHardcodedSubs = false };

        Assert.Equal("Hardcoded subs found: KORSUB", AssertRejected(One(rules, korsub), RejectionCodes.HardcodedSubs).Message);
        AssertAccepted(One(rules with { WhitelistedHardcodedSubs = ["korsub"] }, korsub));
        AssertAccepted(One(rules with { AllowHardcodedSubs = true }, korsub));
        AssertAccepted(One(rules, Candidate(Title, Quality.WebDl1080p)));
    }

    [Fact]
    public void A_movie_that_is_not_available_yet_is_rejected_temporarily()
    {
        var later = Target() with { AvailableFrom = Now.AddDays(3) };
        var rejection = AssertRejected(One(ReleaseRules.None, Candidate(Title, Quality.WebDl1080p), later), RejectionCodes.Availability);
        Assert.Equal(RejectionKind.Temporary, rejection.Kind);
        Assert.Equal("Movie is only available from 2026-10-20 (minimum availability)", rejection.Message);

        var unknown = Target() with { AvailableFrom = DateTimeOffset.MaxValue };
        Assert.Equal(
            "Movie is not available yet (minimum availability)",
            AssertRejected(One(ReleaseRules.None, Candidate(Title, Quality.WebDl1080p), unknown), RejectionCodes.Availability).Message);

        AssertAccepted(One(ReleaseRules.None, Candidate(Title, Quality.WebDl1080p), Target() with { AvailableFrom = Now }));
    }

    [Fact]
    public void Every_rule_reports_its_reason()
    {
        var rules = new ReleaseRules { MinimumAgeMinutes = 60, MaximumSizeMb = 1000, AllowHardcodedSubs = false };
        var decision = One(rules, Candidate("Dune.Part.Two.2024.German.DL.KORSUB.1080p.WEB.H264-GRP", Quality.WebDl1080p, published: Now.AddMinutes(-5)));

        Assert.Equal(
            [RejectionCodes.MaximumSizeExceeded, RejectionCodes.MinimumAge, RejectionCodes.HardcodedSubs],
            decision.Rejections.Select(r => r.Code));
        Assert.False(decision.TemporarilyRejected);
    }
}
