// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Indexers;
using StreamZH.Library;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Search;

public sealed class ReleaseSearchServiceTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    [Fact]
    public async Task Movie_search_ranks_accepted_releases_first_and_keeps_every_result()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1,
            _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6),
            _bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12),
            _bed.Release("Dune.2021.1080p.WEB.H264-ENGLISH", 6),
            _bed.Release("Arrival.2016.German.DL.1080p.WEB.H264-GRP", 6),
            _bed.Release("Dune.S01E01.German.1080p.WEB.H264-GRP", 2));

        var result = await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);

        Assert.Equal(1, result.IndexerCount);
        Assert.Empty(result.Failures);
        Assert.Equal(5, result.Decisions.Count);
        Assert.Equal(
            ["Dune.2021.German.DL.1080p.BluRay.x264-GRP", "Dune.2021.German.DL.1080p.WEB.H264-GRP"],
            result.Decisions.Where(d => d.Accepted).Select(d => d.Release.Title));
        Assert.Equal(result.Best, result.Decisions[0]);

        var english = result.Decisions.Single(d => d.Release.Title.Contains("ENGLISH", StringComparison.Ordinal));
        Assert.Contains(english.Rejections, r => r.Code == RejectionCodes.WantedLanguage);

        // Another movie: only that reason, no quality or size noise.
        var arrival = result.Decisions.Single(d => d.Release.Title.StartsWith("Arrival", StringComparison.Ordinal));
        Assert.Equal(RejectionCodes.WrongMedia, Assert.Single(arrival.Rejections).Code);

        // A name that is not a movie name comes back too, last.
        var unparsable = result.Decisions[^1];
        Assert.Null(unparsable.Parsed);
        Assert.Equal(RejectionCodes.UnableToParse, Assert.Single(unparsable.Rejections).Code);
        Assert.Equal("Unable to parse release", unparsable.Rejections[0].Message);

        // Every result can be grabbed later by (indexer, guid).
        Assert.All(result.Decisions, d => Assert.Same(d, _bed.Cache.Find(d.Release.IndexerId, d.Release.Guid)));
    }

    [Fact]
    public async Task Ids_from_the_indexer_beat_a_title_that_does_not_match()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1,
            _bed.Release("Der.Wuestenplanet.2021.German.DL.1080p.WEB.H264-GRP", 6, tmdbId: 438631),
            _bed.Release("Wuestenplanet.Remastered.2021.German.DL.1080p.WEB.H264-GRP", 6, imdbId: "1160419"),
            _bed.Release("Der.Wuestenplanet.1984.German.DL.1080p.WEB.H264-GRP", 6, tmdbId: 841));

        var result = await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);

        Assert.True(result.Decisions.Single(d => d.Release.TmdbId == 438631).Accepted);
        Assert.True(result.Decisions.Single(d => d.Release.ImdbId == "1160419").Accepted);
        Assert.Equal(RejectionCodes.WrongMedia, Assert.Single(result.Decisions.Single(d => d.Release.TmdbId == 841).Rejections).Code);

        var criteria = Assert.IsType<MovieSearchCriteria>(Assert.Single(_bed.Client.Searches).Criteria);
        Assert.Equal(438631, criteria.TmdbId);
        Assert.Equal("tt1160419", criteria.ImdbId);
        Assert.Equal(["Dune"], criteria.Titles);
        Assert.Equal(2021, criteria.Year);
    }

    [Fact]
    public async Task Indexers_are_asked_in_parallel_and_a_failing_one_does_not_stop_the_others()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.AddIndexer(2, "Beta", priority: 10);
        _bed.AddIndexer(3, "Broken");

        // Alpha and Beta each wait until the other one has started: only works when both run at the same time.
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<ReleaseInfo>> Rendezvous(ReleaseInfo release)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return [release];
        }

        var title = "Dune.2021.German.DL.1080p.WEB.H264-GRP";
        _bed.Client.Handlers[1] = (_, _) => Rendezvous(_bed.Release(title, 6, indexerId: 1));
        _bed.Client.Handlers[2] = (_, _) => Rendezvous(_bed.Release(title, 6, indexerId: 2));
        _bed.Client.Handlers[3] = (_, _) => throw new IndexerException("Invalid API key", 100);

        var result = await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.AutomaticSearch);

        Assert.Equal(3, result.IndexerCount);
        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, d => Assert.True(d.Accepted));

        // Same release on two indexers: the higher priority (lower number) wins the tie.
        Assert.Equal("Beta", result.Decisions[0].Release.Indexer);
        Assert.Equal(10, result.Decisions[0].Release.IndexerPriority);

        var failure = Assert.Single(result.Failures);
        Assert.Equal((3, "Broken", "Invalid API key"), (failure.IndexerId, failure.Indexer, failure.Message));
        Assert.Equal(3, Assert.Single(_bed.Indexers.Failures).IndexerId);
        Assert.Equal([1, 2], _bed.Indexers.Successes.Order());
    }

    [Fact]
    public async Task A_slow_indexer_times_out_and_is_recorded()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.AddIndexer(2, "Slow");
        _bed.Client.Returns(1, _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP"));
        _bed.Client.Handlers[2] = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };

        var result = await _bed.Search(TimeSpan.FromMilliseconds(200)).SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);

        Assert.Single(result.Decisions);
        Assert.Equal("Slow", Assert.Single(result.Failures).Indexer);
        Assert.IsType<TimeoutException>(Assert.Single(_bed.Indexers.Failures).Error);
    }

    [Fact]
    public async Task Duplicates_from_one_indexer_are_removed_and_no_indexers_means_no_results()
    {
        var search = _bed.Search();
        var empty = await search.SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);
        Assert.Equal(0, empty.IndexerCount);
        Assert.Empty(empty.Decisions);

        _bed.AddIndexer(1, "Alpha");
        var release = _bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP");
        _bed.Client.Returns(1, release, release with { Title = "Dune.2021.German.DL.1080p.WEB.H264-GRP.REPOST" });

        var result = await search.SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);
        Assert.Single(result.Decisions);
    }

    [Fact]
    public async Task Unknown_movie_is_reported()
    {
        var error = await Assert.ThrowsAsync<LibraryException>(() => _bed.Search().SearchMovieAsync(999, IndexerUse.InteractiveSearch));
        Assert.Equal(LibraryError.NotFound, error.Error);
    }

    [Fact]
    public async Task Season_search_takes_season_packs_and_episodes_of_that_season_only()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1,
            _bed.Release("Breaking.Bad.S01.German.DL.1080p.BluRay.x264-GRP", 30),
            _bed.Release("Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP", 2),
            _bed.Release("Breaking.Bad.S02E01.German.DL.1080p.WEB.h264-GRP", 2),
            _bed.Release("Breaking.Bad.S01-S02.German.DL.1080p.BluRay.x264-GRP", 60),
            _bed.Release("Breaking.Bad.S01E09.German.DL.1080p.WEB.h264-GRP", 2),
            _bed.Release("Better.Call.Saul.S01E02.German.DL.1080p.WEB.h264-GRP", 2),
            _bed.Release("Breaking Bad German 1080p complete", 2));

        var result = await _bed.Search().SearchEpisodesAsync(_bed.Show.Id, 1, null, IndexerUse.InteractiveSearch);
        string Code(string title) => Assert.Single(result.Decisions.Single(d => d.Release.Title == title).Rejections).Code;

        Assert.Equal(
            ["Breaking.Bad.S01.German.DL.1080p.BluRay.x264-GRP", "Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP"],
            result.Decisions.Where(d => d.Accepted).Select(d => d.Release.Title));

        var pack = result.Decisions[0];
        Assert.Equal(_bed.Episodes.Where(e => e.SeasonNumber == 1).Select(e => e.Id), pack.EpisodeIds);
        Assert.Equal([_bed.Episode(1, 2).Id], result.Decisions[1].EpisodeIds);

        Assert.Equal(RejectionCodes.WrongSeason, Code("Breaking.Bad.S02E01.German.DL.1080p.WEB.h264-GRP"));
        Assert.Equal(RejectionCodes.MultiSeason, Code("Breaking.Bad.S01-S02.German.DL.1080p.BluRay.x264-GRP"));
        Assert.Equal(RejectionCodes.UnknownEpisode, Code("Breaking.Bad.S01E09.German.DL.1080p.WEB.h264-GRP"));
        Assert.Equal(RejectionCodes.WrongMedia, Code("Better.Call.Saul.S01E02.German.DL.1080p.WEB.h264-GRP"));
        Assert.Equal(RejectionCodes.UnableToParse, Code("Breaking Bad German 1080p complete"));

        var criteria = Assert.IsType<EpisodeSearchCriteria>(Assert.Single(_bed.Client.Searches).Criteria);
        Assert.True(criteria.IsSeasonSearch);
        Assert.Equal((81189, 1), (criteria.TvdbId, criteria.SeasonNumber));
    }

    [Fact]
    public async Task Episode_search_rejects_other_episodes_and_season_packs()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1,
            _bed.Release("Breaking.Bad.S01E02.German.DL.1080p.WEB.h264-GRP", 2),
            _bed.Release("Breaking.Bad.S01E02E03.German.DL.1080p.WEB.h264-GRP", 4),
            _bed.Release("Breaking.Bad.S01E03.German.DL.1080p.WEB.h264-GRP", 2),
            _bed.Release("Breaking.Bad.S01.German.DL.1080p.BluRay.x264-GRP", 30),
            _bed.Release("Totally.Different.Name.S01E02.German.DL.1080p.WEB.h264-GRP", 2, tvdbId: 81189));

        var episode = _bed.Episode(1, 2);
        var result = await _bed.Search().SearchEpisodesAsync(_bed.Show.Id, 1, [episode.Id], IndexerUse.InteractiveSearch);
        string Code(string title) => Assert.Single(result.Decisions.Single(d => d.Release.Title == title).Rejections).Code;

        Assert.Equal(3, result.Decisions.Count(d => d.Accepted));
        Assert.True(result.Decisions.Single(d => d.Release.Title.StartsWith("Totally", StringComparison.Ordinal)).Accepted);
        Assert.Equal(RejectionCodes.WrongEpisode, Code("Breaking.Bad.S01E03.German.DL.1080p.WEB.h264-GRP"));
        Assert.Equal(RejectionCodes.FullSeason, Code("Breaking.Bad.S01.German.DL.1080p.BluRay.x264-GRP"));
        Assert.Equal("Episode was not requested: S01E03", result.Decisions.Single(d => d.Release.Title.Contains("S01E03.", StringComparison.Ordinal)).Rejections[0].Message);

        var criteria = Assert.IsType<EpisodeSearchCriteria>(Assert.Single(_bed.Client.Searches).Criteria);
        Assert.Equal([2], criteria.EpisodeNumbers);
    }

    [Fact]
    public async Task Episode_search_for_an_unknown_season_is_reported()
    {
        var error = await Assert.ThrowsAsync<LibraryException>(() => _bed.Search().SearchEpisodesAsync(_bed.Show.Id, 7, null, IndexerUse.InteractiveSearch));
        Assert.Equal(LibraryError.NotFound, error.Error);
    }

    public void Dispose() => _bed.Dispose();
}
