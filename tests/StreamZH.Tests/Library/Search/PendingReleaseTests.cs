// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Decisions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Library.Rss;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Search;

/// <summary>0.11: protocol delay with pending releases (Radarr's delay profile, one global profile in Settings → Indexers).</summary>
public sealed class PendingReleaseTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    private void Delays(int usenet = 0, int torrent = 0, ProtocolPreference preferred = ProtocolPreference.Usenet, bool bypass = true) =>
        _bed.Options.Save(_bed.Options.Values with { UsenetDelay = usenet, TorrentDelay = torrent, PreferredProtocol = preferred, BypassDelayIfHighestQuality = bypass });

    private ReleaseInfo Fresh(string title, double sizeGb = 6, double ageMinutes = 10, int indexerId = 1) =>
        _bed.Release(title, sizeGb, indexerId, ageDays: ageMinutes / (24 * 60));

    [Fact]
    public async Task Rss_keeps_a_young_release_pending_and_grabs_the_best_one_once_the_delay_passed()
    {
        Delays(usenet: 60, bypass: false);
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.ReturnsRecent(1, Fresh("Dune.2021.German.DL.1080p.WEB.H264-GRP", ageMinutes: 10));

        var first = await _bed.Rss().SyncAsync();
        Assert.Empty(first.Grabbed);
        Assert.Equal((1, 0, 1), (first.Matched, first.Accepted, first.Delayed));
        var pending = Assert.Single(_bed.Pending.All());
        Assert.Equal((_bed.Dune.Id, "Dune.2021.German.DL.1080p.WEB.H264-GRP"), (pending.MovieId!.Value, pending.Release.Title));
        Assert.Contains("60 minute delay for Usenet", pending.Reason, StringComparison.Ordinal);

        // 20 minutes later a better release turns up; it waits too (the delay counts from the oldest pending release).
        _bed.Time.Now += TimeSpan.FromMinutes(20);
        _bed.Client.ReturnsRecent(1, Fresh("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12, ageMinutes: 1));
        Assert.Empty((await _bed.Rss().SyncAsync()).Grabbed);
        Assert.Equal(2, _bed.Pending.All().Count);

        // Delay over: the pending task (no indexer call) grabs the better one and clears the movie's entries.
        _bed.Time.Now += TimeSpan.FromMinutes(41);
        _bed.Client.RecentFetches.Clear();
        var processed = await _bed.Rss().ProcessPendingAsync();
        Assert.Equal(["Dune.2021.German.DL.1080p.BluRay.x264-GRP"], processed.Grabbed);
        Assert.Empty(_bed.Client.RecentFetches);
        Assert.Empty(_bed.Pending.All());
        Assert.Equal(["Dune.2021.German.DL.1080p.BluRay.x264-GRP.nzb"], _bed.NzbFiles());
    }

    [Fact]
    public async Task A_release_of_another_protocol_without_delay_is_grabbed_at_once_and_clears_the_pending_ones()
    {
        Delays(usenet: 0, torrent: 120, preferred: ProtocolPreference.Usenet);
        _bed.AddIndexer(1, "Alpha");
        var torrent = Fresh("Dune.2021.German.DL.1080p.BluRay.x264-TOR", 12) with { Protocol = DownloadProtocol.Torrent, Seeders = 50 };
        _bed.Client.ReturnsRecent(1, torrent);

        Assert.Empty((await _bed.Rss().SyncAsync()).Grabbed);
        Assert.Single(_bed.Pending.All());

        _bed.Client.ReturnsRecent(1, Fresh("Dune.2021.German.DL.1080p.WEB.H264-NZB", 6));
        Assert.Equal(["Dune.2021.German.DL.1080p.WEB.H264-NZB"], (await _bed.Rss().SyncAsync()).Grabbed);
        Assert.Empty(_bed.Pending.All());
    }

    [Fact]
    public async Task Automatic_search_adds_delayed_releases_and_an_interactive_search_ignores_the_delay()
    {
        Delays(usenet: 60, bypass: false);
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1, Fresh("Dune.2021.German.DL.1080p.WEB.H264-GRP"));

        Assert.Equal(0, await _bed.Automatic().SearchMoviesAsync([_bed.Dune.Id]));
        Assert.Single(_bed.Pending.All());

        var interactive = await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);
        Assert.True(Assert.Single(interactive.Decisions).Accepted);

        var automatic = await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.AutomaticSearch);
        Assert.Equal(RejectionCodes.MinimumAgeDelay, Assert.Single(Assert.Single(automatic.Decisions).Rejections).Code);

        _bed.Time.Now += TimeSpan.FromHours(1);
        Assert.Equal(1, await _bed.Automatic().SearchMoviesAsync([_bed.Dune.Id]));
        Assert.Empty(_bed.Pending.All());
    }

    [Fact]
    public async Task Episodes_wait_per_episode_and_a_grab_clears_only_its_episodes()
    {
        Delays(usenet: 60, bypass: false);
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.ReturnsRecent(1,
            Fresh("Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP", 2),
            Fresh("Breaking.Bad.S01E03.German.DL.1080p.WEB.h264-GRP", 2, ageMinutes: 90));

        var result = await _bed.Rss().SyncAsync();
        Assert.Equal(["Breaking.Bad.S01E03.German.DL.1080p.WEB.h264-GRP"], result.Grabbed);
        var waiting = Assert.Single(_bed.Pending.All());
        Assert.Equal([_bed.Episode(1, 2).Id], waiting.EpisodeIds);
        Assert.Equal(_bed.Show.Id, waiting.SeriesId);
    }

    [Fact]
    public void The_list_survives_a_restart_expires_and_can_be_cleared()
    {
        var subject = new SearchSubject(Core.Media.MediaType.Movie, _bed.Dune.Id, null, "Dune");
        var release = Fresh("Dune.2021.German.DL.1080p.WEB.H264-GRP") with { Protocol = DownloadProtocol.Torrent, Seeders = 7, IndexerFlags = 1 };
        var candidate = new ReleaseCandidate(new Core.Quality.ParsedRelease { ReleaseTitle = release.Title, Title = "Dune", CleanTitle = "dune" }, 0, "Alpha", 25, release.PublishDate, release.DownloadUrl, release.Guid);
        var delayed = new ReleaseDecision(release, null, new Decision(candidate, [new Rejection(RejectionCodes.MinimumAgeDelay, "wait", RejectionKind.Temporary)]), subject, []);
        var rejected = delayed with { Decision = new Decision(candidate, [new Rejection(RejectionCodes.MinimumAgeDelay, "wait", RejectionKind.Temporary), new Rejection(RejectionCodes.Sample, "Sample")]) };

        Assert.Single(PendingReleaseService.DelayedOnly([delayed, rejected]));
        _bed.Pending.Add([delayed]);
        _bed.Pending.Add([delayed]);

        var reloaded = new PendingReleaseService(_bed.Config, _bed.Time, NullLogger<PendingReleaseService>.Instance);
        var entry = Assert.Single(reloaded.All());
        Assert.Equal((DownloadProtocol.Torrent, 7, 1, "wait"), (entry.Release.Protocol, entry.Release.Seeders!.Value, entry.Release.IndexerFlags, entry.Reason));
        Assert.Equal(release.PublishDate, reloaded.OldestForMovie(_bed.Dune.Id));

        Assert.True(reloaded.Remove(entry.Id));
        Assert.False(reloaded.Remove(entry.Id));

        _bed.Pending.Add([delayed]);
        _bed.Time.Now += PendingReleaseService.MaxAge + TimeSpan.FromMinutes(1);
        Assert.Empty(new PendingReleaseService(_bed.Config, _bed.Time, NullLogger<PendingReleaseService>.Instance).All());
    }

    [Fact]
    public void The_pending_task_only_runs_with_a_delay_or_pending_releases()
    {
        var task = new PendingReleasesTask(_bed.Options, _bed.Pending);
        Assert.Null(task.Interval);
        Delays(torrent: 30);
        Assert.Equal(PendingReleasesTask.Every, task.Interval);
    }

    public void Dispose() => _bed.Dispose();
}
