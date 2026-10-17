// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Search;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.Rss;

public sealed class RssSyncServiceTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    private void Save(Func<IndexerOptionValues, IndexerOptionValues> change) => _bed.Options.Save(change(_bed.Options.Values));

    [Fact]
    public async Task Grabs_the_best_release_of_wanted_items_only()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.ReturnsRecent(1,
            _bed.Release("Dune.2021.720p.WEB.H264-ENG", 3),
            _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6),
            _bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12),
            _bed.Release("Arrival.2016.German.DL.1080p.BluRay.x264-GRP", 12),
            _bed.Release("Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP", 2),
            _bed.Release("Some.Show.S01E01.German.DL.1080p.WEB.h264-GRP", 2),
            _bed.Release("not a release name"));

        var result = await _bed.Rss().SyncAsync();

        Assert.Equal(["Dune.2021.German.DL.1080p.BluRay.x264-GRP", "Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP"], result.Grabbed);
        Assert.Equal((1, 0, 7, 4, 3), (result.Indexers, result.FailedIndexers, result.Releases, result.Matched, result.Accepted));
        Assert.Equal(
            ["Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP.nzb", "Dune.2021.German.DL.1080p.BluRay.x264-GRP.nzb"],
            _bed.NzbFiles());
        Assert.Empty(_bed.Client.Searches);
        Assert.Equal([1], _bed.Client.RecentFetches);

        // The history knows which episode and which movie.
        var episode = _bed.Episode(1, 2).Id;
        Assert.Contains(episode, _bed.History.EpisodesWithEventSince(_bed.Show.Id, HistoryEventType.Grabbed, DateTime.MinValue));
    }

    [Fact]
    public async Task Unmonitored_items_and_files_that_meet_the_cutoff_are_not_grabbed()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.ReturnsRecent(1,
            _bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12),
            _bed.Release("Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP", 2));
        _bed.Dune.File = new MediaFile("Dune.2021.German.DL.1080p.BluRay.x264-OLD.mkv", 10, DateTime.UtcNow);
        _bed.Movies.Update(_bed.Dune);
        _bed.Show.Monitored = false;
        _bed.Series.Update(_bed.Show);

        var result = await _bed.Rss().SyncAsync();

        Assert.Empty(result.Grabbed);
        Assert.Equal((2, 0), (result.Matched, result.Accepted));
    }

    [Fact]
    public async Task An_upgrade_below_the_cutoff_is_grabbed()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.ReturnsRecent(1, _bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12));
        _bed.Dune.File = new MediaFile("Dune.2021.German.DL.720p.WEB.H264-OLD.mkv", 3, DateTime.UtcNow);
        _bed.Movies.Update(_bed.Dune);

        Assert.Equal(["Dune.2021.German.DL.1080p.BluRay.x264-GRP"], (await _bed.Rss().SyncAsync()).Grabbed);
    }

    [Fact]
    public async Task Items_in_the_download_queue_are_not_grabbed_again()
    {
        _bed.UseBuiltInClient();
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.ReturnsRecent(1,
            _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6),
            _bed.Release("Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP", 2),
            _bed.Release("Breaking.Bad.S01.German.DL.1080p.WEB.h264-GRP", 20));

        // The pack is the best release for the series; the single episode overlaps it and is left out.
        var first = await _bed.Rss().SyncAsync();
        Assert.Equal(["Dune.2021.German.DL.1080p.WEB.H264-GRP", "Breaking.Bad.S01.German.DL.1080p.WEB.h264-GRP"], first.Grabbed);
        Assert.Equal(2, _bed.Engine.Active().Count);

        var second = await _bed.Rss().SyncAsync();
        Assert.Empty(second.Grabbed);
        Assert.Equal(2, second.SkippedPending);
        Assert.Equal(2, _bed.Engine.Active().Count);

        // Out of the queue (e.g. removed): the next sync takes it again.
        await _bed.Engine.DeleteAsync(_bed.Engine.Active().Single(d => d.Link.MovieId == _bed.Dune.Id).Id, deleteFiles: true);
        Assert.Equal(["Dune.2021.German.DL.1080p.WEB.H264-GRP"], (await _bed.Rss().SyncAsync()).Grabbed);
    }

    [Fact]
    public async Task With_the_blackhole_folder_recent_grabs_are_skipped_for_12_hours()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.ReturnsRecent(1, _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6));

        Assert.Single((await _bed.Rss().SyncAsync()).Grabbed);
        Assert.Equal(1, (await _bed.Rss().SyncAsync()).SkippedPending);

        _bed.Time.Now += TimeSpan.FromHours(13);
        Assert.Single((await _bed.Rss().SyncAsync()).Grabbed);
    }

    [Fact]
    public async Task Indexers_without_rss_or_at_their_limit_are_skipped_and_grab_limits_fall_back_to_the_next_indexer()
    {
        _bed.AddIndexer(1, "Alpha", priority: 1);
        _bed.AddIndexer(2, "Beta");
        _bed.Indexers.Indexers.Add(FakeIndexerProvider.Definition(3, "NoRss") with { EnableRss = false });
        _bed.Indexers.Indexers.Add(FakeIndexerProvider.Definition(4, "Limited"));
        _bed.Client.ReturnsRecent(1, _bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12, indexerId: 1));
        _bed.Client.ReturnsRecent(2, _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-OTHER", 6, indexerId: 2));
        _bed.Client.ReturnsRecent(3, _bed.Release("Dune.2021.German.DL.2160p.BluRay.x265-GRP", 30, indexerId: 3));
        _bed.Client.RecentHandlers[4] = _ => throw new IndexerException("Query limit of 10 per day reached") { IsLimitReached = true };
        _bed.Client.Nzb = (indexer, _) => indexer.Id == 1
            ? throw new IndexerException("Grab limit of 5 per day reached") { IsLimitReached = true }
            : FakeIndexerClient.ValidNzb;

        var result = await _bed.Rss().SyncAsync();

        Assert.Equal((3, 1), (result.Indexers, result.FailedIndexers));
        Assert.DoesNotContain(3, _bed.Client.RecentFetches);
        Assert.Equal(["Dune.2021.German.DL.1080p.WEB.H264-OTHER"], result.Grabbed);
        Assert.Equal(2, _bed.Client.Downloads.Count);
    }

    [Fact]
    public async Task A_failing_indexer_is_recorded_and_the_others_still_count()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.AddIndexer(2, "Beta");
        _bed.Client.RecentHandlers[1] = _ => throw new HttpRequestException("Connection refused");
        _bed.Client.ReturnsRecent(2, _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6, indexerId: 2));

        var result = await _bed.Rss().SyncAsync();

        Assert.Single(result.Grabbed);
        Assert.Equal(1, result.FailedIndexers);
        Assert.Equal(1, Assert.Single(_bed.Indexers.Failures).IndexerId);
    }

    [Fact]
    public async Task Release_rules_apply_to_rss()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.ReturnsRecent(1,
            _bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12, ageDays: 0.001),
            _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6),
            _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-BAD", 6));
        _bed.ReleaseProfiles.Add(new ReleaseProfile { Name = "Groups", Ignored = ["-BAD"] });
        Save(v => v with { MinimumAge = 30 });

        // The Bluray is only minutes old (propagation), the -BAD one is excluded: the WEB-DL from GRP is taken.
        Assert.Equal(["Dune.2021.German.DL.1080p.WEB.H264-GRP"], (await _bed.Rss().SyncAsync()).Grabbed);
    }

    [Fact]
    public async Task Preferred_terms_decide_between_releases_of_the_same_quality()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.ReturnsRecent(1,
            _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6),
            _bed.Release("Dune.2021.German.DL.1080p.WEB.H265-FAV", 4));
        _bed.ReleaseProfiles.Add(new ReleaseProfile { Name = "Favourites", Preferred = [new("/-FAV$/", 100)] });

        Assert.Equal(["Dune.2021.German.DL.1080p.WEB.H265-FAV"], (await _bed.Rss().SyncAsync()).Grabbed);
    }

    [Fact]
    public async Task Movies_that_are_not_available_yet_wait()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.ReturnsRecent(1, _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6));
        _bed.Dune.InCinemas = "2026-10-01";
        _bed.Movies.Update(_bed.Dune);
        Save(v => v with { MinimumAvailability = MinimumAvailability.Released });

        var waiting = await _bed.Rss().SyncAsync();
        Assert.Empty(waiting.Grabbed);
        Assert.Equal(0, waiting.Accepted);

        Save(v => v with { MinimumAvailability = MinimumAvailability.InCinemas });
        Assert.Single((await _bed.Rss().SyncAsync()).Grabbed);
    }

    [Fact]
    public async Task Nothing_to_do_without_rss_indexers()
    {
        _bed.Indexers.Indexers.Add(FakeIndexerProvider.Definition(1, "NoRss") with { EnableRss = false });

        Assert.Equal(StreamZH.Library.Rss.RssSyncResult.Empty, await _bed.Rss().SyncAsync());
        Assert.Empty(_bed.Client.RecentFetches);
    }

    public void Dispose() => _bed.Dispose();
}
