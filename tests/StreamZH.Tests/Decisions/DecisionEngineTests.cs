// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using static StreamZH.Tests.Decisions.DecisionTestData;

namespace StreamZH.Tests.Decisions;

public class DecisionEngineTests
{
    [Fact]
    public void Accepts_a_wanted_release()
    {
        var decision = EvaluateOne(Target(), Candidate("Dune.Part.Two.2024.German.DL.1080p.WEB.H264-GROUP", Quality.WebDl1080p));

        AssertAccepted(decision);
        Assert.False(decision.TemporarilyRejected);
        Assert.False(decision.PermanentlyRejected);
    }

    // Monitoring

    [Fact]
    public void Rejects_when_movie_is_not_monitored()
    {
        var decision = EvaluateOne(Target(monitored: false), Candidate("Dune.Part.Two.2024.German.1080p.WEB-GROUP", Quality.WebDl1080p));

        var rejection = AssertRejected(decision, RejectionCodes.NotMonitored);
        Assert.Equal("Movie is not monitored", rejection.Message);
        Assert.Equal(RejectionKind.Permanent, rejection.Kind);
        Assert.True(decision.PermanentlyRejected);
    }

    [Fact]
    public void Rejects_when_episode_is_not_monitored()
    {
        var decision = EvaluateOne(
            Target(mediaType: MediaType.Series, runtimeMinutes: 45, monitored: false),
            Candidate("Show.S01E01.German.1080p.WEB-GROUP", Quality.WebDl1080p, sizeMb: 2000));

        Assert.Equal("Episode is not monitored", AssertRejected(decision, RejectionCodes.NotMonitored).Message);
    }

    // Quality

    [Fact]
    public void Rejects_a_quality_that_is_not_allowed_in_the_profile()
    {
        var decision = EvaluateOne(Target(), Candidate("Dune.Part.Two.2024.German.720p.WEB-GROUP", Quality.WebDl720p));

        var rejection = AssertRejected(decision, RejectionCodes.QualityNotWanted);
        Assert.Equal("Quality WEBDL-720p is not wanted in profile Deutsch 1080p", rejection.Message);
    }

    [Fact]
    public void Rejects_a_quality_that_is_not_in_the_profile_at_all()
    {
        var decision = EvaluateOne(Target(), Candidate("Dune.Part.Two.2024.German.2160p.WEB-GROUP", Quality.WebDl2160p));

        Assert.Equal(
            "Quality WEBDL-2160p is not wanted in profile Deutsch 1080p",
            AssertRejected(decision, RejectionCodes.QualityNotWanted).Message);
    }

    [Fact]
    public void Rejects_an_unknown_quality()
    {
        var decision = EvaluateOne(Target(), Candidate("Dune.Part.Two.2024.German-GROUP", Quality.Unknown));

        AssertRejected(decision, RejectionCodes.UnknownQuality);
        Assert.DoesNotContain(decision.Rejections, r => r.Code == RejectionCodes.QualityNotWanted);
    }

    // Language

    [Fact]
    public void Any_language_profile_accepts_every_language()
    {
        var profile = ProfileWithLanguage(Language.Any);

        AssertAccepted(EvaluateOne(Target(profile), Candidate("Dune.French", Quality.WebDl1080p, languages: [Language.French])));
        AssertAccepted(EvaluateOne(Target(profile), Candidate("Dune.NoLang", Quality.WebDl1080p, languages: [])));
    }

    [Fact]
    public void Specific_language_must_be_contained()
    {
        var target = Target(GermanProfile());

        AssertAccepted(EvaluateOne(target, Candidate("Dune.German", Quality.WebDl1080p, languages: [Language.German])));
        AssertAccepted(EvaluateOne(target, Candidate("Dune.German.DL", Quality.WebDl1080p, languages: [Language.German, Language.English])));

        var rejection = AssertRejected(
            EvaluateOne(target, Candidate("Dune.French.English", Quality.WebDl1080p, languages: [Language.French, Language.English])),
            RejectionCodes.WantedLanguage);
        Assert.Equal("German is wanted, but found French, English", rejection.Message);
    }

    [Fact]
    public void Release_without_a_parsed_language_counts_as_english()
    {
        var release = Candidate("Dune.Part.Two.2024.1080p.WEB-GROUP", Quality.WebDl1080p, languages: []);

        AssertAccepted(EvaluateOne(Target(ProfileWithLanguage(Language.English)), release));

        var rejection = AssertRejected(EvaluateOne(Target(GermanProfile()), release), RejectionCodes.WantedLanguage);
        Assert.Equal("German is wanted, but found English", rejection.Message);
    }

    [Fact]
    public void Original_language_profile_needs_the_original_language()
    {
        var target = Target(ProfileWithLanguage(Language.Original), originalLanguage: Language.Japanese);

        AssertAccepted(EvaluateOne(target, Candidate("Movie.Japanese", Quality.WebDl1080p, languages: [Language.Japanese])));
        AssertAccepted(EvaluateOne(target, Candidate("Movie.German.Japanese", Quality.WebDl1080p, languages: [Language.German, Language.Japanese])));

        var rejection = AssertRejected(
            EvaluateOne(target, Candidate("Movie.English", Quality.WebDl1080p, languages: [Language.English])),
            RejectionCodes.WantedLanguage);
        Assert.Equal("Original language (Japanese) is wanted, but found English", rejection.Message);
    }

    [Fact]
    public void Original_language_profile_accepts_when_the_original_language_is_unknown()
    {
        var target = Target(ProfileWithLanguage(Language.Original), originalLanguage: Language.Unknown);

        AssertAccepted(EvaluateOne(target, Candidate("Movie.French", Quality.WebDl1080p, languages: [Language.French])));
    }

    // Size

    [Theory]
    [InlineData(599, RejectionCodes.BelowMinimumSize)]
    [InlineData(600, null)]
    [InlineData(6000, null)]
    [InlineData(12000, null)]
    [InlineData(12001, RejectionCodes.AboveMaximumSize)]
    public void Movie_size_limits_are_per_minute_times_runtime(long sizeMb, string? expectedCode)
    {
        var decision = EvaluateOne(Target(runtimeMinutes: 120), Candidate("Dune.German.1080p", Quality.WebDl1080p, sizeMb: sizeMb));

        if (expectedCode is null)
        {
            AssertAccepted(decision);
        }
        else
        {
            AssertRejected(decision, expectedCode);
        }
    }

    [Fact]
    public void Size_messages_are_readable()
    {
        var small = AssertRejected(
            EvaluateOne(Target(runtimeMinutes: 120), Candidate("Dune.German.1080p", Quality.WebDl1080p, sizeMb: 300)),
            RejectionCodes.BelowMinimumSize);
        Assert.Equal("300.0 MB is smaller than the minimum of 600.0 MB (for 120 min)", small.Message);

        var large = AssertRejected(
            EvaluateOne(Target(runtimeMinutes: 120), Candidate("Dune.German.1080p", Quality.WebDl1080p, sizeMb: 20 * 1024)),
            RejectionCodes.AboveMaximumSize);
        Assert.Equal("20.0 GB is larger than the maximum of 11.7 GB (for 120 min)", large.Message);
    }

    [Fact]
    public void Size_check_is_skipped_when_runtime_is_unknown()
    {
        AssertAccepted(EvaluateOne(Target(runtimeMinutes: 0), Candidate("Dune.German.1080p", Quality.WebDl1080p, sizeMb: 100_000)));
        AssertAccepted(EvaluateOne(Target(runtimeMinutes: 0), Candidate("Dune.German.1080p", Quality.WebDl1080p, sizeMb: 100)));
    }

    [Fact]
    public void Size_check_is_skipped_when_size_is_unknown()
    {
        AssertAccepted(EvaluateOne(Target(), Candidate("Dune.German.1080p", Quality.WebDl1080p, sizeMb: 0)));
    }

    [Fact]
    public void Missing_or_zero_max_size_means_unlimited()
    {
        var definitions = new Dictionary<int, QualityDefinition>
        {
            [Quality.WebDl1080p.Id] = new(Quality.WebDl1080p.Id, "WEBDL-1080p", 5, null, null),
            [Quality.Bluray1080p.Id] = new(Quality.Bluray1080p.Id, "Bluray-1080p", 5, 0, null),
        };

        var decisions = DecisionEngine.Evaluate(
            [
                Candidate("Dune.German.1080p.WEB", Quality.WebDl1080p, sizeMb: 500_000),
                Candidate("Dune.German.1080p.BluRay", Quality.Bluray1080p, sizeMb: 500_000),
            ],
            Target(),
            definitions,
            Now);

        Assert.All(decisions, AssertAccepted);
    }

    [Fact]
    public void Series_size_uses_episode_runtime_times_episode_count()
    {
        // 45 min per episode: single episode 225 MB .. 4500 MB, season pack of 10: 2250 MB .. 45000 MB.
        var single = Target(mediaType: MediaType.Series, runtimeMinutes: 45, episodeCount: 1);
        var pack = Target(mediaType: MediaType.Series, runtimeMinutes: 45, episodeCount: 10);
        var twentyGb = Candidate("Show.S01.German.DL.1080p.WEB-GROUP", Quality.WebDl1080p, sizeMb: 20 * 1024);

        AssertAccepted(EvaluateOne(pack, twentyGb));
        AssertRejected(EvaluateOne(single, twentyGb), RejectionCodes.AboveMaximumSize);

        var tooSmallPack = AssertRejected(
            EvaluateOne(pack, Candidate("Show.S01.German.1080p.WEB-GROUP", Quality.WebDl1080p, sizeMb: 1024)),
            RejectionCodes.BelowMinimumSize);
        Assert.Equal("1.0 GB is smaller than the minimum of 2.2 GB (for 10 episodes, 450 min)", tooSmallPack.Message);
    }

    [Fact]
    public void Series_specials_skip_the_size_check()
    {
        var target = Target(mediaType: MediaType.Series, runtimeMinutes: 45);

        AssertAccepted(EvaluateOne(target, Candidate("Show.S00E01.German.1080p.WEB", Quality.WebDl1080p, sizeMb: 50_000, isSpecial: true)));
    }

    [Fact]
    public void Movie_ignores_episode_count()
    {
        AssertAccepted(EvaluateOne(Target(runtimeMinutes: 120, episodeCount: 10), Candidate("Dune.German.1080p", Quality.WebDl1080p, sizeMb: 6000)));
    }

    // Sample

    [Fact]
    public void Rejects_samples_even_without_runtime()
    {
        var decision = EvaluateOne(Target(runtimeMinutes: 0), Candidate("Dune.Part.Two.2024.German.1080p.WEB-GROUP.sample", Quality.WebDl1080p, sizeMb: 50));

        Assert.Equal("Release is a sample", AssertRejected(decision, RejectionCodes.Sample).Message);
    }

    [Fact]
    public void A_large_release_with_sample_in_the_title_is_no_sample()
    {
        AssertAccepted(EvaluateOne(Target(), Candidate("Free.Sample.2024.German.1080p.WEB-GROUP", Quality.WebDl1080p)));
    }

    // Blocklist

    [Fact]
    public void Rejects_blocklisted_releases_case_insensitive()
    {
        var target = Target(blocklist: ["Dune.Part.Two.2024.German.1080p.WEB-GROUP"]);

        AssertRejected(EvaluateOne(target, Candidate("Dune.Part.Two.2024.German.1080p.WEB-GROUP", Quality.WebDl1080p)), RejectionCodes.Blocklisted);
        AssertRejected(EvaluateOne(target, Candidate("dune.part.two.2024.german.1080p.web-group", Quality.WebDl1080p)), RejectionCodes.Blocklisted);
        AssertAccepted(EvaluateOne(target, Candidate("Dune.Part.Two.2024.German.1080p.WEB-OTHER", Quality.WebDl1080p)));
    }

    // Engine behaviour

    [Fact]
    public void Rejected_release_carries_all_reasons()
    {
        var decision = EvaluateOne(
            Target(monitored: false, blocklist: ["Dune.English.720p"]),
            Candidate("Dune.English.720p", Quality.WebDl720p, sizeMb: 100, languages: [Language.English]));

        Assert.Equal(
            [RejectionCodes.NotMonitored, RejectionCodes.QualityNotWanted, RejectionCodes.WantedLanguage, RejectionCodes.BelowMinimumSize, RejectionCodes.Blocklisted],
            decision.Rejections.Select(r => r.Code));
    }

    [Fact]
    public void Empty_input_gives_empty_result()
    {
        Assert.Empty(Evaluate(Target()));
    }

    [Fact]
    public void Custom_specifications_can_be_added()
    {
        IReadOnlyList<IDecisionSpecification> specifications = [.. DecisionEngine.DefaultSpecifications, new TemporaryRule()];

        var decision = Assert.Single(DecisionEngine.Evaluate(
            [Candidate("Dune.German.1080p", Quality.WebDl1080p, published: Now.AddMinutes(-30))], Target(), Definitions, Now, specifications));

        Assert.False(decision.Accepted);
        Assert.True(decision.TemporarilyRejected);
        Assert.False(decision.PermanentlyRejected);
        Assert.Equal("MinimumAge", Assert.Single(decision.Rejections).Code);
    }

    [Fact]
    public void A_failing_specification_becomes_a_rejection()
    {
        IReadOnlyList<IDecisionSpecification> specifications = [new ThrowingRule()];

        var decision = Assert.Single(DecisionEngine.Evaluate(
            [Candidate("Dune.German.1080p", Quality.WebDl1080p)], Target(), Definitions, Now, specifications));

        var rejection = AssertRejected(decision, RejectionCodes.DecisionError);
        Assert.Equal("ThrowingRule: boom", rejection.Message);
    }

    [Fact]
    public void Specifications_see_the_current_time()
    {
        IReadOnlyList<IDecisionSpecification> specifications = [new TemporaryRule()];

        var fresh = Candidate("Dune.Fresh", Quality.WebDl1080p, published: Now.AddMinutes(-5));
        var old = Candidate("Dune.Old", Quality.WebDl1080p, published: Now.AddHours(-5));
        var decisions = DecisionEngine.Evaluate([fresh, old], Target(), Definitions, Now, specifications);

        Assert.Equal("Dune.Old", decisions[0].Candidate.Release.ReleaseTitle);
        Assert.True(decisions[0].Accepted);
        Assert.True(decisions[1].TemporarilyRejected);
    }

    private sealed class TemporaryRule : IDecisionSpecification
    {
        public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context) =>
            context.Now - candidate.PublishDate < TimeSpan.FromHours(2)
                ? new Rejection("MinimumAge", "Release is younger than 2 hours", RejectionKind.Temporary)
                : null;
    }

    private sealed class ThrowingRule : IDecisionSpecification
    {
        public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context) =>
            throw new InvalidOperationException("boom");
    }
}
