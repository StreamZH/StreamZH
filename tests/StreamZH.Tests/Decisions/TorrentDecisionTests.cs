// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Decisions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using static StreamZH.Tests.CustomFormats.CustomFormatTestData;
using static StreamZH.Tests.Decisions.DecisionTestData;
using Impl = StreamZH.Core.CustomFormats.CustomFormatImplementations;

namespace StreamZH.Tests.Decisions;

/// <summary>0.11: minimum seeders, Usenet-only rules, indexer flags, protocol preference, seeders ranking and the protocol delay.</summary>
public sealed class TorrentDecisionTests
{
    private static ReleaseCandidate Torrent(
        string title, Quality quality, int? seeders = 10, int? peers = null, int minimumSeeders = 1, DateTimeOffset? published = null, int flags = 0, int priority = 25) =>
        Candidate(title, quality, published: published, indexerPriority: priority) with
        {
            Protocol = DownloadProtocol.Torrent,
            Seeders = seeders,
            Peers = peers ?? seeders,
            MinimumSeeders = minimumSeeders,
            IndexerFlags = flags,
        };

    private static IReadOnlyList<Decision> Evaluate(DecisionTarget target, ReleaseRules rules, params ReleaseCandidate[] candidates) =>
        DecisionEngine.Evaluate(candidates, target, Definitions, Now, rules);

    // ------------------------------------------------------------------ minimum seeders

    [Fact]
    public void Too_few_seeders_are_rejected_unknown_seeders_pass()
    {
        var rejection = AssertRejected(EvaluateOne(Target(), Torrent("Dune.2024.German.1080p.WEB.x264-GRP", Quality.WebDl1080p, seeders: 2, minimumSeeders: 5)),
            RejectionCodes.MinimumSeeders);
        Assert.Equal("Not enough seeders: 2. Minimum seeders: 5", rejection.Message);
        Assert.Equal(RejectionKind.Permanent, rejection.Kind);

        AssertAccepted(EvaluateOne(Target(), Torrent("Dune.2024.German.1080p.WEB.x264-GRP", Quality.WebDl1080p, seeders: 5, minimumSeeders: 5)));
        AssertAccepted(EvaluateOne(Target(), Torrent("Dune.2024.German.1080p.WEB.x264-GRP", Quality.WebDl1080p, seeders: null, minimumSeeders: 5)));

        // Zero seeders with the default minimum of 1: dead torrent.
        AssertRejected(EvaluateOne(Target(), Torrent("Dune.2024.German.1080p.WEB.x264-GRP", Quality.WebDl1080p, seeders: 0)), RejectionCodes.MinimumSeeders);
    }

    [Fact]
    public void Retention_and_minimum_age_are_usenet_rules()
    {
        var rules = new ReleaseRules { RetentionDays = 100, MinimumAgeMinutes = 60 };
        var old = Torrent("Dune.2024.German.1080p.WEB.x264-OLD", Quality.WebDl1080p, published: Now.AddDays(-500));
        var fresh = Torrent("Dune.2024.German.1080p.WEB.x264-NEW", Quality.WebDl1080p, published: Now.AddMinutes(-5));

        Assert.All(Evaluate(Target(), rules, old, fresh), AssertAccepted);

        var usenet = Evaluate(Target(), rules, old with { Protocol = DownloadProtocol.Usenet }, fresh with { Protocol = DownloadProtocol.Usenet });
        Assert.All(usenet, d => Assert.False(d.Accepted));
    }

    // ------------------------------------------------------------------ flags → custom formats

    [Fact]
    public void Freeleech_flag_scores_through_the_indexer_flag_condition_for_movies_and_series()
    {
        var movieFreeleech = Movie("Freeleech", 1, Spec(Impl.IndexerFlag, 1));
        var movieInternal = Movie("Internal", 2, Spec(Impl.IndexerFlag, 32));
        var seriesInternal = Format(MediaType.Series, "Internal", 3, Spec(Impl.IndexerFlag, 8));
        var profile = GermanProfile() with { FormatItems = [new(1, 100), new(2, 10), new(3, 25)] };

        var movie = Target(profile) with { CustomFormats = [movieFreeleech, movieInternal] };
        var free = EvaluateOne(movie, Torrent("Dune.2024.German.1080p.WEB.x264-GRP", Quality.WebDl1080p, flags: IndexerFlagBits.Freeleech | IndexerFlagBits.Internal));
        Assert.Equal(110, free.CustomFormatScore);
        Assert.Equal(["Freeleech", "Internal"], free.CustomFormats.Select(f => f.Name));

        // Series formats use Sonarr's bits (Internal = 8); the candidate's Radarr bit 32 is mapped.
        var series = Target(profile, MediaType.Series) with { CustomFormats = [seriesInternal] };
        var episode = EvaluateOne(series, Torrent("Dune.S01E01.German.1080p.WEB.x264-GRP", Quality.WebDl1080p, flags: IndexerFlagBits.Internal));
        Assert.Equal(25, episode.CustomFormatScore);
    }

    // ------------------------------------------------------------------ ranking

    [Fact]
    public void Preferred_protocol_wins_when_quality_and_scores_are_equal()
    {
        var usenet = Candidate("Dune.2024.German.1080p.WEB.x264-NZB", Quality.WebDl1080p, indexerPriority: 50);
        var torrent = Torrent("Dune.2024.German.1080p.WEB.x264-TOR", Quality.WebDl1080p, priority: 1);
        var better = Torrent("Dune.2024.German.1080p.BluRay.x264-TOR", Quality.Bluray1080p);

        Assert.Equal("Dune.2024.German.1080p.WEB.x264-NZB", Evaluate(Target(), new ReleaseRules(), usenet, torrent)[0].Candidate.Guid);
        Assert.Equal("Dune.2024.German.1080p.WEB.x264-TOR",
            Evaluate(Target(), new ReleaseRules { PreferredProtocol = ProtocolPreference.Torrent }, usenet, torrent)[0].Candidate.Guid);

        // No preference: the indexer priority decides.
        Assert.Equal("Dune.2024.German.1080p.WEB.x264-TOR",
            Evaluate(Target(), new ReleaseRules { PreferredProtocol = ProtocolPreference.NoPreference }, usenet, torrent)[0].Candidate.Guid);

        // Quality still comes first.
        Assert.Equal("Dune.2024.German.1080p.BluRay.x264-TOR", Evaluate(Target(), new ReleaseRules(), usenet, better)[0].Candidate.Guid);
    }

    [Fact]
    public void More_seeders_rank_first_on_a_log_scale()
    {
        var few = Torrent("Dune.2024.German.1080p.WEB.x264-FEW", Quality.WebDl1080p, seeders: 3);
        var many = Torrent("Dune.2024.German.1080p.WEB.x264-MANY", Quality.WebDl1080p, seeders: 250);
        var similar = Torrent("Dune.2024.German.1080p.WEB.x264-SIM", Quality.WebDl1080p, seeders: 260, published: Now.AddDays(-3));

        var ranked = Evaluate(Target(), new ReleaseRules(), few, similar, many);
        Assert.Equal(["Dune.2024.German.1080p.WEB.x264-MANY", "Dune.2024.German.1080p.WEB.x264-SIM", "Dune.2024.German.1080p.WEB.x264-FEW"],
            ranked.Select(d => d.Candidate.Guid));
    }

    // ------------------------------------------------------------------ delay

    [Fact]
    public void A_young_release_waits_for_its_protocol_delay()
    {
        var rules = new ReleaseRules { TorrentDelayMinutes = 120, UsenetDelayMinutes = 0 };
        var young = Torrent("Dune.2024.German.1080p.WEB.x264-GRP", Quality.WebDl1080p, published: Now.AddMinutes(-30));

        var rejection = AssertRejected(Assert.Single(Evaluate(Target(), rules, young)), RejectionCodes.MinimumAgeDelay);
        Assert.Equal(RejectionKind.Temporary, rejection.Kind);
        Assert.Equal("Waiting for a better release: 120 minute delay for torrents, 90 minutes left", rejection.Message);

        AssertAccepted(Assert.Single(Evaluate(Target(), rules, young with { PublishDate = Now.AddMinutes(-121) })));
        AssertAccepted(Assert.Single(Evaluate(Target(), rules, young with { Protocol = DownloadProtocol.Usenet })));
        AssertAccepted(Assert.Single(Evaluate(Target(), rules.WithoutDelays(), young)));
    }

    [Fact]
    public void Best_quality_of_the_preferred_protocol_skips_the_delay()
    {
        var rules = new ReleaseRules { PreferredProtocol = ProtocolPreference.Torrent, TorrentDelayMinutes = 60, UsenetDelayMinutes = 60 };
        var remux = Torrent("Dune.2024.German.1080p.Remux-GRP", Quality.Remux1080p, published: Now.AddMinutes(-1));
        var web = Torrent("Dune.2024.German.1080p.WEB.x264-GRP", Quality.WebDl1080p, published: Now.AddMinutes(-1));
        var usenetRemux = remux with { Protocol = DownloadProtocol.Usenet };

        AssertAccepted(Assert.Single(Evaluate(Target(), rules, remux)));
        AssertRejected(Assert.Single(Evaluate(Target(), rules, web)), RejectionCodes.MinimumAgeDelay);
        AssertRejected(Assert.Single(Evaluate(Target(), rules, usenetRemux)), RejectionCodes.MinimumAgeDelay);
        AssertRejected(Assert.Single(Evaluate(Target(), rules with { BypassDelayIfHighestQuality = false }, remux)), RejectionCodes.MinimumAgeDelay);
    }

    [Fact]
    public void A_better_revision_of_the_file_on_disk_skips_the_delay_for_the_preferred_protocol()
    {
        var rules = new ReleaseRules { UsenetDelayMinutes = 60 };
        var target = Target(existing: new QualityModel(Quality.WebDl1080p), existingLanguages: [Language.German]);
        var proper = Candidate("Dune.2024.German.1080p.WEB.PROPER.x264-GRP", Quality.WebDl1080p, revision: new Revision(2), published: Now.AddMinutes(-1));

        AssertAccepted(Assert.Single(Evaluate(target, rules, proper)));
    }

    [Fact]
    public void Once_the_oldest_pending_release_waited_long_enough_newer_ones_are_taken_too()
    {
        var rules = new ReleaseRules { TorrentDelayMinutes = 60 };
        var newest = Torrent("Dune.2024.German.1080p.WEB.x264-GRP", Quality.WebDl1080p, published: Now.AddMinutes(-5));

        AssertRejected(Assert.Single(Evaluate(Target() with { PendingSince = Now.AddMinutes(-30) }, rules, newest)), RejectionCodes.MinimumAgeDelay);
        AssertAccepted(Assert.Single(Evaluate(Target() with { PendingSince = Now.AddMinutes(-61) }, rules, newest)));
    }

    [Fact]
    public void Delayed_decisions_are_temporary_only_when_nothing_else_rejects_them()
    {
        var rules = new ReleaseRules { TorrentDelayMinutes = 60 };
        var dead = Torrent("Dune.2024.German.1080p.WEB.x264-GRP", Quality.WebDl1080p, seeders: 0, published: Now.AddMinutes(-5));

        var decision = Assert.Single(Evaluate(Target(), rules, dead));
        Assert.False(decision.TemporarilyRejected);
        Assert.Contains(decision.Rejections, r => r.Code == RejectionCodes.MinimumAgeDelay);
    }
}
