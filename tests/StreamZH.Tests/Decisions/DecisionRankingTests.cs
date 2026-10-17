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

public class DecisionRankingTests
{
    private static string[] Titles(IReadOnlyList<Decision> decisions) =>
        decisions.Select(d => d.Candidate.Release.ReleaseTitle).ToArray();

    [Fact]
    public void German_dual_language_beats_german_and_english_is_rejected_last()
    {
        var decisions = Evaluate(
            Target(GermanProfile()),
            Candidate("Dune.Part.Two.2024.1080p.WEB.H264-ENG", Quality.WebDl1080p, languages: [Language.English]),
            Candidate("Dune.Part.Two.2024.German.1080p.WEB.H264-GER", Quality.WebDl1080p, languages: [Language.German]),
            Candidate("Dune.Part.Two.2024.German.DL.1080p.WEB.H264-DL", Quality.WebDl1080p, languages: [Language.German, Language.English]));

        Assert.Equal(
            ["Dune.Part.Two.2024.German.DL.1080p.WEB.H264-DL", "Dune.Part.Two.2024.German.1080p.WEB.H264-GER", "Dune.Part.Two.2024.1080p.WEB.H264-ENG"],
            Titles(decisions));
        Assert.True(decisions[0].Accepted);
        Assert.True(decisions[1].Accepted);
        Assert.False(decisions[2].Accepted);
        Assert.Equal(RejectionCodes.WantedLanguage, Assert.Single(decisions[2].Rejections).Code);
    }

    [Fact]
    public void Without_dual_language_preference_the_language_does_not_rank()
    {
        var decisions = Evaluate(
            Target(GermanProfile(preferDualLanguage: false)),
            Candidate("German", Quality.WebDl1080p, languages: [Language.German], indexerPriority: 1),
            Candidate("German.DL", Quality.WebDl1080p, languages: [Language.German, Language.English], indexerPriority: 2));

        Assert.Equal(["German", "German.DL"], Titles(decisions));
    }

    [Fact]
    public void Higher_profile_quality_ranks_first()
    {
        var decisions = Evaluate(
            Target(),
            Candidate("WEB", Quality.WebDl1080p),
            Candidate("BluRay", Quality.Bluray1080p),
            Candidate("WEBRip", Quality.WebRip1080p));

        Assert.Equal(["BluRay", "WEBRip", "WEB"], Titles(decisions));
    }

    [Fact]
    public void Quality_beats_dual_language()
    {
        var decisions = Evaluate(
            Target(),
            Candidate("WEB.German.DL", Quality.WebDl1080p, languages: [Language.German, Language.English]),
            Candidate("BluRay.German", Quality.Bluray1080p, languages: [Language.German]));

        Assert.Equal(["BluRay.German", "WEB.German.DL"], Titles(decisions));
    }

    [Fact]
    public void Profile_order_decides_not_the_global_quality_weight()
    {
        // A profile that prefers WEB over BluRay (e.g. for streaming-first releases).
        var profile = GermanProfile() with
        {
            Items = [new(Quality.Bluray1080p.Id, true), new(Quality.WebDl1080p.Id, true)],
            CutoffQualityId = Quality.WebDl1080p.Id,
        };

        var decisions = Evaluate(Target(profile), Candidate("BluRay", Quality.Bluray1080p), Candidate("WEB", Quality.WebDl1080p));

        Assert.Equal(["WEB", "BluRay"], Titles(decisions));
    }

    [Fact]
    public void Higher_revision_ranks_first_within_the_same_quality()
    {
        var decisions = Evaluate(
            Target(),
            Candidate("v1", Quality.WebDl1080p),
            Candidate("REAL", Quality.WebDl1080p, revision: new Revision(1, 1)),
            Candidate("PROPER", Quality.WebDl1080p, revision: new Revision(2)));

        Assert.Equal(["REAL", "PROPER", "v1"], Titles(decisions));
    }

    [Fact]
    public void Revision_beats_dual_language()
    {
        var decisions = Evaluate(
            Target(),
            Candidate("German.DL", Quality.WebDl1080p, languages: [Language.German, Language.English]),
            Candidate("German.PROPER", Quality.WebDl1080p, revision: new Revision(2)));

        Assert.Equal(["German.PROPER", "German.DL"], Titles(decisions));
    }

    [Fact]
    public void Lower_indexer_priority_number_ranks_first()
    {
        var decisions = Evaluate(
            Target(),
            Candidate("Prio50", Quality.WebDl1080p, indexerPriority: 50),
            Candidate("Prio1", Quality.WebDl1080p, indexerPriority: 1),
            Candidate("Prio25", Quality.WebDl1080p, indexerPriority: 25));

        Assert.Equal(["Prio1", "Prio25", "Prio50"], Titles(decisions));
    }

    [Fact]
    public void Dual_language_beats_indexer_priority()
    {
        var decisions = Evaluate(
            Target(),
            Candidate("German", Quality.WebDl1080p, indexerPriority: 1),
            Candidate("German.DL", Quality.WebDl1080p, languages: [Language.German, Language.English], indexerPriority: 50));

        Assert.Equal(["German.DL", "German"], Titles(decisions));
    }

    [Fact]
    public void Size_closest_to_preferred_ranks_first()
    {
        // Preferred: 50 MB/min × 120 min = 6000 MB.
        var decisions = Evaluate(
            Target(),
            Candidate("11000MB", Quality.WebDl1080p, sizeMb: 11000),
            Candidate("6100MB", Quality.WebDl1080p, sizeMb: 6100),
            Candidate("3000MB", Quality.WebDl1080p, sizeMb: 3000));

        Assert.Equal(["6100MB", "3000MB", "11000MB"], Titles(decisions));
    }

    [Fact]
    public void Without_preferred_size_larger_ranks_first()
    {
        var definitions = new Dictionary<int, QualityDefinition>
        {
            [Quality.WebDl1080p.Id] = new(Quality.WebDl1080p.Id, "WEBDL-1080p", 5, 100, null),
        };

        var decisions = DecisionEngine.Evaluate(
            [
                Candidate("3000MB", Quality.WebDl1080p, sizeMb: 3000),
                Candidate("9000MB", Quality.WebDl1080p, sizeMb: 9000),
                Candidate("6000MB", Quality.WebDl1080p, sizeMb: 6000),
            ],
            Target(),
            definitions,
            Now);

        Assert.Equal(["9000MB", "6000MB", "3000MB"], Titles(decisions));
    }

    [Fact]
    public void Without_runtime_larger_ranks_first()
    {
        var decisions = Evaluate(
            Target(runtimeMinutes: 0),
            Candidate("6000MB", Quality.WebDl1080p, sizeMb: 6000),
            Candidate("20000MB", Quality.WebDl1080p, sizeMb: 20000));

        Assert.Equal(["20000MB", "6000MB"], Titles(decisions));
    }

    [Fact]
    public void Season_pack_preferred_size_uses_episode_count()
    {
        // Preferred: 50 MB/min × 45 min × 10 episodes = 22500 MB.
        var decisions = Evaluate(
            Target(mediaType: MediaType.Series, runtimeMinutes: 45, episodeCount: 10),
            Candidate("40000MB", Quality.WebDl1080p, sizeMb: 40000),
            Candidate("22000MB", Quality.WebDl1080p, sizeMb: 22000),
            Candidate("4000MB", Quality.WebDl1080p, sizeMb: 4000));

        Assert.Equal(["22000MB", "40000MB", "4000MB"], Titles(decisions));
    }

    [Fact]
    public void Indexer_priority_beats_size()
    {
        var decisions = Evaluate(
            Target(),
            Candidate("Ideal.Prio50", Quality.WebDl1080p, sizeMb: 6000, indexerPriority: 50),
            Candidate("Small.Prio1", Quality.WebDl1080p, sizeMb: 1000, indexerPriority: 1));

        Assert.Equal(["Small.Prio1", "Ideal.Prio50"], Titles(decisions));
    }

    [Fact]
    public void Newer_publish_date_breaks_the_tie()
    {
        // Sizes within the same 200 MB step count as equal.
        var decisions = Evaluate(
            Target(),
            Candidate("Old", Quality.WebDl1080p, sizeMb: 6000, published: Now.AddDays(-300)),
            Candidate("New", Quality.WebDl1080p, sizeMb: 6050, published: Now.AddHours(-2)),
            Candidate("Middle", Quality.WebDl1080p, sizeMb: 5990, published: Now.AddDays(-10)));

        Assert.Equal(["New", "Middle", "Old"], Titles(decisions));
    }

    [Fact]
    public void Accepted_come_first_even_when_a_rejected_one_ranks_higher()
    {
        var decisions = Evaluate(
            Target(blocklist: ["Remux.Blocked"]),
            Candidate("WEB", Quality.WebDl1080p),
            Candidate("Remux.Blocked", Quality.Remux1080p, sizeMb: 10_000),
            Candidate("720p", Quality.WebDl720p),
            Candidate("BluRay", Quality.Bluray1080p));

        Assert.Equal(["BluRay", "WEB", "Remux.Blocked", "720p"], Titles(decisions));
        Assert.Equal([true, true, false, false], decisions.Select(d => d.Accepted));
        Assert.Equal(["BluRay", "WEB"], Titles(DecisionEngine.Accepted(decisions)));
    }

    [Fact]
    public void Ranking_is_stable_for_identical_candidates()
    {
        var decisions = Evaluate(
            Target(),
            Candidate("First", Quality.WebDl1080p, published: Now.AddDays(-1)),
            Candidate("Second", Quality.WebDl1080p, published: Now.AddDays(-1)));

        Assert.Equal(["First", "Second"], Titles(decisions));
    }
}
