// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.SceneNumbering;
using StreamZH.Library.Search;
using StreamZH.Parsing.Releases;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.SceneNumbering;

/// <summary>TheXEM scene numbering and scene names, with fixtures in TheXEM's answer format (no network).</summary>
public sealed class XemServiceTests : IDisposable
{
    public const int TvdbId = 289574;

    /// <summary>TheTVDB numbers season 1 with 25 episodes; the scene splits it: S01E01-E12, then S02E01-E13 (abs 13-25).</summary>
    public static readonly string SplitSeasonMap = Map(Enumerable.Range(1, 25).Select(n =>
        (Scene: n <= 12 ? (1, n, n) : (2, n - 12, n), Tvdb: (1, n, n))));

    /// <summary>TheTVDB has two seasons (12 + 13); TMDB lists all 25 episodes as season 1. The scene follows TheTVDB.</summary>
    public static readonly string TvdbSeasonsMap = Map(Enumerable.Range(1, 25).Select(n =>
        (Scene: n <= 12 ? (1, n, n) : (2, n - 12, n), Tvdb: n <= 12 ? (1, n, n) : (2, n - 12, n))));

    public const string HaveMap = """{"result":"success","data":["289574","81189"],"message":""}""";

    public const string Names = """
        {"result":"success","data":{
          "289574":[{"Crystal Lake":-1},{"Kurisutaru Reiku":-1},{"Crystal Lake Second Season":2}],
          "79151":[{"Fate Zero":-1},{"Fate Zero 2nd Season":2}]
        },"message":""}
        """;

    private readonly SearchTestBed _bed = new();
    private readonly FakeXem _xem = new();
    private readonly AlternativeTitleRepository _titles;
    private readonly Series _show;

    public XemServiceTests()
    {
        _titles = new AlternativeTitleRepository(_bed.Database);
        _show = new Series
        {
            TmdbId = 61459, TvdbId = TvdbId, Title = "Crystal Lake", OriginalTitle = "Crystal Lake", SortTitle = "crystal lake", Year = 2014,
            Status = "Ended", Monitored = true, QualityProfileId = QualityDefaults.DefaultProfileId, RootFolderId = 1,
            FolderPath = Path.Combine(_bed.DataDirectory, "media", "Crystal Lake (2014)"), Added = DateTime.UtcNow, SeriesType = SeriesType.Anime,
        };
        _bed.Series.Add(_show);
        var episodes = Enumerable.Range(1, 25)
            .Select(e => new Episode { SeasonNumber = 1, EpisodeNumber = e, Title = $"Episode {e}", AirDate = $"2014-{(e / 4) + 1:00}-{(e % 28) + 1:00}" })
            .Append(new Episode { SeasonNumber = 0, EpisodeNumber = 1, Title = "OVA" })
            .ToList();
        EpisodeNumbering.AssignAbsoluteNumbers(episodes);
        _bed.Series.UpsertEpisodes(_show.Id, episodes, monitorNew: true);

        _xem.Responses["map/havemap"] = HaveMap;
        _xem.Responses["map/allNames"] = Names;
        _xem.Responses[$"map/all?id={TvdbId}&origin=tvdb"] = SplitSeasonMap;
    }

    private XemService Service() => new(new XemClient(new HandlerFactory(_xem)), _bed.Series, _titles, NullLogger<XemService>.Instance, _bed.Time);

    private Episode Tmdb(int season, int episode) => _bed.Series.Episodes(_show.Id).Single(e => e.SeasonNumber == season && e.EpisodeNumber == episode);

    [Fact]
    public async Task Scene_S02E01_maps_to_TMDB_S01E13()
    {
        var result = await Service().UpdateSeriesAsync(_show);

        Assert.Equal(25, result.Mapped);
        var episode = Tmdb(1, 13);
        Assert.Equal((2, 1, 13), (episode.SceneSeasonNumber, episode.SceneEpisodeNumber, episode.SceneAbsoluteEpisodeNumber));
        Assert.Equal((1, 12), (Tmdb(1, 12).SceneSeasonNumber, Tmdb(1, 12).SceneEpisodeNumber));
        Assert.Null(Tmdb(0, 1).SceneSeasonNumber);

        // A release in scene numbering finds the TMDB episode.
        var parsed = ReleaseParser.ParseEpisode("Crystal.Lake.S02E01.German.DL.AniME.1080p.BluRay.x264-GRP")!;
        var (mapped, rejection) = new EpisodeMapper(_titles).Map(parsed, _show, _bed.Series.Episodes(_show.Id));
        Assert.Null(rejection);
        Assert.Equal((1, 13), (mapped.Single().SeasonNumber, mapped.Single().EpisodeNumber));
    }

    [Fact]
    public async Task Seasons_split_differently_on_TMDB_are_matched_by_absolute_number()
    {
        _xem.Responses[$"map/all?id={TvdbId}&origin=tvdb"] = TvdbSeasonsMap;

        await Service().UpdateSeriesAsync(_show);

        // TheTVDB S02E01 (absolute 13) is TMDB S01E13; TMDB has no season 2.
        Assert.Equal((2, 1, 13), (Tmdb(1, 13).SceneSeasonNumber, Tmdb(1, 13).SceneEpisodeNumber, Tmdb(1, 13).SceneAbsoluteEpisodeNumber));
        Assert.Equal((1, 1), (Tmdb(1, 1).SceneSeasonNumber, Tmdb(1, 1).SceneEpisodeNumber));
    }

    [Fact]
    public void New_episodes_after_the_last_mapping_continue_the_scene_numbering()
    {
        var episodes = Enumerable.Range(1, 27).Select(e => new Episode { Id = e, SeasonNumber = 1, EpisodeNumber = e }).ToList();
        EpisodeNumbering.AssignAbsoluteNumbers(episodes);
        var mappings = Enumerable.Range(1, 25)
            .Select(n => new XemMapping(n <= 12 ? new XemNumber(1, n, n) : new XemNumber(2, n - 12, n), new XemNumber(1, n, n)))
            .ToList();

        var numbering = XemService.Translate(episodes, mappings);

        Assert.Equal(new SceneNumber(2, 14, 26), numbering[26]);
        Assert.Equal(new SceneNumber(2, 15, 27), numbering[27]);
    }

    [Fact]
    public async Task Scene_names_become_alternative_titles_with_their_scene_season()
    {
        var result = await Service().UpdateSeriesAsync(_show);

        Assert.Equal(2, result.Names);
        var titles = _titles.For(MediaType.Series, _show.Id);
        Assert.All(titles, t => Assert.Equal(AlternativeTitleSources.Xem, t.Source));

        // The series' own title adds nothing; the romaji name counts for all seasons, the season name for scene season 2.
        Assert.DoesNotContain(titles, t => t.Title == "Crystal Lake");
        Assert.Contains(titles, t => t is { Title: "Kurisutaru Reiku", SceneSeasonNumber: null, CleanTitle: "kurisutarureiku" });
        Assert.Contains(titles, t => t is { Title: "Crystal Lake Second Season", SceneSeasonNumber: 2 });
    }

    [Fact]
    public async Task An_absolute_number_in_a_scene_season_name_counts_within_that_season()
    {
        await Service().UpdateSeriesAsync(_show);
        var mapper = new EpisodeMapper(_titles);
        var library = _bed.Series.Episodes(_show.Id);

        var parsed = ReleaseParser.ParseEpisode("[GER] Crystal Lake Second Season - 01 [1080p]")!;
        Assert.Equal("crystallakesecondseason", parsed.CleanTitle);
        var (episodes, _) = mapper.Map(parsed, _show, library);
        Assert.Equal((1, 13), (episodes.Single().SeasonNumber, episodes.Single().EpisodeNumber));

        // Without the season name the scene absolute number decides.
        var (absolute, _) = mapper.Map(ReleaseParser.ParseEpisode("[GER] Crystal Lake - 13 [1080p]")!, _show, library);
        Assert.Equal((1, 13), (absolute.Single().SeasonNumber, absolute.Single().EpisodeNumber));
    }

    [Fact]
    public async Task Users_titles_and_other_sources_stay_when_the_scene_names_are_replaced()
    {
        _titles.Add(new AlternativeTitle
        {
            MediaType = MediaType.Series, ItemId = _show.Id, Title = "Kristallsee", CleanTitle = "kristallsee", Source = AlternativeTitleSources.User,
        });

        await Service().UpdateSeriesAsync(_show);
        _xem.Responses["map/allNames"] = """{"result":"success","data":{},"message":""}""";
        await Service().UpdateAllAsync();

        var titles = _titles.For(MediaType.Series, _show.Id);
        Assert.Equal("Kristallsee", Assert.Single(titles).Title);
    }

    [Fact]
    public async Task TheXEM_down_keeps_the_numbering_and_names_and_never_fails_the_refresh()
    {
        await Service().UpdateSeriesAsync(_show);
        _xem.Down = true;

        var service = Service();
        var result = await service.UpdateSeriesAsync(_show);

        Assert.Equal(new XemUpdate(null, null), result);
        Assert.NotNull(service.LastError);
        Assert.Equal(2, Tmdb(1, 13).SceneSeasonNumber);
        Assert.Equal(2, _titles.For(MediaType.Series, _show.Id).Count);

        // The scheduled task reports the failure (System → Tasks shows it).
        await Assert.ThrowsAsync<XemException>(() => service.UpdateAllAsync());
    }

    [Fact]
    public async Task Unreadable_answers_count_as_TheXEM_being_down()
    {
        _xem.Responses["map/havemap"] = "<html>Service unavailable</html>";
        _xem.Responses["map/allNames"] = """{"result":"failure","data":[],"message":"maintenance"}""";

        var service = Service();
        var result = await service.UpdateSeriesAsync(_show);

        Assert.Equal(new XemUpdate(null, null), result);
        Assert.Contains("unreadable", service.LastError, StringComparison.Ordinal);

        // After a failure refreshes do not ask again for a while; the scheduled task does.
        Assert.Equal(0, _xem.Count("map/allNames"));
        await Assert.ThrowsAsync<XemException>(() => service.UpdateAllAsync());
        Assert.Contains("maintenance", service.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_of_mapped_series_and_the_names_are_kept_for_12_hours()
    {
        var service = Service();
        await service.UpdateSeriesAsync(_show);
        await service.UpdateSeriesAsync(_show);
        Assert.Equal(1, _xem.Count("map/allNames"));
        Assert.Equal(1, _xem.Count("map/havemap"));
        Assert.Equal(2, _xem.Count("map/all?"));

        _bed.Time.Now += XemService.CacheLifetime + TimeSpan.FromMinutes(1);
        await service.UpdateSeriesAsync(_show);
        Assert.Equal(2, _xem.Count("map/allNames"));
        Assert.Equal(2, _xem.Count("map/havemap"));
    }

    [Fact]
    public async Task A_series_TheXEM_no_longer_maps_loses_its_scene_numbering()
    {
        await Service().UpdateSeriesAsync(_show);
        _xem.Responses["map/havemap"] = """{"result":"success","data":[81189],"message":""}""";

        var result = await Service().UpdateSeriesAsync(_show);

        Assert.Equal(0, result.Mapped);
        Assert.DoesNotContain(_bed.Series.Episodes(_show.Id), e => e.SceneSeasonNumber is not null);
        Assert.Equal(1, _xem.Count("map/all?"));
    }

    [Fact]
    public async Task Series_without_a_TVDB_id_are_skipped()
    {
        var result = await Service().UpdateSeriesAsync(new Series { Id = _show.Id, Title = "Crystal Lake", TvdbId = null });

        Assert.Equal(new XemUpdate(null, null), result);
        Assert.Empty(_xem.Requests);
    }

    [Fact]
    public async Task Scheduled_task_updates_every_series_with_a_TVDB_id()
    {
        var updated = await Service().UpdateAllAsync();

        // Crystal Lake has a map; Breaking Bad (81189) is in havemap but its map is empty here.
        Assert.Equal(1, updated);
        Assert.Equal(2, Tmdb(1, 13).SceneSeasonNumber);
        Assert.Equal("Scene mappings", new SceneMappingsTask().Name);
        Assert.Equal(TimeSpan.FromHours(12), new SceneMappingsTask().Interval);
    }

    [Fact]
    public async Task Fate_Zero_season_names_are_ignored_like_in_Sonarr()
    {
        var names = await new XemClient(new HandlerFactory(_xem)).GetSceneNamesAsync(CancellationToken.None);

        Assert.Equal(["Fate Zero"], names[79151].Select(n => n.Title));
        Assert.Equal(3, names[TvdbId].Count);
    }

    public void Dispose() => _bed.Dispose();

    /// <summary>A TheXEM map answer: <c>{"scene": {...}, "tvdb": {...}}</c> per episode.</summary>
    public static string Map(IEnumerable<((int S, int E, int A) Scene, (int S, int E, int A) Tvdb)> rows) =>
        """{"result":"success","data":[""" + string.Join(',', rows.Select(r =>
            $$$"""{"scene":{"season":{{{r.Scene.S}}},"episode":{{{r.Scene.E}}},"absolute":{{{r.Scene.A}}}},"tvdb":{"season":{{{r.Tvdb.S}}},"episode":{{{r.Tvdb.E}}},"absolute":{{{r.Tvdb.A}}}}}""")) +
        """],"message":"full mapping for 289574 on tvdb. this was a cached version"}""";

    /// <summary>Every client goes to the fake TheXEM.</summary>
    public sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = XemClient.DefaultBaseAddress };
    }
}
