// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.Rss;
using StreamZH.Library.Search;
using StreamZH.Library.Titles;
using StreamZH.Parsing.Releases;

namespace StreamZH.Tests.Library.Titles;

/// <summary>Release names in German match movies and series by their alternative titles, like Radarr and Sonarr.</summary>
public class AlternativeTitleMatchingTests
{
    private static readonly Movie Prada = new() { Id = 1, TmdbId = 1100988, Title = "The Devil Wears Prada 2", OriginalTitle = "The Devil Wears Prada 2", Year = 2026 };
    private static readonly Movie Gatsby = new() { Id = 2, TmdbId = 64682, Title = "The Great Gatsby", OriginalTitle = "The Great Gatsby", Year = 2013 };
    private static readonly Movie Beauty = new() { Id = 3, TmdbId = 321612, Title = "Beauty and the Beast", OriginalTitle = "Beauty and the Beast", Year = 2017 };
    private static readonly Series Simpsons = new() { Id = 1, TmdbId = 456, Title = "The Simpsons", OriginalTitle = "The Simpsons", Year = 1989 };
    private static readonly Series Heist = new() { Id = 2, TmdbId = 71446, Title = "Money Heist", OriginalTitle = "La casa de papel", Year = 2017 };
    private static readonly Series Kaguya = new() { Id = 3, TmdbId = 83121, Title = "Kaguya-sama: Love Is War", OriginalTitle = "かぐや様は告らせたい", Year = 2019 };

    private static readonly AlternativeTitle[] Titles =
    [
        Title(MediaType.Movie, 1, "Der Teufel trägt Prada 2", AlternativeTitleSources.Translation, Language.German),
        Title(MediaType.Movie, 2, "Der große Gatsby", AlternativeTitleSources.Translation, Language.German),
        Title(MediaType.Movie, 3, "Die Schöne und das Biest", AlternativeTitleSources.Translation, Language.German),
        Title(MediaType.Series, 1, "Die Simpsons", AlternativeTitleSources.Translation, Language.German),
        Title(MediaType.Series, 2, "Haus des Geldes", AlternativeTitleSources.Translation, Language.German),
        Title(MediaType.Series, 3, "Kaguya-sama wa Kokurasetai", AlternativeTitleSources.Tmdb, Language.Japanese),
        Title(MediaType.Series, 3, "Kaguya-sama Love is War Ultra Romantic", AlternativeTitleSources.Xem, null, season: 1, sceneSeason: 3),
        Title(MediaType.Series, 3, "Kaguya-sama Love is War The First Kiss", AlternativeTitleSources.User, null, season: 4),
    ];

    private static readonly AlternativeTitleLookup Lookup = new(Titles);

    [Theory]
    [InlineData("Der.Teufel.traegt.Prada.2.2026.German.DL.1080p.WEB.h264-ViDEOTHEK")]
    [InlineData("Der.Teufel.trägt.Prada.2.2026.German.AC3.720p.BluRay.x264-GRP")]
    [InlineData("Der Teufel tragt Prada 2 2026 German DL 2160p WEB H265-GRP")]
    [InlineData("The.Devil.Wears.Prada.2.2026.German.DL.1080p.WEB.h264-GRP")]
    public void A_movie_matches_by_its_german_title(string name)
    {
        var parsed = ReleaseParser.Parse(name)!;

        Assert.Equal(ReleaseMatch.Match, ReleaseMatching.MatchMovie(parsed, Prada, null, Lookup.For(MediaType.Movie, Prada.Id)));
    }

    [Fact]
    public void Without_alternative_titles_the_german_name_is_another_movie()
    {
        var parsed = ReleaseParser.Parse("Der.Teufel.traegt.Prada.2.2026.German.DL.1080p.WEB.h264-ViDEOTHEK")!;

        Assert.Equal(ReleaseMatch.WrongTitle, ReleaseMatching.MatchMovie(parsed, Prada));
    }

    [Fact]
    public void The_year_still_has_to_fit_for_movies()
    {
        var parsed = ReleaseParser.Parse("Der.Teufel.traegt.Prada.2.2006.German.DL.1080p.BluRay.x264-GRP")!;

        Assert.Equal(ReleaseMatch.WrongYear, ReleaseMatching.MatchMovie(parsed, Prada, null, Lookup.For(MediaType.Movie, Prada.Id)));
    }

    [Theory]
    [InlineData("Der.Grosse.Gatsby.2013.German.DL.1080p.BluRay.x264-GRP", 2)]
    [InlineData("Der.Große.Gatsby.2013.German.1080p.BluRay.x264-GRP", 2)]
    [InlineData("Die.Schoene.und.das.Biest.2017.German.DL.1080p.BluRay.x264-GRP", 3)]
    [InlineData("Die.Schone.und.das.Biest.2017.German.DL.1080p.BluRay.x264-GRP", 3)]
    [InlineData("Die.Schöne.und.das.Biest.2017.German.DL.1080p.BluRay.x264-GRP", 3)]
    public void Umlauts_and_sharp_s_are_folded(string name, int movieId)
    {
        var parsed = ReleaseParser.Parse(name)!;
        var movie = movieId == 2 ? Gatsby : Beauty;

        Assert.Equal(ReleaseMatch.Match, ReleaseMatching.MatchMovie(parsed, movie, null, Lookup.For(MediaType.Movie, movie.Id)));
    }

    [Fact]
    public void A_series_matches_by_its_german_title()
    {
        var parsed = ReleaseParser.Parse("Die.Simpsons.S35E01.German.DL.1080p.WEB.h264-GRP")!;

        Assert.Equal(ReleaseMatch.WrongTitle, ReleaseMatching.MatchSeries(parsed, Simpsons));
        Assert.Equal(ReleaseMatch.Match, ReleaseMatching.MatchSeries(parsed, Simpsons, null, Lookup.For(MediaType.Series, Simpsons.Id)));
    }

    [Theory]
    [InlineData("Haus.des.Geldes.S01E01.German.DL.1080p.WEB.h264-GRP")]
    [InlineData("La.Casa.De.Papel.S01E01.German.DL.1080p.NF.WEB.h264-GRP")]
    [InlineData("Money.Heist.S01E01.1080p.NF.WEB.h264-GRP")]
    public void Money_heist_matches_by_every_title(string name)
    {
        var parsed = ReleaseParser.Parse(name)!;

        Assert.Equal(ReleaseMatch.Match, ReleaseMatching.MatchSeries(parsed, Heist, null, Lookup.For(MediaType.Series, Heist.Id)));
    }

    [Theory]
    [InlineData("Kaguya-sama.Love.is.War.Ultra.Romantic.S03E01.German.DL.1080p.WEB.h264-GRP", ReleaseMatch.Match)]
    [InlineData("Kaguya-sama.Love.is.War.Ultra.Romantic.S01E01.German.DL.1080p.WEB.h264-GRP", ReleaseMatch.WrongTitle)]
    [InlineData("Kaguya-sama.Love.is.War.The.First.Kiss.S04E01.German.DL.1080p.WEB.h264-GRP", ReleaseMatch.Match)]
    [InlineData("Kaguya-sama.Love.is.War.The.First.Kiss.S02E01.German.DL.1080p.WEB.h264-GRP", ReleaseMatch.WrongTitle)]
    [InlineData("Kaguya-sama.wa.Kokurasetai.S02E01.German.DL.1080p.WEB.h264-GRP", ReleaseMatch.Match)]
    public void A_title_for_one_season_only_matches_that_season(string name, ReleaseMatch expected)
    {
        var parsed = ReleaseParser.Parse(name)!;

        Assert.Equal(expected, ReleaseMatching.MatchSeries(parsed, Kaguya, null, Lookup.For(MediaType.Series, Kaguya.Id)));
    }

    [Fact]
    public void Season_rules_follow_sonarr_scene_mappings()
    {
        var scene = Titles.Single(t => t.Source == AlternativeTitleSources.Xem);
        var user = Titles.Single(t => t.Source == AlternativeTitleSources.User);

        Assert.True(ReleaseMatching.AppliesToSeason(scene, 3));
        Assert.False(ReleaseMatching.AppliesToSeason(scene, 1));
        Assert.True(ReleaseMatching.AppliesToSeason(scene, null));
        Assert.True(ReleaseMatching.AppliesToSeason(user, 4));
        Assert.False(ReleaseMatching.AppliesToSeason(user, 1));
        Assert.True(ReleaseMatching.AppliesToSeason(Titles[0], 7));
    }

    [Fact]
    public void Rss_finds_the_item_by_an_alternative_title()
    {
        var index = new LibraryIndex([Prada, Gatsby, Beauty], [Simpsons, Heist, Kaguya], Lookup);

        Assert.Same(Prada, index.FindMovie(ReleaseParser.Parse("Der.Teufel.traegt.Prada.2.2026.German.DL.1080p.WEB.h264-ViDEOTHEK")!));
        Assert.Same(Beauty, index.FindMovie(ReleaseParser.Parse("Die.Schone.und.das.Biest.2017.German.DL.1080p.BluRay.x264-GRP")!));
        Assert.Same(Simpsons, index.FindSeries(ReleaseParser.Parse("Die.Simpsons.S35E01.German.DL.1080p.WEB.h264-GRP")!));
        Assert.Same(Heist, index.FindSeries(ReleaseParser.Parse("Haus.des.Geldes.S01E01.German.DL.1080p.WEB.h264-GRP")!));
        Assert.Same(Kaguya, index.FindSeries(ReleaseParser.Parse("Kaguya-sama.Love.is.War.Ultra.Romantic.S03E01.German.DL.1080p.WEB.h264-GRP")!));
        Assert.Null(index.FindSeries(ReleaseParser.Parse("Kaguya-sama.Love.is.War.Ultra.Romantic.S01E01.German.DL.1080p.WEB.h264-GRP")!));

        var withoutTitles = new LibraryIndex([Prada], [Simpsons]);
        Assert.Null(withoutTitles.FindMovie(ReleaseParser.Parse("Der.Teufel.traegt.Prada.2.2026.German.DL.1080p.WEB.h264-ViDEOTHEK")!));
        Assert.Null(withoutTitles.FindSeries(ReleaseParser.Parse("Die.Simpsons.S35E01.German.DL.1080p.WEB.h264-GRP")!));
    }

    [Fact]
    public void The_matched_title_is_named()
    {
        var parsed = ReleaseParser.Parse("Die.Simpsons.S35E01.German.DL.1080p.WEB.h264-GRP")!;

        Assert.Equal("Die Simpsons", ReleaseMatching.MatchedTitle(parsed, ["The Simpsons", "Die Simpsons"], 1989, isSeries: true));
        Assert.Null(ReleaseMatching.MatchedTitle(parsed, ["The Simpsons"], 1989, isSeries: true));
    }

    [Fact]
    public void Clean_variants_add_the_spelling_without_e()
    {
        Assert.Equal(["dieschoeneunddasbiest", "dieschoneunddasbiest"], TitleNormalizer.CleanVariants("Die Schöne und das Biest"));
        Assert.Equal(["dergrossegatsby"], TitleNormalizer.CleanVariants("Der große Gatsby"));
        Assert.Equal(["thesimpsons"], TitleNormalizer.CleanVariants("The Simpsons"));
        Assert.Empty(TitleNormalizer.CleanVariants("  "));
    }

    private static AlternativeTitle Title(MediaType type, int itemId, string title, string source, Language? language, int? season = null, int? sceneSeason = null) => new()
    {
        MediaType = type,
        ItemId = itemId,
        Title = title,
        CleanTitle = TitleNormalizer.Clean(title),
        Source = source,
        LanguageId = language?.Id,
        SeasonNumber = season,
        SceneSeasonNumber = sceneSeason,
    };
}
