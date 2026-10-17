// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Search;

/// <summary>Search for missing movies and episodes (scheduled task, search on add) with the minimum availability.</summary>
public sealed class MissingSearchTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    public MissingSearchTests()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Handlers[1] = (criteria, _) => Task.FromResult<IReadOnlyList<ReleaseInfo>>(criteria switch
        {
            MovieSearchCriteria => [_bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6)],
            EpisodeSearchCriteria { IsSeasonSearch: true } season =>
                [_bed.Release($"Breaking.Bad.S{season.SeasonNumber:00}.German.DL.1080p.WEB.h264-GRP", 20)],
            EpisodeSearchCriteria episode =>
                [.. episode.EpisodeNumbers.Select(e => _bed.Release($"Breaking.Bad.S{episode.SeasonNumber:00}E{e:00}.German.DL.1080p.WEB.h264-GRP", 2))],
            _ => [],
        });
    }

    [Fact]
    public async Task Missing_movies_respect_the_minimum_availability()
    {
        _bed.Dune.InCinemas = "2026-10-01";
        _bed.Movies.Update(_bed.Dune);
        _bed.Options.Save(_bed.Options.Values with { MinimumAvailability = MinimumAvailability.Released });

        Assert.Equal(0, await _bed.Automatic().SearchMissingMoviesAsync());
        Assert.Equal(0, await _bed.Automatic().SearchNewMovieAsync(_bed.Dune.Id));
        Assert.Empty(_bed.Client.Searches);

        // The Search button is the user's choice: it ignores the availability like Radarr.
        Assert.Equal(1, await _bed.Automatic().SearchMoviesAsync([_bed.Dune.Id]));

        _bed.Options.Save(_bed.Options.Values with { MinimumAvailability = MinimumAvailability.Announced });
        _bed.Time.Now += TimeSpan.FromHours(13);
        Assert.Equal(1, await _bed.Automatic().SearchNewMovieAsync(_bed.Dune.Id));
    }

    [Fact]
    public async Task Missing_episodes_search_seasons_with_several_gaps_and_single_episodes_alone()
    {
        // Season 1: only E03 is missing; season 2: all 13 are missing.
        _bed.Series.SetEpisodeFiles(
            _bed.Show.Id,
            _bed.Episodes.Where(e => e.SeasonNumber == 1 && e.EpisodeNumber != 3)
                .ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber), e => new MediaFile($"S01E{e.EpisodeNumber:00}.German.DL.1080p.WEB.mkv", 1, DateTime.UtcNow)));

        Assert.Equal(2, await _bed.Automatic().SearchMissingEpisodesAsync(_bed.Show.Id));

        var searches = _bed.Client.Searches.Select(s => (EpisodeSearchCriteria)s.Criteria).ToList();
        Assert.Equal([(1, "3"), (2, "")], searches.Select(s => (s.SeasonNumber, string.Join(',', s.EpisodeNumbers))));
        Assert.Equal(
            ["Breaking.Bad.S01E03.German.DL.1080p.WEB.h264-GRP.nzb", "Breaking.Bad.S02.German.DL.1080p.WEB.h264-GRP.nzb"],
            _bed.NzbFiles());
        Assert.Equal((MediaType.Series, (int?)_bed.Show.Id, 14, 2), (_bed.Finished.Last().MediaType, _bed.Finished.Last().Id, _bed.Finished.Last().Searched, _bed.Finished.Last().Grabbed));
    }

    [Fact]
    public async Task An_unmonitored_series_is_not_searched()
    {
        _bed.Show.Monitored = false;
        _bed.Series.Update(_bed.Show);

        Assert.Equal(0, await _bed.Automatic().SearchMissingEpisodesAsync(_bed.Show.Id));
        Assert.Empty(_bed.Client.Searches);
    }

    public void Dispose() => _bed.Dispose();
}
