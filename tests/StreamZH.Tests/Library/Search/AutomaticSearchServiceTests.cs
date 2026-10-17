// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Search;

public sealed class AutomaticSearchServiceTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    [Fact]
    public async Task Grabs_the_best_accepted_release_and_skips_the_movie_for_12_hours()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1,
            _bed.Release("Dune.2021.720p.WEB.H264-ENG", 3),
            _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6),
            _bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12));
        var automatic = _bed.Automatic();

        Assert.Equal(1, await automatic.SearchMoviesAsync([_bed.Dune.Id]));
        Assert.Equal(["Dune.2021.German.DL.1080p.BluRay.x264-GRP.nzb"], _bed.NzbFiles());
        Assert.Equal((MediaType.Movie, (int?)_bed.Dune.Id, 1, 1), Fields(Assert.Single(_bed.Finished)));

        // Grabbed 11 hours ago: no new search, the indexer is not even asked.
        _bed.Time.Now += TimeSpan.FromHours(11);
        Assert.Equal(0, await automatic.SearchMoviesAsync([_bed.Dune.Id]));
        Assert.Single(_bed.Client.Searches);

        _bed.Time.Now += TimeSpan.FromHours(2);
        Assert.Equal(1, await _bed.Automatic().SearchMoviesAsync([_bed.Dune.Id]));
        Assert.Equal(2, _bed.Client.Searches.Count);
    }

    [Fact]
    public async Task Nothing_acceptable_means_nothing_grabbed()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1, _bed.Release("Dune.2021.720p.WEB.H264-ENG", 3), _bed.Release("Arrival.2016.German.1080p.WEB.H264-GRP"));

        Assert.Equal(0, await _bed.Automatic().SearchMoviesAsync([_bed.Dune.Id]));
        Assert.Empty(_bed.NzbFiles());
        Assert.Equal(0, _bed.History.Page(1, 10).Total);
    }

    [Fact]
    public async Task When_the_nzb_download_fails_the_next_release_from_another_indexer_is_taken()
    {
        _bed.AddIndexer(1, "Alpha", priority: 1);
        _bed.AddIndexer(2, "Beta");
        _bed.Client.Returns(1,
            _bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12, indexerId: 1),
            _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6, indexerId: 1));
        _bed.Client.Returns(2, _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-OTHER", 6, indexerId: 2));
        _bed.Client.Nzb = (indexer, release) =>
            indexer.Id == 1 ? throw new IndexerException("Download limit reached", 501) : FakeIndexerClient.ValidNzb;

        Assert.Equal(1, await _bed.Automatic().SearchMoviesAsync([_bed.Dune.Id]));
        Assert.Equal(["Dune.2021.German.DL.1080p.WEB.H264-OTHER.nzb"], _bed.NzbFiles());

        // Only one attempt on the failing indexer.
        Assert.Equal(1, _bed.Client.Downloads.Count(r => r.IndexerId == 1));
    }

    [Fact]
    public async Task Missing_movies_are_monitored_released_movies_without_a_file()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1, _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP"));
        var upcoming = new Movie
        {
            TmdbId = 1, Title = "Future", OriginalTitle = "Future", SortTitle = "future", Year = 2027, Status = "In Production",
            Monitored = true, QualityProfileId = QualityDefaults.DefaultProfileId, RootFolderId = _bed.Dune.RootFolderId,
            FolderPath = Path.Combine(_bed.DataDirectory, "media", "Future"), Added = DateTime.UtcNow,
        };
        _bed.Movies.Add(upcoming);

        Assert.Equal(1, await _bed.Automatic().SearchMissingMoviesAsync());
        Assert.Single(_bed.Client.Searches);
        Assert.Equal((MediaType.Movie, (int?)null, 1, 1), Fields(Assert.Single(_bed.Finished)));
    }

    [Fact]
    public async Task Season_search_takes_the_best_release_and_skips_overlapping_ones()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1,
            _bed.Release("Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP", 2),
            _bed.Release("Breaking.Bad.S01.German.DL.1080p.BluRay.x264-GRP", 30),
            _bed.Release("Breaking.Bad.S01E03.German.DL.720p.WEB.h264-GRP", 1));
        var automatic = _bed.Automatic();

        Assert.Equal(1, await automatic.SearchSeasonAsync(_bed.Show.Id, 1));
        Assert.Equal(["Breaking.Bad.S01.German.DL.1080p.BluRay.x264-GRP.nzb"], _bed.NzbFiles());

        var entry = _bed.History.Page(1, 10, seriesId: _bed.Show.Id).Records.Single();
        Assert.Equal(7, entry.EpisodeIds.Count);
        Assert.Equal(MediaType.Series, entry.MediaType);

        // All episodes were grabbed just now.
        Assert.Equal(0, await automatic.SearchSeasonAsync(_bed.Show.Id, 1));
        Assert.Single(_bed.Client.Searches);
    }

    [Fact]
    public async Task Episode_search_grabs_single_episodes_and_skips_recently_grabbed_ones()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Handlers[1] = (criteria, _) =>
        {
            var episode = (EpisodeSearchCriteria)criteria;
            return Task.FromResult<IReadOnlyList<ReleaseInfo>>(
                [.. episode.EpisodeNumbers.Select(e => _bed.Release($"Breaking.Bad.S{episode.SeasonNumber:00}E{e:00}.German.DL.1080p.WEB.h264-GRP", 2))]);
        };
        var e2 = _bed.Episode(1, 2).Id;
        var e3 = _bed.Episode(1, 3).Id;
        var s2e1 = _bed.Episode(2, 1).Id;

        Assert.Equal(1, await _bed.Automatic().SearchEpisodesAsync(_bed.Show.Id, [e2]));

        // E02 was grabbed; E03 and S02E01 are new (two seasons: two searches).
        Assert.Equal(2, await _bed.Automatic().SearchEpisodesAsync(_bed.Show.Id, [e2, e3, s2e1]));
        Assert.Equal(
            ["Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP.nzb", "Breaking.Bad.S01E03.German.DL.1080p.WEB.h264-GRP.nzb"],
            _bed.NzbFiles().Where(f => f.Contains("S01", StringComparison.Ordinal)));
        Assert.Equal(3, _bed.NzbFiles().Length);
    }

    private static (MediaType, int?, int, int) Fields(AutomaticSearchFinished e) => (e.MediaType, e.Id, e.Searched, e.Grabbed);

    public void Dispose() => _bed.Dispose();
}
