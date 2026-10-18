// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.Search;
using StreamZH.Parsing.Releases;

namespace StreamZH.Tests.Library.SceneNumbering;

/// <summary>Release names mapped to library episodes per series type, like Sonarr's ParsingService (no database).</summary>
public class SeriesTypeMatchingTests
{
    private static readonly Series Anime = new() { Id = 7, Title = "Frieren", SeriesType = SeriesType.Anime };
    private static readonly Series Daily = new() { Id = 8, Title = "Late Night Talk", SeriesType = SeriesType.Daily };
    private static readonly Series Standard = new() { Id = 9, Title = "Crystal Lake", SeriesType = SeriesType.Standard };

    /// <summary>Two seasons of 12 episodes and one special; absolute numbers 1-24.</summary>
    private static List<Episode> AnimeLibrary()
    {
        var episodes = Enumerable.Range(1, 12).Select(e => new Episode { SeasonNumber = 1, EpisodeNumber = e })
            .Concat(Enumerable.Range(1, 12).Select(e => new Episode { SeasonNumber = 2, EpisodeNumber = e }))
            .Append(new Episode { SeasonNumber = 0, EpisodeNumber = 1, Title = "OVA" })
            .ToList();
        for (var i = 0; i < episodes.Count; i++)
        {
            episodes[i].Id = i + 1;
        }

        EpisodeNumbering.AssignAbsoluteNumbers(episodes);
        return episodes;
    }

    private static List<Episode> DailyLibrary() =>
    [
        .. Enumerable.Range(12, 5).Select(day => new Episode
        {
            Id = day, SeasonNumber = 11, EpisodeNumber = day - 11, AirDate = $"2026-10-{day:00}", Title = $"Show {day}",
        }),
    ];

    private static (IReadOnlyList<Episode> Episodes, Rejection? Rejection) Map(string release, Series series, IReadOnlyList<Episode> library) =>
        ReleaseMatching.MapEpisodes(ReleaseParser.ParseEpisode(release) ?? throw new InvalidOperationException($"Not parsed: {release}"), library, series);

    private static string Codes(IEnumerable<Episode> episodes) =>
        string.Join(",", episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).Select(e => $"S{e.SeasonNumber:00}E{e.EpisodeNumber:00}"));

    [Fact]
    public void Absolute_numbers_count_regular_seasons_in_order_and_skip_specials()
    {
        var episodes = new List<Episode>
        {
            new() { SeasonNumber = 2, EpisodeNumber = 1 },
            new() { SeasonNumber = 0, EpisodeNumber = 1 },
            new() { SeasonNumber = 1, EpisodeNumber = 2 },
            new() { SeasonNumber = 1, EpisodeNumber = 1 },
        };

        EpisodeNumbering.AssignAbsoluteNumbers(episodes);

        Assert.Equal([3, null, 2, 1], episodes.Select(e => e.AbsoluteEpisodeNumber));
    }

    [Theory]
    [InlineData("[GER] Frieren - 05 [1080p]", "S01E05")]
    [InlineData("[SubsPlease] Frieren - 13 (1080p) [ABCD1234].mkv", "S02E01")]
    [InlineData("Frieren - 013 [1080p]", "S02E01")]
    [InlineData("[Group] Frieren - 12v2 [1080p]", "S01E12")]
    [InlineData("Frieren.E13.German.DL.1080p.WEB.x264-GRP", "S02E01")]
    [InlineData("Frieren.S01E05.German.DL.AniME.1080p.BluRay.x264-GRP", "S01E05")]
    [InlineData("Frieren.S02E03.German.DL.ANiME.1080p.WEB.h264-GRP", "S02E03")]
    [InlineData("[Group] Frieren - 12-14 [1080p]", "S01E12,S02E01,S02E02")]
    [InlineData("[DeadFish] Frieren - 01 - OVA [BD][720p][AAC]", "S00E01")]
    [InlineData("Frieren.S02.German.DL.AniME.1080p.BluRay.x264-GRP", "S02E01,S02E02,S02E03,S02E04,S02E05,S02E06,S02E07,S02E08,S02E09,S02E10,S02E11,S02E12")]
    public void Anime_releases_find_episodes_by_absolute_number(string release, string expected)
    {
        var (episodes, rejection) = Map(release, Anime, AnimeLibrary());

        Assert.Null(rejection);
        Assert.Equal(expected, Codes(episodes));
    }

    [Fact]
    public void German_anime_tag_counts_as_the_language()
    {
        var parsed = ReleaseParser.ParseEpisode("[GER] Frieren - 05 [1080p]")!;

        Assert.Equal([Language.German], parsed.Languages);
        Assert.Contains(Language.German, ReleaseParser.ParseEpisode("Frieren.S01E05.German.DL.AniME.1080p.BluRay.x264-GRP")!.Languages);
        Assert.Equal([Language.Unknown], ReleaseParser.ParseEpisode("[SubsPlease] Frieren - 05 (1080p) [ABCD1234]")!.Languages);
    }

    [Fact]
    public void Unknown_absolute_number_is_rejected_with_a_readable_reason()
    {
        var (episodes, rejection) = Map("[GER] Frieren - 99 [1080p]", Anime, AnimeLibrary());

        Assert.Empty(episodes);
        Assert.Equal(RejectionCodes.UnknownEpisode, rejection?.Code);
        Assert.Equal("Absolute episode 99 is not in the library", rejection?.Message);
    }

    [Fact]
    public void Scene_absolute_numbers_win_over_the_counted_ones()
    {
        var library = AnimeLibrary();

        // TheXEM: the scene counts the special as episode 13, so scene 14 is TMDB S02E01.
        foreach (var e in library.Where(e => e.SeasonNumber == 2))
        {
            e.SceneSeasonNumber = 2;
            e.SceneEpisodeNumber = e.EpisodeNumber;
            e.SceneAbsoluteEpisodeNumber = e.AbsoluteEpisodeNumber + 1;
        }

        Assert.Equal("S02E01", Codes(Map("[GER] Frieren - 14 [1080p]", Anime, library).Episodes));

        // A file in the library is named in TMDB numbering: no scene lookup.
        var parsed = ReleaseParser.ParseEpisode("Frieren - 014")!;
        Assert.Equal("S02E02", Codes(ReleaseMatching.MapEpisodes(parsed, library, Anime, sceneSource: false).Episodes));
    }

    [Fact]
    public void Several_episodes_with_the_same_scene_absolute_number_are_ambiguous()
    {
        var library = AnimeLibrary();
        library.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1).SceneAbsoluteEpisodeNumber = 30;
        library.Single(e => e.SeasonNumber == 2 && e.EpisodeNumber == 1).SceneAbsoluteEpisodeNumber = 30;

        // Like Sonarr: no guess between the two; nothing has the counted absolute number 30 either.
        Assert.NotNull(Map("[GER] Frieren - 30 [1080p]", Anime, library).Rejection);
    }

    [Theory]
    [InlineData("Late.Night.Talk.2026.10.14.German.1080p.WEB.h264-GRP")]
    [InlineData("Late.Night.Talk.14.10.2026.German.720p.WEB.x264-GRP")]
    [InlineData("Late Night Talk 2026-10-14 1080p WEB h264-GRP")]
    public void Daily_releases_find_the_episode_by_air_date(string release)
    {
        var (episodes, rejection) = Map(release, Daily, DailyLibrary());

        Assert.Null(rejection);
        Assert.Equal("2026-10-14", Assert.Single(episodes).AirDate);
    }

    [Fact]
    public void Daily_release_for_a_date_without_an_episode_is_rejected()
    {
        var (_, rejection) = Map("Late.Night.Talk.2026.10.18.German.1080p.WEB.h264-GRP", Daily, DailyLibrary());

        Assert.Equal("No episode aired on 2026-10-18", rejection?.Message);
    }

    [Fact]
    public void Daily_series_still_take_season_and_episode_names()
    {
        Assert.Equal("S11E03", Codes(Map("Late.Night.Talk.S11E03.German.1080p.WEB.h264-GRP", Daily, DailyLibrary()).Episodes));
    }

    [Fact]
    public void Standard_series_use_scene_numbering_from_TheXEM()
    {
        var library = Enumerable.Range(1, 25).Select(e => new Episode { Id = e, SeasonNumber = 1, EpisodeNumber = e }).ToList();
        EpisodeNumbering.AssignAbsoluteNumbers(library);
        foreach (var e in library)
        {
            (e.SceneSeasonNumber, e.SceneEpisodeNumber, e.SceneAbsoluteEpisodeNumber) = e.EpisodeNumber <= 12 ? (1, e.EpisodeNumber, e.EpisodeNumber) : (2, e.EpisodeNumber - 12, e.EpisodeNumber);
        }

        Assert.Equal("S01E13", Codes(Map("Crystal.Lake.S02E01.German.1080p.WEB.h264-GRP", Standard, library).Episodes));
        Assert.Equal("S01E13,S01E14", Codes(Map("Crystal.Lake.S02E01E02.German.1080p.WEB.h264-GRP", Standard, library).Episodes));
        Assert.Equal("S01E05", Codes(Map("Crystal.Lake.S01E05.German.1080p.WEB.h264-GRP", Standard, library).Episodes));

        // Scene season 2 pack = the second half of TMDB season 1.
        var pack = Map("Crystal.Lake.S02.German.1080p.WEB.h264-GRP", Standard, library).Episodes;
        Assert.Equal(13, pack.Count);
        Assert.All(pack, e => Assert.True(e.EpisodeNumber >= 13));

        // A TMDB number the scene does not use falls back to TMDB (Sonarr does the same).
        Assert.Equal("S01E20", Codes(Map("Crystal.Lake.S01E20.German.1080p.WEB.h264-GRP", Standard, library).Episodes));

        // Without scene source (library files), TMDB numbers count.
        var parsed = ReleaseParser.ParseEpisode("Crystal Lake - S02E01")!;
        Assert.NotNull(ReleaseMatching.MapEpisodes(parsed, library, Standard, sceneSource: false).Rejection);
    }

    [Fact]
    public void Standard_series_take_absolute_only_names_but_keep_SxxEyy_first()
    {
        var library = AnimeLibrary();

        Assert.Equal("S02E01", Codes(Map("[GER] Crystal Lake - 13 [1080p]", Standard, library).Episodes));

        // S01E13 does not exist and a standard series does not reinterpret it.
        Assert.NotNull(Map("Crystal.Lake.E13.German.1080p.WEB.x264-GRP", Standard, library).Rejection);
    }

    [Fact]
    public void Scene_season_from_a_TheXEM_name()
    {
        var parsed = ReleaseParser.ParseEpisode("[GER] Frieren Second Season - 01 [1080p]")!;
        AlternativeTitle[] titles =
        [
            new() { MediaType = MediaType.Series, ItemId = 7, Title = "Frieren", CleanTitle = "frieren", Source = AlternativeTitleSources.Xem },
            new()
            {
                MediaType = MediaType.Series, ItemId = 7, Title = "Frieren Second Season", CleanTitle = TitleNormalizer.Clean("Frieren Second Season"),
                Source = AlternativeTitleSources.Xem, SceneSeasonNumber = 2,
            },
        ];

        var season = ReleaseMatching.SceneSeasonFor(parsed, titles);

        Assert.Equal(2, season);
        Assert.Equal("S02E01", Codes(ReleaseMatching.MapEpisodes(parsed, AnimeLibrary(), Anime, sceneSeasonNumber: season).Episodes));
        Assert.Null(ReleaseMatching.SceneSeasonFor(ReleaseParser.ParseEpisode("[GER] Frieren - 01 [1080p]")!, titles));
    }

    [Theory]
    [InlineData(new[] { 16, 18 }, new[] { "JP" }, "ja", SeriesType.Anime)]
    [InlineData(new[] { 16 }, new string[0], "ja", SeriesType.Anime)]
    [InlineData(new[] { 16 }, new[] { "US" }, "en", SeriesType.Standard)]
    [InlineData(new[] { 10767 }, new[] { "US" }, "en", SeriesType.Daily)]
    [InlineData(new[] { 10763 }, new[] { "DE" }, "de", SeriesType.Daily)]
    [InlineData(new[] { 18 }, new[] { "JP" }, "ja", SeriesType.Standard)]
    public void Series_type_is_guessed_from_TMDB(int[] genres, string[] countries, string language, SeriesType expected) =>
        Assert.Equal(expected, SeriesTypes.Suggest(genres, null, countries, language));

    [Fact]
    public void Series_type_names_for_the_api()
    {
        Assert.Equal("anime", SeriesTypes.ToApi(SeriesType.Anime));
        Assert.True(SeriesTypes.TryParse("Daily", out var daily));
        Assert.Equal(SeriesType.Daily, daily);
        Assert.False(SeriesTypes.TryParse("weekly", out _));
        Assert.Equal(SeriesType.Anime, SeriesTypes.Suggest(null, ["Animation"], ["JP"], null));
    }
}
