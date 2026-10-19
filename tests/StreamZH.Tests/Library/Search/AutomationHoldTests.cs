// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Import;
using StreamZH.Library.Rss;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Search;

/// <summary>Side-by-side mode: no automatic grab on any path, grabbing by hand still works.</summary>
public sealed class AutomationHoldTests : IDisposable
{
    private const string Dune = "Dune.2021.German.DL.1080p.BluRay.x264-GRP";
    private const string Episode = "Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP";

    private readonly SearchTestBed _bed = new();
    private readonly AutomationHold _hold;

    public AutomationHoldTests()
    {
        _hold = new AutomationHold(_bed.Config, _bed.Time, _bed.Events);
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1, _bed.Release(Dune, 12), _bed.Release(Episode, 2), _bed.Release("Breaking.Bad.S01.German.DL.1080p.WEB.h264-GRP", 20));
        _bed.Client.ReturnsRecent(1, _bed.Release(Dune, 12), _bed.Release(Episode, 2));
    }

    [Fact]
    public void Off_by_default_and_on_keeps_the_first_since()
    {
        Assert.Equal(new AutomationHoldState(false, null), _hold.State());

        var on = _hold.Set(true);
        Assert.True(on.Hold);
        Assert.Equal(_bed.Time.Now.UtcDateTime, on.Since);

        _bed.Time.Now += TimeSpan.FromHours(3);
        Assert.Equal(on, _hold.Set(true));

        Assert.Equal(new AutomationHoldState(false, null), _hold.Set(false));
        Assert.Equal("false", _bed.Config.Get(ConfigKeys.AutomationHold));
    }

    [Fact]
    public async Task Rss_sync_asks_no_indexer_and_grabs_nothing()
    {
        _hold.Set(true);

        var result = await Rss().SyncAsync();

        Assert.Empty(result.Grabbed);
        Assert.Empty(_bed.Client.RecentFetches);
        Assert.Empty(_bed.NzbFiles());

        _hold.Set(false);
        Assert.Equal([Dune, Episode], (await Rss().SyncAsync()).Grabbed);
    }

    [Fact]
    public async Task Automatic_movie_searches_do_not_run()
    {
        _hold.Set(true);
        var automatic = Automatic();

        Assert.Equal(0, await automatic.SearchMoviesAsync([_bed.Dune.Id]));
        Assert.Equal(0, await automatic.SearchNewMovieAsync(_bed.Dune.Id));
        Assert.Equal(0, await automatic.SearchMissingMoviesAsync());

        Assert.Empty(_bed.Client.Searches);
        Assert.Empty(_bed.NzbFiles());
        Assert.All(_bed.Finished, f => Assert.Equal(AutomationHold.SkipReason, f.SkipReason));
        Assert.Equal(3, _bed.Finished.Count);
    }

    [Fact]
    public async Task Automatic_episode_searches_do_not_run()
    {
        _hold.Set(true);
        var automatic = Automatic();

        Assert.Equal(0, await automatic.SearchMissingEpisodesAsync(_bed.Show.Id));
        Assert.Equal(0, await automatic.SearchSeasonAsync(_bed.Show.Id, 1));
        Assert.Equal(0, await automatic.SearchEpisodesAsync(_bed.Show.Id, [_bed.Episode(1, 2).Id]));

        Assert.Empty(_bed.Client.Searches);
        Assert.Empty(_bed.NzbFiles());
        Assert.All(_bed.Finished, f => Assert.Equal(AutomationHold.SkipReason, f.SkipReason));
    }

    [Fact]
    public async Task A_failed_download_is_not_searched_again()
    {
        _hold.Set(true);
        var failed = new DownloadItem
        {
            Id = 7,
            Title = "Dune.2021.German.DL.1080p.WEB.H264-BROKEN",
            Status = DownloadStatus.Failed,
            Link = new MediaLink(MediaType.Movie, _bed.Dune.Id, null, []),
        };

        await FailedDownloadHandler.SearchAgainAsync(failed, Automatic(), _bed.Series, CancellationToken.None);

        Assert.Empty(_bed.Client.Searches);
        Assert.Empty(_bed.NzbFiles());
    }

    [Fact]
    public async Task The_grab_itself_refuses_automatic_flows()
    {
        var search = await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);
        var decision = search.Decisions.First(d => d.Release.Title == Dune);
        _hold.Set(true);

        foreach (var source in new[] { IndexerCallSource.Automatic, IndexerCallSource.Rss })
        {
            using (IndexerCallContext.Begin(source))
            {
                var error = await Assert.ThrowsAsync<LibraryException>(() => Grab().GrabAsync(decision));
                Assert.Equal(AutomationHold.Message, error.Message);
            }
        }

        Assert.Empty(_bed.NzbFiles());
        Assert.Empty(_bed.Client.Downloads);
    }

    [Fact]
    public async Task Searching_and_grabbing_by_hand_still_work()
    {
        _hold.Set(true);

        var search = await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);
        var release = search.Decisions.First(d => d.Release.Title == Dune).Release;
        var grabbed = await Grab().GrabAsync(release.IndexerId, release.Guid);

        Assert.Equal($"{Dune}.nzb", Path.GetFileName(grabbed.NzbPath));
        Assert.Equal([$"{Dune}.nzb"], _bed.NzbFiles());
    }

    public void Dispose() => _bed.Dispose();

    private GrabService Grab() =>
        new(_bed.Indexers, _bed.Client, _bed.Cache, _bed.Clients, _bed.Downloads, _bed.History, _bed.Events, NullLogger<GrabService>.Instance, _bed.Time, _hold);

    private AutomaticSearchService Automatic() =>
        new(_bed.Search(), Grab(), _bed.Movies, _bed.Series, _bed.History, _bed.Downloads, _bed.Events, NullLogger<AutomaticSearchService>.Instance, _bed.Engine, _bed.Time,
            _bed.Options, new PendingImports(new DownloadRepository(_bed.Database), new DownloadImportRepository(_bed.Database)), automationHold: _hold);

    private RssSyncService Rss() =>
        new(_bed.Indexers, _bed.Client, _bed.Movies, _bed.Series, new DecisionTargetFactory(_bed.Profiles), _bed.Definitions, Grab(), _bed.History, _bed.Downloads,
            _bed.Options, NullLogger<RssSyncService>.Instance, _bed.Engine, _bed.Time, automationHold: _hold)
        {
            IndexerTimeout = TimeSpan.FromSeconds(10),
        };
}
