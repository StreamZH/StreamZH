// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.Import;

namespace StreamZH.Tests.Library.Import;

public sealed class FileNameBuilderTests
{
    private static readonly Movie Movie = new()
    {
        Title = "Dune: Part Two", OriginalTitle = "Dune: Part Two", Year = 2024, ImdbId = "tt15239678", TmdbId = 693134,
    };

    private static readonly Series Show = new() { Title = "The Office", Year = 2005, TvdbId = 73244, TmdbId = 2316 };

    private static readonly NamingFileInfo File = new()
    {
        Quality = new QualityModel(Quality.Bluray1080p, new Revision(2)),
        Languages = [Language.German, Language.English],
        ReleaseGroup = "DETAiLS",
        Edition = "IMAX",
        VideoCodec = "x264",
        AudioCodec = "DTS",
        SceneName = "Dune.Part.Two.2024.IMAX.German.DL.1080p.BluRay.PROPER.x264-DETAiLS",
        OriginalFileName = "dune.part.two.2024-details",
    };

    private static FileNameBuilder Builder(Func<MediaManagementValues, MediaManagementValues>? change = null) =>
        new(change is null ? new MediaManagementValues() : change(new MediaManagementValues()));

    private static Episode Ep(int season, int number, string title = "", string? airDate = "2005-03-24") =>
        new() { SeasonNumber = season, EpisodeNumber = number, Title = title, AirDate = airDate };

    [Fact]
    public void Default_movie_format_like_radarr()
    {
        Assert.Equal("Dune - Part Two (2024) Bluray-1080p Proper", Builder().MovieFileName(Movie, File));
        Assert.Equal("Dune - Part Two (2024)", Builder().MovieFolderName(Movie));
    }

    [Theory]
    [InlineData(ColonReplacement.Delete, "Dune Part Two")]
    [InlineData(ColonReplacement.Dash, "Dune- Part Two")]
    [InlineData(ColonReplacement.SpaceDash, "Dune - Part Two")]
    [InlineData(ColonReplacement.SpaceDashSpace, "Dune - Part Two")]
    [InlineData(ColonReplacement.Smart, "Dune - Part Two")]
    public void Colon_replacement(ColonReplacement replacement, string expected) =>
        Assert.Equal(expected, Builder(v => v with { ColonReplacement = replacement, StandardMovieFormat = "{Movie Title}" }).MovieFileName(Movie, File));

    [Fact]
    public void Smart_colon_turns_times_into_dashes()
    {
        var movie = new Movie { Title = "Kill Time 12:30", Year = 2001 };
        Assert.Equal("Kill Time 12-30", Builder(v => v with { StandardMovieFormat = "{Movie Title}" }).MovieFileName(movie, File));
    }

    [Fact]
    public void Illegal_characters_are_replaced_or_removed()
    {
        var movie = new Movie { Title = "Face/Off? <What> *Now* \"Quoted\" A|B", Year = 1997 };
        Assert.Equal("Face+Off! What -Now- Quoted AB", Builder(v => v with { StandardMovieFormat = "{Movie Title}" }).MovieFileName(movie, File));
        Assert.Equal(
            "FaceOff What Now Quoted AB",
            Builder(v => v with { StandardMovieFormat = "{Movie Title}", ReplaceIllegalCharacters = false }).MovieFileName(movie, File));
    }

    [Fact]
    public void Tokens_separators_case_prefix_and_suffix()
    {
        var builder = Builder(v => v with
        {
            StandardMovieFormat =
                "{Movie.CleanTitle}.{Release.Year}.{EDITION TAGS}.{quality title}{-Release Group} {[MediaInfo AudioLanguages]} {MediaInfo Simple} {imdb-{ImdbId}} {Tmdb Id}",
        });

        Assert.Equal("Dune.Part.Two.2024.IMAX.bluray-1080p-DETAiLS [DE+EN] x264 DTS {imdb-tt15239678} 693134", builder.MovieFileName(Movie, File));
    }

    [Fact]
    public void Plex_style_labels_disappear_when_empty()
    {
        var builder = Builder(v => v with { StandardMovieFormat = "{Movie Title} {edition-{Edition Tags}} {tmdb-{TmdbId}}" });
        Assert.Equal("Dune - Part Two {edition-IMAX} {tmdb-693134}", builder.MovieFileName(Movie, File));
        Assert.Equal("Dune - Part Two {tmdb-693134}", builder.MovieFileName(Movie, File with { Edition = null }));
    }

    [Fact]
    public void Escaped_braces_and_empty_tokens_leave_no_traces()
    {
        var bare = new NamingFileInfo();
        var builder = Builder(v => v with { StandardMovieFormat = "{Movie Title} ({Release Year}) [{Quality Full}] {-Release Group} {{x}}" });
        Assert.Equal("Dune - Part Two (2024) {x}", builder.MovieFileName(Movie, bare));
    }

    [Fact]
    public void Title_the_clean_title_and_first_character()
    {
        Assert.Equal("Matrix, The", FileNameBuilder.TitleThe("The Matrix"));
        Assert.Equal("Fast and Furious 6", FileNameBuilder.CleanTitle("Fast & Furious 6"));
        Assert.Equal("Dune Part Two", FileNameBuilder.CleanTitle("Dune: Part Two"));
        var movie = new Movie { Title = "The Matrix", Year = 1999 };
        Assert.Equal("Matrix, The (1999)", Builder(v => v with { MovieFolderFormat = "{Movie TitleThe} ({Release Year})" }).MovieFolderName(movie));
        Assert.Equal("M", Builder(v => v with { MovieFolderFormat = "{Movie TitleFirstCharacter}" }).MovieFolderName(movie));
    }

    [Fact]
    public void Default_episode_format_like_sonarr()
    {
        var name = Builder().EpisodeFileName(Show, [Ep(2, 3, "Office Olympics")], File with { Quality = new QualityModel(Quality.WebDl1080p) });
        Assert.Equal("The Office - S02E03 - Office Olympics WEBDL-1080p", name);
        Assert.Equal("The Office (2005)", Builder().SeriesFolderName(Show));
        Assert.Equal("Season 2", Builder().SeasonFolderName(Show, 2));
        Assert.Equal("Specials", Builder().SeasonFolderName(Show, 0));
        Assert.Null(Builder(v => v with { UseSeasonFolders = false }).SeasonFolderName(Show, 2));
        Assert.Equal("Season 02", Builder(v => v with { SeasonFolderFormat = "Season {season:00}" }).SeasonFolderName(Show, 2));
    }

    [Theory]
    [InlineData(MultiEpisodeStyle.Extend, "The Office - S01E01-02-03 - Pilot")]
    [InlineData(MultiEpisodeStyle.Duplicate, "The Office - S01E01 - S01E02 - S01E03 - Pilot")]
    [InlineData(MultiEpisodeStyle.Repeat, "The Office - S01E01E02E03 - Pilot")]
    [InlineData(MultiEpisodeStyle.Scene, "The Office - S01E01-E02-E03 - Pilot")]
    [InlineData(MultiEpisodeStyle.Range, "The Office - S01E01-03 - Pilot")]
    [InlineData(MultiEpisodeStyle.PrefixedRange, "The Office - S01E01-E03 - Pilot")]
    public void Multi_episode_styles(MultiEpisodeStyle style, string expected)
    {
        var builder = Builder(v => v with { MultiEpisodeStyle = style, StandardEpisodeFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title}" });
        Assert.Equal(expected, builder.EpisodeFileName(Show, [Ep(1, 1, "Pilot (1)"), Ep(1, 2, "Pilot (2)"), Ep(1, 3, "Pilot (3)")], File));
    }

    [Fact]
    public void Multi_episode_with_different_titles_and_dot_format()
    {
        var builder = Builder(v => v with
        {
            MultiEpisodeStyle = MultiEpisodeStyle.Duplicate,
            StandardEpisodeFormat = "{Series.Title}.S{season:00}E{episode:00}.{Episode.Title}",
        });
        Assert.Equal("The.Office.S01E01.S01E02.Pilot.+.Diversity.Day", builder.EpisodeFileName(Show, [Ep(1, 1, "Pilot"), Ep(1, 2, "Diversity Day")], File));
    }

    [Fact]
    public void Daily_and_anime_formats()
    {
        Assert.Equal("The Office - 2005-03-24 - Pilot Bluray-1080p Proper", Builder().EpisodeFileName(Show, [Ep(1, 1, "Pilot")], File, EpisodeFormat.Daily));
        var anime = new Series { Title = "Frieren", OriginalLanguageId = Language.Japanese.Id, Genres = ["Animation", "Fantasy"] };
        Assert.Equal(EpisodeFormat.Anime, FileNameBuilder.FormatFor(anime, namedByAirDate: false));
        Assert.Equal(EpisodeFormat.Standard, FileNameBuilder.FormatFor(Show, namedByAirDate: false));
        Assert.Equal(EpisodeFormat.Daily, FileNameBuilder.FormatFor(Show, namedByAirDate: true));
    }

    [Fact]
    public void Names_are_trimmed_and_safe_for_windows()
    {
        var movie = new Movie { Title = "CON", Year = 0 };
        Assert.Equal("CON_", Builder(v => v with { StandardMovieFormat = "{Movie Title} {Release Year}" }).MovieFileName(movie, new NamingFileInfo()));
        var dotted = new Movie { Title = "Hello...", Year = 0 };
        Assert.Equal("Hello", Builder(v => v with { StandardMovieFormat = "{Movie Title}" }).MovieFileName(dotted, new NamingFileInfo()));
        var longTitle = new Movie { Title = new string('a', 400), Year = 2000 };
        Assert.True(Builder().MovieFileName(longTitle, File).Length <= FileNameBuilder.MaxNameLength);
    }

    [Theory]
    [InlineData("{Movie Title} ({Release Year})", NamingFormatKind.MovieFile, null)]
    [InlineData("{Original Title}", NamingFormatKind.MovieFile, null)]
    [InlineData("{Release Year} {Quality Full}", NamingFormatKind.MovieFile, "Add the movie title")]
    [InlineData("{Movie Title} {Series Title}", NamingFormatKind.MovieFile, "Unknown token {Series Title}")]
    [InlineData("{Movie Title} {Quality Full}", NamingFormatKind.MovieFolder, "Unknown token {Quality Full}")]
    [InlineData("{Movie Title", NamingFormatKind.MovieFile, "A brace is not closed")]
    [InlineData("{Movie Title}: x", NamingFormatKind.MovieFile, "Remove the characters")]
    [InlineData("{Movie Title}/x", NamingFormatKind.MovieFile, "Subfolders are not supported")]
    [InlineData("{Series Title} - {Episode Title}", NamingFormatKind.StandardEpisode, "Add the season and episode numbers")]
    [InlineData("{Series Title} - {Air-Date}", NamingFormatKind.DailyEpisode, null)]
    [InlineData("{Series Title}", NamingFormatKind.DailyEpisode, "Add the air date")]
    [InlineData("Season", NamingFormatKind.SeasonFolder, "Add the season number")]
    [InlineData("", NamingFormatKind.MovieFile, "Enter a format")]
    [InlineData("{season:00} {episode:000} {SERIES TITLE}", NamingFormatKind.StandardEpisode, null)]
    [InlineData("{Movie Title} {imdb-{ImdbId}} {edition-{Edition Tags}}", NamingFormatKind.MovieFile, null)]
    [InlineData("{Movie Title} {imdb-{Quality Full}}", NamingFormatKind.MovieFolder, "Unknown token {Quality Full}")]
    public void Formats_are_validated(string format, NamingFormatKind kind, string? problem)
    {
        var result = FileNameBuilder.ValidateFormat(format, kind);
        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Contains(problem, result, StringComparison.Ordinal);
        }
    }
}
