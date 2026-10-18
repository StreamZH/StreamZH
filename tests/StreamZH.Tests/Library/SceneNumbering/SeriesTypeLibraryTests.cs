// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Indexers.Newznab;
using StreamZH.Library;
using StreamZH.Library.Import;
using StreamZH.Library.SceneNumbering;
using StreamZH.Tests.Host;
using StreamZH.Tests.Library.Import;

namespace StreamZH.Tests.Library.SceneNumbering;

/// <summary>
/// Series type and numbering through the library: repository, refresh (absolute numbers, TheXEM), searches per type,
/// download import, disk scan and naming. Breaking Bad of the test bed: S01 has 7 episodes, S02 13 (absolute 8-20).
/// </summary>
public sealed class SeriesTypeLibraryTests : IDisposable
{
    private readonly ImportTestBed _import = new();
    private readonly FakeXem _xem = new();

    private SeriesRepository Series => _import.Bed.Series;

    private Series Show => Series.Find(_import.Bed.Show.Id)!;

    private Episode Ep(int season, int episode) => _import.Bed.Episode(season, episode);

    /// <summary>Makes the test bed's series anime or daily, with absolute numbers like a refresh gives them.</summary>
    private void Use(SeriesType type)
    {
        Series.SetSeriesType(Show.Id, type);
        var episodes = Series.Episodes(Show.Id).ToList();
        EpisodeNumbering.AssignAbsoluteNumbers(episodes);
        Series.UpsertEpisodes(Show.Id, episodes, monitorNew: true);
    }

    private LibraryService Library(XemService? xem = null) => new(
        new FakeTmdbClient(), new RootFolderRepository(_import.Bed.Database), _import.Bed.Movies, Series,
        new QualityProfileService(_import.Bed.Profiles, _import.Bed.Config, NullLogger<QualityProfileService>.Instance),
        _import.Bed.Events, NullLogger<LibraryService>.Instance, _import.Settings, xem);

    private XemService Xem() => new(
        new XemClient(new XemServiceTests.HandlerFactory(_xem)), Series, new AlternativeTitleRepository(_import.Bed.Database),
        NullLogger<XemService>.Instance, _import.Bed.Time);

    [Fact]
    public void Series_type_and_numbering_columns_are_stored()
    {
        Assert.Equal(SeriesType.Standard, Show.SeriesType);

        var show = Show;
        Series.SetSeriesType(show.Id, SeriesType.Daily);
        Assert.Equal(SeriesType.Daily, Show.SeriesType);

        // Update (a refresh) keeps the type the user chose.
        show.SeriesType = SeriesType.Standard;
        Series.Update(show);
        Assert.Equal(SeriesType.Daily, Show.SeriesType);
        Series.SetSeriesType(show.Id, SeriesType.Anime);
        Assert.Equal(SeriesType.Anime, Series.All().Single(s => s.Id == show.Id).SeriesType);

        Use(SeriesType.Anime);
        Series.SetSceneNumbering(show.Id, new Dictionary<int, SceneNumber> { [Ep(2, 1).Id] = new(3, 1, 9) });
        var episode = Ep(2, 1);
        Assert.Equal((8, 3, 1, 9), (episode.AbsoluteEpisodeNumber, episode.SceneSeasonNumber, episode.SceneEpisodeNumber, episode.SceneAbsoluteEpisodeNumber));

        // A refresh keeps the scene numbers (TheXEM sets them again afterwards).
        Series.UpsertEpisodes(show.Id, Series.Episodes(show.Id), monitorNew: true);
        Assert.Equal(3, Ep(2, 1).SceneSeasonNumber);
    }

    [Fact]
    public async Task Refresh_counts_absolute_numbers_and_takes_TheXEM_numbering()
    {
        _xem.Responses["map/havemap"] = """{"result":"success","data":[81189],"message":""}""";
        _xem.Responses["map/all?id=81189&origin=tvdb"] = XemServiceTests.Map([((2, 1, 7), (1, 7, 7))]);
        _xem.Responses["map/allNames"] = """{"result":"success","data":{"81189":[{"Breaking Bad":-1},{"Breaking Bad Season 2":2}]},"message":""}""";

        await Library(Xem()).RefreshSeriesAsync(Show.Id);

        Assert.Equal(1, Ep(1, 1).AbsoluteEpisodeNumber);
        Assert.Equal(8, Ep(2, 1).AbsoluteEpisodeNumber);
        Assert.Null(Series.Episodes(Show.Id).Single(e => e.SeasonNumber == 0).AbsoluteEpisodeNumber);
        Assert.Equal((2, 1), (Ep(1, 7).SceneSeasonNumber, Ep(1, 7).SceneEpisodeNumber));
        var title = Assert.Single(new AlternativeTitleRepository(_import.Bed.Database).For(MediaType.Series, Show.Id));
        Assert.Equal(("Breaking Bad Season 2", 2, AlternativeTitleSources.Xem), (title.Title, title.SceneSeasonNumber, title.Source));
    }

    [Fact]
    public async Task Refresh_works_while_TheXEM_is_down()
    {
        _xem.Down = true;

        await Library(Xem()).RefreshSeriesAsync(Show.Id);

        Assert.Equal(8, Ep(2, 1).AbsoluteEpisodeNumber);
        Assert.NotNull(Show.LastRefreshed);
    }

    [Fact]
    public async Task Adding_a_series_guesses_its_type_unless_one_is_given()
    {
        Series.Delete(Show.Id);
        var root = new RootFolderRepository(_import.Bed.Database).Add(Path.Combine(_import.Bed.DataDirectory, "tv"), MediaType.Series);

        var added = await Library().AddSeriesAsync(FakeTmdbClient.Show.TmdbId, root, monitored: true, seriesType: SeriesType.Daily);

        Assert.Equal(SeriesType.Daily, Series.Find(added.Id)!.SeriesType);
    }

    [Fact]
    public async Task Anime_episodes_are_searched_by_absolute_number_too()
    {
        Use(SeriesType.Anime);
        _import.Bed.AddIndexer(1, "Alpha");
        _import.Bed.Client.Returns(1, _import.Bed.Release("[GER] Breaking Bad - 09 [1080p]", 1.5));

        var result = await _import.Bed.Search().SearchEpisodesAsync(Show.Id, 2, [Ep(2, 2).Id], IndexerUse.InteractiveSearch);

        var criteria = Assert.IsType<EpisodeSearchCriteria>(Assert.Single(_import.Bed.Client.Searches).Criteria);
        Assert.Equal((2, 2, 9), (criteria.SeasonNumber, criteria.EpisodeNumbers.Single(), criteria.AbsoluteEpisodeNumbers.Single()));
        Assert.Equal([Ep(2, 2).Id], Assert.Single(result.Decisions).EpisodeIds);
    }

    [Fact]
    public async Task Daily_episodes_are_searched_by_air_date_and_seasons_by_air_year()
    {
        Use(SeriesType.Daily);
        _import.Bed.AddIndexer(1, "Alpha");
        _import.Bed.Client.Returns(1, _import.Bed.Release("Breaking.Bad.2008.01.22.German.1080p.WEB.h264-GRP", 1.5));

        var result = await _import.Bed.Search().SearchEpisodesAsync(Show.Id, 1, [Ep(1, 3).Id], IndexerUse.InteractiveSearch);
        await _import.Bed.Search().SearchEpisodesAsync(Show.Id, 1, null, IndexerUse.InteractiveSearch);

        var searches = _import.Bed.Client.Searches.Select(s => Assert.IsType<EpisodeSearchCriteria>(s.Criteria)).ToList();
        Assert.Equal("2008-01-22", searches[0].AirDate);
        Assert.Equal((2008, true), (searches[1].SeasonNumber, searches[1].IsSeasonSearch));
        Assert.Equal([Ep(1, 3).Id], Assert.Single(result.Decisions).EpisodeIds);

        // Newznab: tvsearch season=yyyy ep=MM/dd, as Sonarr sends it.
        var plan = Assert.Single(NewznabRequestBuilder.ForEpisodes(searches[0], [5000], NewznabCaps.Default));
        Assert.Equal("&q=Breaking%20Bad&season=2008&ep=01/22", plan.Tiers[0][0].Parameters);
    }

    [Fact]
    public async Task Scene_numbered_series_are_searched_by_scene_season_and_episode()
    {
        // TheXEM: TMDB S01E06-E07 are scene S02E01-E02.
        Series.SetSceneNumbering(Show.Id, new Dictionary<int, SceneNumber>
        {
            [Ep(1, 5).Id] = new(1, 5, 5),
            [Ep(1, 6).Id] = new(2, 1, 6),
            [Ep(1, 7).Id] = new(2, 2, 7),
        });
        _import.Bed.AddIndexer(1, "Alpha");
        _import.Bed.Client.Returns(1, _import.Bed.Release("Breaking.Bad.S02E02.German.DL.1080p.WEB.h264-GRP", 1.5));

        var result = await _import.Bed.Search().SearchEpisodesAsync(Show.Id, 1, [Ep(1, 7).Id], IndexerUse.InteractiveSearch);
        await _import.Bed.Search().SearchEpisodesAsync(Show.Id, 1, null, IndexerUse.InteractiveSearch);

        var searches = _import.Bed.Client.Searches.Select(s => Assert.IsType<EpisodeSearchCriteria>(s.Criteria)).ToList();
        Assert.Equal((2, 2), (searches[0].SeasonNumber, searches[0].EpisodeNumbers.Single()));
        Assert.Equal([1, 2], searches.Skip(1).Select(s => s.SeasonNumber));
        Assert.All(searches.Skip(1), s => Assert.True(s.IsSeasonSearch));

        // The scene name S02E02 is TMDB S01E07.
        Assert.Equal([Ep(1, 7).Id], Assert.Single(result.Decisions).EpisodeIds);
    }

    [Fact]
    public void Newznab_searches_anime_by_title_and_absolute_number()
    {
        var criteria = new EpisodeSearchCriteria
        {
            Titles = ["Frieren"], TvdbId = 424536, SeasonNumber = 2, EpisodeNumbers = [1], AbsoluteEpisodeNumbers = [29],
        };

        var plans = NewznabRequestBuilder.ForEpisodes(criteria, [5070], NewznabCaps.Default with { TvSearchParams = ["q", "season", "ep", "tvdbid"] });

        Assert.Equal(2, plans.Count);
        Assert.Equal("&tvdbid=424536&season=2&ep=1", plans[0].Tiers[0][0].Parameters);
        var absolute = Assert.Single(Assert.Single(plans[1].Tiers));
        Assert.Equal(("search", "&q=Frieren%2029"), (absolute.Function, absolute.Parameters));

        // Single-digit numbers are padded like Sonarr's {AbsoluteEpisodeNumber:00}.
        var five = NewznabRequestBuilder.ForEpisodes(criteria with { AbsoluteEpisodeNumbers = [5] }, [5070], NewznabCaps.Default);
        Assert.Equal("&q=Frieren%2005", five[^1].Tiers[0][0].Parameters);
    }

    [Fact]
    public async Task German_anime_release_is_imported_by_absolute_number_with_the_anime_format()
    {
        Use(SeriesType.Anime);
        _import.Configure(v => v with { AnimeEpisodeFormat = "{Series Title} - S{season:00}E{episode:00} - {absolute:000} - {Episode Title}" });
        var item = _import.Completed(
            1, "[GER] Breaking Bad - 09 [1080p]", _import.EpisodeLink((2, 2)), ("[GER] Breaking Bad - 09 [1080p].mkv", 40));

        var result = await _import.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        Assert.Equal(Path.Combine("Season 2", "Breaking Bad - S02E02 - 009 - Episode 2.mkv"), Ep(2, 2).File?.RelativePath);
    }

    [Fact]
    public async Task Daily_release_is_imported_by_air_date_and_found_again_by_the_disk_scan()
    {
        Use(SeriesType.Daily);
        _import.Configure(v => v with { DailyEpisodeFormat = "{Series Title} - {Air-Date} - {Episode Title}" });
        var item = _import.Completed(
            2, "Breaking.Bad.2008.01.22.German.1080p.WEB.h264-GRP", _import.EpisodeLink((1, 3)),
            ("Breaking.Bad.2008.01.22.German.1080p.WEB.h264-GRP.mkv", 40));

        var result = await _import.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        var relative = Ep(1, 3).File?.RelativePath;
        Assert.Equal(Path.Combine("Season 1", "Breaking Bad - 2008-01-22 - Episode 3.mkv"), relative);

        // The rescan knows the date-named file (the plain SxxEyy scan does not).
        Assert.Empty(DiskScanner.ScanSeries(Show.FolderPath));
        var found = DiskScanner.ScanSeries(Show.FolderPath, Show, Series.Episodes(Show.Id));
        Assert.Equal(relative, found[(1, 3)].RelativePath);
        await Library().RescanAllAsync();
        Assert.Equal(relative, Ep(1, 3).File?.RelativePath);
    }

    [Fact]
    public void Disk_scan_reads_absolute_names_only_for_anime_series()
    {
        ImportTestBed.WriteFile(Path.Combine(Show.FolderPath, "Breaking Bad - 009.mkv"), 1);
        Use(SeriesType.Standard);
        Assert.Empty(DiskScanner.ScanSeries(Show.FolderPath, Show, Series.Episodes(Show.Id)));

        Use(SeriesType.Anime);
        var found = DiskScanner.ScanSeries(Show.FolderPath, Show, Series.Episodes(Show.Id));
        Assert.Equal("Breaking Bad - 009.mkv", Assert.Single(found).Value.RelativePath);
        Assert.Equal((2, 2), Assert.Single(found).Key);
    }

    [Fact]
    public void Naming_picks_the_format_of_the_series_type()
    {
        var values = new MediaManagementValues
        {
            StandardEpisodeFormat = "{Series Title} - S{season:00}E{episode:00}",
            DailyEpisodeFormat = "{Series Title} - {Air-Date} - {Episode Title}",
            AnimeEpisodeFormat = "{Series Title} - {absolute:000} - {Episode Title}",
        };
        var builder = new FileNameBuilder(values);
        var file = new NamingFileInfo();
        var episode = new Episode { SeasonNumber = 2, EpisodeNumber = 1, Title = "Pilot", AirDate = "2026-10-18", AbsoluteEpisodeNumber = 13 };
        var anime = new Series { Title = "Frieren", SeriesType = SeriesType.Anime };
        var daily = new Series { Title = "Late Night Talk", SeriesType = SeriesType.Daily };

        Assert.Equal("Frieren - 013 - Pilot", builder.EpisodeFileName(anime, [episode], file, FileNameBuilder.FormatFor(anime, false)));
        Assert.Equal("Late Night Talk - 2026-10-18 - Pilot", builder.EpisodeFileName(daily, [episode], file, FileNameBuilder.FormatFor(daily, false)));

        // Several episodes: a range; a special (no absolute number) and a daily episode without a date use the standard format.
        var next = new Episode { SeasonNumber = 2, EpisodeNumber = 2, Title = "Pilot", AbsoluteEpisodeNumber = 14 };
        Assert.Equal("Frieren - 013-014 - Pilot", builder.EpisodeFileName(anime, [episode, next], file, EpisodeFormat.Anime));
        Assert.Equal("Frieren - S00E01", builder.EpisodeFileName(anime, [new Episode { SeasonNumber = 0, EpisodeNumber = 1 }], file, EpisodeFormat.Anime));
        Assert.Equal("Late Night Talk - S02E02", builder.EpisodeFileName(daily, [next], file, EpisodeFormat.Daily));
        Assert.Equal("Frieren - 13", new FileNameBuilder(values with { AnimeEpisodeFormat = "{Series Title} - {absolute}" })
            .EpisodeFileName(anime, [episode], file, EpisodeFormat.Anime));
    }

    [Fact]
    public void Anime_format_may_use_the_absolute_number_instead_of_season_and_episode()
    {
        Assert.Null(FileNameBuilder.ValidateFormat("{Series Title} - {absolute:000} - {Episode Title}", NamingFormatKind.AnimeEpisode));
        Assert.Null(FileNameBuilder.ValidateFormat("{Series Title} - S{season:00}E{episode:00} - {absolute:000}", NamingFormatKind.AnimeEpisode));
        Assert.NotNull(FileNameBuilder.ValidateFormat("{Series Title} - {Episode Title}", NamingFormatKind.AnimeEpisode));
        Assert.NotNull(FileNameBuilder.ValidateFormat("{Series Title} - {absolute:000}", NamingFormatKind.StandardEpisode));
        Assert.Null(FileNameBuilder.ValidateFormat("{Series Title} - {Air Date}", NamingFormatKind.DailyEpisode));
    }

    public void Dispose() => _import.Dispose();
}
