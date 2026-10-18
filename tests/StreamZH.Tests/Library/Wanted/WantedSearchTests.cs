// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Indexers;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Library.Wanted;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.Wanted;

/// <summary>The search buttons of Wanted queue jobs that run the automatic search.</summary>
public sealed class WantedSearchTests : IDisposable
{
    private readonly SearchTestBed _bed = new();
    private readonly JobQueue _jobs;
    private readonly ServiceProvider _services;

    public WantedSearchTests()
    {
        _jobs = new JobQueue(_bed.Events);
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Handlers[1] = (criteria, _) => Task.FromResult<IReadOnlyList<ReleaseInfo>>(criteria switch
        {
            MovieSearchCriteria => [_bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12)],
            EpisodeSearchCriteria { IsSeasonSearch: true } season => [_bed.Release($"Breaking.Bad.S{season.SeasonNumber:00}.German.DL.1080p.BluRay.x264-GRP", 30)],
            EpisodeSearchCriteria episode => [.. episode.EpisodeNumbers.Select(e => _bed.Release($"Breaking.Bad.S{episode.SeasonNumber:00}E{e:00}.German.DL.1080p.BluRay.x264-GRP", 2))],
            _ => [],
        });

        var services = new ServiceCollection();
        services.AddSingleton(_ => _bed.AutomaticWithLastSearch());
        services.AddSingleton(_ => _bed.Wanted());
        _services = services.BuildServiceProvider();
    }

    private WantedSearch Search() => new(_jobs, _bed.Movies, _bed.Series, _bed.Queries());

    /// <summary>Runs every queued job like the host's job runner.</summary>
    private async Task<List<string>> RunAllAsync()
    {
        var names = new List<string>();
        while (_jobs.Reader.TryRead(out var job))
        {
            await _jobs.StartedAsync(job);
            names.Add(job.Name);
            await job.Run(_services, CancellationToken.None);
            await _jobs.FinishedAsync(true);
        }

        return names;
    }

    [Fact]
    public async Task Selected_items_are_searched_once_per_series_and_duplicates_are_not_queued()
    {
        var e3 = _bed.Episode(1, 3).Id;
        var e9 = _bed.Episode(2, 9).Id;

        Assert.Equal(2, Search().Selected([_bed.Dune.Id, 4711], [e3, e9]));
        Assert.Equal(0, Search().Selected([_bed.Dune.Id], [e9, e3]));
        Assert.Equal(2, _jobs.Status.Queued);
        Assert.Equal(0, Search().Selected([4711], [99_999]));

        Assert.Equal(["Search Dune", "Search 2 episodes of Breaking Bad"], await RunAllAsync());
        Assert.Equal(3, _bed.NzbFiles().Length);
        Assert.Equal(_bed.Time.Now.UtcDateTime, _bed.Wanted().Missing(new WantedQuery(WantedItemType.Movies)).Records.Single().LastSearched);
    }

    [Fact]
    public async Task Search_all_missing_works_out_the_list_when_it_runs()
    {
        // Season 1 has all files but E05; season 2 has none.
        _bed.Series.SetEpisodeFiles(
            _bed.Show.Id,
            _bed.Episodes.Where(e => e.SeasonNumber == 1 && e.EpisodeNumber != 5)
                .ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber), e => new MediaFile($"S01E{e.EpisodeNumber:00}.German.DL.1080p.BluRay.mkv", 1, DateTime.UtcNow)));

        Assert.Equal(2, Search().AllMissing(null));
        Assert.Equal(0, Search().AllMissing(WantedItemType.Movies));

        Assert.Equal(["Search missing movies", "Search missing episodes"], await RunAllAsync());
        Assert.Equal(
            ["Breaking.Bad.S01E05.German.DL.1080p.BluRay.x264-GRP.nzb", "Breaking.Bad.S02.German.DL.1080p.BluRay.x264-GRP.nzb", "Dune.2021.German.DL.1080p.BluRay.x264-GRP.nzb"],
            _bed.NzbFiles());
    }

    [Fact]
    public async Task Search_all_cutoff_unmet_searches_upgrades_only_for_files_below_the_profile()
    {
        _bed.Dune.File = new MediaFile("Dune.2021.German.DL.720p.WEB.h264-GRP.mkv", 1, DateTime.UtcNow);
        _bed.Movies.Update(_bed.Dune);
        _bed.Series.SetEpisodeFiles(_bed.Show.Id, new Dictionary<(int, int), MediaFile>
        {
            [(1, 1)] = new("Breaking.Bad.S01E01.German.DL.1080p.BluRay.x264-GRP.mkv", 1, DateTime.UtcNow),
            [(1, 2)] = new("Breaking.Bad.S01E02.German.DL.720p.WEB.h264-GRP.mkv", 1, DateTime.UtcNow),
        });

        Assert.Equal(1, Search().AllCutoffUnmet(WantedItemType.Movies));
        Assert.Equal(1, Search().AllCutoffUnmet(WantedItemType.Episodes));
        await RunAllAsync();

        var searched = _bed.Client.Searches.Select(s => s.Criteria).ToList();
        Assert.Single(searched.OfType<MovieSearchCriteria>());
        var episode = Assert.Single(searched.OfType<EpisodeSearchCriteria>());
        Assert.Equal((1, "2"), (episode.SeasonNumber, string.Join(',', episode.EpisodeNumbers)));
        Assert.Equal(["Breaking.Bad.S01E02.German.DL.1080p.BluRay.x264-GRP.nzb", "Dune.2021.German.DL.1080p.BluRay.x264-GRP.nzb"], _bed.NzbFiles());
    }

    public void Dispose()
    {
        _services.Dispose();
        _bed.Dispose();
    }
}
