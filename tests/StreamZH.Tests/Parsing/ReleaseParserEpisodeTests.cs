// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Quality;
using StreamZH.Parsing.Releases;

namespace StreamZH.Tests.Parsing;

/// <summary>
/// Episode release names. Most cases are ported from Sonarr's parser fixtures. Unlike Sonarr, StreamZH strips a trailing
/// year from the series title into <see cref="ParsedRelease.Year"/>, so "Series Title 2010" is expected as ("Series Title", 2010).
/// </summary>
public class ReleaseParserEpisodeTests
{
    [Theory]
    [InlineData("Breaking.Bad.S01E02.720p.HDTV.x264-GRP", "Breaking Bad", 1, new[] { 2 })]
    [InlineData("Show.S01E01E02.1080p.WEB.h264-GRP", "Show", 1, new[] { 1, 2 })]
    [InlineData("Show.S03E01-E03.1080p", "Show", 3, new[] { 1, 2, 3 })]
    [InlineData("Show.4x07.Title.720p", "Show", 4, new[] { 7 })]
    public void Owner_episode_cases(string release, string title, int season, int[] episodes)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", episodes, parsed?.EpisodeNumbers)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Verify();

        Assert.Null(ReleaseParser.ParseMovie(release));
        Assert.True(ReleaseParser.Parse(release)?.IsEpisode);
    }

    [Fact]
    public void Owner_season_pack()
    {
        const string release = "Show.S02.German.DL.1080p.WEB.h264-GRP";
        var parsed = ReleaseParser.Parse(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", "Show", parsed?.Title)
            .Equal("SeasonNumber", 2, parsed?.SeasonNumber)
            .Equal("FullSeason", true, parsed?.FullSeason)
            .Equal("MultiSeason", false, parsed?.MultiSeason)
            .Sequence("EpisodeNumbers", [], parsed?.EpisodeNumbers)
            .Verify();
        Assert.Null(ReleaseParser.ParseMovie(release));
    }

    [Fact]
    public void Owner_multi_season_pack()
    {
        const string release = "Show.S01-S03.1080p.BluRay";
        var parsed = ReleaseParser.Parse(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", "Show", parsed?.Title)
            .Equal("SeasonNumber", 1, parsed?.SeasonNumber)
            .Equal("FullSeason", true, parsed?.FullSeason)
            .Equal("MultiSeason", true, parsed?.MultiSeason)
            .Verify();
        Assert.Null(ReleaseParser.ParseMovie(release));
    }

    [Fact]
    public void Owner_daily_show()
    {
        const string release = "The.Daily.Show.2026.10.16.1080p.WEB.h264-GRP";
        var parsed = ReleaseParser.Parse(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", "The Daily Show", parsed?.Title)
            .Equal("AirDate", "2026-10-16", parsed?.AirDate)
            .Equal("SeasonNumber", null, parsed?.SeasonNumber)
            .Equal("IsEpisode", true, parsed?.IsEpisode)
            .Verify();
        Assert.Null(ReleaseParser.ParseMovie(release));
    }

    [Fact]
    public void Owner_anime_absolute()
    {
        const string release = "[SubGroup] Anime Title - 1025 [1080p]";
        var parsed = ReleaseParser.Parse(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", "Anime Title", parsed?.Title)
            .Sequence("AbsoluteEpisodeNumbers", [1025], parsed?.AbsoluteEpisodeNumbers)
            .Equal("IsEpisode", true, parsed?.IsEpisode)
            .Verify();
        Assert.Null(ReleaseParser.ParseMovie(release));
    }

    [Fact]
    public void Owner_special()
    {
        const string release = "Show.S00E05.Special.720p";
        var parsed = ReleaseParser.Parse(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", "Show", parsed?.Title)
            .Equal("SeasonNumber", 0, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [5], parsed?.EpisodeNumbers)
            .Equal("IsSpecial", true, parsed?.IsSpecial)
            .Verify();
    }

    [Theory]
    [InlineData("Show.S01E01.1080p.WEB.H264-GRP", "x264", null)]
    [InlineData("Show.S01E01.2160p.WEB.H.265.DDP5.1.Atmos-GRP", "x265", "EAC3 Atmos")]
    [InlineData("Show.S01E01.1080p.BluRay.HEVC.10bit.TrueHD.7.1.Atmos-GRP", "x265", "TrueHD Atmos")]
    [InlineData("Show.S01E01.720p.HDTV.XviD.AC3-GRP", "XviD", "AC3")]
    [InlineData("Show.S01E01.1080p.WEB-DL.AAC2.0.H.264-GRP", "x264", "AAC")]
    [InlineData("Show.S01E01.1080p.AV1.Opus-GRP", "AV1", "Opus")]
    public void Parses_codecs(string release, string? videoCodec, string? audioCodec)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("VideoCodec", videoCodec, parsed?.VideoCodec)
            .Equal("AudioCodec", audioCodec, parsed?.AudioCodec)
            .Verify();
    }

    [Fact]
    public void Codecs_are_not_read_from_the_title()
    {
        var parsed = ReleaseParser.ParseEpisode("Opus.Dei.S01E01.1080p.WEB.H264-GRP");
        Assert.NotNull(parsed);
        Assert.Equal("Opus Dei", parsed.Title);
        Assert.Null(parsed.AudioCodec);
    }

    [Theory]
    [InlineData("Show.S01E01.1080p.WEB.h264-GRP", "show")]
    [InlineData("The.Office.US.S01E01.720p", "theofficeus")]
    [InlineData("Law.and.Order.S01E01.720p", "laworder")]
    public void Fills_clean_title(string release, string cleanTitle)
    {
        Assert.Equal(cleanTitle, ReleaseParser.ParseEpisode(release)?.CleanTitle);
    }

    [Theory]
    [InlineData("yrucreM-462.H.0.2CAA.LD-BEW.p027.10E10S.esaeleR.dehsaH.emoS.mkv", "Some Hashed Release", 1, 1)]
    [InlineData("yrucreM-LN 1.5DD LD-BEW P0801 10E10S esaeleR dehsaH emoS.mkv", "Some Hashed Release", 1, 1)]
    [InlineData("50E50S.denorD.mkv", "Droned", 5, 5)]
    [InlineData("11E-20S seires.mkv", "series", 2, 11)]
    public void Parses_reversed_release_names(string release, string title, int season, int episode)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [episode], parsed?.EpisodeNumbers)
            .Verify();
    }

    [Theory]
    [InlineData("Show.S01E01.1080p.WEB.h264-GRP", "Show.S01E01.1080p.WEB.h264-GRP")]
    [InlineData("Show.S01E01.1080p.WEB.h264-GRP.mkv", "Show.S01E01.1080p.WEB.h264-GRP.mkv")]
    public void Keeps_the_release_title(string release, string releaseTitle)
    {
        Assert.Equal(releaseTitle, ReleaseParser.ParseEpisode(release)?.ReleaseTitle);
    }

    [Fact]
    public void Parses_imdb_and_tmdb_ids()
    {
        var parsed = ReleaseParser.ParseEpisode("Show (2020) {tmdb-12345} [tt7654321] - S01E02 - Title");
        Assert.NotNull(parsed);
        Assert.Equal(12345, parsed.TmdbId);
        Assert.Equal("tt7654321", parsed.ImdbId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Season 3")]
    [InlineData("Specials")]
    public void Returns_null_for_empty_names_and_season_folders(string release)
    {
        Assert.Null(ReleaseParser.ParseEpisode(release));
    }

    [Fact]
    public void Survives_pathological_input()
    {
        var release = string.Concat(Enumerable.Repeat("a.-[", 2000)) + "S01E01";
        var parsed = ReleaseParser.Parse(release);
        Assert.True(parsed is null || parsed.ReleaseTitle == release);
    }

    [Theory]
    [InlineData("Series.With.Title.S02E15", "Series With Title", 0, 2, 15)]
    [InlineData("Series.and.a.Title.103.720p.HDTV.X264-DIMENSION", "Series and a Title", 0, 1, 3)]
    [InlineData("Series.and.a.Title.113.720p.HDTV.X264-DIMENSION", "Series and a Title", 0, 1, 13)]
    [InlineData("Series.and.a.Title.1013.720p.HDTV.X264-DIMENSION", "Series and a Title", 0, 10, 13)]
    [InlineData("Series.Title.4x05.HDTV.XviD-LOL", "Series Title", 0, 4, 5)]
    [InlineData("Series.Title.S03E06.DVDRip.XviD-WiDE", "Series Title", 0, 3, 6)]
    [InlineData("Series.Title.S10E27.WS.DSR.XviD-2HD", "Series Title", 0, 10, 27)]
    [InlineData("Series.Title.2010.S02E14.HDTV.XviD-LOL", "Series Title", 2010, 2, 14)]
    [InlineData("Series Title 0 S01E19 720p WEB DL DD5 1 H 264 NT", "Series Title 0", 0, 1, 19)]
    [InlineData("The Series Title S01E14 A Message Back 720p WEB DL DD5 1 H264 SURFER", "The Series Title", 0, 1, 14)]
    [InlineData("Series Title S01E07 WS PDTV XviD FUtV", "Series Title", 0, 1, 7)]
    [InlineData("Series.Title.S03E19.DVDRip.XviD-OSiTV", "Series Title", 0, 3, 19)]
    [InlineData("S03E09 WS PDTV XviD FUtV", "", 0, 3, 9)]
    [InlineData("5x10 WS PDTV XviD FUtV", "", 0, 5, 10)]
    [InlineData("Series.Title.2009.S01E14.HDTV.XviD-LOL", "Series Title", 2009, 1, 14)]
    [InlineData("Series.Title.1995.S03E20.HDTV.XviD-LOL", "Series Title", 1995, 3, 20)]
    [InlineData("Series.Title.S03E115.DVDRip.XviD-OSiTV", "Series Title", 0, 3, 115)]
    [InlineData("Series Title - S02E21 - 94 Meetings - 720p TV.mkv", "Series Title", 0, 2, 21)]
    [InlineData("24-7 Series - Title - Road to the Sonarr - S01E03 - Episode 3.mkv", "24-7 Series - Title - Road to the Sonarr", 0, 1, 3)]
    [InlineData("Series.Title.Inc.S03E19.DVDRip.\"XviD\"-OSiTV", "Series Title Inc", 0, 3, 19)]
    [InlineData("Series Title-0 (2010) - 1x05 - Missing Title", "Series Title-0", 2010, 1, 5)]
    [InlineData("Series Title-0 (2010) - 1x05 - Title", "Series Title-0", 2010, 1, 5)]
    [InlineData("Series Title - S06E13 - 5 to 9 [DVD]", "Series Title", 0, 6, 13)]
    [InlineData("Series Title - S02E21 - 18-5-4", "Series Title", 0, 2, 21)]
    [InlineData("Series.Title.S01E07.21.0.Jump.Street.720p.WEB-DL.DD5.1.h.264-KiNGS", "Series Title", 0, 1, 7)]
    [InlineData("Series.Title.525", "Series Title", 0, 5, 25)]
    [InlineData("Series Title - 10x12 - 24 Hour Propane People [SDTV]", "Series Title", 0, 10, 12)]
    [InlineData("Series Title S01E06 3 Beers For Batali DVDRip XviD SPRiNTER", "Series Title", 0, 1, 6)]
    [InlineData("24 7 Series-Title - Road to the Sonarr Part01 720p HDTV x264 ORENJI", "24 7 Series-Title - Road to the Sonarr", 0, 1, 1)]
    [InlineData("24 7 Series-Title - Road to the Sonarr Part 02 720p HDTV x264 ORENJI", "24 7 Series-Title - Road to the Sonarr", 0, 1, 2)]
    [InlineData("24-7 Series-Title - Road to the Sonarr - S01E01 - Part 1", "24-7 Series-Title - Road to the Sonarr", 0, 1, 1)]
    [InlineData("S6E02-Unwrapped-(Playing With Food) - [DarkData]", "", 0, 6, 2)]
    [InlineData("S06E03-Unwrapped-(Number Ones Unwrapped) - [DarkData]", "", 0, 6, 3)]
    [InlineData("Series Title S02E21 18 5 4 720p WEB DL DD5 1 h 264 EbP", "Series Title", 0, 2, 21)]
    [InlineData("01x04 - Halloween, Part 1 - 720p WEB-DL", "", 0, 1, 4)]
    [InlineData("series.s03.e05.ws.dvdrip.xvid-m00tv", "series", 0, 3, 5)]
    [InlineData("series.2009.416.hdtv-lol", "series", 2009, 4, 16)]
    [InlineData("series.six-0.2010.217.hdtv-lol", "series six-0", 2010, 2, 17)]
    [InlineData("Series Title - S1936E18 - I Love to Singa", "Series Title", 0, 1936, 18)]
    [InlineData("Series_Title!_-_7x6_-_The_Scarlett_Getter_[SDTV]", "Series Title!", 0, 7, 6)]
    [InlineData("Series_Title_-_1x1_-_Live_and_Learn_[HDTV-720p]", "Series Title", 0, 1, 1)]
    [InlineData("Top Series - 07x03 - 2005.11.70", "Top Series", 0, 7, 3)]
    [InlineData("Series.S04E09.Swan.Song.1080p.WEB-DL.DD5.1.H.264-ECI", "Series", 0, 4, 9)]
    [InlineData("S08E20 50-50 Carla [DVD]", "", 0, 8, 20)]
    [InlineData("Series S08E20 50-50 Carla [DVD]", "Series", 0, 8, 20)]
    [InlineData("S02E10 6-50 to SLC [SDTV]", "", 0, 2, 10)]
    [InlineData("Developers & Coders S02E10 6-50 to SLC [SDTV]", "Developers & Coders", 0, 2, 10)]
    [InlineData("The_Big_Series_Title_-_6x12_-_The_Code_Review_Equivalency_[HDTV-720p]", "The Big Series Title", 0, 6, 12)]
    [InlineData("Series_Title.19x06.720p_HDTV_x264-FoV", "Series Title", 0, 19, 6)]
    [InlineData("Series.S03E10.Alexandra.720p.WEB-DL.AAC2.0.H.264-CROM.mkv", "Series", 0, 3, 10)]
    [InlineData("(Series of Title s03 e - \"Series of Title Season 3 Episode 10\"", "Series of Title", 0, 3, 10)]
    [InlineData("Series.Hunters.Galatic.S05E607.720p.hdtv.x264", "Series Hunters Galatic", 0, 5, 607)]
    [InlineData("Series.Time.With.Dev1.And.Dev2.S01E20.720p.BluRay.x264-DEiMOS", "Series Time With Dev1 And Dev2", 0, 1, 20)]
    [InlineData("Series.S01E04.2-45.PM.[HDTV-720p].mkv", "Series", 0, 1, 4)]
    [InlineData("S01E04", "", 0, 1, 4)]
    [InlineData("1x04", "", 0, 1, 4)]
    [InlineData("10.Lines.You.Know.About.Code.S02E04.Prohibition.HDTV.XviD-AFG", "10 Lines You Know About Code", 0, 2, 4)]
    [InlineData("30 Series - S01E01 - Pilot.avi", "30 Series", 0, 1, 1)]
    [InlineData("666 Series Title - S01E01", "666 Series Title", 0, 1, 1)]
    [InlineData("Series 13 - S01E01", "Series 13", 0, 1, 1)]
    [InlineData("Don't Ever Trust The B---- in Code 23.S01E01", "Don't Ever Trust The B---- in Code 23", 0, 1, 1)]
    [InlineData("Warehouse.13.S01E01", "Warehouse 13", 0, 1, 1)]
    [InlineData("Never.Trust.The.B----.in.Code.23.S01E01", "Never Trust The B---- in Code 23", 0, 1, 1)]
    [InlineData("42 S01E01", "42", 0, 1, 1)]
    [InlineData("69.S01E01", "69", 0, 1, 1)]
    [InlineData("Series - 2x12 - The Choice [HDTV-1080p].mkv", "Series", 0, 2, 12)]
    [InlineData("Series - 2x4 - New Car Smell [HDTV-1080p].mkv", "Series", 0, 2, 4)]
    [InlineData("Top Series - 06x11 - 2005.08.07", "Top Series", 0, 6, 11)]
    [InlineData("The_Series_US_s06e19_04.28.2014_hdtv.x264.Poke.mp4", "The Series US", 0, 6, 19)]
    [InlineData("the.Series.110.hdtv-lol", "the Series", 0, 1, 10)]
    [InlineData("2009x09 [SDTV].avi", "", 0, 2009, 9)]
    [InlineData("S2009E09 [SDTV].avi", "", 0, 2009, 9)]
    [InlineData("Series Week S2009E09 [SDTV].avi", "Series Week", 0, 2009, 9)]
    [InlineData("St_Series_209_Aids_And_Comfort", "St Series", 0, 2, 9)]
    [InlineData("[Impatience] Series - 0x01 [720p][34073169].mkv", "Series", 0, 0, 1)]
    [InlineData("Series.Title.S15.E06.City.Sushi", "Series Title", 0, 15, 6)]
    [InlineData("Series Title - S15 E06 - City Code", "Series Title", 0, 15, 6)]
    [InlineData("Series S1-E1-WEB-DL-1080p-NZBgeek", "Series", 0, 1, 1)]
    [InlineData("Series S1E1-WEB-DL-1080p-NZBgeek", "Series", 0, 1, 1)]
    [InlineData("Series.S010E16.720p.HDTV.X264-DIMENSION", "Series", 0, 10, 16)]
    [InlineData("[ www.Torrenting.com ] - Series.2012.S02E17.720p.HDTV.X264-DIMENSION", "Series", 2012, 2, 17)]
    [InlineData("Series.2012.S02E18.720p.HDTV.X264-DIMENSION.mkv", "Series", 2012, 2, 18)]
    [InlineData("Series - Season 1 - Episode 01 (Resolution).avi", "Series", 0, 1, 1)]
    [InlineData("5x09 - 100 [720p WEB-DL].mkv", "", 0, 5, 9)]
    [InlineData("1x03 - 274 [1080p BluRay].mkv", "", 0, 1, 3)]
    [InlineData("1x03 - The 112th Congress [1080p BluRay].mkv", "", 0, 1, 3)]
    [InlineData("Series.2012.S02E14.720p.HDTV.X264-DIMENSION [PublicHD].mkv", "Series", 2012, 2, 14)]
    [InlineData("Series.2009.S06E03.720p.HDTV.X264-DIMENSION [PublicHD].mkv", "Series", 2009, 6, 3)]
    [InlineData("20-1.2014.S02E01.720p.HDTV.x264-CROOKS", "20-1", 2014, 2, 1)]
    [InlineData("Series - S01E09 - Debate 109", "Series", 0, 1, 9)]
    [InlineData("Series - S02E02 - My Maserati Does 185", "Series", 0, 2, 2)]
    [InlineData("6x13 - The Series Show 100th Episode Special", "", 0, 6, 13)]
    [InlineData("The Series And the Show - S41 E10478 - 2014-08-15", "The Series And the Show", 0, 41, 10478)]
    [InlineData("The Series And the Show - S42 E10591 - 2015-01-27", "The Series And the Show", 0, 42, 10591)]
    [InlineData("Series Title [1x05] Episode Title", "Series Title", 0, 1, 5)]
    [InlineData("Series Title [S01E05] Episode Title", "Series Title", 0, 1, 5)]
    [InlineData("Series Title Season 01 Episode 05 720p", "Series Title", 0, 1, 5)]
    [InlineData("The Series And the Show - S42 E10713 - 2015-07-20.mp4", "The Series And the Show", 0, 42, 10713)]
    [InlineData("Series.103.hdtv-lol[ettv].mp4", "Series", 0, 1, 3)]
    [InlineData("Series - 01x02 - The Rooster Prince - [itz_theo]", "Series", 0, 1, 2)]
    [InlineData("Series (2009) - [06x16] - Room 147.mp4", "Series", 2009, 6, 16)]
    [InlineData("grp-zoos01e11-1080p", "grp-zoo", 0, 1, 11)]
    [InlineData("grp-zoo-s01e11-1080p", "grp-zoo", 0, 1, 11)]
    [InlineData("Series!.S2016E14.2016-01-20.avi", "Series!", 0, 2016, 14)]
    [InlineData("John.Smith.The.Series.Title.5of9.The.Universe.Of.Development.1990.DVDRip.x264-HANDJOB", "John Smith The Series Title", 0, 1, 5)]
    [InlineData("Judge Developer 2016 02 25 S20E142", "Judge Developer", 0, 20, 142)]
    [InlineData("Judge Developer 2016 02 25 S20E143", "Judge Developer", 0, 20, 143)]
    [InlineData("Red Show - S02 - E06 - Parallel Series", "Red Show", 0, 2, 6)]
    [InlineData("App.Sonarr.Made.in.Canada.Part.Two.720p.HDTV.x264-2HD", "App Sonarr Made in Canada", 0, 1, 2)]
    [InlineData("The.100000.Series.Title.2016.S01E05.720p.HDTV.x264-W4F", "The 100000 Series Title", 2016, 1, 5)]
    [InlineData("Series S01E02 (22 October 2016) HDTV 720p [Webrip]", "Series", 0, 1, 2)]
    [InlineData("this.is.a.show.2015.0308-yestv", "this is a show", 2015, 3, 8)]
    [InlineData("Series - S2016E231", "Series", 0, 2016, 231)]
    [InlineData("Series - 2016x231", "Series", 0, 2016, 231)]
    [InlineData("Short.Series.S26E022.HDTV.x264-FiHTV", "Short Series", 0, 26, 22)]
    [InlineData("Super.Series.S01.Ep06.1080p.BluRay.DTS.x264-MiR", "Super Series", 0, 1, 6)]
    [InlineData("Series 104 - S01E07 The Developers [SDTV]", "Series 104", 0, 1, 7)]
    [InlineData("11-02 The Series Reaction (HD).m4v", "", 0, 11, 2)]
    [InlineData("Plus Series la title - S14E3533 FRENCH WEBRIP H.264 AAC (09.05.2018)", "Plus Series la title", 0, 14, 3533)]
    [InlineData("The Series - S01E02 - Earth Skills HDTV-1080p AVC DTS [EN+FR+ES+PT+DA+FI+NB+SV]", "The Series", 0, 1, 2)]
    [InlineData("Series Title - S01E01 - Day 100 [SDTV]", "Series Title", 0, 1, 1)]
    [InlineData("Series.Title.S01.Ep.01.English.AC3.DL.1080p.BluRay-Sonarr", "Series Title", 0, 1, 1)]
    [InlineData("Series.Title.S01.E.01.English.AC3.DL.1080p.BluRay-Sonarr", "Series Title", 0, 1, 1)]
    [InlineData("Series.Title.S01.Ep01.English.AC3.DL.1080p.BluRay-Sonarr", "Series Title", 0, 1, 1)]
    [InlineData("Series.Title.S01.E01.English.AC3.DL.1080p.BluRay-Sonarr", "Series Title", 0, 1, 1)]
    [InlineData("Series.Title.S01EP01.English.AC3.DL.1080p.BluRay-Sonarr", "Series Title", 0, 1, 1)]
    [InlineData("tvs-amgo-dd51-dl-7p-azhd-x264-103", "tvs-amgo-dd51-dl-7p-azhd", 0, 1, 3)]
    [InlineData("Series Title - S01E01 [AC3 5.1 Castellano][www.descargas2020.org]", "Series Title", 0, 1, 1)]
    [InlineData("Series Title - [02x01] - Episode 1", "Series Title", 0, 2, 1)]
    [InlineData("Series.Title.Of.S01E01.xyz", "Series Title Of", 0, 1, 1)]
    [InlineData("[RlsGrp] Series Title - S01E27 - 24-Hour", "Series Title", 0, 1, 27)]
    [InlineData("Series Title - S02E01 1920x910", "Series Title", 0, 2, 1)]
    [InlineData("Anime Title - S2020E1527 [1527] [2020-10-11] - Episode Title", "Anime Title", 0, 2020, 1527)]
    [InlineData("Anime Title - S2010E994 [0994] [2010-02-28] - Episode Title [x264 720p][AAC 2ch][HS][Shion+GakiDave]", "Anime Title", 0, 2010, 994)]
    [InlineData("Series Title - Temporada 2 [HDTV 720p][Cap.201][AC3 5.1 Castellano][www.pctnew.com]", "Series Title", 0, 2, 1)]
    [InlineData("Series Title - Temporada 2 [HDTV 720p][Cap.1901][AC3 5.1 Castellano][www.pctnew.com]", "Series Title", 0, 19, 1)]
    [InlineData("Series Title 1x1", "Series Title", 0, 1, 1)]
    [InlineData("1x1", "", 0, 1, 1)]
    [InlineData("Series Title [2022] [S25E13] [PL] [720p] [WEB-DL-CZRG] [x264] ", "Series Title", 2022, 25, 13)]
    [InlineData("Series T Se.3 afl.3", "Series T", 0, 3, 3)]
    [InlineData("[Anime Chap] Anime Title! S01E09 [WEB 1080p] {OP & ED Lyrics} - Episode 9 (The Eminence in Shadow)", "Anime Title!", 0, 1, 9)]
    [InlineData("[Anime Chap] Anime Title! S01E12 [WEB 1080p] {OP & ED Lyrics} - Episode 12 (The Eminence in Shadow)", "Anime Title!", 0, 1, 12)]
    [InlineData("SeriesTitle-S16E08-10426008-0.mkv", "SeriesTitle", 0, 16, 8)]
    [InlineData("Series-S07E12-31st_Century_Fox-[Bluray-1080p].mkv", "Series", 0, 7, 12)]
    [InlineData("TheTitle-S12E13-3_Acts_of_God.mkv", "TheTitle", 0, 12, 13)]
    [InlineData("Series Title - Temporada 2 [HDTV 720p][Cap.408]", "Series Title", 0, 4, 8)]
    [InlineData("Series Title [HDTV][Cap.104](website.com).avi", "Series Title", 0, 1, 4)]
    [InlineData("Series Title [HDTV][Cap.402](website.com).avi", "Series Title", 0, 4, 2)]
    [InlineData("Series Title [HDTV 720p][Cap.101](website.com).mkv", "Series Title", 0, 1, 1)]
    [InlineData("Босх: Спадок (S2E1) / Series: Legacy (S2E1) (2023) WEB-DL 1080p Ukr/Eng | sub Eng", "Series: Legacy", 0, 2, 1)]
    [InlineData("Босх: Спадок / Series: Legacy / S2E1 of 10 (2023) WEB-DL 1080p Ukr/Eng | sub Eng", "Series: Legacy", 0, 2, 1)]
    [InlineData("Titles.s06e01.1999.BDRip.1080p.Ukr.Eng.AC3.Hurtom.TNU.Tenax555", "Titles", 0, 6, 1)]
    [InlineData("Titles.s06.01.1999.BDRip.1080p.Ukr.Eng.AC3.Hurtom.TNU.Tenax555", "Titles", 0, 6, 1)]
    [InlineData("[Judas] Series Title (2024) - S01E14", "Series Title", 2024, 1, 14)]
    [InlineData("[ReleaseGroup] SeriesTitle S01E1 Webdl 1080p", "SeriesTitle", 0, 1, 1)]
    [InlineData("[SubsPlus+] Series no Chill - S02E01 (NF WEB 1080p AVC AAC)", "Series no Chill", 0, 2, 1)]
    [InlineData("[SubsPlus+] Series no Chill - S02E01v2 (NF WEB 1080p AVC AAC)", "Series no Chill", 0, 2, 1)]
    [InlineData("Series - Temporada 1 - [HDTV 1080p][Cap.101](wolfmax4k.com)", "Series", 0, 1, 1)]
    [InlineData("Series [HDTV 1080p][Cap.101](wolfmax4k.com)", "Series", 0, 1, 1)]
    [InlineData("Series [HDTV 1080p][Cap. 101](wolfmax4k.com).mkv", "Series", 0, 1, 1)]
    [InlineData("Amazing Title (2024/S01E07/DSNP/WEB-DL/1080p/ESP/EAC3 5.1/ING/EAC3 5.1 Atmos/SUBS) SPWEB", "Amazing Title", 2024, 1, 7)]
    [InlineData("Mini Title (Miniserie) (2024/S01E07/DSNP/WEB-DL/1080p/ESP/EAC3 5.1/ING/EAC3 5.1 Atmos/SUBS) SPWEB", "Mini Title", 2024, 1, 7)]
    [InlineData("Series.S006E18.Some.Title.Name-Part.1.1080p.WEB-DL.AAC2.0.H.264-Release", "Series", 0, 6, 18)]
    [InlineData("Series.2006.S006E18.Some.Title.Name-Part.1.1080p.WEB-DL.AAC2.0.H.264-Release", "Series", 2006, 6, 18)]
    [InlineData("我的人间烟火.Fireworks.Series.S01E01.2023.V2.1080p.WEB-DL.H264.AAC-SeeWEB", "Fireworks Series", 0, 1, 1)]
    [InlineData("Fireworks.Series.S01E01.2023.V2.1080p.WEB-DL.H264.AAC-SeeWEB", "Fireworks Series", 0, 1, 1)]
    [InlineData("S3E3 - Seventeen Seconds [1080p]", "", 0, 3, 3)]
    [InlineData("S3E3 - Part 3 Seventeen Seconds [1080p]", "", 0, 3, 3)]
    [InlineData("S03E03 - Part 3 Seventeen Seconds [1080p]", "", 0, 3, 3)]
    [InlineData("[VARYG] Series at Age 29 S01E03 Rirui and Anyango 1080p CR WEB-DL AAC2.0 H.264 (29-sai Dokushin Chuuken Boukensha no Nichijou, Multi-Subs)", "Series at Age 29", 0, 1, 3)]
    [InlineData("Series S093E081 1080p ALL4 WEB-DL AAC2 0 H 264-JJUNAHJAMESON", "Series", 0, 93, 81)]
    [InlineData("Series S87E136 1080p WEB-DL x264-NGP", "Series", 0, 87, 136)]
    [InlineData("Series S092E001 1080p ALL4 WEB-DL AAC2 0 H 264-JJunahJameson", "Series", 0, 92, 1)]
    [InlineData("Series s90 e43 1080p HDTV AAC H264", "Series", 0, 90, 43)]
    [InlineData("Series - S3E10 - Episode #3.10 - rus 720p WEBDL (LostFilm)", "Series", 0, 3, 10)]
    [InlineData("Series - S03E10 - Episode #3.10 - rus 720p WEBDL (LostFilm)", "Series", 0, 3, 10)]
    public void Parses_single_episode(string release, string title, int year, int season, int episode)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [episode], parsed?.EpisodeNumbers)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Verify();
    }

    [Theory]
    [InlineData("221208 ABC123 Series Title Season 39 ep11.mp4", "ABC123 Series Title", 0, 39, 11)]
    [InlineData("221208 ABC123 Series Title ep34[1080p60 H264].mp4", "ABC123 Series Title", 0, 1, 34)]
    [InlineData("221205 ABC123 17研究所！ #17.ts", "ABC123 17研究所！", 0, 1, 17)]
    [InlineData("221201 Series Title! ABC123 ep219[720p.h264].mp4", "Series Title! ABC123", 0, 1, 219)]
    [InlineData("221206 Series Title! ep08(Tanaka Miku).ts", "Series Title!", 0, 1, 8)]
    [InlineData("210810 ABC123 Series Title ep05.mp4", "ABC123 Series Title", 0, 1, 5)]
    [InlineData("221204 乃木坂工事中 ep389.mp4", "乃木坂工事中", 0, 1, 389)]
    public void Parses_japanese_variety_show(string release, string title, int year, int season, int episode)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [episode], parsed?.EpisodeNumbers)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Verify();
    }

    [Theory]
    [InlineData("Series Title S01E11.5 [SP]-The Poppies Bloom Red on the Battlefield", "Series Title", 0, 1, 11)]
    public void Parses_decimal_episode_as_special(string release, string title, int year, int season, int episode)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [episode], parsed?.EpisodeNumbers)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Equal("IsSpecial", true, parsed?.IsSpecial)
            .Verify();
    }

    [Theory]
    [InlineData("Series.Title.S06E01b.Fade.Out.Fade.in.Part.2.1080p.DSNP.WEB-DL.AAC2.0.H.264-FLUX", "Series Title", 0, 6, 1)]
    public void Parses_split_episode(string release, string title, int year, int season, int episode)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [episode], parsed?.EpisodeNumbers)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Verify();
    }

    [Theory]
    [InlineData("Series.S03E01-06.DUAL.BDRip.XviD.AC3.-HELLYWOOD", "Series", 0, 3, new[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData("Series.Title.103.104.720p.HDTV.X264-DIMENSION", "Series Title", 0, 1, new[] { 3, 4 })]
    [InlineData("Series.S03E01.S03E02.720p.HDTV.X264-DIMENSION", "Series", 0, 3, new[] { 1, 2 })]
    [InlineData("The Series S01e01 e02 ShoHD On Demand 1080i DD5 1 ALANiS", "The Series", 0, 1, new[] { 1, 2 })]
    [InlineData("Series.Title.2x04.2x05.720p.BluRay-FUTV", "Series Title", 0, 2, new[] { 4, 5 })]
    [InlineData("Series.Title.S07E22E23.720p.HDTV.X264-DIMENSION", "Series Title", 0, 7, new[] { 22, 23 })]
    [InlineData("Series Title - S07E22 - S07E23 - And Lots of Security.. [HDTV-720p].mkv", "Series Title", 0, 7, new[] { 22, 23 })]
    [InlineData("S03E01.S03E02.720p.HDTV.X264-DIMENSION", "", 0, 3, new[] { 1, 2 })]
    [InlineData("2x04x05.720p.BluRay-FUTV", "", 0, 2, new[] { 4, 5 })]
    [InlineData("S02E04E05.720p.BluRay-FUTV", "", 0, 2, new[] { 4, 5 })]
    [InlineData("S02E03-04-05.720p.BluRay-FUTV", "", 0, 2, new[] { 3, 4, 5 })]
    [InlineData("Series.Kings.S02E09-E10.HDTV.x264-ASAP", "Series Kings", 0, 2, new[] { 9, 10 })]
    [InlineData("Series Kings - 2x9-2x10 - Served Code [SDTV] ", "Series Kings", 0, 2, new[] { 9, 10 })]
    [InlineData("Series Kings - 2x09-2x10 - Served Code [SDTV] ", "Series Kings", 0, 2, new[] { 9, 10 })]
    [InlineData("Hell on Series S02E09 E10 HDTV x264 EVOLVE", "Hell on Series", 0, 2, new[] { 9, 10 })]
    [InlineData("Hell.on.Series.S02E09-E10.720p.HDTV.x264-EVOLVE", "Hell on Series", 0, 2, new[] { 9, 10 })]
    [InlineData("Series's Sonarr - 8x01_02 - Free Falling", "Series's Sonarr", 0, 8, new[] { 1, 2 })]
    [InlineData("8x01_02 - Free Falling", "", 0, 8, new[] { 1, 2 })]
    [InlineData("Series.S01E91-E100", "Series", 0, 1, new[] { 91, 92, 93, 94, 95, 96, 97, 98, 99, 100 })]
    [InlineData("Series.S29E161-E165.PDTV.x264-FQM", "Series", 0, 29, new[] { 161, 162, 163, 164, 165 })]
    [InlineData("Shortland.Series.S22E5363-E5366.HDTV.x264-FiHTV", "Shortland Series", 0, 22, new[] { 5363, 5364, 5365, 5366 })]
    [InlineData("the.Series.101.102.hdtv-lol", "the Series", 0, 1, new[] { 1, 2 })]
    [InlineData("Series.10708.hdtv-lol.mp4", "Series", 0, 1, new[] { 7, 8 })]
    [InlineData("Series.10910.hdtv-lol.mp4", "Series", 0, 1, new[] { 9, 10 })]
    [InlineData("E.010910.HDTVx264REPACKLOL.mp4", "E", 0, 1, new[] { 9, 10 })]
    [InlineData("World Series of Sonarr - 2010x15 - 2010x16 - HD TV.mkv", "World Series of Sonarr", 0, 2010, new[] { 15, 16 })]
    [InlineData("The Series US S01E01-E02 720p HDTV x264", "The Series US", 0, 1, new[] { 1, 2 })]
    [InlineData("Series Title Season 01 Episode 05-06 720p", "Series Title", 0, 1, new[] { 5, 6 })]
    [InlineData("The Series And The Code - S42 Ep10718 - Ep10722", "The Series And The Code", 0, 42, new[] { 10718, 10719, 10720, 10721, 10722 })]
    [InlineData("The Series And The Code - S42 Ep10688 - Ep10692", "The Series And The Code", 0, 42, new[] { 10688, 10689, 10690, 10691, 10692 })]
    [InlineData("Series.S01E02E03.1080p.BluRay.x264-DeBTViD", "Series", 0, 1, new[] { 2, 3 })]
    [InlineData("grp-zoos01e11e12-1080p", "grp-zoo", 0, 1, new[] { 11, 12 })]
    [InlineData("grp-zoo-s01e11e12-1080p", "grp-zoo", 0, 1, new[] { 11, 12 })]
    [InlineData("Series Title.S6.E1.E2.Episode Name.1080p.WEB-DL", "Series Title", 0, 6, new[] { 1, 2 })]
    [InlineData("Series Title.S6E1-E2.Episode Name.1080p.WEB-DL", "Series Title", 0, 6, new[] { 1, 2 })]
    [InlineData("Series Title.S6E1-S6E2.Episode Name.1080p.WEB-DL", "Series Title", 0, 6, new[] { 1, 2 })]
    [InlineData("Series Title.S6E1E2.Episode Name.1080p.WEB-DL", "Series Title", 0, 6, new[] { 1, 2 })]
    [InlineData("Series Title.S6E1-E2-E3.Episode Name.1080p.WEB-DL", "Series Title", 0, 6, new[] { 1, 2, 3 })]
    [InlineData("Series Title.S6.E1E3.Episode Name.1080p.WEB-DL", "Series Title", 0, 6, new[] { 1, 2, 3 })]
    [InlineData("Series Title.S6.E1-E2.Episode Name.1080p.WEB-DL", "Series Title", 0, 6, new[] { 1, 2 })]
    [InlineData("Series Title.S6.E1-S6E2.Episode Name.1080p.WEB-DL", "Series Title", 0, 6, new[] { 1, 2 })]
    [InlineData("Series Title.S6.E1E2.Episode Name.1080p.WEB-DL", "Series Title", 0, 6, new[] { 1, 2 })]
    [InlineData("Series Title.S6.E1-E2-E3.Episode Name.1080p.WEB-DL", "Series Title", 0, 6, new[] { 1, 2, 3 })]
    [InlineData("Series.Title.S05E01-E02.720p.5.1Ch.BluRay", "Series Title", 0, 5, new[] { 1, 2 })]
    [InlineData("Series.Title.S05E01-02.720p.5.1Ch.BluRay", "Series Title", 0, 5, new[] { 1, 2 })]
    [InlineData("S01E01-E03 - Episode Title.HDTV-720p", "", 0, 1, new[] { 1, 2, 3 })]
    [InlineData("1x01-x03 - Episode Title.HDTV-720p", "", 0, 1, new[] { 1, 2, 3 })]
    [InlineData("Series.Title.E07-E08.180612.1080p-NEXT", "Series Title", 0, 1, new[] { 7, 8 })]
    [InlineData("Series Title? E11-E12 1080p HDTV AAC H.264-NEXT", "Series Title", 0, 1, new[] { 11, 12 })]
    [InlineData("The Series Title (2010) - [S01E01-02-03] - Episode Title", "The Series Title", 2010, 1, new[] { 1, 2, 3 })]
    [InlineData("[AqusiQ-TorrentS.pl]The.Name.of.the.Series.S01E05-06.PL.2160p-Ralf[shogho]", "The Name of the Series", 0, 1, new[] { 5, 6 })]
    [InlineData("[AgusiQ-TorrentS.pl] The.Name.of.the.Series.S01E05-E06.PL.1080i.Ralf [jans12]", "The Name of the Series", 0, 1, new[] { 5, 6 })]
    [InlineData("The.Name.of.the.Series.S01E05-6.PL.1080p.WEBRip.x264-666", "The Name of the Series", 0, 1, new[] { 5, 6 })]
    [InlineData("Series Title - S15E06-07 - City Sushi HDTV-720p", "Series Title", 0, 15, new[] { 6, 7 })]
    [InlineData("Series Title - S01E01-02-03 - Episode Title HDTV-720p", "Series Title", 0, 1, new[] { 1, 2, 3 })]
    [InlineData("Series Title - [02x01x02] - Episode 1", "Series Title", 0, 2, new[] { 1, 2 })]
    [InlineData("Series Title - [02x01-x02] - Episode 1", "Series Title", 0, 2, new[] { 1, 2 })]
    [InlineData("Series Title - [02x01-02] - Episode 1", "Series Title", 0, 2, new[] { 1, 2 })]
    [InlineData("Series Title (2011) - S01E23-E24 - ...E i nuovi orizzonti [HDTV 360p] [ITA].mkv", "Series Title", 2011, 1, new[] { 23, 24 })]
    [InlineData("The Series Title! - S01E01-02-03", "The Series Title!", 0, 1, new[] { 1, 2, 3 })]
    [InlineData("Series Title! (2013) - S04E44-E45 - Il 200 spettacolare episodio da narcisisti!", "Series Title!", 2013, 4, new[] { 44, 45 })]
    [InlineData("Series Title! (2013) - S04E44-E45 - Il 200 spettacolare episodio da narcisisti! [NetflixHD 720p HEVC] [ITA+ENG].mkv", "Series Title!", 2013, 4, new[] { 44, 45 })]
    [InlineData("13 Series Se.1 afl.2-3-4 [VTM]", "13 Series", 0, 1, new[] { 2, 3, 4 })]
    [InlineData("Series T Se.3 afl.3 en 4", "Series T", 0, 3, new[] { 3, 4 })]
    [InlineData("Series Title (S15E06-08) City Sushi", "Series Title", 0, 15, new[] { 6, 7, 8 })]
    [InlineData("Series Title (S05E06-08 of 24) City Sushi", "Series Title", 0, 5, new[] { 6, 7, 8 })]
    [InlineData("Series Title (S05E06-08_of_24) City Sushi", "Series Title", 0, 5, new[] { 6, 7, 8 })]
    [InlineData("Series Title (S5E6-8.of.24) City Sushi", "Series Title", 0, 5, new[] { 6, 7, 8 })]
    [InlineData("Series Title (S5E6-8.of.XX) City Sushi", "Series Title", 0, 5, new[] { 6, 7, 8 })]
    [InlineData("Босх: Спадок (S2E1-4) / Series: Legacy (S2E1-4) (2023) WEB-DL 1080p Ukr/Eng | sub Eng", "Series: Legacy", 0, 2, new[] { 1, 2, 3, 4 })]
    [InlineData("Босх: Спадок / Series: Legacy / S2E1-4 of 10 (2023) WEB-DL 1080p Ukr/Eng | sub Eng", "Series: Legacy", 0, 2, new[] { 1, 2, 3, 4 })]
    [InlineData("Series Title - S26E96-97-98-99-100 - Episode 5931 + Episode 5932 + Episode 5933 + Episode 5934 + Episode 5935", "Series Title", 0, 26, new[] { 96, 97, 98, 99, 100 })]
    [InlineData("Series falls - Temporada 1 [HDTV][Cap.111_120]", "Series falls", 0, 1, new[] { 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 })]
    [InlineData("Series.S101E01-E02.Some.Title.Name.1080p.WEB-DL.AAC2.0.H.264-Release", "Series", 0, 101, new[] { 1, 2 })]
    [InlineData("Series.2006.S101E01-E02.Some.Title.Name.1080p.WEB-DL.AAC2.0.H.264-Release", "Series", 2006, 101, new[] { 1, 2 })]
    [InlineData("[AL] Series.Title.S04E17-28.Part.2.German.DL.DTS.1080p.BluRay.x264-Pudding.GerSub-sama", "Series Title", 0, 4, new[] { 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28 })]
    [InlineData("[AL] Series.Title.S04E01-16.German.DL.DTS.1080p.BluRay.x264-Pudding.GerSub-sama", "Series Title", 0, 4, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 })]
    public void Parses_multi_episode(string release, string title, int year, int season, int[] episodes)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", episodes, parsed?.EpisodeNumbers)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Verify();
    }

    [Theory]
    [InlineData("30.Series.Season.04.HDTV.XviD-DIMENSION", "30 Series", 0, 4)]
    [InlineData("Sonarr.and.Series.S02.720p.x264-DIMENSION", "Sonarr and Series", 0, 2)]
    [InlineData("The.Series.US.S03.720p.x264-DIMENSION", "The Series US", 0, 3)]
    [InlineData("Series.of.Sonarr.S03.720p.BluRay-CLUE\\REWARD", "Series of Sonarr", 0, 3)]
    [InlineData("Series Time S02 720p HDTV x264 CRON", "Series Time", 0, 2)]
    [InlineData("Series.2021.S04.iNTERNAL.DVDRip.XviD-VCDVaULT", "Series", 2021, 4)]
    [InlineData("Series Five 0 S01 720p WEB DL DD5 1 H 264 NT", "Series Five 0", 0, 1)]
    [InlineData("30 Series S03 WS PDTV XviD FUtV", "30 Series", 0, 3)]
    [InlineData("The Series Season 4 WS PDTV XviD FUtV", "The Series", 0, 4)]
    [InlineData("Series Season 1 720p WEB DL DD 5 1 h264 TjHD", "Series", 0, 1)]
    [InlineData("The Series Season4 WS PDTV XviD FUtV", "The Series", 0, 4)]
    [InlineData("Series S 01 720p WEB DL DD 5 1 h264 TjHD", "Series", 0, 1)]
    [InlineData("Series Confidential   Season 3", "Series Confidential", 0, 3)]
    [InlineData("Series.S01.720p.WEBDL.DD5.1.H.264-NTb", "Series", 0, 1)]
    [InlineData("Series.Makes.It.Right.S02.720p.HDTV.AAC5.1.x265-NOGRP", "Series Makes It Right", 0, 2)]
    [InlineData("My.Series.S2014.720p.HDTV.x264-ME", "My Series", 0, 2014)]
    [InlineData("Series.Saison3.VOSTFR.HDTV.XviD-NOTAG", "Series", 0, 3)]
    [InlineData("Series.SAISON.1.VFQ.PDTV.H264-ACC-ROLLED", "Series", 0, 1)]
    [InlineData("Series Title - Series 1 (1970) DivX", "Series Title", 0, 1)]
    [InlineData("SeriesTitle.S03.540p.AMZN.WEB-DL.DD+2.0.x264-RTN", "SeriesTitle", 0, 3)]
    [InlineData("Series.Title.S01.576p.BluRay.DD5.1.x264-HiSD", "Series Title", 0, 1)]
    [InlineData("Series.Stagione.3.HDTV.XviD-NOTAG", "Series", 0, 3)]
    [InlineData("Series No More S01 2023 1080p WEB-DL AVC AC3 2.0 Dual Audio -ZR-", "Series No More", 0, 1)]
    [InlineData("Series Title / S1E1-8 of 8 [2024, WEB-DL 1080p] + Original + RUS", "Series Title", 0, 1)]
    [InlineData("Series Title / S2E1-16 of 16 [2022, WEB-DL] RUS", "Series Title", 0, 2)]
    [InlineData("[hchcsen] Mobile Series 00 S01 [BD Remux Dual Audio 1080p AVC 2xFLAC] (Kidou Senshi Gundam 00 Season 1)", "Mobile Series 00", 0, 1)]
    [InlineData("[HorribleRips] Mobile Series 00 S1 [1080p]", "Mobile Series 00", 0, 1)]
    [InlineData("[Zoombie] Series 100: Bucket List S01 [Web][MKV][h265 10-bit][1080p][AC3 2.0][Softsubs (Zoombie)]", "Series 100: Bucket List", 0, 1)]
    [InlineData("[GROUP] Series: Title (2023) (Season 1) [BDRip] [1080p Dual Audio HEVC 10 bits DDP] (serie) (Batch)", "Series: Title", 2023, 1)]
    [InlineData("[GROUP] Series: Title (2023) (Season 1) [BDRip] [1080p Dual Audio HEVC 10-bits DDP] (serie) (Batch)", "Series: Title", 2023, 1)]
    [InlineData("[GROUP] Series: Title (2023) (Season 1) [BDRip] [1080p Dual Audio HEVC 10-bit DDP] (serie) (Batch)", "Series: Title", 2023, 1)]
    [InlineData("Seriesless (2016/S01/WEB-DL/1080p/AC3 5.1/DUAL/SUB)", "Seriesless", 2016, 1)]
    [InlineData("Series (1994) - Temporada 10", "Series", 1994, 10)]
    [InlineData("Series S01 1080p HMAX WEB-DL DD+ 5.1 H.265", "Series", 0, 1)]
    [InlineData("Series S01 1440p HMAX WEB-DL DD+ 5.1 H.265", "Series", 0, 1)]
    [InlineData("[Judas] Series E (Series J) (Season 04) [1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs] (Batch)", "Series E (Series J)", 0, 4)]
    [InlineData("1234 [Sezon 3] [1080p] [NF] [WEB-DL] [H264] [DDP5.1.Atmos-K83] [Serial Polski]", "1234", 0, 3)]
    [InlineData("1234 [Season 3] [1080p] [NF] [WEB-DL] [H264] [DDP5.1.Atmos-K83] [Serial Polski]", "1234", 0, 3)]
    [InlineData("1234 (2026) (S03) [MULTi] [1080p] [NF] [WEB-DL] [H.264] [DD5.1-RX] [Serial polski]", "1234", 2026, 3)]
    [InlineData("Acropolis Series S05 EXTRAS DVDRip XviD RUNNER", "Acropolis Series", 0, 5)]
    [InlineData("Punky Series S01 EXTRAS DVDRip XviD RUNNER", "Punky Series", 0, 1)]
    [InlineData("Instant Series S03 EXTRAS DVDRip XviD OSiTV", "Instant Series", 0, 3)]
    [InlineData("The.Series.S03.Extras.01.Deleted.Scenes.720p", "The Series", 0, 3)]
    [InlineData("The.Series.S03.Extras.02.720p", "The Series", 0, 3)]
    [InlineData("Series.to.Me.S03.SUBPACK.DVDRip.XviD-REWARD", "Series to Me", 0, 3)]
    [InlineData("The.Series.S02.SUBPACK.DVDRip.XviD-REWARD", "The Series", 0, 2)]
    [InlineData("Series.S11.SUBPACK.DVDRip.XviD-REWARD", "Series", 0, 11)]
    public void Parses_full_season(string release, string title, int year, int season)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [], parsed?.EpisodeNumbers)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Equal("FullSeason", true, parsed?.FullSeason)
            .Equal("MultiSeason", false, parsed?.MultiSeason)
            .Verify();
    }

    [Theory]
    [InlineData("The.Series.2016.S02.Part.1.1080p.NF.WEBRip.DD5.1.x264-NTb", "The Series", 2016, 2)]
    [InlineData("The.Series.S07.Vol.1.1080p.NF.WEBRip.DD5.1.x264-NTb", "The Series", 0, 7)]
    [InlineData("The.Series.S06.P1.1080p.Blu-Ray.10-Bit.Dual-Audio.TrueHD.x265-iAHD", "The Series", 0, 6)]
    [InlineData("[Group] Series Title S03 - Vol.02 [BD 1080p FLAC]", "Series Title", 0, 3)]
    [InlineData("[Group] Series Title S03 Vol.02 [BD 1080p FLAC]", "Series Title", 0, 3)]
    [InlineData("[Group] Series Title S03 Part 2 [1080p]", "Series Title", 0, 3)]
    public void Parses_partial_season_as_neither_full_season_nor_episode(string release, string title, int year, int season)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [], parsed?.EpisodeNumbers)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Verify();
    }

    [Theory]
    [InlineData("The Series S01-05 WS BDRip X264-REWARD-No Rars", "The Series", 0, 1)]
    [InlineData("Series.Title.S01-S09.1080p.AMZN.WEB-DL.DDP2.0.H.264-NTb", "Series Title", 0, 1)]
    [InlineData("Series Title S01 - S07 BluRay 1080p x264 REPACK -SacReD", "Series Title", 0, 1)]
    [InlineData("Series Title Season 01-07 BluRay 1080p x264 REPACK -SacReD", "Series Title", 0, 1)]
    [InlineData("Series Title Season 01 - Season 07 BluRay 1080p x264 REPACK -SacReD", "Series Title", 0, 1)]
    [InlineData("Series Title Complete Series S01 S04 (1080p BluRay x265 HEVC 10bit AAC 5.1 Vyndros)", "Series Title", 0, 1)]
    [InlineData("Series Title S01 S04 (1080p BluRay x265 HEVC 10bit AAC 5.1 Vyndros)", "Series Title", 0, 1)]
    [InlineData("Series Title S01 04 (1080p BluRay x265 HEVC 10bit AAC 5.1 Vyndros)", "Series Title", 0, 1)]
    [InlineData("Series Title S01 S02 S03 S04", "Series Title", 0, 1)]
    [InlineData("Series Title S01 S03 S04", "Series Title", 0, 1)]
    [InlineData("Series Title S01 S04", "Series Title", 0, 1)]
    [InlineData("[Group] Series Title S01-04 (BD 1080p) [Dual Audio]", "Series Title", 0, 1)]
    [InlineData("[Group] Series Title S01-S04 (BD 1080p)", "Series Title", 0, 1)]
    public void Parses_multi_season_pack(string release, string title, int year, int firstSeason)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", firstSeason, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [], parsed?.EpisodeNumbers)
            .Equal("FullSeason", true, parsed?.FullSeason)
            .Equal("MultiSeason", true, parsed?.MultiSeason)
            .Verify();
    }

    [Theory]
    [InlineData("30.Series.Season.04.HDTV.XviD-DIMENSION", 4)]
    [InlineData("Sonarr.and.Series.S02.720p.x264-DIMENSION", 2)]
    [InlineData("[Group] Series Title S02 [1080p]", 2)]
    [InlineData("[Group] Series Title 2 S01 [BD 1080p]", 1)]
    public void Single_season_is_not_multi_season(string release, int season)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Equal("MultiSeason", false, parsed?.MultiSeason)
            .Verify();
    }

    [Theory]
    [InlineData("Series Title 2011 04 18 Emma Roberts HDTV XviD BFF", "Series Title", 0, "2011-04-18")]
    [InlineData("A Late Talk Show 2011 04 15 1080i HDTV DD5 1 MPEG2 TrollHD", "A Late Talk Show", 0, "2011-04-15")]
    [InlineData("A.Late.Talk.Show.2010.10.11.Johnny.Knoxville.iTouch-MW", "A Late Talk Show", 0, "2010-10-11")]
    [InlineData("A Late Talk Show - 2011-04-12 - Gov. Deval Patrick", "A Late Talk Show", 0, "2011-04-12")]
    [InlineData("2011.01.10 - A Late Talk Show- HD TV.mkv", "", 0, "2011-01-10")]
    [InlineData("2011.03.13 - A Late Talk Show - HD TV.mkv", "", 0, "2011-03-13")]
    [InlineData("A Late Talk Show - 2011-06-16 - Larry David, \"Bachelorette\" Ashley Hebert, Pitbull with Ne-Yo", "A Late Talk Show", 0, "2011-06-16")]
    [InlineData("2020.A.Late.Talk.Show.2012.16.02.PDTV.XviD-C4TV", "2020 A Late Talk Show", 0, "2012-02-16")]
    [InlineData("2020.A.Late.Talk.Show.2012.13.02.PDTV.XviD-C4TV", "2020 A Late Talk Show", 0, "2012-02-13")]
    [InlineData("2020.A.Late.Talk.Show.2011.12.02.PDTV.XviD-C4TV", "2020 A Late Talk Show", 0, "2011-12-02")]
    [InlineData("Series Title - 2013-10-30 - Episode Title (1) [HDTV-720p]", "Series Title", 0, "2013-10-30")]
    [InlineData("The_Series_US_04.28.2014_hdtv.x264.Poke.mp4", "The Series US", 0, "2014-04-28")]
    [InlineData("A.Late.Talk.Show.140722.720p.HDTV.x264-YesTV", "A Late Talk Show", 0, "2014-07-22")]
    [InlineData("A_Late_Talk_Show_140722_720p_HDTV_x264-YesTV", "A Late Talk Show", 0, "2014-07-22")]
    [InlineData("The Show Series 2015 02 09 WEBRIP s01e13", "The Show Series", 0, "2015-02-09")]
    [InlineData("Tree_Series_2018_06_22_Seth_Meyers_720p_HEVC_x265-MeGusta", "Tree Series", 0, "2018-06-22")]
    [InlineData("20161024- Exotic Payback.21x41_720.mkv", "", 0, "2016-10-24")]
    [InlineData("2018-11-14.1080.all.mp4", "", 0, "2018-11-14")]
    [InlineData("2019_08_20_1080_all.mp4", "", 0, "2019-08-20")]
    [InlineData("Series and Title 20201013 Ep7432 [720p WebRip (x264)] [SUBS]", "Series and Title", 0, "2020-10-13")]
    [InlineData("Series Title (1955) - 1954-01-23 05 00 00 - Cottage for Sale.ts", "Series Title", 1955, "1954-01-23")]
    [InlineData("Series Title - 30-04-2024 HDTV 1080p H264 AAC", "Series Title", 0, "2024-04-30")]
    [InlineData("Series On TitleClub E76 2024 08 08 1080p WEB H264-RnB96 [TJET]", "Series On TitleClub", 0, "2024-08-08")]
    [InlineData("Series.Title.13.02.2025.1080i.HDTV.MPA2.0.H.264", "Series Title", 0, "2025-02-13")]
    [InlineData("Series.2025.09.01.The.170.Million.Pound.Diamond.Scam.1080p.HDTV.H264-DEADPOOL'", "Series", 0, "2025-09-01")]
    [InlineData("Series.2025.09.01.The.Million.Pound.Diamond.Scam.1080p.HDTV.H264-DEADPOOL'", "Series", 0, "2025-09-01")]
    [InlineData("Series Title - 2026-07-03 - Episode Title 2026 [WEBDL-1080p]-RlsGrp", "Series Title", 0, "2026-07-03")]
    [InlineData("Series Title - 2026-07-03 - Friday July 3 2026 [AMZN][WEBDL-1080p][EAC3 2.0][h264]-RlsGrp", "Series Title", 0, "2026-07-03")]
    [InlineData("Series Title - 2026-07-03 - Recap 2025 Part 2 [WEBDL-1080p]-RlsGrp", "Series Title", 0, "2026-07-03")]
    [InlineData("Series Title - 2026-07-03 - Look Back At 2025 Part.1", "Series Title", 0, "2026-07-03")]
    [InlineData("Series.Title.2015.09.07.Part1.720p.HULU.WEBRip.AAC2.0.H.264-Sonarr", "Series Title", 0, "2015-09-07")]
    [InlineData("Series.Title.2015.09.07.Part2.720p.HULU.WEBRip.AAC2.0.H.264-Sonarr", "Series Title", 0, "2015-09-07")]
    [InlineData("Series.Title.2015.09.07.Part.1.720p.HULU.WEBRip.AAC2.0.H.264-Sonarr", "Series Title", 0, "2015-09-07")]
    [InlineData("Series.Title.2015.09.07.Part.2.720p.HULU.WEBRip.AAC2.0.H.264-Sonarr", "Series Title", 0, "2015-09-07")]
    [InlineData("Series 5th Mar 2025 1080 (Deep61)", "Series", 0, "2025-03-05")]
    [InlineData("Series 31st Jan 2025 1080 (Deep61)", "Series", 0, "2025-01-31")]
    [InlineData("Series 23rd Feb 2024 (Deep61)", "Series", 0, "2024-02-23")]
    public void Parses_daily_episode(string release, string title, int year, string airDate)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("AirDate", airDate, parsed?.AirDate)
            .Sequence("EpisodeNumbers", [], parsed?.EpisodeNumbers)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Equal("IsEpisode", true, parsed?.IsEpisode)
            .Verify();
    }

    [Theory]
    [InlineData("Tmc - Quotidien - 05-06-2024 HDTV 1080p H264 AAC")]
    [InlineData("Series.Title.12.02.2025.1080i.HDTV.MPA2.0.H.264")]
    public void Rejects_ambiguous_daily_dates(string release)
    {
        ReleaseExpectation.ExpectNull(release, ReleaseParser.ParseEpisode(release));
    }

    [Theory]
    [InlineData("A Late Talk Show {year} {month} {day} Emma Roberts HDTV XviD BFF")]
    [InlineData("A Late Talk Show {year} {month} {day} 1080i HDTV DD5 1 MPEG2 TrollHD")]
    [InlineData("A.Late.Talk.Show.{year}.{month}.{day}.Johnny.Knoxville.iTouch-MW")]
    [InlineData("A Late Talk Show - {year}-{month}-{day} - Gov. Deval Patrick")]
    [InlineData("{year}.{month}.{day} - A Late Talk Show  - HD TV.mkv")]
    [InlineData("A Late Talk Show - {year}-{month}-{day} - Larry David, \"Bachelorette\" Ashley Hebert, Pitbull with Ne-Yo")]
    [InlineData("2020.NZ.{year}.{month}.{day}.PDTV.XviD-C4TV")]
    public void Rejects_ancient_daily_dates(string template)
    {
        var release = template.Replace("{year}", "1950").Replace("{month}", "10").Replace("{day}", "14");
        Assert.Null(ReleaseParser.ParseEpisode(release)?.AirDate);
    }

    [Theory]
    [InlineData("A Late Talk Show {year} {month} {day} Emma Roberts HDTV XviD BFF")]
    [InlineData("A Late Talk Show {year} {month} {day} 1080i HDTV DD5 1 MPEG2 TrollHD")]
    [InlineData("A.Late.Talk.Show.{year}.{month}.{day}.Johnny.Knoxville.iTouch-MW")]
    [InlineData("A Late Talk Show  - {year}-{month}-{day} - Gov. Deval Patrick")]
    [InlineData("{year}.{month}.{day} - A Late Talk Show  - HD TV.mkv")]
    [InlineData("A Late Talk Show - {year}-{month}-{day} - Larry David, \"Bachelorette\" Ashley Hebert, Pitbull with Ne-Yo")]
    [InlineData("2020.NZ.{year}.{month}.{day}.PDTV.XviD-C4TV")]
    public void Accepts_daily_dates_within_the_future_limit(string template)
    {
        var date = DateTime.Today.AddDays(2);
        var release = template.Replace("{year}", date.ToString("yyyy")).Replace("{month}", date.ToString("MM")).Replace("{day}", date.ToString("dd"));
        Assert.Equal(date.ToString("yyyy-MM-dd"), ReleaseParser.ParseEpisode(release)?.AirDate);
    }

    [Theory]
    [InlineData(-7)]
    [InlineData(7)]
    [InlineData(14)]
    public void Parses_episodes_released_close_to_their_air_date(int days)
    {
        var date = DateTime.Today.AddDays(days);
        var release = $"A.Talk.Show.{date:yyyy.MM.dd}.1080p.WEB-DL.H264-Sonarr";
        Assert.Equal(date.ToString("yyyy-MM-dd"), ReleaseParser.ParseEpisode(release)?.AirDate);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(366)]
    public void Rejects_dates_beyond_the_future_limit(int days)
    {
        var release = $"A.Talk.Show.{DateTime.Today.AddDays(days):yyyy.MM.dd}.1080p.WEB-DL.H264-Sonarr";
        Assert.Null(ReleaseParser.ParseEpisode(release));
    }

    [Theory]
    [InlineData("[SubDESU]_Show_One_07_(1280x720_x264-AAC)_[6B7FD717]", "Show One", 0, 7, null, 0)]
    [InlineData("[Chihiro]_Show!!_-_06_[848x480_H.264_AAC][859EEAFA]", "Show!!", 0, 6, null, 0)]
    [InlineData("[Commie]_Some_Anime_Show_-_11_[65F220B4]", "Some Anime Show", 0, 11, null, 0)]
    [InlineData("[Underwater]_Some_Anime_Show_-_12_(720p)_[5C7BC4F9]", "Some Anime Show", 0, 12, null, 0)]
    [InlineData("[Commie]_Some_Anime_Show_-_15_[E76552EA]", "Some Anime Show", 0, 15, null, 0)]
    [InlineData("[HorribleSubs]_Some_Anime_Show_-_33_[720p]", "Some Anime Show", 0, 33, null, 0)]
    [InlineData("[HorribleSubs]_Some_Anime_Show_-_145_[720p]", "Some Anime Show", 0, 145, null, 0)]
    [InlineData("[HorribleSubs] Some Anime Show - 13 [1080p].mkv", "Some Anime Show", 0, 13, null, 0)]
    [InlineData("[Doremi].Some.Anime.Show.8.Go!.31.[1280x720].[C65D4B1F].mkv", "Some Anime Show 8 Go!", 0, 31, null, 0)]
    [InlineData("[K-F] Some Anime Show 214", "Some Anime Show", 0, 214, null, 0)]
    [InlineData("[K-F] Some Anime Show S10E14 214", "Some Anime Show", 0, 214, 10, 14)]
    [InlineData("[K-F] Some Anime Show 10x14 214", "Some Anime Show", 0, 214, 10, 14)]
    [InlineData("[K-F] Some Anime Show 214 10x14", "Some Anime Show", 0, 214, 10, 14)]
    [InlineData("Some Anime Show - 031 - The Resolution to Kill [Lunar].avi", "Some Anime Show", 0, 31, null, 0)]
    [InlineData("Some Anime Show - 031 - The Resolution to Kill [Lunar]", "Some Anime Show", 0, 31, null, 0)]
    [InlineData("[ACX]Some Anime Show 01 Role Play [Kosaka] [9C57891E].mkv", "Some Anime Show", 0, 1, null, 0)]
    [InlineData("[SFW-sage] Some Anime Show S3 - 12 [720p][D07C91FC]", "Some Anime Show S3", 0, 12, null, 0)]
    [InlineData("Some_Anime_Show_e66_time_is_money_part_one_marking_time", "Some Anime Show", 0, 66, null, 0)]
    [InlineData("[Underwater-FFF] No Series Title No Life - 01 (720p) [27AAA0A0].mkv", "No Series Title No Life", 0, 1, null, 0)]
    [InlineData("[FroZen] Series Title - 23 [DVD][7F6170E6]", "Series Title", 0, 23, null, 0)]
    [InlineData("[Commie] Series Title - 32 [0BA19D5B]", "Series Title", 0, 32, null, 0)]
    [InlineData("[Doki]Series Title - 07 (1280x720 Hi10P AAC) [80AF7DDE]", "Series Title", 0, 7, null, 0)]
    [InlineData("[HorribleSubs] Series Title - 32 [480p]", "Series Title", 0, 32, null, 0)]
    [InlineData("[CR] Series Title - 004 [480p][48CE2D0F]", "Series Title", 0, 4, null, 0)]
    [InlineData("[Chibiki] Series Title!! - 42 [360p][7A4FC77B]", "Series Title!!", 0, 42, null, 0)]
    [InlineData("[HorribleSubs] Series Title - 32 [1080p]", "Series Title", 0, 32, null, 0)]
    [InlineData("[HorribleSubs] Series Title! S2 - 07 [720p]", "Series Title! S2", 0, 7, null, 0)]
    [InlineData("[DeadFish] Series Title - 09v2 [720p][AAC]", "Series Title", 0, 9, null, 0)]
    [InlineData("[Underwater-FFF] Series Title - 01 (720p) [27AAA0A0]", "Series Title", 0, 1, null, 0)]
    [InlineData("[S-T-D] Series Title! - 06 (1280x720 10bit AAC) [59B3F2EA].mkv", "Series Title!", 0, 6, null, 0)]
    [InlineData("Series Title - 010 (720p) [27AAA0A0].mkv", "Series Title", 0, 10, null, 0)]
    [InlineData("Initial_Series_Title - 01 DVD - Central Anime", "Initial Series Title", 0, 1, null, 0)]
    [InlineData("Initial_Series_Title_-_01(DVD)_-_(Central_Anime)[5AF6F1E4].mkv", "Initial Series Title", 0, 1, null, 0)]
    [InlineData("Initial_Series_Title_-_02(DVD)_-_(Central_Anime)[0CA65F00].mkv", "Initial Series Title", 0, 2, null, 0)]
    [InlineData("Initial_Series_Title - 03 DVD - Central Anime", "Initial Series Title", 0, 3, null, 0)]
    [InlineData("Initial_Series_Title_-_03(DVD)_-_(Central_Anime)[629BD592].mkv", "Initial Series Title", 0, 3, null, 0)]
    [InlineData("Initial_Series_Title - 14 DVD - Central Anime", "Initial Series Title", 0, 14, null, 0)]
    [InlineData("Initial_Series_Title_-_14(DVD)_-_(Central_Anime)[0183D922].mkv", "Initial Series Title", 0, 14, null, 0)]
    [InlineData("[ChihiroDesuYo].Series.Title.-.09.1280x720.10bit.AAC.[24CCE81D]", "Series Title", 0, 9, null, 0)]
    [InlineData("Series Title - 001 - Fairy Tail", "Series Title", 0, 1, null, 0)]
    [InlineData("Series Title - 049 - The Day of Fated Meeting", "Series Title", 0, 49, null, 0)]
    [InlineData("Series Title - 050 - Special Request Watch Out for the Guy You Like!", "Series Title", 0, 50, null, 0)]
    [InlineData("Series Title - 099 - Natsu vs. Gildarts", "Series Title", 0, 99, null, 0)]
    [InlineData("Series Title - 100 - Mest", "Series Title", 0, 100, null, 0)]
    [InlineData("[Exiled-Destiny] Series Title Ep01 (D2201EC5).mkv", "Series Title", 0, 1, null, 0)]
    [InlineData("[Commie] Series Title - 23 [5396CA24].mkv", "Series Title", 0, 23, null, 0)]
    [InlineData("[FFF] Series Title - 01 [1FB538B5].mkv", "Series Title", 0, 1, null, 0)]
    [InlineData("[Hatsuyuki]Series_Title-01[1280x720][122E6EF8]", "Series Title", 0, 1, null, 0)]
    [InlineData("[CBM]_Series_Title_-_11_-_511_Kinderheim_[6C70C4E4].mkv", "Series Title", 0, 11, null, 0)]
    [InlineData("[HorribleSubs] Series Title 2 - 05 [720p].mkv", "Series Title 2", 0, 5, null, 0)]
    [InlineData("[Commie] Series Title 2 - 05 [FCE4D070].mkv", "Series Title 2", 0, 5, null, 0)]
    [InlineData("[DRONE]Series.Title.100", "Series Title", 0, 100, null, 0)]
    [InlineData("[RlsGrp]Series.Title.2010.S01E01.001.HDTV-720p.x264-DTS", "Series Title", 2010, 1, 1, 1)]
    [InlineData("Series Title - 130 - Found You, Gohan! Harsh Training in the Kaioshin Realm! [Baaro][720p][5A1AD35B].mkv", "Series Title", 0, 130, null, 0)]
    [InlineData("Series Title - 131 - A Merged Super-Warrior Is Born, His Name Is Gotenks!! [Baaro][720p][32E03F96].mkv", "Series Title", 0, 131, null, 0)]
    [InlineData("[HorribleSubs] Series Title - 01 [1080p]", "Series Title", 0, 1, null, 0)]
    [InlineData("[Jumonji-Giri]_[F-B]_Series_Title_Ep04_(0b0e2c10).mkv", "Series Title", 0, 4, null, 0)]
    [InlineData("[Jumonji-Giri]_[F-B]_Series_Title_Ep08_(8246e542).mkv", "Series Title", 0, 8, null, 0)]
    [InlineData("Knights Series Title - 01 [1080p 10b DTSHD-MA eng sub].mkv", "Knights Series Title", 0, 1, null, 0)]
    [InlineData("Series Title (2010) {01} Episode Title (1).hdtv-720p", "Series Title", 2010, 1, null, 0)]
    [InlineData("[HorribleSubs] Series Title - 20 [720p].mkv", "Series Title", 0, 20, null, 0)]
    [InlineData("[Hatsuyuki] Series Title (2014) - 017 (115) [1280x720][B2CFBC0F]", "Series Title", 2014, 17, null, 0)]
    [InlineData("[Hatsuyuki] Series Title (2014) - 018 (116) [1280x720][C4A3B16E]", "Series Title", 2014, 18, null, 0)]
    [InlineData("Series Title (2014) - 39 (137) [v2][720p.HDTV][Unison Fansub]", "Series Title", 2014, 39, null, 0)]
    [InlineData("[HorribleSubs] Series Title 21 - 101 [480p].mkv", "Series Title 21", 0, 101, null, 0)]
    [InlineData("[Cthuyuu].Series.Title.-.03.[720p.H264.AAC][8AD82C3A]", "Series Title", 0, 3, null, 0)]
    [InlineData("[Cthuyuu] Series Title - 03 [720p H264 AAC][8AD82C3A]", "Series Title", 0, 3, null, 0)]
    [InlineData("Series Title Episode 56 [VOSTFR V2][720p][AAC]-Mystic Z-Team", "Series Title", 0, 56, null, 0)]
    [InlineData("[Mystic Z-Team] Series Title Episode 69 [VOSTFR_Finale][1080p][AAC].mp4", "Series Title", 0, 69, null, 0)]
    [InlineData("[Shark-Raws] Series Title #957 (NBN 1280x720 x264 AAC).mp4", "Series Title", 0, 957, null, 0)]
    [InlineData("Series Title EP06 720p x265 AOZ.mp4", "Series Title", 0, 6, null, 0)]
    [InlineData("Series Title 2018 EP06 720p x265 AOZ.mp4", "Series Title", 2018, 6, null, 0)]
    [InlineData("Series Title 2018 06 720p x265 AOZ.mp4", "Series Title", 2018, 6, null, 0)]
    [InlineData("Series Title S03 - EP14 VOSTFR [1080p] [HardSub] Yass'Kun", "Series Title S03", 0, 14, null, 0)]
    [InlineData("Series Title S3 -  15 VOSTFR [720p]", "Series Title S3", 0, 15, null, 0)]
    [InlineData("A Series: RE S2 - Episode 4 VOSTFR (1080p)", "A Series: RE S2", 0, 4, null, 0)]
    [InlineData("To Another Series III - Episode 5 VOSTFR (1080p)", "To Another Series III", 0, 5, null, 0)]
    [InlineData("[Prout] Show;Title 0 - Episode 5 VOSTFR (BDRip 1920x1080 x264 FLAC)", "Show;Title 0", 0, 5, null, 0)]
    [InlineData("[BakedFish] Some Show [Anime] - 01 [720p][AAC].mp4", "Some Show [Anime]", 0, 1, null, 0)]
    [InlineData("Abc x Abc (2011) - 141 - Magician [KaiDubs] [1080p]", "Abc x Abc", 2011, 141, null, 0)]
    [InlineData("Abc Abc 484 VOSTFR par Abc-Abc (1280*720) - version MQ", "Abc Abc", 0, 484, null, 0)]
    [InlineData("Abc - Abc Abc Abc - 107 VOSTFR par Fansub-Miracle Sharingan (1920x1080) - HQ_Draft", "Abc - Abc Abc Abc", 0, 107, null, 0)]
    [InlineData("Abc Abc Abc Abc Episode 10 VOSTFR (1920x1080) Miracle Sharingan Fansub.MKV - Team - (� suivre)", "Abc Abc Abc Abc", 0, 10, null, 0)]
    [InlineData("[Glenn] Series! 3 - 11 (1080p AAC)[C34B2B3B].mkv", "Series! 3", 0, 11, null, 0)]
    [InlineData("SeriesTitle.E1135.Lasst.den.Mond.am.Himmel.stehen.GERMAN.1080p.WEBRip.x264-Group", "SeriesTitle", 0, 1135, null, 0)]
    [InlineData("SeriesTitle E1135 Lasst den Mond am Himmel stehen GERMAN 1080p WEBRip x264-Group", "SeriesTitle", 0, 1135, null, 0)]
    [InlineData("SeriesTitle.E1206.In.seinen.Augen.2022.GERMAN.1080p.WEB.h264-Group", "SeriesTitle", 0, 1206, null, 0)]
    [InlineData("SeriesTitle E1206 In seinen Augen 2022 GERMAN 1080p WEB h264-Group", "SeriesTitle", 0, 1206, null, 0)]
    [InlineData("Series.Title.E195.Zurueck.auf.die.Grand.Line.UNCUT.German.Dubbed.1999.ANiME.DVDRiP.XviD-Group", "Series Title", 0, 195, null, 0)]
    [InlineData("[HorribleSubs] Series 100 - 07 [1080p].mkv", "Series 100", 0, 7, null, 0)]
    [InlineData("[HorribleSubs] Series 100 S2 - 07 [1080p].mkv", "Series 100 S2", 0, 7, null, 0)]
    [InlineData("[abc] Adventure Series: 30 [Web][MKV][h264][720p][AAC 2.0][abc]", "Adventure Series:", 0, 30, null, 0)]
    [InlineData("[XKsub] Series Title S2 [05][HEVC-10bit 1080p AAC][CHS&CHT&JPN]", "Series Title S2", 0, 5, null, 0)]
    [InlineData("[Cheetah-Raws] Super Long Anime - 1000 (YTV 1280x720 x264 AAC)", "Super Long Anime", 0, 1000, null, 0)]
    [InlineData("[DameDesuYo] Another Anime With Special Naming (Season 2) - 33 (1280x720 10bit EAC3) [42A12A76].mkv", "Another Anime With Special Naming", 0, 33, 2, 0)]
    [InlineData("[SubsPlease] Anime Title 300-nen, With Even More Title - 01 (1080p) [8DE44442]", "Anime Title 300-nen, With Even More Title", 0, 1, null, 0)]
    [InlineData("[Chihiro] Anime Title 300-nen, With Even More Title 02 [720p Hi10P AAC][031FA533]", "Anime Title 300-nen, With Even More Title", 0, 2, null, 0)]
    [InlineData("[BakeSubs] 86 - 01 [1080p][D40A9E55].mkv", "86", 0, 1, null, 0)]
    [InlineData("Anime Title the Final - 09 (2021) [SubsPlease] [WEBRip] [HD 1080p]", "Anime Title the Final", 0, 9, null, 0)]
    [InlineData("Anime Title S21 999", "Anime Title S21", 0, 999, null, 0)]
    [InlineData("Anime Title S21 1000", "Anime Title S21", 0, 1000, null, 0)]
    [InlineData("[HatSubs] Anime Title 1004 [E63F2984].mkv", "Anime Title", 0, 1004, null, 0)]
    [InlineData("Anime Title 100 S3 - 01 (1080p) [5A493522]", "Anime Title 100 S3", 0, 1, null, 0)]
    [InlineData("[SubsPlease] Anime Title 100 S3 - 01 (1080p) [5A493522]", "Anime Title 100 S3", 0, 1, null, 0)]
    [InlineData("[CameEsp] Another Anime 100 - Another 100 Anime - 01 [720p][ESP-ENG][mkv]", "Another Anime 100 - Another 100 Anime", 0, 1, null, 0)]
    [InlineData("[SubsPlease] Another Anime 100 - Another 100 Anime - 01 (1080p) [4E6B4518].mkv", "Another Anime 100 - Another 100 Anime", 0, 1, null, 0)]
    [InlineData("Some show 69. Blm (29.10.2023) 1080p WebDL #turkseed", "Some show", 0, 69, null, 0)]
    [InlineData("Soap opera 01 BLM(01.11.2023) 1080p HDTV AC3 x264 TURG", "Soap opera", 0, 1, null, 0)]
    [InlineData("Turkish show 60.Bolum (31.01.2023) 720p WebDL AAC H.264 - TURG", "Turkish show", 0, 60, null, 0)]
    [InlineData("Different show 1. Bölüm (23.10.2023) 720p WebDL AAC H.264 - TURG", "Different show", 0, 1, null, 0)]
    [InlineData("Dubbed show 79.BLM Sezon Finali(25.06.2023) 720p WEB-DL AAC2.0 H.264-TURG", "Dubbed show", 0, 79, null, 0)]
    [InlineData("Exclusive BLM Documentary with no false positives EP03.1080p.AAC.x264", "Exclusive BLM Documentary with no false positives", 0, 3, null, 0)]
    [InlineData("[SubsPlease] Title de Series S2 - 03 (540p) [63501322]", "Title de Series S2", 0, 3, null, 0)]
    [InlineData("[Naruto-Kun.Hu] Dr Series S3 - 21 [1080p]", "Dr Series S3", 0, 21, null, 0)]
    [InlineData("[Naruto-Kun.Hu] Series Title - 12 [1080p].mkv", "Series Title", 0, 12, null, 0)]
    [InlineData("[Naruto-Kun.Hu] Anime Triangle - 08 [1080p].mkv", "Anime Triangle", 0, 8, null, 0)]
    [InlineData("[Mystic Z-Team] Series Title Super - Episode 013 VF - Non-censuré [720p].mp4", "Series Title Super", 0, 13, null, 0)]
    [InlineData("Series Title Kai Episodio 13 Audio Latino", "Series Title Kai", 0, 13, null, 0)]
    [InlineData("Series_Title_2_[01]_[AniLibria_TV]_[WEBRip_1080p]", "Series Title 2", 0, 1, null, 0)]
    [InlineData("[SubsPlease] Series Title - 100 Years Quest - 01 (1080p) [1107F3A9].mkv", "Series Title - 100 Years Quest", 0, 1, null, 0)]
    [InlineData("[SubsPlease] Series Title 100 Years Quest - 01 (1080p) [1107F3A9].mkv", "Series Title 100 Years Quest", 0, 1, null, 0)]
    [InlineData("[Dae-P9] Anime Series - 05 - S01E05 - Marrying by Contesting (BD 1080p HEVC FLAC AAC) [Dual Audio] [5BCD56B8]", "Anime Series", 0, 5, 1, 5)]
    [InlineData("[Kaleido-subs] Animation - 12 (S01E12) - (WEB 1080p HEVC x265 10-bit E-AC3 2.0) [1ADD8F6D]", "Animation", 0, 12, 1, 12)]
    public void Parses_absolute_episode(string release, string title, int year, int absolute, int? season, int episode)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Sequence("AbsoluteEpisodeNumbers", [absolute], parsed?.AbsoluteEpisodeNumbers)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", episode == 0 ? [] : [episode], parsed?.EpisodeNumbers)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Verify();
    }

    [Theory]
    [InlineData("[DeadFish] Another Anime Show - 01 - Special [BD][720p][AAC]", "Another Anime Show", 1)]
    [InlineData("[DeadFish] Another Anime Show - 01 - OVA [BD][720p][AAC]", "Another Anime Show", 1)]
    [InlineData("[DeadFish] Another Anime Show - 01 - OVD [BD][720p][AAC]", "Another Anime Show", 1)]
    public void Parses_absolute_special(string release, string title, int absolute)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Sequence("AbsoluteEpisodeNumbers", [absolute], parsed?.AbsoluteEpisodeNumbers)
            .Equal("IsSpecial", true, parsed?.IsSpecial)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Verify();
    }

    [Theory]
    [InlineData("[Underwater] Another OVA - The Other -Karma- (BD 1080p) [3A561D0E].mkv", "Another")]
    [InlineData("[sam] Long Series - NCOP [BD 1080p FLAC] [BBC3BC68].mkv", "Long Series")]
    [InlineData("[sam] Long Series - NCED [BD 1080p FLAC] [BBC3BC68].mkv", "Long Series")]
    [InlineData("[Anime Time] Series no Mayo - 12.5.mkv", "Series no Mayo")]
    [InlineData("[SubsPlease] Series Title - 26.5.1 (1080p) [29AF1C23].mkv", "Series Title")]
    [InlineData("[HorribleSubs] Show Slayer - 10.5 [1080p].mkv", "Show Slayer")]
    public void Parses_special_without_absolute_number(string release, string title)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Sequence("EpisodeNumbers", [], parsed?.EpisodeNumbers)
            .Equal("SeasonNumber", 0, parsed?.SeasonNumber)
            .Equal("IsSpecial", true, parsed?.IsSpecial)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Verify();
    }

    [Theory]
    [InlineData("[Dae-P9] Anime Series - 05.5 - S00E01 - Marrying by Contesting (BD 1080p HEVC FLAC AAC) [Dual Audio] [5BCD56B8]", "Anime Series", 0, 1)]
    [InlineData("[Thighs] Anime Reincarnation - 17.5 (S00E01) (BD 1080p FLAC AAC) [Dual-Audio] [A6E2110E]", "Anime Reincarnation", 0, 1)]
    [InlineData("[sam] Anime - 15.5 (S00E01) [BD 1080p FLAC] [3E8D676D]", "Anime", 0, 1)]
    [InlineData("[Kaleido-subs] Sky Series - 07.5 (S00E01) - (BD 1080p HEVC x265 10-bit Opus 2.0) [A548C980].mkv", "Sky Series", 0, 1)]
    public void Parses_decimal_absolute_followed_by_standard_numbers(string release, string title, int season, int episode)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [episode], parsed?.EpisodeNumbers)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Equal("IsSpecial", true, parsed?.IsSpecial)
            .Verify();
    }

    [Theory]
    [InlineData("[ANBU-AonE]_SeriesTitle_26-27_[F224EF26].avi", "SeriesTitle", 0, 26, 27)]
    [InlineData("[Doutei] Some Good, Anime Show - 01-12 [BD][720p-AAC]", "Some Good, Anime Show", 0, 1, 12)]
    [InlineData("Series Title (2010) - 01-02-03 - Episode Title (1) HDTV-720p", "Series Title", 2010, 1, 3)]
    [InlineData("[RlsGrp] Series Title (2010) - S01E01-02-03 - 001-002-003 - Episode Title HDTV-720p v2", "Series Title", 2010, 1, 3)]
    [InlineData("[RlsGrp] Series Title (2010) - S01E01-02 - 001-002 - Episode Title HDTV-720p v2", "Series Title", 2010, 1, 2)]
    [InlineData("Series Title (2010) - S01E01-02 (001-002) - Episode Title (1) HDTV-720p v2 [RlsGrp]", "Series Title", 2010, 1, 2)]
    [InlineData("[HorribleSubs] Some Anime Show!! (01-25) [1080p] (Batch)", "Some Anime Show!!", 0, 1, 25)]
    [InlineData("Some Anime Show (2011) Episode 99-100 [1080p] [Dual.Audio] [x265]", "Some Anime Show", 2011, 99, 100)]
    [InlineData("Some Anime Show 1-13 (English Dub) [720p]", "Some Anime Show", 0, 1, 13)]
    [InlineData("Series.Title.Ep01-12.Complete.English.AC3.DL.1080p.BluRay.x264", "Series Title", 0, 1, 12)]
    [InlineData("[Judas] Some Anime Show 091-123 [1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs]", "Some Anime Show", 0, 91, 123)]
    [InlineData("[Judas] Some Anime Show - 091-123 [1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs]", "Some Anime Show", 0, 91, 123)]
    [InlineData("[HorribleSubs] Some Anime Show 01 - 119 [1080p] [Batch]", "Some Anime Show", 0, 1, 119)]
    [InlineData("[Erai-raws] Series Title! - 01~10 [1080p][Multiple Subtitle]", "Series Title!", 0, 1, 10)]
    [InlineData("[Erai-raws] Series-Title! 2 - 01~10 [1080p][Multiple Subtitle]", "Series-Title! 2", 0, 1, 10)]
    [InlineData("[Erai-raws] Series Title! - 01 ~ 10 [1080p][Multiple Subtitle]", "Series Title!", 0, 1, 10)]
    [InlineData("[Erai-raws] Series-Title! 2 - 01 ~ 10 [1080p][Multiple Subtitle]", "Series-Title! 2", 0, 1, 10)]
    [InlineData("Series_Title_2_[01-05]_[AniLibria_TV]_[WEBRip_1080p]", "Series Title 2", 0, 1, 5)]
    [InlineData("[Moxie] One Series - The Country (892-916) (BD Remux 1080p AAC FLAC) [Dual Audio]", "One Series - The Country", 0, 892, 916)]
    [InlineData("[HatSubs] One Series (1017-1088) (WEB 1080p)", "One Series", 0, 1017, 1088)]
    [InlineData("[HatSubs] One Series 1017-1088 (WEB 1080p)", "One Series", 0, 1017, 1088)]
    [InlineData("[Group] Title S01 - 01-12 [1080p]", "Title S01", 0, 1, 12)]
    [InlineData("[Group] Title S01 - 01-02 [1080p]", "Title S01", 0, 1, 2)]
    [InlineData("[YMDR][慕留人 -火影忍者新時代-][Anime Series Title-][2017][88-91][1080p][AVC][JAP][BIG5][MP4-AAC][繁中]", "Anime Series Title", 0, 88, 91)]
    [InlineData("[诸神字幕组][战栗杀机][ANIME SERIES TITLE][01-24完][简日双语字幕][720P][MP4]", "ANIME SERIES TITLE", 0, 1, 24)]
    public void Parses_multi_episode_absolute_numbers(string release, string title, int year, int first, int last)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Sequence("AbsoluteEpisodeNumbers", Enumerable.Range(first, last - first + 1), parsed?.AbsoluteEpisodeNumbers)
            .Equal("FullSeason", false, parsed?.FullSeason)
            .Verify();
    }

    [Theory]
    [InlineData("[Vivid] Some Anime Show S01 [Web][MKV][h264 10-bit][1080p][AAC 2.0]", "Some Anime Show", 0, 1)]
    [InlineData("[Group] Title S01 12 13 [1080p]", "Title", 0, 1)]
    [InlineData("[Group] Title S03 10 11 [1080p]", "Title", 0, 3)]
    [InlineData("[Group] Title S1 05 [1080p]", "Title", 0, 1)]
    [InlineData("Anime, Title? | Japanse Anime, Title? [Season 1 + EXTRA] [BD 1080p x265 HEVC OPUS] [Dual-Audio]", "Anime, Title | Japanse Anime, Title", 0, 1)]
    [InlineData("[Judas] Japanse Anime, Title (Anime, Title?) (Season 1) [1080p][HEVC x265 10bit][Multi-Subs] (Batch)", "Japanse Anime, Title (Anime, Title)", 0, 1)]
    [InlineData("【高清剧集网发布 www.DDHDTV.com】当我飞奔向你[全24集][国语音轨+简繁英字幕].Series.Towards.You.S01.2023.1080p.NF.WEB-DL.H264.DDP2.0-SeeWEB", "Series Towards You", 0, 1)]
    public void Parses_anime_season_pack(string release, string title, int year, int season)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Equal("FullSeason", true, parsed?.FullSeason)
            .Verify();
    }

    [Theory]
    [InlineData("【喵萌奶茶屋】★10月新番★[哥布林杀手/Anime Series Title][12END][720p][繁体][招募翻译校对]", "Anime Series Title", 0, 12)]
    [InlineData("[桜都字幕组][盾之勇者成名录/Anime Series Title][01][BIG5][720P]", "Anime Series Title", 0, 1)]
    [InlineData("[YMDR][輝夜姬想讓人告白～天才們的戀愛頭腦戰～][Anime Series Title][2019][02][1080p][HEVC][JAP][BIG5][MP4-AAC][繁中]", "Anime Series Title", 0, 2)]
    [InlineData("【DHR百合組】[天使降臨到我身邊！_Anime Series Title][05][繁體][1080P10][WebRip][HEVC][MP4]", "Anime Series Title", 0, 5)]
    [InlineData("【傲娇零&自由字幕组】[刀剑神域III UnderWorld/ Anime Series Title ][17][HEVC-10Bit-2160P AAC][外挂GB/BIG5][WEB-Rip][MKV+ass ]", "Anime Series Title", 0, 17)]
    [InlineData("【悠哈璃羽字幕社＆拉斯观测组】[刀剑神域Alicization _ Anime Series Title ][17][x264 1080p][CHS]", "Anime Series Title", 0, 17)]
    [InlineData("【极影字幕社】 ★04月新番 【巴哈姆特之怒 Virgin Soul】【Anime Series Title】【24】 【END】GB MP4_720P", "Anime Series Title", 0, 24)]
    [InlineData("[愛戀&漫貓字幕组][10月新番][關於我轉生後成爲史萊姆那件事][18][720P][BIG5][MP4]", "關於我轉生後成爲史萊姆那件事", 0, 18)]
    [InlineData("【咕咕茶字幕組】★1月新番[天使降臨到了我身邊! / Anime Series Title!][04][1080P][繁體][MP4]", "Anime Series Title!", 0, 4)]
    [InlineData("【千夏字幕组】【天使降临到了我身边！_Anime Series Title】[第05话][1080p_HEVC][简繁外挂]", "Anime Series Title", 0, 5)]
    [InlineData("[星空字幕组] 剃须。然后捡到女高中生。 / Anime Series Title [05][1080p][简日内嵌]", "Anime Series Title", 0, 5)]
    [InlineData("【DHR动研字幕组】[多田君不恋爱_Anime Series Title][13完][繁体][720P][MP4]", "Anime Series Title", 0, 13)]
    [InlineData("【动漫国字幕组】★01月新番[Anime Series Title～！][01][1080P][简体][MP4]", "Anime Series Title～！", 0, 1)]
    [InlineData("[风车字幕组][名侦探柯南][857][米花町反复变化之谜（前篇）][简体][MP4][1080P]", "名侦探柯南", 0, 857)]
    [InlineData("[风车字幕组][名侦探柯南][857集][米花町反复变化之谜（前篇）][简体][MP4][1080P]", "名侦探柯南", 0, 857)]
    [InlineData("【喵萌奶茶屋】★10月新番★[后宫之乌/后宫の乌/Series Title][07][1080p][简日双语][招募翻译校对]", "Series Title", 0, 7)]
    [InlineData("[Lilith-Raws] 在地下城尋求邂逅是否搞錯了什麼 / Anime-Series Title S04 - 12 [Baha][WEB-DL][1080p][AVC AAC][CHT][MP4]", "Anime-Series Title S4", 0, 12)]
    [InlineData("[Lilith-Raws] 魔王學院的不適任者 / Anime-Series Title S02 - 01 [Baha][WEB-DL][1080p][AVC AAC][CHT][MP4]", "Anime-Series Title S2", 0, 1)]
    [InlineData("[Lilith-Raws x WitEx.io] 盾之勇者成名录 / Anime-Series Title S02 - 03 [Baha][WEB-DL][1080p][AVC AAC][CHT][MP4] [557.3MB]", "Anime-Series Title S2", 0, 3)]
    [InlineData("[SweetSub&LoliHouse] 来自深渊 烈日黄金乡 / Anime-Series Title S2 - 07 [WebRip 1080p HEVC-10bit AAC][简繁日内封字幕]", "Anime-Series Title S2", 0, 7)]
    [InlineData("[LoliHouse] Love Live! 虹咲学园学园偶像同好会 第二季 / Anime-Series Title S2 - 10 [WebRip 1080p HEVC-10bit AAC][简繁内封字幕]", "Anime-Series Title S2", 0, 10)]
    [InlineData("[澄空学园&雪飘工作室&LoliHouse] 辉夜大小姐想让我告白 第三季 / Anime-Series Title S3 - 06 [WebRip 1080p HEVC-10bit AAC][简繁内封字幕]", "Anime-Series Title S3", 0, 6)]
    [InlineData("[诸神字幕组][致不灭的你 第二季][Anime-Series Title S2][10][简繁日语字幕][1080P][MKV HEVC]", "Anime-Series Title S2", 0, 10)]
    [InlineData("[NC-Raws] 魔王学院的不适任者～史上最强的魔王始祖，转生就读子孙们的学校～第二季 / Anime-Series Title S2 - 01 (Baha 1920x1080 AVC AAC MP4)", "Anime-Series Title S2", 0, 1)]
    [InlineData("[Lilith-Raws] Anime-Series Title S02 - 11 [Baha][WEB-DL][1080p][AVC AAC][CHT][MP4].mp4", "Anime-Series Title S2", 0, 11)]
    [InlineData("[天月搬运组] 不要欺负我，长瀞同学 2nd Attack / Anime-Series Title S02 - 01 [1080P][简繁日外挂]", "Anime-Series Title S2", 0, 1)]
    [InlineData("[Skymoon-Raws] 怕痛的我，把防御力点满就对了 第二季 / Anime-Series Title S02 - 01 [ViuTV][WEB-DL][1080p][AVC AAC][繁体外挂][MP4+ASS](正式版本) ", "Anime-Series Title S2", 0, 1)]
    [InlineData("[Skymoon-Raws] Anime-Series Title S02 - 01 [ViuTV][CHT][WEB-DL][1080p][AVC AAC][MP4+ASS]", "Anime-Series Title S2", 0, 1)]
    [InlineData("[orion origin] Anime-Series Title S02[07][1080p][H264 AAC][CHS][ENG＆JPN stidio]", "Anime-Series Title S2", 0, 7)]
    [InlineData("[UHA-WINGS][Anime-Series Title S02][01][x264 1080p][CHT].mp4", "Anime-Series Title S2", 0, 1)]
    [InlineData("[Suzuya Raws] 腼腆英雄 东京夺还篇 / Series 2nd Season - 01 [CR WebRip 1080p HEVC-10bit AAC][Multi-Subs]", "Series 2nd Season", 0, 1)]
    [InlineData("[ANi] SERIES / SERIES 靦腆英雄 - 11 [1080P][Baha][WEB-DL][AAC AVC][CHT][MP4]", "SERIES", 0, 11)]
    [InlineData("[Q] 全职高手 第3季 / Series S3 - 09 (1080p HBR HEVC Multi-Sub)", "Series S3", 0, 9)]
    [InlineData("[Q] 全职高手 第3季 | Series S3 - 09 (1080p HBR HEVC Multi-Sub)", "Series S3", 0, 9)]
    [InlineData("[喵萌奶茶屋&LoliHouse]玛娜利亚魔法学院/巴哈姆特之怒Anime Series Title - 03 [WebRip 1080p HEVC-10bit AAC][简繁内封字幕]", "巴哈姆特之怒Anime Series Title", 0, 3)]
    [InlineData("[悠哈璃羽字幕社&拉斯观测组&LoliHouse] 刀剑神域: Alicization / Anime Series: Title - 17 [WebRip 1080p HEVC-10bit AAC][简繁内封字幕]", "Anime Series: Title", 0, 17)]
    [InlineData("[ZERO字幕組]嫁給非人類·Anime-Series Title[11][BIG5][1080p]", "Anime-Series Title", 0, 11)]
    [InlineData("[Lilith-Raws] 艾梅洛閣下 II 世事件簿 -魔眼蒐集列車 Grace note- / Anime-Series Title - 04 [BiliBili][WEB-DL][1080p][AVC AAC][CHT][MKV]", "Anime-Series Title", 0, 4)]
    [InlineData("[NC-Raws] 影宅 / Anime-Series Title - 07 [B-Global][WEB-DL][1080p][AVC AAC][CHS_CHT_ENG_TH_SRT][MKV]", "Anime-Series Title", 0, 7)]
    [InlineData("[NC-Raws] ANIME-SERIES TITLE－影宅－ / Anime-Series Title - 07 [Baha][WEB-DL][1080p][AVC AAC][CHT][MP4]", "Anime-Series Title", 0, 7)]
    [InlineData("[OPFans楓雪動漫][ANIME SERIES 海賊王][第1008話][典藏版][1080P][MKV][簡繁]", "ANIME SERIES", 0, 1008)]
    [InlineData("[Skymoon-Raws][Anime Series 海賊王][1008][ViuTV][WEB-RIP][CHT][SRTx2][1080p][MKV]", "Anime Series", 0, 1008)]
    [InlineData("[银光字幕组][名侦探柯南·Series Title][871][信长四五〇事件][繁日][HDrip][X264-AAC][720P][MP4]", "Series Title", 0, 871)]
    [InlineData("[BeanSub][Anime_Series_Title][01][GB][1080P][x264_AAC]", "Anime Series Title", 0, 1)]
    [InlineData("[GM-Team][国漫][斗罗大陆][Anime Title][Douro Mainland][2019][215][AVC][GB][1080P]", "Anime Title", 0, 215)]
    [InlineData("[GM-Team][国漫][斗罗大陆][Anime Title][Douro Mainland][2019][215 END][AVC][GB][1080P]", "Anime Title", 0, 215)]
    [InlineData("[GM-Team][国漫][斗罗大陆][Anime Title][2019][215 Fin][AVC][GB][1080P]", "Anime Title", 0, 215)]
    [InlineData("[GM-Team][国漫][Anime Title][Douro Mainland][2019][234][AVC][GB][1080P]", "Anime Title", 0, 234)]
    [InlineData("[GM-Team][国漫][Anime Title][2019][234][AVC][GB][1080P]", "Anime Title", 0, 234)]
    [InlineData("[GM-Team][国漫][Anime Title][2019][234 END][AVC][GB][1080P]", "Anime Title", 0, 234)]
    [InlineData("[GM-Team][国漫][Anime Title][2019][234 Fin][AVC][GB][1080P]", "Anime Title", 0, 234)]
    [InlineData("[Subz] My Series - １５８ [h264 10-bit][1080p]", "My Series", 0, 158)]
    public void Parses_chinese_and_unicode_anime_releases(string release, string title, int year, int absolute)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Sequence("AbsoluteEpisodeNumbers", [absolute], parsed?.AbsoluteEpisodeNumbers)
            .Verify();
    }

    [Theory]
    [InlineData("【高清剧集网发布 www.DDHDTV.com】当我飞奔向你[第01-15集][国语配音+中文字幕].Series.Towards.You.S01.2023.2160p.YK.WEB-DL.H265.AAC-BlackTV", "Series Towards You", 0, 1, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })]
    public void Parses_chinese_multi_episode_release(string release, string title, int year, int season, int[] episodes)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", episodes, parsed?.EpisodeNumbers)
            .Verify();
    }

    [Theory]
    [InlineData("[SubDESU]_Show_Title_DxD_07_(1280x720_x264-AAC)_[6B7FD717]", "6B7FD717")]
    [InlineData("[Chihiro]_Show_Title!!_-_06_[848x480_H.264_AAC][859EEAFA]", "859EEAFA")]
    [InlineData("[Underwater]_Show_Title_-_12_(720p)_[5C7BC4F9]", "5C7BC4F9")]
    [InlineData("[HorribleSubs]_Show_Title_-_33_[720p]", null)]
    [InlineData("[HorribleSubs] Show-Title - 13 [1080p].mkv", null)]
    [InlineData("[Doremi].Show.Title.5.Go.Go!.31.[1280x720].[C65D4B1F].mkv", "C65D4B1F")]
    [InlineData("[Doremi].Show.Title.5.Go.Go!.31[1280x720].[C65D4B1F]", "C65D4B1F")]
    [InlineData("[Doremi].Show.Title.5.Go.Go!.31.[1280x720].mkv", null)]
    [InlineData("[K-F] Series Title 214", null)]
    [InlineData("[K-F] Series Title S10E14 214", null)]
    [InlineData("[K-F] Series Title 10x14 214", null)]
    [InlineData("[K-F] Series Title 214 10x14", null)]
    [InlineData("Series Title - 031 - The Resolution to Kill [Lunar].avi", null)]
    [InlineData("[ACX]Series Title 01 Episode Name [Kosaka] [9C57891E].mkv", "9C57891E")]
    [InlineData("[S-T-D] Series Title! - 06 (1280x720 10bit AAC) [59B3F2EA].mkv", "59B3F2EA")]
    [InlineData("[SubDESU]_Show_Title_DxD_07_(1280x720_x264-AAC)_(6B7FD717)", "6B7FD717")]
    [InlineData("[Chihiro]_Show_Title!!_-_06_[848x480_H.264_AAC](859EEAFA)", "859EEAFA")]
    [InlineData("[Underwater]_Show_Title_-_12_(720p)_(5C7BC4F9)", "5C7BC4F9")]
    [InlineData("[Doremi].Show.Title.5.Go.Go!.31.[1280x720].(C65D4B1F).mkv", "C65D4B1F")]
    [InlineData("[Doremi].Show.Title.5.Go.Go!.31[1280x720].(C65D4B1F)", "C65D4B1F")]
    [InlineData("[ACX]Series Title 01 Episode Name [Kosaka] (9C57891E).mkv", "9C57891E")]
    [InlineData("[S-T-D] Series Title! - 06 (1280x720 10bit AAC) (59B3F2EA).mkv", "59B3F2EA")]
    [InlineData("[DHD] Series Title! - 08 (1280x720 10bit AAC) [8B00F2EA].mkv", "8B00F2EA")]
    [InlineData("[DHD] Series Title! - 10 (1280x720 10bit AAC) [10BBF2EA].mkv", "10BBF2EA")]
    [InlineData("[DHD] Series Title! - 08 (1280x720 10bit AAC) [008BF28B].mkv", "008BF28B")]
    [InlineData("[DHD] Series Title! - 10 (1280x720 10bit AAC) [000BF10B].mkv", "000BF10B")]
    [InlineData("[DHD] Series Title! - 08 (1280x720 8bit AAC) [8B8BF2EA].mkv", "8B8BF2EA")]
    [InlineData("[DHD] Series Title! - 10 (1280x720 8bit AAC) [10B10BEA].mkv", "10B10BEA")]
    public void Parses_release_hash(string release, string? hash)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("ReleaseHash", hash, parsed?.ReleaseHash)
            .Verify();
    }

    [Theory]
    [InlineData("The.Big.Series.Leader.Part.2.DSR.XviD-SYS", "The Big Series Leader", 0, 2)]
    [InlineData("kill-roy-was-here-e07-720p", "kill-roy-was-here", 0, 7)]
    [InlineData("Series and Show 2012 Part 1 REPACK 720p HDTV x264 2HD", "Series and Show", 2012, 1)]
    [InlineData("Series Show.2016.E04.Power.720p.WEB-DL.DD5.1.H.264-MARS", "Series Show", 2016, 4)]
    [InlineData("It's a Series Title.E56.190121.720p-NEXT.mp4", "It's a Series Title", 0, 56)]
    [InlineData("My Only Series Title.E37.190120.1080p-NEXT.mp4", "My Only Series Title", 0, 37)]
    [InlineData("Series.E191.190121.720p-NEXT.mp4", "Series", 0, 191)]
    [InlineData("The Series Title Challenge.E932.190120.720p-NEXT.mp4", "The Series Title Challenge", 0, 932)]
    [InlineData("백번의 추억.E12.251019.1080p-F1RST", "백번의 추억", 0, 12)]
    [InlineData("나는 SOLO <나는 솔로>.E223.251015.1080p-F1RST", "나는 SOLO 나는 솔로", 0, 223)]
    [InlineData("나는 SOLO 그 후, 사랑은 계속된다(나솔사계).E135.251016.1080p-F1RST", "나는 SOLO 그 후, 사랑은 계속된다(나솔사계)", 0, 135)]
    public void Parses_mini_series_as_season_one(string release, string title, int year, int episode)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Equal("SeasonNumber", 1, parsed?.SeasonNumber)
            .Sequence("EpisodeNumbers", [episode], parsed?.EpisodeNumbers)
            .Sequence("AbsoluteEpisodeNumbers", [], parsed?.AbsoluteEpisodeNumbers)
            .Verify();
    }

    [Theory]
    [InlineData("Series.With.Title.S02E15")]
    [InlineData("Series.and.a.Title.103.720p.HDTV.X264-DIMENSION")]
    [InlineData("Series.and.a.Title.113.720p.HDTV.X264-DIMENSION")]
    [InlineData("Series.and.a.Title.1013.720p.HDTV.X264-DIMENSION")]
    [InlineData("Series.Title.4x05.HDTV.XviD-LOL")]
    [InlineData("Series.Title.S03E06.DVDRip.XviD-WiDE")]
    [InlineData("Series.Title.S10E27.WS.DSR.XviD-2HD")]
    [InlineData("Series.Title.2010.S02E14.HDTV.XviD-LOL")]
    [InlineData("Series Title 0 S01E19 720p WEB DL DD5 1 H 264 NT")]
    [InlineData("The Series Title S01E14 A Message Back 720p WEB DL DD5 1 H264 SURFER")]
    [InlineData("Series Title S01E07 WS PDTV XviD FUtV")]
    [InlineData("Series.Title.S03E19.DVDRip.XviD-OSiTV")]
    [InlineData("S03E09 WS PDTV XviD FUtV")]
    [InlineData("5x10 WS PDTV XviD FUtV")]
    [InlineData("Series.Title.2009.S01E14.HDTV.XviD-LOL")]
    [InlineData("Series.Title.1995.S03E20.HDTV.XviD-LOL")]
    [InlineData("Series.Title.S03E115.DVDRip.XviD-OSiTV")]
    [InlineData("Series Title - S02E21 - 94 Meetings - 720p TV.mkv")]
    [InlineData("24-7 Series - Title - Road to the Sonarr - S01E03 - Episode 3.mkv")]
    [InlineData("Series.Title.Inc.S03E19.DVDRip.\"XviD\"-OSiTV")]
    [InlineData("Series Title-0 (2010) - 1x05 - Missing Title")]
    [InlineData("Series Title-0 (2010) - 1x05 - Title")]
    [InlineData("Series Title - S06E13 - 5 to 9 [DVD]")]
    [InlineData("Series Title - S02E21 - 18-5-4")]
    [InlineData("Series.Title.S01E07.21.0.Jump.Street.720p.WEB-DL.DD5.1.h.264-KiNGS")]
    [InlineData("Series.Title.525")]
    [InlineData("Series Title - 10x12 - 24 Hour Propane People [SDTV]")]
    [InlineData("Series Title S01E06 3 Beers For Batali DVDRip XviD SPRiNTER")]
    [InlineData("24 7 Series-Title - Road to the Sonarr Part01 720p HDTV x264 ORENJI")]
    [InlineData("24 7 Series-Title - Road to the Sonarr Part 02 720p HDTV x264 ORENJI")]
    [InlineData("24-7 Series-Title - Road to the Sonarr - S01E01 - Part 1")]
    [InlineData("S6E02-Unwrapped-(Playing With Food) - [DarkData]")]
    [InlineData("S06E03-Unwrapped-(Number Ones Unwrapped) - [DarkData]")]
    [InlineData("Series Title S02E21 18 5 4 720p WEB DL DD5 1 h 264 EbP")]
    [InlineData("01x04 - Halloween, Part 1 - 720p WEB-DL")]
    [InlineData("series.s03.e05.ws.dvdrip.xvid-m00tv")]
    [InlineData("series.2009.416.hdtv-lol")]
    [InlineData("series.six-0.2010.217.hdtv-lol")]
    [InlineData("Series Title - S1936E18 - I Love to Singa")]
    [InlineData("Series_Title!_-_7x6_-_The_Scarlett_Getter_[SDTV]")]
    [InlineData("Series_Title_-_1x1_-_Live_and_Learn_[HDTV-720p]")]
    [InlineData("Top Series - 07x03 - 2005.11.70")]
    [InlineData("Series.S04E09.Swan.Song.1080p.WEB-DL.DD5.1.H.264-ECI")]
    [InlineData("S08E20 50-50 Carla [DVD]")]
    [InlineData("Series S08E20 50-50 Carla [DVD]")]
    [InlineData("S02E10 6-50 to SLC [SDTV]")]
    [InlineData("Developers & Coders S02E10 6-50 to SLC [SDTV]")]
    [InlineData("The_Big_Series_Title_-_6x12_-_The_Code_Review_Equivalency_[HDTV-720p]")]
    [InlineData("Series_Title.19x06.720p_HDTV_x264-FoV")]
    [InlineData("Series.S03E10.Alexandra.720p.WEB-DL.AAC2.0.H.264-CROM.mkv")]
    [InlineData("(Series of Title s03 e - \"Series of Title Season 3 Episode 10\"")]
    [InlineData("Series.Hunters.Galatic.S05E607.720p.hdtv.x264")]
    [InlineData("Series.Time.With.Dev1.And.Dev2.S01E20.720p.BluRay.x264-DEiMOS")]
    [InlineData("Series.S01E04.2-45.PM.[HDTV-720p].mkv")]
    [InlineData("S01E04")]
    [InlineData("1x04")]
    [InlineData("10.Lines.You.Know.About.Code.S02E04.Prohibition.HDTV.XviD-AFG")]
    [InlineData("30 Series - S01E01 - Pilot.avi")]
    [InlineData("666 Series Title - S01E01")]
    [InlineData("Series 13 - S01E01")]
    [InlineData("Don't Ever Trust The B---- in Code 23.S01E01")]
    [InlineData("Warehouse.13.S01E01")]
    [InlineData("Never.Trust.The.B----.in.Code.23.S01E01")]
    [InlineData("42 S01E01")]
    [InlineData("69.S01E01")]
    [InlineData("Series - 2x12 - The Choice [HDTV-1080p].mkv")]
    [InlineData("Series - 2x4 - New Car Smell [HDTV-1080p].mkv")]
    [InlineData("Top Series - 06x11 - 2005.08.07")]
    [InlineData("The_Series_US_s06e19_04.28.2014_hdtv.x264.Poke.mp4")]
    [InlineData("the.Series.110.hdtv-lol")]
    [InlineData("2009x09 [SDTV].avi")]
    [InlineData("S2009E09 [SDTV].avi")]
    [InlineData("Series Week S2009E09 [SDTV].avi")]
    [InlineData("St_Series_209_Aids_And_Comfort")]
    [InlineData("[Impatience] Series - 0x01 [720p][34073169].mkv")]
    [InlineData("Series.Title.S15.E06.City.Sushi")]
    [InlineData("Series Title - S15 E06 - City Code")]
    [InlineData("Series S1-E1-WEB-DL-1080p-NZBgeek")]
    [InlineData("Series S1E1-WEB-DL-1080p-NZBgeek")]
    [InlineData("Series.S010E16.720p.HDTV.X264-DIMENSION")]
    [InlineData("[ www.Torrenting.com ] - Series.2012.S02E17.720p.HDTV.X264-DIMENSION")]
    [InlineData("Series.2012.S02E18.720p.HDTV.X264-DIMENSION.mkv")]
    [InlineData("Series - Season 1 - Episode 01 (Resolution).avi")]
    [InlineData("5x09 - 100 [720p WEB-DL].mkv")]
    [InlineData("1x03 - 274 [1080p BluRay].mkv")]
    [InlineData("1x03 - The 112th Congress [1080p BluRay].mkv")]
    [InlineData("Series.2012.S02E14.720p.HDTV.X264-DIMENSION [PublicHD].mkv")]
    [InlineData("Series.2009.S06E03.720p.HDTV.X264-DIMENSION [PublicHD].mkv")]
    [InlineData("20-1.2014.S02E01.720p.HDTV.x264-CROOKS")]
    [InlineData("Series - S01E09 - Debate 109")]
    [InlineData("Series - S02E02 - My Maserati Does 185")]
    [InlineData("6x13 - The Series Show 100th Episode Special")]
    [InlineData("The Series And the Show - S41 E10478 - 2014-08-15")]
    [InlineData("The Series And the Show - S42 E10591 - 2015-01-27")]
    [InlineData("Series Title [1x05] Episode Title")]
    [InlineData("Series Title [S01E05] Episode Title")]
    [InlineData("Series Title Season 01 Episode 05 720p")]
    [InlineData("The Series And the Show - S42 E10713 - 2015-07-20.mp4")]
    [InlineData("Series.103.hdtv-lol[ettv].mp4")]
    [InlineData("Series - 01x02 - The Rooster Prince - [itz_theo]")]
    [InlineData("Series (2009) - [06x16] - Room 147.mp4")]
    [InlineData("grp-zoos01e11-1080p")]
    [InlineData("grp-zoo-s01e11-1080p")]
    [InlineData("Series!.S2016E14.2016-01-20.avi")]
    [InlineData("John.Smith.The.Series.Title.5of9.The.Universe.Of.Development.1990.DVDRip.x264-HANDJOB")]
    [InlineData("Judge Developer 2016 02 25 S20E142")]
    [InlineData("Judge Developer 2016 02 25 S20E143")]
    [InlineData("Red Show - S02 - E06 - Parallel Series")]
    [InlineData("App.Sonarr.Made.in.Canada.Part.Two.720p.HDTV.x264-2HD")]
    [InlineData("The.100000.Series.Title.2016.S01E05.720p.HDTV.x264-W4F")]
    [InlineData("Series S01E02 (22 October 2016) HDTV 720p [Webrip]")]
    [InlineData("this.is.a.show.2015.0308-yestv")]
    [InlineData("Series - S2016E231")]
    [InlineData("Series - 2016x231")]
    [InlineData("Short.Series.S26E022.HDTV.x264-FiHTV")]
    [InlineData("Super.Series.S01.Ep06.1080p.BluRay.DTS.x264-MiR")]
    [InlineData("Series 104 - S01E07 The Developers [SDTV]")]
    [InlineData("11-02 The Series Reaction (HD).m4v")]
    [InlineData("Plus Series la title - S14E3533 FRENCH WEBRIP H.264 AAC (09.05.2018)")]
    [InlineData("The Series - S01E02 - Earth Skills HDTV-1080p AVC DTS [EN+FR+ES+PT+DA+FI+NB+SV]")]
    [InlineData("Series Title - S01E01 - Day 100 [SDTV]")]
    [InlineData("Series.Title.S01.Ep.01.English.AC3.DL.1080p.BluRay-Sonarr")]
    [InlineData("Series.Title.S01.E.01.English.AC3.DL.1080p.BluRay-Sonarr")]
    [InlineData("Series.Title.S01.Ep01.English.AC3.DL.1080p.BluRay-Sonarr")]
    [InlineData("Series.Title.S01.E01.English.AC3.DL.1080p.BluRay-Sonarr")]
    [InlineData("Series.Title.S01EP01.English.AC3.DL.1080p.BluRay-Sonarr")]
    [InlineData("tvs-amgo-dd51-dl-7p-azhd-x264-103")]
    [InlineData("Series Title - S01E01 [AC3 5.1 Castellano][www.descargas2020.org]")]
    [InlineData("Series Title - [02x01] - Episode 1")]
    [InlineData("Series.Title.Of.S01E01.xyz")]
    [InlineData("[RlsGrp] Series Title - S01E27 - 24-Hour")]
    [InlineData("Series Title - S02E01 1920x910")]
    [InlineData("Anime Title - S2020E1527 [1527] [2020-10-11] - Episode Title")]
    [InlineData("Anime Title - S2010E994 [0994] [2010-02-28] - Episode Title [x264 720p][AAC 2ch][HS][Shion+GakiDave]")]
    [InlineData("Series Title - Temporada 2 [HDTV 720p][Cap.201][AC3 5.1 Castellano][www.pctnew.com]")]
    [InlineData("Series Title - Temporada 2 [HDTV 720p][Cap.1901][AC3 5.1 Castellano][www.pctnew.com]")]
    [InlineData("Series Title 1x1")]
    [InlineData("1x1")]
    [InlineData("Series Title [2022] [S25E13] [PL] [720p] [WEB-DL-CZRG] [x264] ")]
    [InlineData("Series T Se.3 afl.3")]
    [InlineData("[Anime Chap] Anime Title! S01E09 [WEB 1080p] {OP & ED Lyrics} - Episode 9 (The Eminence in Shadow)")]
    [InlineData("[Anime Chap] Anime Title! S01E12 [WEB 1080p] {OP & ED Lyrics} - Episode 12 (The Eminence in Shadow)")]
    [InlineData("SeriesTitle-S16E08-10426008-0.mkv")]
    [InlineData("Series-S07E12-31st_Century_Fox-[Bluray-1080p].mkv")]
    [InlineData("TheTitle-S12E13-3_Acts_of_God.mkv")]
    [InlineData("Series Title - Temporada 2 [HDTV 720p][Cap.408]")]
    [InlineData("Series Title [HDTV][Cap.104](website.com).avi")]
    [InlineData("Series Title [HDTV][Cap.402](website.com).avi")]
    [InlineData("Series Title [HDTV 720p][Cap.101](website.com).mkv")]
    [InlineData("Босх: Спадок (S2E1) / Series: Legacy (S2E1) (2023) WEB-DL 1080p Ukr/Eng | sub Eng")]
    [InlineData("Босх: Спадок / Series: Legacy / S2E1 of 10 (2023) WEB-DL 1080p Ukr/Eng | sub Eng")]
    [InlineData("Titles.s06e01.1999.BDRip.1080p.Ukr.Eng.AC3.Hurtom.TNU.Tenax555")]
    [InlineData("Titles.s06.01.1999.BDRip.1080p.Ukr.Eng.AC3.Hurtom.TNU.Tenax555")]
    [InlineData("[Judas] Series Title (2024) - S01E14")]
    [InlineData("[ReleaseGroup] SeriesTitle S01E1 Webdl 1080p")]
    [InlineData("[SubsPlus+] Series no Chill - S02E01 (NF WEB 1080p AVC AAC)")]
    [InlineData("[SubsPlus+] Series no Chill - S02E01v2 (NF WEB 1080p AVC AAC)")]
    [InlineData("Series - Temporada 1 - [HDTV 1080p][Cap.101](wolfmax4k.com)")]
    [InlineData("Series [HDTV 1080p][Cap.101](wolfmax4k.com)")]
    [InlineData("Series [HDTV 1080p][Cap. 101](wolfmax4k.com).mkv")]
    [InlineData("Amazing Title (2024/S01E07/DSNP/WEB-DL/1080p/ESP/EAC3 5.1/ING/EAC3 5.1 Atmos/SUBS) SPWEB")]
    [InlineData("Mini Title (Miniserie) (2024/S01E07/DSNP/WEB-DL/1080p/ESP/EAC3 5.1/ING/EAC3 5.1 Atmos/SUBS) SPWEB")]
    [InlineData("Series.S006E18.Some.Title.Name-Part.1.1080p.WEB-DL.AAC2.0.H.264-Release")]
    [InlineData("Series.2006.S006E18.Some.Title.Name-Part.1.1080p.WEB-DL.AAC2.0.H.264-Release")]
    [InlineData("我的人间烟火.Fireworks.Series.S01E01.2023.V2.1080p.WEB-DL.H264.AAC-SeeWEB")]
    [InlineData("Fireworks.Series.S01E01.2023.V2.1080p.WEB-DL.H264.AAC-SeeWEB")]
    [InlineData("S3E3 - Seventeen Seconds [1080p]")]
    [InlineData("S3E3 - Part 3 Seventeen Seconds [1080p]")]
    [InlineData("S03E03 - Part 3 Seventeen Seconds [1080p]")]
    [InlineData("[VARYG] Series at Age 29 S01E03 Rirui and Anyango 1080p CR WEB-DL AAC2.0 H.264 (29-sai Dokushin Chuuken Boukensha no Nichijou, Multi-Subs)")]
    [InlineData("Series S093E081 1080p ALL4 WEB-DL AAC2 0 H 264-JJUNAHJAMESON")]
    [InlineData("Series S87E136 1080p WEB-DL x264-NGP")]
    [InlineData("Series S092E001 1080p ALL4 WEB-DL AAC2 0 H 264-JJunahJameson")]
    [InlineData("Series s90 e43 1080p HDTV AAC H264")]
    [InlineData("Series - S3E10 - Episode #3.10 - rus 720p WEBDL (LostFilm)")]
    [InlineData("Series - S03E10 - Episode #3.10 - rus 720p WEBDL (LostFilm)")]
    [InlineData("221208 ABC123 Series Title Season 39 ep11.mp4")]
    [InlineData("221208 ABC123 Series Title ep34[1080p60 H264].mp4")]
    [InlineData("221205 ABC123 17研究所！ #17.ts")]
    [InlineData("221201 Series Title! ABC123 ep219[720p.h264].mp4")]
    [InlineData("221206 Series Title! ep08(Tanaka Miku).ts")]
    [InlineData("210810 ABC123 Series Title ep05.mp4")]
    [InlineData("221204 乃木坂工事中 ep389.mp4")]
    [InlineData("Series Title S01E11.5 [SP]-The Poppies Bloom Red on the Battlefield")]
    [InlineData("Series.Title.S06E01b.Fade.Out.Fade.in.Part.2.1080p.DSNP.WEB-DL.AAC2.0.H.264-FLUX")]
    [InlineData("Series.S03E01-06.DUAL.BDRip.XviD.AC3.-HELLYWOOD")]
    [InlineData("Series.Title.103.104.720p.HDTV.X264-DIMENSION")]
    [InlineData("Series.S03E01.S03E02.720p.HDTV.X264-DIMENSION")]
    [InlineData("The Series S01e01 e02 ShoHD On Demand 1080i DD5 1 ALANiS")]
    [InlineData("Series.Title.2x04.2x05.720p.BluRay-FUTV")]
    [InlineData("Series.Title.S07E22E23.720p.HDTV.X264-DIMENSION")]
    [InlineData("Series Title - S07E22 - S07E23 - And Lots of Security.. [HDTV-720p].mkv")]
    [InlineData("S03E01.S03E02.720p.HDTV.X264-DIMENSION")]
    [InlineData("2x04x05.720p.BluRay-FUTV")]
    [InlineData("S02E04E05.720p.BluRay-FUTV")]
    [InlineData("S02E03-04-05.720p.BluRay-FUTV")]
    [InlineData("Series.Kings.S02E09-E10.HDTV.x264-ASAP")]
    [InlineData("Series Kings - 2x9-2x10 - Served Code [SDTV] ")]
    [InlineData("Series Kings - 2x09-2x10 - Served Code [SDTV] ")]
    [InlineData("Hell on Series S02E09 E10 HDTV x264 EVOLVE")]
    [InlineData("Hell.on.Series.S02E09-E10.720p.HDTV.x264-EVOLVE")]
    [InlineData("Series's Sonarr - 8x01_02 - Free Falling")]
    [InlineData("8x01_02 - Free Falling")]
    [InlineData("Series.S01E91-E100")]
    [InlineData("Series.S29E161-E165.PDTV.x264-FQM")]
    [InlineData("Shortland.Series.S22E5363-E5366.HDTV.x264-FiHTV")]
    [InlineData("the.Series.101.102.hdtv-lol")]
    [InlineData("Series.10708.hdtv-lol.mp4")]
    [InlineData("Series.10910.hdtv-lol.mp4")]
    [InlineData("E.010910.HDTVx264REPACKLOL.mp4")]
    [InlineData("World Series of Sonarr - 2010x15 - 2010x16 - HD TV.mkv")]
    [InlineData("The Series US S01E01-E02 720p HDTV x264")]
    [InlineData("Series Title Season 01 Episode 05-06 720p")]
    [InlineData("The Series And The Code - S42 Ep10718 - Ep10722")]
    [InlineData("The Series And The Code - S42 Ep10688 - Ep10692")]
    [InlineData("Series.S01E02E03.1080p.BluRay.x264-DeBTViD")]
    [InlineData("grp-zoos01e11e12-1080p")]
    [InlineData("grp-zoo-s01e11e12-1080p")]
    [InlineData("Series Title.S6.E1.E2.Episode Name.1080p.WEB-DL")]
    [InlineData("Series Title.S6E1-E2.Episode Name.1080p.WEB-DL")]
    [InlineData("Series Title.S6E1-S6E2.Episode Name.1080p.WEB-DL")]
    [InlineData("Series Title.S6E1E2.Episode Name.1080p.WEB-DL")]
    [InlineData("Series Title.S6E1-E2-E3.Episode Name.1080p.WEB-DL")]
    [InlineData("Series Title.S6.E1E3.Episode Name.1080p.WEB-DL")]
    [InlineData("Series Title.S6.E1-E2.Episode Name.1080p.WEB-DL")]
    [InlineData("Series Title.S6.E1-S6E2.Episode Name.1080p.WEB-DL")]
    [InlineData("Series Title.S6.E1E2.Episode Name.1080p.WEB-DL")]
    [InlineData("Series Title.S6.E1-E2-E3.Episode Name.1080p.WEB-DL")]
    [InlineData("Series.Title.S05E01-E02.720p.5.1Ch.BluRay")]
    [InlineData("Series.Title.S05E01-02.720p.5.1Ch.BluRay")]
    [InlineData("S01E01-E03 - Episode Title.HDTV-720p")]
    [InlineData("1x01-x03 - Episode Title.HDTV-720p")]
    [InlineData("Series.Title.E07-E08.180612.1080p-NEXT")]
    [InlineData("Series Title? E11-E12 1080p HDTV AAC H.264-NEXT")]
    [InlineData("The Series Title (2010) - [S01E01-02-03] - Episode Title")]
    [InlineData("[AqusiQ-TorrentS.pl]The.Name.of.the.Series.S01E05-06.PL.2160p-Ralf[shogho]")]
    [InlineData("[AgusiQ-TorrentS.pl] The.Name.of.the.Series.S01E05-E06.PL.1080i.Ralf [jans12]")]
    [InlineData("The.Name.of.the.Series.S01E05-6.PL.1080p.WEBRip.x264-666")]
    [InlineData("Series Title - S15E06-07 - City Sushi HDTV-720p")]
    [InlineData("Series Title - S01E01-02-03 - Episode Title HDTV-720p")]
    [InlineData("Series Title - [02x01x02] - Episode 1")]
    [InlineData("Series Title - [02x01-x02] - Episode 1")]
    [InlineData("Series Title - [02x01-02] - Episode 1")]
    [InlineData("Series Title (2011) - S01E23-E24 - ...E i nuovi orizzonti [HDTV 360p] [ITA].mkv")]
    [InlineData("The Series Title! - S01E01-02-03")]
    [InlineData("Series Title! (2013) - S04E44-E45 - Il 200 spettacolare episodio da narcisisti!")]
    [InlineData("Series Title! (2013) - S04E44-E45 - Il 200 spettacolare episodio da narcisisti! [NetflixHD 720p HEVC] [ITA+ENG].mkv")]
    [InlineData("13 Series Se.1 afl.2-3-4 [VTM]")]
    [InlineData("Series T Se.3 afl.3 en 4")]
    [InlineData("Series Title (S15E06-08) City Sushi")]
    [InlineData("Series Title (S05E06-08 of 24) City Sushi")]
    [InlineData("Series Title (S05E06-08_of_24) City Sushi")]
    [InlineData("Series Title (S5E6-8.of.24) City Sushi")]
    [InlineData("Series Title (S5E6-8.of.XX) City Sushi")]
    [InlineData("Босх: Спадок (S2E1-4) / Series: Legacy (S2E1-4) (2023) WEB-DL 1080p Ukr/Eng | sub Eng")]
    [InlineData("Босх: Спадок / Series: Legacy / S2E1-4 of 10 (2023) WEB-DL 1080p Ukr/Eng | sub Eng")]
    [InlineData("Series Title - S26E96-97-98-99-100 - Episode 5931 + Episode 5932 + Episode 5933 + Episode 5934 + Episode 5935")]
    [InlineData("Series falls - Temporada 1 [HDTV][Cap.111_120]")]
    [InlineData("Series.S101E01-E02.Some.Title.Name.1080p.WEB-DL.AAC2.0.H.264-Release")]
    [InlineData("Series.2006.S101E01-E02.Some.Title.Name.1080p.WEB-DL.AAC2.0.H.264-Release")]
    [InlineData("[AL] Series.Title.S04E17-28.Part.2.German.DL.DTS.1080p.BluRay.x264-Pudding.GerSub-sama")]
    [InlineData("[AL] Series.Title.S04E01-16.German.DL.DTS.1080p.BluRay.x264-Pudding.GerSub-sama")]
    [InlineData("30.Series.Season.04.HDTV.XviD-DIMENSION")]
    [InlineData("Sonarr.and.Series.S02.720p.x264-DIMENSION")]
    [InlineData("The.Series.US.S03.720p.x264-DIMENSION")]
    [InlineData("Series.of.Sonarr.S03.720p.BluRay-CLUE\\REWARD")]
    [InlineData("Series Time S02 720p HDTV x264 CRON")]
    [InlineData("Series.2021.S04.iNTERNAL.DVDRip.XviD-VCDVaULT")]
    [InlineData("Series Five 0 S01 720p WEB DL DD5 1 H 264 NT")]
    [InlineData("30 Series S03 WS PDTV XviD FUtV")]
    [InlineData("The Series Season 4 WS PDTV XviD FUtV")]
    [InlineData("Series Season 1 720p WEB DL DD 5 1 h264 TjHD")]
    [InlineData("The Series Season4 WS PDTV XviD FUtV")]
    [InlineData("Series S 01 720p WEB DL DD 5 1 h264 TjHD")]
    [InlineData("Series Confidential   Season 3")]
    [InlineData("Series.S01.720p.WEBDL.DD5.1.H.264-NTb")]
    [InlineData("Series.Makes.It.Right.S02.720p.HDTV.AAC5.1.x265-NOGRP")]
    [InlineData("My.Series.S2014.720p.HDTV.x264-ME")]
    [InlineData("Series.Saison3.VOSTFR.HDTV.XviD-NOTAG")]
    [InlineData("Series.SAISON.1.VFQ.PDTV.H264-ACC-ROLLED")]
    [InlineData("Series Title - Series 1 (1970) DivX")]
    [InlineData("SeriesTitle.S03.540p.AMZN.WEB-DL.DD+2.0.x264-RTN")]
    [InlineData("Series.Title.S01.576p.BluRay.DD5.1.x264-HiSD")]
    [InlineData("Series.Stagione.3.HDTV.XviD-NOTAG")]
    [InlineData("Series No More S01 2023 1080p WEB-DL AVC AC3 2.0 Dual Audio -ZR-")]
    [InlineData("Series Title / S1E1-8 of 8 [2024, WEB-DL 1080p] + Original + RUS")]
    [InlineData("Series Title / S2E1-16 of 16 [2022, WEB-DL] RUS")]
    [InlineData("[hchcsen] Mobile Series 00 S01 [BD Remux Dual Audio 1080p AVC 2xFLAC] (Kidou Senshi Gundam 00 Season 1)")]
    [InlineData("[HorribleRips] Mobile Series 00 S1 [1080p]")]
    [InlineData("[Zoombie] Series 100: Bucket List S01 [Web][MKV][h265 10-bit][1080p][AC3 2.0][Softsubs (Zoombie)]")]
    [InlineData("[GROUP] Series: Title (2023) (Season 1) [BDRip] [1080p Dual Audio HEVC 10 bits DDP] (serie) (Batch)")]
    [InlineData("[GROUP] Series: Title (2023) (Season 1) [BDRip] [1080p Dual Audio HEVC 10-bits DDP] (serie) (Batch)")]
    [InlineData("[GROUP] Series: Title (2023) (Season 1) [BDRip] [1080p Dual Audio HEVC 10-bit DDP] (serie) (Batch)")]
    [InlineData("Seriesless (2016/S01/WEB-DL/1080p/AC3 5.1/DUAL/SUB)")]
    [InlineData("Series (1994) - Temporada 10")]
    [InlineData("Series S01 1080p HMAX WEB-DL DD+ 5.1 H.265")]
    [InlineData("Series S01 1440p HMAX WEB-DL DD+ 5.1 H.265")]
    [InlineData("[Judas] Series E (Series J) (Season 04) [1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs] (Batch)")]
    [InlineData("1234 [Sezon 3] [1080p] [NF] [WEB-DL] [H264] [DDP5.1.Atmos-K83] [Serial Polski]")]
    [InlineData("1234 [Season 3] [1080p] [NF] [WEB-DL] [H264] [DDP5.1.Atmos-K83] [Serial Polski]")]
    [InlineData("1234 (2026) (S03) [MULTi] [1080p] [NF] [WEB-DL] [H.264] [DD5.1-RX] [Serial polski]")]
    [InlineData("Acropolis Series S05 EXTRAS DVDRip XviD RUNNER")]
    [InlineData("Punky Series S01 EXTRAS DVDRip XviD RUNNER")]
    [InlineData("Instant Series S03 EXTRAS DVDRip XviD OSiTV")]
    [InlineData("The.Series.S03.Extras.01.Deleted.Scenes.720p")]
    [InlineData("The.Series.S03.Extras.02.720p")]
    [InlineData("Series.to.Me.S03.SUBPACK.DVDRip.XviD-REWARD")]
    [InlineData("The.Series.S02.SUBPACK.DVDRip.XviD-REWARD")]
    [InlineData("Series.S11.SUBPACK.DVDRip.XviD-REWARD")]
    [InlineData("The.Series.2016.S02.Part.1.1080p.NF.WEBRip.DD5.1.x264-NTb")]
    [InlineData("The.Series.S07.Vol.1.1080p.NF.WEBRip.DD5.1.x264-NTb")]
    [InlineData("The.Series.S06.P1.1080p.Blu-Ray.10-Bit.Dual-Audio.TrueHD.x265-iAHD")]
    [InlineData("[Group] Series Title S03 - Vol.02 [BD 1080p FLAC]")]
    [InlineData("[Group] Series Title S03 Vol.02 [BD 1080p FLAC]")]
    [InlineData("[Group] Series Title S03 Part 2 [1080p]")]
    [InlineData("The Series S01-05 WS BDRip X264-REWARD-No Rars")]
    [InlineData("Series.Title.S01-S09.1080p.AMZN.WEB-DL.DDP2.0.H.264-NTb")]
    [InlineData("Series Title S01 - S07 BluRay 1080p x264 REPACK -SacReD")]
    [InlineData("Series Title Season 01-07 BluRay 1080p x264 REPACK -SacReD")]
    [InlineData("Series Title Season 01 - Season 07 BluRay 1080p x264 REPACK -SacReD")]
    [InlineData("Series Title Complete Series S01 S04 (1080p BluRay x265 HEVC 10bit AAC 5.1 Vyndros)")]
    [InlineData("Series Title S01 S04 (1080p BluRay x265 HEVC 10bit AAC 5.1 Vyndros)")]
    [InlineData("Series Title S01 04 (1080p BluRay x265 HEVC 10bit AAC 5.1 Vyndros)")]
    [InlineData("Series Title S01 S02 S03 S04")]
    [InlineData("Series Title S01 S03 S04")]
    [InlineData("Series Title S01 S04")]
    [InlineData("[Group] Series Title S01-04 (BD 1080p) [Dual Audio]")]
    [InlineData("[Group] Series Title S01-S04 (BD 1080p)")]
    [InlineData("Series Title 2011 04 18 Emma Roberts HDTV XviD BFF")]
    [InlineData("A Late Talk Show 2011 04 15 1080i HDTV DD5 1 MPEG2 TrollHD")]
    [InlineData("A.Late.Talk.Show.2010.10.11.Johnny.Knoxville.iTouch-MW")]
    [InlineData("A Late Talk Show - 2011-04-12 - Gov. Deval Patrick")]
    [InlineData("2011.01.10 - A Late Talk Show- HD TV.mkv")]
    [InlineData("2011.03.13 - A Late Talk Show - HD TV.mkv")]
    [InlineData("A Late Talk Show - 2011-06-16 - Larry David, \"Bachelorette\" Ashley Hebert, Pitbull with Ne-Yo")]
    [InlineData("2020.A.Late.Talk.Show.2012.16.02.PDTV.XviD-C4TV")]
    [InlineData("2020.A.Late.Talk.Show.2012.13.02.PDTV.XviD-C4TV")]
    [InlineData("2020.A.Late.Talk.Show.2011.12.02.PDTV.XviD-C4TV")]
    [InlineData("Series Title - 2013-10-30 - Episode Title (1) [HDTV-720p]")]
    [InlineData("The_Series_US_04.28.2014_hdtv.x264.Poke.mp4")]
    [InlineData("A.Late.Talk.Show.140722.720p.HDTV.x264-YesTV")]
    [InlineData("A_Late_Talk_Show_140722_720p_HDTV_x264-YesTV")]
    [InlineData("The Show Series 2015 02 09 WEBRIP s01e13")]
    [InlineData("Tree_Series_2018_06_22_Seth_Meyers_720p_HEVC_x265-MeGusta")]
    [InlineData("20161024- Exotic Payback.21x41_720.mkv")]
    [InlineData("2018-11-14.1080.all.mp4")]
    [InlineData("2019_08_20_1080_all.mp4")]
    [InlineData("Series and Title 20201013 Ep7432 [720p WebRip (x264)] [SUBS]")]
    [InlineData("Series Title (1955) - 1954-01-23 05 00 00 - Cottage for Sale.ts")]
    [InlineData("Series Title - 30-04-2024 HDTV 1080p H264 AAC")]
    [InlineData("Series On TitleClub E76 2024 08 08 1080p WEB H264-RnB96 [TJET]")]
    [InlineData("Series.Title.13.02.2025.1080i.HDTV.MPA2.0.H.264")]
    [InlineData("Series.2025.09.01.The.170.Million.Pound.Diamond.Scam.1080p.HDTV.H264-DEADPOOL'")]
    [InlineData("Series.2025.09.01.The.Million.Pound.Diamond.Scam.1080p.HDTV.H264-DEADPOOL'")]
    [InlineData("Series Title - 2026-07-03 - Episode Title 2026 [WEBDL-1080p]-RlsGrp")]
    [InlineData("Series Title - 2026-07-03 - Friday July 3 2026 [AMZN][WEBDL-1080p][EAC3 2.0][h264]-RlsGrp")]
    [InlineData("Series Title - 2026-07-03 - Recap 2025 Part 2 [WEBDL-1080p]-RlsGrp")]
    [InlineData("Series Title - 2026-07-03 - Look Back At 2025 Part.1")]
    [InlineData("Series.Title.2015.09.07.Part1.720p.HULU.WEBRip.AAC2.0.H.264-Sonarr")]
    [InlineData("Series.Title.2015.09.07.Part2.720p.HULU.WEBRip.AAC2.0.H.264-Sonarr")]
    [InlineData("Series.Title.2015.09.07.Part.1.720p.HULU.WEBRip.AAC2.0.H.264-Sonarr")]
    [InlineData("Series.Title.2015.09.07.Part.2.720p.HULU.WEBRip.AAC2.0.H.264-Sonarr")]
    [InlineData("Series 5th Mar 2025 1080 (Deep61)")]
    [InlineData("Series 31st Jan 2025 1080 (Deep61)")]
    [InlineData("Series 23rd Feb 2024 (Deep61)")]
    [InlineData("[SubDESU]_Show_One_07_(1280x720_x264-AAC)_[6B7FD717]")]
    [InlineData("[Chihiro]_Show!!_-_06_[848x480_H.264_AAC][859EEAFA]")]
    [InlineData("[Commie]_Some_Anime_Show_-_11_[65F220B4]")]
    [InlineData("[Underwater]_Some_Anime_Show_-_12_(720p)_[5C7BC4F9]")]
    [InlineData("[Commie]_Some_Anime_Show_-_15_[E76552EA]")]
    [InlineData("[HorribleSubs]_Some_Anime_Show_-_33_[720p]")]
    [InlineData("[HorribleSubs]_Some_Anime_Show_-_145_[720p]")]
    [InlineData("[HorribleSubs] Some Anime Show - 13 [1080p].mkv")]
    [InlineData("[Doremi].Some.Anime.Show.8.Go!.31.[1280x720].[C65D4B1F].mkv")]
    [InlineData("[K-F] Some Anime Show 214")]
    [InlineData("[K-F] Some Anime Show S10E14 214")]
    [InlineData("[K-F] Some Anime Show 10x14 214")]
    [InlineData("[K-F] Some Anime Show 214 10x14")]
    [InlineData("Some Anime Show - 031 - The Resolution to Kill [Lunar].avi")]
    [InlineData("Some Anime Show - 031 - The Resolution to Kill [Lunar]")]
    [InlineData("[ACX]Some Anime Show 01 Role Play [Kosaka] [9C57891E].mkv")]
    [InlineData("[SFW-sage] Some Anime Show S3 - 12 [720p][D07C91FC]")]
    [InlineData("Some_Anime_Show_e66_time_is_money_part_one_marking_time")]
    [InlineData("[Underwater-FFF] No Series Title No Life - 01 (720p) [27AAA0A0].mkv")]
    [InlineData("[FroZen] Series Title - 23 [DVD][7F6170E6]")]
    [InlineData("[Commie] Series Title - 32 [0BA19D5B]")]
    [InlineData("[Doki]Series Title - 07 (1280x720 Hi10P AAC) [80AF7DDE]")]
    [InlineData("[HorribleSubs] Series Title - 32 [480p]")]
    [InlineData("[CR] Series Title - 004 [480p][48CE2D0F]")]
    [InlineData("[Chibiki] Series Title!! - 42 [360p][7A4FC77B]")]
    [InlineData("[HorribleSubs] Series Title - 32 [1080p]")]
    [InlineData("[HorribleSubs] Series Title! S2 - 07 [720p]")]
    [InlineData("[DeadFish] Series Title - 09v2 [720p][AAC]")]
    [InlineData("[Underwater-FFF] Series Title - 01 (720p) [27AAA0A0]")]
    [InlineData("[S-T-D] Series Title! - 06 (1280x720 10bit AAC) [59B3F2EA].mkv")]
    [InlineData("Series Title - 010 (720p) [27AAA0A0].mkv")]
    [InlineData("Initial_Series_Title - 01 DVD - Central Anime")]
    [InlineData("Initial_Series_Title_-_01(DVD)_-_(Central_Anime)[5AF6F1E4].mkv")]
    [InlineData("Initial_Series_Title_-_02(DVD)_-_(Central_Anime)[0CA65F00].mkv")]
    [InlineData("Initial_Series_Title - 03 DVD - Central Anime")]
    [InlineData("Initial_Series_Title_-_03(DVD)_-_(Central_Anime)[629BD592].mkv")]
    [InlineData("Initial_Series_Title - 14 DVD - Central Anime")]
    [InlineData("Initial_Series_Title_-_14(DVD)_-_(Central_Anime)[0183D922].mkv")]
    [InlineData("[ChihiroDesuYo].Series.Title.-.09.1280x720.10bit.AAC.[24CCE81D]")]
    [InlineData("Series Title - 001 - Fairy Tail")]
    [InlineData("Series Title - 049 - The Day of Fated Meeting")]
    [InlineData("Series Title - 050 - Special Request Watch Out for the Guy You Like!")]
    [InlineData("Series Title - 099 - Natsu vs. Gildarts")]
    [InlineData("Series Title - 100 - Mest")]
    [InlineData("[Exiled-Destiny] Series Title Ep01 (D2201EC5).mkv")]
    [InlineData("[Commie] Series Title - 23 [5396CA24].mkv")]
    [InlineData("[FFF] Series Title - 01 [1FB538B5].mkv")]
    [InlineData("[Hatsuyuki]Series_Title-01[1280x720][122E6EF8]")]
    [InlineData("[CBM]_Series_Title_-_11_-_511_Kinderheim_[6C70C4E4].mkv")]
    [InlineData("[HorribleSubs] Series Title 2 - 05 [720p].mkv")]
    [InlineData("[Commie] Series Title 2 - 05 [FCE4D070].mkv")]
    [InlineData("[DRONE]Series.Title.100")]
    [InlineData("[RlsGrp]Series.Title.2010.S01E01.001.HDTV-720p.x264-DTS")]
    [InlineData("Series Title - 130 - Found You, Gohan! Harsh Training in the Kaioshin Realm! [Baaro][720p][5A1AD35B].mkv")]
    [InlineData("Series Title - 131 - A Merged Super-Warrior Is Born, His Name Is Gotenks!! [Baaro][720p][32E03F96].mkv")]
    [InlineData("[HorribleSubs] Series Title - 01 [1080p]")]
    [InlineData("[Jumonji-Giri]_[F-B]_Series_Title_Ep04_(0b0e2c10).mkv")]
    [InlineData("[Jumonji-Giri]_[F-B]_Series_Title_Ep08_(8246e542).mkv")]
    [InlineData("Knights Series Title - 01 [1080p 10b DTSHD-MA eng sub].mkv")]
    [InlineData("Series Title (2010) {01} Episode Title (1).hdtv-720p")]
    [InlineData("[HorribleSubs] Series Title - 20 [720p].mkv")]
    [InlineData("[Hatsuyuki] Series Title (2014) - 017 (115) [1280x720][B2CFBC0F]")]
    [InlineData("[Hatsuyuki] Series Title (2014) - 018 (116) [1280x720][C4A3B16E]")]
    [InlineData("Series Title (2014) - 39 (137) [v2][720p.HDTV][Unison Fansub]")]
    [InlineData("[HorribleSubs] Series Title 21 - 101 [480p].mkv")]
    [InlineData("[Cthuyuu].Series.Title.-.03.[720p.H264.AAC][8AD82C3A]")]
    [InlineData("[Cthuyuu] Series Title - 03 [720p H264 AAC][8AD82C3A]")]
    [InlineData("Series Title Episode 56 [VOSTFR V2][720p][AAC]-Mystic Z-Team")]
    [InlineData("[Mystic Z-Team] Series Title Episode 69 [VOSTFR_Finale][1080p][AAC].mp4")]
    [InlineData("[Shark-Raws] Series Title #957 (NBN 1280x720 x264 AAC).mp4")]
    [InlineData("Series Title EP06 720p x265 AOZ.mp4")]
    [InlineData("Series Title 2018 EP06 720p x265 AOZ.mp4")]
    [InlineData("Series Title 2018 06 720p x265 AOZ.mp4")]
    [InlineData("Series Title S03 - EP14 VOSTFR [1080p] [HardSub] Yass'Kun")]
    [InlineData("Series Title S3 -  15 VOSTFR [720p]")]
    [InlineData("A Series: RE S2 - Episode 4 VOSTFR (1080p)")]
    [InlineData("To Another Series III - Episode 5 VOSTFR (1080p)")]
    [InlineData("[Prout] Show;Title 0 - Episode 5 VOSTFR (BDRip 1920x1080 x264 FLAC)")]
    [InlineData("[BakedFish] Some Show [Anime] - 01 [720p][AAC].mp4")]
    [InlineData("Abc x Abc (2011) - 141 - Magician [KaiDubs] [1080p]")]
    [InlineData("Abc Abc 484 VOSTFR par Abc-Abc (1280*720) - version MQ")]
    [InlineData("Abc - Abc Abc Abc - 107 VOSTFR par Fansub-Miracle Sharingan (1920x1080) - HQ_Draft")]
    [InlineData("Abc Abc Abc Abc Episode 10 VOSTFR (1920x1080) Miracle Sharingan Fansub.MKV - Team - (� suivre)")]
    [InlineData("[Glenn] Series! 3 - 11 (1080p AAC)[C34B2B3B].mkv")]
    [InlineData("SeriesTitle.E1135.Lasst.den.Mond.am.Himmel.stehen.GERMAN.1080p.WEBRip.x264-Group")]
    [InlineData("SeriesTitle E1135 Lasst den Mond am Himmel stehen GERMAN 1080p WEBRip x264-Group")]
    [InlineData("SeriesTitle.E1206.In.seinen.Augen.2022.GERMAN.1080p.WEB.h264-Group")]
    [InlineData("SeriesTitle E1206 In seinen Augen 2022 GERMAN 1080p WEB h264-Group")]
    [InlineData("Series.Title.E195.Zurueck.auf.die.Grand.Line.UNCUT.German.Dubbed.1999.ANiME.DVDRiP.XviD-Group")]
    [InlineData("[HorribleSubs] Series 100 - 07 [1080p].mkv")]
    [InlineData("[HorribleSubs] Series 100 S2 - 07 [1080p].mkv")]
    [InlineData("[abc] Adventure Series: 30 [Web][MKV][h264][720p][AAC 2.0][abc]")]
    [InlineData("[XKsub] Series Title S2 [05][HEVC-10bit 1080p AAC][CHS&CHT&JPN]")]
    [InlineData("[Cheetah-Raws] Super Long Anime - 1000 (YTV 1280x720 x264 AAC)")]
    [InlineData("[DameDesuYo] Another Anime With Special Naming (Season 2) - 33 (1280x720 10bit EAC3) [42A12A76].mkv")]
    [InlineData("[SubsPlease] Anime Title 300-nen, With Even More Title - 01 (1080p) [8DE44442]")]
    [InlineData("[Chihiro] Anime Title 300-nen, With Even More Title 02 [720p Hi10P AAC][031FA533]")]
    [InlineData("[BakeSubs] 86 - 01 [1080p][D40A9E55].mkv")]
    [InlineData("Anime Title the Final - 09 (2021) [SubsPlease] [WEBRip] [HD 1080p]")]
    [InlineData("Anime Title S21 999")]
    [InlineData("Anime Title S21 1000")]
    [InlineData("[HatSubs] Anime Title 1004 [E63F2984].mkv")]
    [InlineData("Anime Title 100 S3 - 01 (1080p) [5A493522]")]
    [InlineData("[SubsPlease] Anime Title 100 S3 - 01 (1080p) [5A493522]")]
    [InlineData("[CameEsp] Another Anime 100 - Another 100 Anime - 01 [720p][ESP-ENG][mkv]")]
    [InlineData("[SubsPlease] Another Anime 100 - Another 100 Anime - 01 (1080p) [4E6B4518].mkv")]
    [InlineData("Some show 69. Blm (29.10.2023) 1080p WebDL #turkseed")]
    [InlineData("Soap opera 01 BLM(01.11.2023) 1080p HDTV AC3 x264 TURG")]
    [InlineData("Turkish show 60.Bolum (31.01.2023) 720p WebDL AAC H.264 - TURG")]
    [InlineData("Different show 1. Bölüm (23.10.2023) 720p WebDL AAC H.264 - TURG")]
    [InlineData("Dubbed show 79.BLM Sezon Finali(25.06.2023) 720p WEB-DL AAC2.0 H.264-TURG")]
    [InlineData("Exclusive BLM Documentary with no false positives EP03.1080p.AAC.x264")]
    [InlineData("[SubsPlease] Title de Series S2 - 03 (540p) [63501322]")]
    [InlineData("[Naruto-Kun.Hu] Dr Series S3 - 21 [1080p]")]
    [InlineData("[Naruto-Kun.Hu] Series Title - 12 [1080p].mkv")]
    [InlineData("[Naruto-Kun.Hu] Anime Triangle - 08 [1080p].mkv")]
    [InlineData("[Mystic Z-Team] Series Title Super - Episode 013 VF - Non-censuré [720p].mp4")]
    [InlineData("Series Title Kai Episodio 13 Audio Latino")]
    [InlineData("Series_Title_2_[01]_[AniLibria_TV]_[WEBRip_1080p]")]
    [InlineData("[SubsPlease] Series Title - 100 Years Quest - 01 (1080p) [1107F3A9].mkv")]
    [InlineData("[SubsPlease] Series Title 100 Years Quest - 01 (1080p) [1107F3A9].mkv")]
    [InlineData("[Dae-P9] Anime Series - 05 - S01E05 - Marrying by Contesting (BD 1080p HEVC FLAC AAC) [Dual Audio] [5BCD56B8]")]
    [InlineData("[Kaleido-subs] Animation - 12 (S01E12) - (WEB 1080p HEVC x265 10-bit E-AC3 2.0) [1ADD8F6D]")]
    [InlineData("[DeadFish] Another Anime Show - 01 - Special [BD][720p][AAC]")]
    [InlineData("[DeadFish] Another Anime Show - 01 - OVA [BD][720p][AAC]")]
    [InlineData("[DeadFish] Another Anime Show - 01 - OVD [BD][720p][AAC]")]
    [InlineData("[ANBU-AonE]_SeriesTitle_26-27_[F224EF26].avi")]
    [InlineData("[Doutei] Some Good, Anime Show - 01-12 [BD][720p-AAC]")]
    [InlineData("Series Title (2010) - 01-02-03 - Episode Title (1) HDTV-720p")]
    [InlineData("[RlsGrp] Series Title (2010) - S01E01-02-03 - 001-002-003 - Episode Title HDTV-720p v2")]
    [InlineData("[RlsGrp] Series Title (2010) - S01E01-02 - 001-002 - Episode Title HDTV-720p v2")]
    [InlineData("Series Title (2010) - S01E01-02 (001-002) - Episode Title (1) HDTV-720p v2 [RlsGrp]")]
    [InlineData("[HorribleSubs] Some Anime Show!! (01-25) [1080p] (Batch)")]
    [InlineData("Some Anime Show (2011) Episode 99-100 [1080p] [Dual.Audio] [x265]")]
    [InlineData("Some Anime Show 1-13 (English Dub) [720p]")]
    [InlineData("Series.Title.Ep01-12.Complete.English.AC3.DL.1080p.BluRay.x264")]
    [InlineData("[Judas] Some Anime Show 091-123 [1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs]")]
    [InlineData("[Judas] Some Anime Show - 091-123 [1080p][HEVC x265 10bit][Dual-Audio][Multi-Subs]")]
    [InlineData("[HorribleSubs] Some Anime Show 01 - 119 [1080p] [Batch]")]
    [InlineData("[Erai-raws] Series Title! - 01~10 [1080p][Multiple Subtitle]")]
    [InlineData("[Erai-raws] Series-Title! 2 - 01~10 [1080p][Multiple Subtitle]")]
    [InlineData("[Erai-raws] Series Title! - 01 ~ 10 [1080p][Multiple Subtitle]")]
    [InlineData("[Erai-raws] Series-Title! 2 - 01 ~ 10 [1080p][Multiple Subtitle]")]
    [InlineData("Series_Title_2_[01-05]_[AniLibria_TV]_[WEBRip_1080p]")]
    [InlineData("[Moxie] One Series - The Country (892-916) (BD Remux 1080p AAC FLAC) [Dual Audio]")]
    [InlineData("[HatSubs] One Series (1017-1088) (WEB 1080p)")]
    [InlineData("[HatSubs] One Series 1017-1088 (WEB 1080p)")]
    [InlineData("[Group] Title S01 - 01-12 [1080p]")]
    [InlineData("[Group] Title S01 - 01-02 [1080p]")]
    [InlineData("[Vivid] Some Anime Show S01 [Web][MKV][h264 10-bit][1080p][AAC 2.0]")]
    [InlineData("[Group] Title S01 12 13 [1080p]")]
    [InlineData("[Group] Title S03 10 11 [1080p]")]
    [InlineData("[Group] Title S1 05 [1080p]")]
    [InlineData("Anime, Title? | Japanse Anime, Title? [Season 1 + EXTRA] [BD 1080p x265 HEVC OPUS] [Dual-Audio]")]
    [InlineData("[Judas] Japanse Anime, Title (Anime, Title?) (Season 1) [1080p][HEVC x265 10bit][Multi-Subs] (Batch)")]
    [InlineData("The.Big.Series.Leader.Part.2.DSR.XviD-SYS")]
    [InlineData("kill-roy-was-here-e07-720p")]
    [InlineData("Series and Show 2012 Part 1 REPACK 720p HDTV x264 2HD")]
    [InlineData("Series Show.2016.E04.Power.720p.WEB-DL.DD5.1.H.264-MARS")]
    [InlineData("It's a Series Title.E56.190121.720p-NEXT.mp4")]
    [InlineData("My Only Series Title.E37.190120.1080p-NEXT.mp4")]
    [InlineData("Series.E191.190121.720p-NEXT.mp4")]
    [InlineData("The Series Title Challenge.E932.190120.720p-NEXT.mp4")]
    [InlineData("백번의 추억.E12.251019.1080p-F1RST")]
    [InlineData("나는 SOLO <나는 솔로>.E223.251015.1080p-F1RST")]
    [InlineData("나는 SOLO 그 후, 사랑은 계속된다(나솔사계).E135.251016.1080p-F1RST")]
    public void Parse_picks_the_episode_path_for_episode_names(string release)
    {
        var parsed = ReleaseParser.Parse(release);
        new ReleaseExpectation(release, parsed)
            .Equal("IsEpisode", true, parsed?.IsEpisode)
            .Verify();
    }

    [Theory]
    [InlineData("S07E23 .avi ", 7, 23)]
    [InlineData("The.Series.S01E13.x264-CtrlSD", 1, 13)]
    [InlineData("The Series S02E01 HDTV XviD 2HD", 2, 1)]
    [InlineData("The Series S05E11 PROPER HDTV XviD 2HD", 5, 11)]
    [InlineData("The Series Show S02E08 HDTV x264 FTP", 2, 8)]
    [InlineData("The.Series.2011.S02E01.WS.PDTV.x264-TLA", 2, 1)]
    [InlineData("The.Series.2011.S02E01.WS.PDTV.x264-REPACK-TLA", 2, 1)]
    [InlineData("The Series S01E04 DSR x264 2HD", 1, 4)]
    [InlineData("The Series S01E04 Series Death Train DSR x264 MiNDTHEGAP", 1, 4)]
    [InlineData("The Series S11E03 has no periods or extension HDTV", 11, 3)]
    [InlineData("The.Series.S04E05.HDTV.XviD-LOL", 4, 5)]
    [InlineData("The.Series.S02E15.avi", 2, 15)]
    [InlineData("The.Series.S02E15.xvid", 2, 15)]
    [InlineData("The.Series.S02E15.divx", 2, 15)]
    [InlineData("The.Series.S03E06.HDTV-WiDE", 3, 6)]
    [InlineData("Series.S10E27.WS.DSR.XviD-2HD", 10, 27)]
    [InlineData("The.Series.S01E13.NTSC.x264-CtrlSD", 1, 13)]
    [InlineData("The.Series.S03E06.DVDRip.XviD-WiDE", 3, 6)]
    [InlineData("The.Series.S03E06.DVD.Rip.XviD-WiDE", 3, 6)]
    [InlineData("The.Series.S01E10.The.Leviathan.480p.WEB-DL.x264-mSD", 1, 10)]
    [InlineData("The.Series.S04E10.Glee.Actually.480p.WEB-DL.x264-mSD", 4, 10)]
    [InlineData("The.Series.S02E04.480p.WEB.DL.nSD.x264-NhaNc3", 2, 4)]
    [InlineData("The.Series.S01E08.Das.geloeschte.Ich.German.Dubbed.DL.AmazonHD.x264-TVS", 1, 8)]
    [InlineData("The.Series.S01E04.Rod.Trip.mit.meinem.Onkel.German.DL.NetflixUHD.x264", 1, 4)]
    [InlineData("Series.Title.S13E11.Ausgebacken.German.AmazonSD.h264-4SF", 13, 11)]
    [InlineData("SERIES.S03E01-06.DUAL.XviD.Bluray.AC3-REPACK.-HELLYWOOD.avi", 3, 1)]
    [InlineData("SERIES.S03E01-06.DUAL.BDRip.XviD.AC3.-HELLYWOOD", 3, 1)]
    [InlineData("SERIES.S03E01-06.DUAL.BDRip.X-viD.AC3.-HELLYWOOD", 3, 1)]
    [InlineData("SERIES.S03E01-06.DUAL.BDRip.AC3.-HELLYWOOD", 3, 1)]
    [InlineData("SERIES.S03E01-06.DUAL.BDRip.XviD.AC3.-HELLYWOOD.avi", 3, 1)]
    [InlineData("SERIES.S03E01-06.DUAL.XviD.Bluray.AC3.-HELLYWOOD.avi", 3, 1)]
    [InlineData("The.Series.S01E05.480p.BluRay.DD5.1.x264-HiSD", 1, 5)]
    [InlineData("Adventures.of.Sonic.the.Hedgehog.S01E01.Best.Hedgehog.480i.DD.2.0.AVC.REMUX-FraMeSToR", 1, 1)]
    [InlineData("The.Series.S02E10.480p.HULU.WEBRip.x264-Puffin", 2, 10)]
    [InlineData("The.Series.S10E14.Techs.And.Balances.480p.AE.WEBRip.AAC2.0.x264-SEA", 10, 14)]
    [InlineData("The.Series.S01E05.576p.BluRay.DD5.1.x264-HiSD", 1, 5)]
    [InlineData("Series - S01E01 - Title [HDTV]", 1, 1)]
    [InlineData("Series - S01E01 - Title [HDTV-720p]", 1, 1)]
    [InlineData("The Series S04E87 REPACK 720p HDTV x264 aAF", 4, 87)]
    [InlineData("The.Series.S02E15.720p", 2, 15)]
    [InlineData("S07E23 - [HDTV-720p].mkv ", 7, 23)]
    [InlineData("Series - S22E03 - MoneyBART - HD TV.mkv", 22, 3)]
    [InlineData("S07E23.mkv ", 7, 23)]
    [InlineData("The.Series.S08E05.720p.HDTV.X264-DIMENSION", 8, 5)]
    [InlineData("The.Series.S02E15.mkv", 2, 15)]
    [InlineData("The.Series.S01E08.Tourmaline.Nepal.720p.HDTV.x264-DHD", 1, 8)]
    [InlineData("The.Series.US.S12E17.HR.WS.PDTV.X264-DIMENSION", 12, 17)]
    [InlineData("The Series S01E07 - Motor zmen (CZ)[TvRip][HEVC][720p]", 1, 7)]
    [InlineData("The.Series.S05E06.720p.HDTV.x264-FHD", 5, 6)]
    [InlineData("Under the Series S01E10 Let the Sonarr Begin 1080p", 1, 10)]
    [InlineData("Series.S07E01.ARE.YOU.1080P.HDTV.X264-QCF", 7, 1)]
    [InlineData("Series.S07E01.ARE.YOU.1080P.HDTV.x264-QCF", 7, 1)]
    [InlineData("Series.S07E01.ARE.YOU.1080P.HDTV.proper.X264-QCF", 7, 1)]
    [InlineData("Series - S01E01 - Title [HDTV-1080p]", 1, 1)]
    [InlineData("Series S01E07 - Sonarr zmen (CZ)[TvRip][HEVC][1080p]", 1, 7)]
    [InlineData("Super.Seires.Go.S01E02.Depths.of.Sonarr.1080i.HDTV.DD5.1.H.264-NOGRP", 1, 2)]
    [InlineData("My Title - S01E01 - EpTitle [HEVC 4k DTSHD-MA-6ch]", 1, 1)]
    [InlineData("My Title - S01E01 - EpTitle [HEVC-4k DTSHD-MA-6ch]", 1, 1)]
    [InlineData("My Title - S01E01 - EpTitle [4k HEVC DTSHD-MA-6ch]", 1, 1)]
    [InlineData("Series S01E04 Mexicos Death Train 720p WEB DL", 1, 4)]
    [InlineData("Series Five 0 S02E21 720p WEB DL DD5 1 H 264", 2, 21)]
    [InlineData("Series S04E22 720p WEB DL DD5 1 H 264 NFHD", 4, 22)]
    [InlineData("Series - S11E06 - D-Yikes! - 720p WEB-DL.mkv", 11, 6)]
    [InlineData("The.Series.S02E15.720p.WEB-DL.DD5.1.H.264-SURFER", 2, 15)]
    [InlineData("S07E23 - [WEBDL].mkv ", 7, 23)]
    [InlineData("Series S04E22 720p WEB-DL DD5.1 H264-EbP.mkv", 4, 22)]
    [InlineData("Da.Series.Shows.S02E04.720p.WEB.DL.nSD.x264-NhaNc3", 2, 4)]
    [InlineData("Series.Miami.S04E25.720p.iTunesHD.AVC-TVS", 4, 25)]
    [InlineData("Series.S06E23.720p.WebHD.h264-euHD", 6, 23)]
    [InlineData("Series.S01E08.Das.geloeschte.Ich.German.DD51.Dubbed.DL.720p.AmazonHD.x264-TVS", 1, 8)]
    [InlineData("Series.Polo.S01E11.One.Hundred.Sonarrs.2015.German.DD51.DL.720p.NetflixUHD.x264.NewUp.by.Wunschtante", 1, 11)]
    [InlineData("into.the.Series.s03e16.h264.720p-web-handbrake.mkv", 3, 16)]
    [InlineData("Series.S01E01.The.Sonarr.Principle.720p.WEB-DL.DD5.1.H.264-BD", 1, 1)]
    [InlineData("Series.S03E05.Griebnitzsee.German.720p.MaxdomeHD.AVC-TVS", 3, 5)]
    [InlineData("Series.Title.S04E13.960p.WEB-DL.AAC2.0.H.264-squalor", 4, 13)]
    [InlineData("Series.Title.S01E01.Erste.Begegnungen.German.DD51.Synced.DL.720p.HBOMaxHD.AVC-TVS", 1, 1)]
    [InlineData("Series.Title.S01E05.Tavora.greift.an.German.DL.720p.DisneyHD.h264-4SF", 1, 5)]
    [InlineData("Series.Title.S04E01.720p.WEBRip.AAC2.0.x264-NFRiP", 4, 1)]
    [InlineData("Series.Title.S01E07.A.Prayer.For.Mad.Sweeney.720p.AMZN.WEBRip.DD5.1.x264-NTb", 1, 7)]
    [InlineData("Series.Title.S07E01.A.New.Home.720p.DSNY.WEBRip.AAC2.0.x264-TVSmash", 7, 1)]
    [InlineData("Series S09E03 1080p WEB DL DD5 1 H264 NFHD", 9, 3)]
    [InlineData("Two and a Half Developers of the Series S10E03 1080p WEB DL DD5 1 H 264 NFHD", 10, 3)]
    [InlineData("Series.S08E01.1080p.WEB-DL.DD5.1.H264-NFHD", 8, 1)]
    [InlineData("Its.Always.Sonarrs.Fault.S08E01.1080p.WEB-DL.proper.AAC2.0.H.264", 8, 1)]
    [InlineData("This is an Easter Egg S10E03 1080p WEB DL DD5 1 H 264 REPACK NFHD", 10, 3)]
    [InlineData("Series.S04E09.Swan.Song.1080p.WEB-DL.DD5.1.H.264-ECI", 4, 9)]
    [InlineData("The.Big.Easter.Theory.S06E11.The.Sonarr.Simulation.1080p.WEB-DL.DD5.1.H.264", 6, 11)]
    [InlineData("Sonarr's.Baby.S01E02.Night.2.[WEBDL-1080p].mkv", 1, 2)]
    [InlineData("Series Title S06E08 1080p WEB h264-EXCLUSIVE", 6, 8)]
    [InlineData("Series Title S06E08 No One PROPER 1080p WEB DD5 1 H 264-EXCLUSIVE", 6, 8)]
    [InlineData("Series Title S06E08 No One PROPER 1080p WEB H 264-EXCLUSIVE", 6, 8)]
    [InlineData("The.Series.S25E21.Pay.No1.1080p.WEB-DL.DD5.1.H.264-NTb", 25, 21)]
    [InlineData("Series.S01E08.Das.geloeschte.Ich.German.DD51.Dubbed.DL.1080p.AmazonHD.x264-TVS", 1, 8)]
    [InlineData("Series.S01E08.Pro.Gamer.1440p.BKPL.WEB-DL.H.264-LiGHT", 1, 8)]
    [InlineData("Series.Title.S04E11.Teddy's.Choice.FHD.1080p.Web-DL", 4, 11)]
    [InlineData("Series.S04E03.The.False.Bride.1080p.NF.WEB.DDP5.1.x264-NTb[rartv]", 4, 3)]
    [InlineData("Series.Title.S02E02.This.Year.Will.Be.Different.1080p.AMZN.WEB...", 2, 2)]
    [InlineData("Series.Title.S02E02.This.Year.Will.Be.Different.1080p.AMZN.WEB.", 2, 2)]
    [InlineData("Series Title - S01E11 2020 1080p Viva MKV WEB", 1, 11)]
    [InlineData("Series.Title.S02E02.This.Year.Will.Be.Different.1080p.WEB.H 265", 2, 2)]
    [InlineData("Series.Title.S01E01.Erste.Begegnungen.German.DD51.Synced.DL.1080p.HBOMaxHD.AVC-TVS", 1, 1)]
    [InlineData("Series.Title.S01E05.Tavora.greift.an.German.DL.1080p.DisneyHD.h264-4SF", 1, 5)]
    [InlineData("Series.Title.S02E04.German.Dubbed.DL.AAC.1080p.WEB.AVC-GROUP", 2, 4)]
    [InlineData("The.Series.S25E21.Pay.No1.1080p.WEB-DL.DD5.1.H.266-GROUP", 25, 21)]
    [InlineData("Series.Title.S04E01.1080p.WEB.H266-GROUP", 4, 1)]
    [InlineData("Series.Title.S04E01.iNTERNAL.1080p.WEBRip.x264-QRUS", 4, 1)]
    [InlineData("Series.Title.S07E20.1080p.AMZN.WEBRip.DDP5.1.x264-ViSUM ac3.(NLsub)", 7, 20)]
    [InlineData("Series.Title.S03E09.1080p.NF.WEBRip.DD5.1.x264-ViSUM", 3, 9)]
    [InlineData("The Series 42 S09E13 1.54 GB WEB-RIP 1080p Dual-Audio 2019 MKV", 9, 13)]
    [InlineData("Series.Title.2019.S02E07.Chapter.15.The.Believer.4Kto1080p.DSNYP.Webrip.x265.10bit.EAC3.5.1.Atmos.GokiTAoE", 2, 7)]
    [InlineData("House.of.Sonarr.AK.s05e13.4K.UHD.WEB.DL", 5, 13)]
    [InlineData("House.of.Sonarr.AK.s05e13.UHD.4K.WEB.DL", 5, 13)]
    [InlineData("Series.Title.S02E02.This.Year.Will.Be.Different.2160p.WEB.H.265", 2, 2)]
    [InlineData("Series.Title.S02E04.German.Dubbed.DL.AAC.2160p.DV.HDR.WEB.HEVC-GROUP", 2, 4)]
    [InlineData("Series S01E01.2160P AMZN WEBRIP DD2.0 HI10P X264-TROLLUHD", 1, 1)]
    [InlineData("JUST ADD SONARR S01E01.2160P AMZN WEBRIP DD2.0 X264-TROLLUHD", 1, 1)]
    [InlineData("The.Man.In.The.Series.S01E01.2160p.AMZN.WEBRip.DD2.0.Hi10p.X264-TrollUHD", 1, 1)]
    [InlineData("The Man In the Series S01E01 2160p AMZN WEBRip DD2.0 Hi10P x264-TrollUHD", 1, 1)]
    [InlineData("House.of.Sonarr.AK.S05E08.Chapter.60.2160p.NF.WEBRip.DD5.1.x264-NTb.NLsubs", 5, 8)]
    [InlineData("SERIES.S03E01-06.DUAL.Bluray.AC3.-HELLYWOOD.avi", 3, 1)]
    [InlineData("Series - S01E03 - Come Fly With Me - 720p BluRay.mkv", 1, 3)]
    [InlineData("The Big Series.S03E01.The Sonarr Can Opener.m2ts", 3, 1)]
    [InlineData("Series.S01E02.Chained.Sonarr.[Bluray720p].mkv", 1, 2)]
    [InlineData("SERIES.S03E01-06.DUAL.Blu-ray.AC3.-HELLYWOOD.avi", 3, 1)]
    [InlineData("SERIES.S03E01-06.DUAL.720p.Blu-ray.AC3.-HELLYWOOD.avi", 3, 1)]
    [InlineData("Series.Galaxy.S01E01.33.720p.HDDVD.x264-SiNNERS.mkv", 1, 1)]
    [InlineData("The.Series.S01E07.RERIP.720p.BluRay.x264-DEMAND", 1, 7)]
    [InlineData("Adventures.of.Sonic.the.Hedgehog.S01E01.Best.Hedgehog.720p.DD.2.0.AVC.REMUX-FraMeSToR", 1, 1)]
    [InlineData("Series - S01E03 - Come Fly With Me - 1080p BluRay.mkv", 1, 3)]
    [InlineData("Sonarr.Of.Series.S02E13.1080p.BluRay.x264-AVCDVD", 2, 13)]
    [InlineData("Series.S01E02.Chained.Heat.[Bluray1080p].mkv", 1, 2)]
    [InlineData("SERIES.S03E01-06.DUAL.1080p.Blu-ray.AC3.-HELLYWOOD.avi", 3, 1)]
    [InlineData("Planet.Series.S01E11.Code.Deep.1080p.HD-DVD.DD.VC1-TRB", 1, 11)]
    [InlineData("Fall.Of.The.Release.Groups.S02E13.1080p.BDLight.x265-AVCDVD", 2, 13)]
    [InlineData("Series Title S01E01 1080p BluRay DD 5 1 Atmos x266-GROUP", 1, 1)]
    [InlineData("Series!!! on ICE - S01E12[JP BD Remux][ENG subs]", 1, 12)]
    [InlineData("Series.Title.S01E08.The.Well.BluRay.1080p.AVC.DTS-HD.MA.5.1.REMUX-FraMeSToR", 1, 8)]
    [InlineData("Series.Title.S03E01.The.Calm.1080p.DTS-HD.MA.5.1.AVC.REMUX-FraMeSToR", 3, 1)]
    [InlineData("Adventures.of.Sonic.the.Hedgehog.S01E01.Best.Hedgehog.1080p.DD.2.0.AVC.REMUX-FraMeSToR", 1, 1)]
    [InlineData("Series.Title.US.s05e13.4K.UHD.Bluray", 5, 13)]
    [InlineData("Series.Title.US.s05e13.UHD.4K.Bluray", 5, 13)]
    [InlineData("Series Title S01E01 2160p UHD BluRay H266-GROUP", 1, 1)]
    [InlineData("Series!!! on ICE - S01E12[JP BD 2160p Remux][ENG subs]", 1, 12)]
    [InlineData("Series.Title.S01E08.The.Sonarr.BluRay.2160p.AVC.DTS-HD.MA.5.1.REMUX-FraMeSToR", 1, 8)]
    [InlineData("Adventures.of.Sonic.the.Hedgehog.S01E01.Best.Hedgehog.2160p.DD.2.0.AVC.REMUX-FraMeSToR", 1, 1)]
    [InlineData("POI S02E11 1080i HDTV DD5.1 MPEG2-TrollHD", 2, 11)]
    [InlineData("How I Met Your Developer S01E18 Nothing Good Happens After Sonarr 720p HDTV DD5.1 MPEG2-TrollHD", 1, 18)]
    [InlineData("The Series S01E11 The Finals 1080i HDTV DD5.1 MPEG2-TrollHD", 1, 11)]
    [InlineData("Series.Title.S07E11.1080i.HDTV.DD5.1.MPEG2-NTb.ts", 7, 11)]
    [InlineData("Game of Series S04E10 1080i HDTV MPEG2 DD5.1-CtrlHD.ts", 4, 10)]
    [InlineData("Series.Title.S02E05.1080i.HDTV.DD2.0.MPEG2-NTb.ts", 2, 5)]
    [InlineData("Show - S03E01 - Episode Title Raw-HD.ts", 3, 1)]
    [InlineData("Series.Title.S10E09.Title.1080i.UPSCALE.HDTV.DD5.1.MPEG2-zebra", 10, 9)]
    [InlineData("The.Series.S02E15", 2, 15)]
    [InlineData("Series.Title.S01E01.webm", 1, 1)]
    [InlineData("Series.Title.S01E01.The.Web.MT-dd", 1, 1)]
    [InlineData("Series.Title.S10E09.Episode.Title.1080i.UPSCALE.HDTV.DD5.1.MPEG2-zebra", 10, 9)]
    [InlineData("Series.Title.S01E01.Bluray720p", 1, 1)]
    [InlineData("Series.Title.S01E01.Bluray1080p", 1, 1)]
    [InlineData("Series.Title.S01E01.Bluray2160p", 1, 1)]
    [InlineData("Series.Title.S01E01.848x480.dvd", 1, 1)]
    [InlineData("Series.Title.S01E01.848x480.Bluray", 1, 1)]
    [InlineData("Series.Title.S01E01.1280x720.Bluray", 1, 1)]
    [InlineData("Series.Title.S01E01.1920x1080.Bluray", 1, 1)]
    [InlineData("Series.Title.S01E01.848x480", 1, 1)]
    [InlineData("Series.Title.S01E01.1280x720", 1, 1)]
    [InlineData("Series.Title.S01E01.1920x1080", 1, 1)]
    [InlineData("Series.Title.2011.S02E01.WS.PDTV.x264-REPACK-TLA", 2, 1)]
    [InlineData("Series.Title.S01E01.Bluray", 1, 1)]
    [InlineData("Series.Title.S01E01.HD.TV", 1, 1)]
    [InlineData("Series.Title.S01E01.SD.TV", 1, 1)]
    [InlineData("Series.Title.S01E02.Chained.Heat.mkv", 1, 2)]
    [InlineData("Series - S01E01 - Title.avi", 1, 1)]
    [InlineData("Series.Title.S01E02.Chained.Heat.1080p.mkv", 1, 2)]
    [InlineData("Series - S01E01 - Title.720p.avi", 1, 1)]
    [InlineData("Series Title S04E87 REPACK 720p HDTV x264 aAF", 4, 87)]
    [InlineData("Series.Title.S04E87.REPACK.720p.HDTV.x264-aAF", 4, 87)]
    [InlineData("Series.Title.S04E87.REPACK2.720p.HDTV.x264-aAF", 4, 87)]
    [InlineData("Series.Title.S04E87.PROPER.720p.HDTV.x264-aAF", 4, 87)]
    [InlineData("Series.Title.S01E07.RERIP.720p.BluRay.x264-DEMAND", 1, 7)]
    [InlineData("Series.Title.S01E07.RERIP2.720p.BluRay.x264-DEMAND", 1, 7)]
    [InlineData("[MTBB] Series Title - S02E02 - 027 - Episode Title [WEBDL-1080p v2][x264][AAC]", 2, 2)]
    [InlineData("Series Title - S01E01 - Pilot.sub", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot.en.sub", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot.EN.sub", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot.eng.sub", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot.ENG.sub", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot.English.sub", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot.english.sub", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot.en.cc.sub", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot.en.sdh.sub", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot.en.forced.sub", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot.en.sdh.forced.sub", 1, 1)]
    [InlineData("Spanish Killroy was Here S02E02 Flodden 720p AMZN WEB-DL DDP5 1 H 264-NTb", 2, 2)]
    [InlineData("Title.the.Spanish.Series.S02E02.1080p.WEB.H264-CAKES", 2, 2)]
    [InlineData("Title.the.Spanish.Series.S02E06.Field.of.Cloth.of.Gold.1080p.AMZN.WEBRip.DDP5.1.x264-NTb", 2, 6)]
    [InlineData("Title.the.Series.2009.S01E14.Germany.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Italian.Series.S01E01.The.Family.720p.HDTV.x264-FTP", 1, 1)]
    [InlineData("Title.the.Italy.Series.S02E01.720p.HDTV.x264-TLA", 2, 1)]
    [InlineData("Series.Title.S01E01.SUBFRENCH.1080p.WEB.x264-GROUP", 1, 1)]
    [InlineData("[Judas] Series Japanese Name (Series English Name) - S02E10 [1080P][HEVC x256 10bit][Eng-Subs] (Weekly)", 2, 10)]
    [InlineData("Detektiv.Conan.1996.S33E39.Ger.Eng.Sub.AAC.1080p.WEB.H264-WeebPinn", 33, 39)]
    [InlineData("Detektiv.Conan.1996.S33E39.Ger.Eng.Fre.Sub.AAC.1080p.WEB.H264-WeebPinn", 33, 39)]
    [InlineData("Detektiv.Conan.1996.S33E39.Ger.Fre.Eng.Sub.AAC.1080p.WEB.H264-WeebPinn", 33, 39)]
    [InlineData("Detektiv.Conan.1996.S33E39.Ger.Eng.Spa.Sub.AAC.1080p.WEB.H264-WeebPinn", 33, 39)]
    [InlineData("Detektiv.Conan.1996.S33E39.Ger.Spa.Eng.Sub.AAC.1080p.WEB.H264-WeebPinn", 33, 39)]
    [InlineData("Title.the.Series.2009.S01E14.English.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.French.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Spanish.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.German.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.S04E15.Brotherly.Love.GERMAN.DUBBED.WS.WEBRiP.XviD.REPACK-TVP", 4, 15)]
    [InlineData("The Series Title - S02E16 - Kampfhaehne - mkv - by Videomann", 2, 16)]
    [InlineData("Series.Title.S01E03.Ger.Dub.AAC.1080p.WebDL.x264-TKP21", 1, 3)]
    [InlineData("Series.Title.S02E10.Episode.Title.German.DL.BD.x264-TVS", 2, 10)]
    [InlineData("Series.Title.S02E09.EpisodeName.German.DL.BD.x264-TVS", 2, 9)]
    [InlineData("Series.Title.S01E10.EpisodeName.SwissGerman.WEB-DL.h264-RlsGrp", 1, 10)]
    [InlineData("Title.the.Series.2009.S01E14.Italian.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Danish.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Dutch.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Japanese.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Icelandic.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.S01E03.1080p.WEB-DL.DD5.1.H.264-SbR Icelandic", 1, 3)]
    [InlineData("Title.the.Series.2009.S01E14.Chinese.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Cantonese.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Mandarin.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Korean.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E08.2160p.WEB-DL.LAV.ENG", 1, 8)]
    [InlineData("Title.the.Series.S02E02.LATViAN.1080p.WEB.XviD-LOL", 2, 2)]
    [InlineData("Title.the.Series.2009.S01E14.Russian.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.S01E01.1080p.WEB-DL.Rus.Eng.TVKlondike", 1, 1)]
    [InlineData("Title.the.Series.2009.S01E14.Polish.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.PL.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.PLLEK.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.PL-LEK.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.LEKPL.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.LEK-PL.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.PLDUB.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.PL-DUB.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.DUBPL.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.DUB-PL.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Vietnamese.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Swedish.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Norwegian.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Finnish.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Turkish.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Portuguese.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.S01E01.FLEMISH.HDTV.x264-BRiGAND", 1, 1)]
    [InlineData("Title.the.Series.S03E13.Greek.PDTV.XviD-Ouzo", 3, 13)]
    [InlineData("Title.the.Series.2009.S01E14.HDTV.XviD.HUNDUB-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.HDTV.XviD.ENG.HUN-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.HDTV.XviD.HUN-LOL", 1, 14)]
    [InlineData("Title.the.Series.S05E01.WEBRip.x264.AC3.LT.EN-CNN", 5, 1)]
    [InlineData("Title.the.Series.​S07E11.​WEB Rip.​XviD.​Louige-​CZ.​EN.​5.​1", 7, 11)]
    [InlineData("Title.the.Series.2009.S01E14.Bulgarian.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.BGAUDIO.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.BG.AUDIO.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Series Title S01E01 Malayalam.1080p.WebRip.AVC.5.1-Rjaa", 1, 1)]
    [InlineData("Series Title S01E01 Malayalam DVDRip XviD 5.1 ESub MTR", 1, 1)]
    [InlineData("Series.Title.S01E01.DVDRip.1CD.Malayalam.Xvid.MP3 @Mastitorrents", 1, 1)]
    [InlineData("Title.the.Series.2022.S02E22.Slovak.HDTV.XviD-LOL", 2, 22)]
    [InlineData("Title.the.Series.2021.S01E11.HDTV.XviD.ENG.SK-LOL", 1, 11)]
    [InlineData("Title.the.Series.2009.S01E14.Brazilian.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Title.the.Series.2009.S01E14.Dublado.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Series.Title.S08E01.1080p.WEB.H264.Latino.YG", 8, 1)]
    [InlineData("TV Series S22E36 ROMANIAN 1080p WEB-DL AAC 2.0 H.264-RLSGRP", 22, 36)]
    [InlineData("Series S02E08 M+ WEBDL 1080p SPA-CAT DD5.1 SUBS x264", 2, 8)]
    [InlineData("Series.Title.S01E01.German.DL.1080p.BluRay.x264-RlsGrp", 1, 1)]
    [InlineData("Series.Title.S01E01.GERMAN.DL.1080P.WEB.H264-RlsGrp", 1, 1)]
    [InlineData("Series.Title.2023.S01E01.German.DL.EAC3.1080p.DSNP.WEB.H264-RlsGrp", 1, 1)]
    [InlineData("Series.Title.2023.S01E01.GERMAN.1080P.WEB-DL.H264-RlsGrp", 1, 1)]
    [InlineData("Series.Title.2023.S01E01.GERMAN.1080P.WEB.DL.H264-RlsGrp", 1, 1)]
    [InlineData("Series Title 2023 S01E01 GERMAN 1080P WEB DL H264-RlsGrp", 1, 1)]
    [InlineData("Series.Title.2023.S01E01.GERMAN.1080P.WEBDL.H264-RlsGrp", 1, 1)]
    [InlineData("Series.Title.S01E01.Original.1080P.WEB.H264-RlsGrp", 1, 1)]
    [InlineData("Series.Title.S01E01.Orig.1080P.WEB.H264-RlsGrp", 1, 1)]
    [InlineData("series.title.s01e01.2023.[Azerbaijan.Dubbed].1080p.WEB-DLRip.TeeWee", 1, 1)]
    [InlineData("Series s02e04 (2023) [Azerbaijani Dubbed] 1080p WEB-DLRip TeeWee", 2, 4)]
    [InlineData("series.title.s01e01.2023.[Uzbekistan.Dubbed].1080p.WEB-DLRip.TeeWee", 1, 1)]
    [InlineData("Sweet.Series.S02E08.2023.[Uzbek.Dubbed].1080p.WEB-DLRip.TeeWee", 2, 8)]
    [InlineData("Title.the.Series.2009.S01E14.Urdu.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].testtitle.default.eng.forced.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].eng.default.testtitle.forced.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].default.eng.testtitle.forced.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].testtitle.forced.eng.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].eng.forced.testtitle.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].forced.eng.testtitle.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].testtitle.default.fra.forced.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].fra.default.testtitle.forced.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].default.fra.testtitle.forced.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].testtitle.forced.fra.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].fra.forced.testtitle.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].forced.fra.testtitle.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].default.forced.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].default.ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].ass", 1, 20)]
    [InlineData("Name (2020) - S01E20 - [AAC 2.0].testtitle.ass", 1, 20)]
    [InlineData("Series.2009.S01E14.English.HDTV.XviD-LOL", 1, 14)]
    [InlineData("Series 2009 S01E14 English HDTV XviD LOL", 1, 14)]
    [InlineData("The.Series.S03E115.DVDRip.XviD-OSiTV", 3, 115)]
    [InlineData("Series Title - S01E01 - Pilot [HTDV-480p]", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot [HTDV-720p]", 1, 1)]
    [InlineData("Series Title - S01E01 - Pilot [HTDV-1080p]", 1, 1)]
    [InlineData("The.Series.Title.S04E13.720p.WEB-DL.AAC2.0.H.264-Cyphanix", 4, 13)]
    [InlineData("Series.S02E01.720p.WEB-DL.DD5.1.H.264.mkv", 2, 1)]
    [InlineData("Series Title S01E01 Episode Title", 1, 1)]
    [InlineData("The Series Title S12E17 May 23, 2014.mp4", 12, 17)]
    [InlineData("Reizen Waes - S01E08 - Transistrië, Zuid-Ossetië en Abchazië SDTV.avi", 1, 8)]
    [InlineData("[ www.Torrenting.com ] - Series.S03E14.720p.HDTV.X264-DIMENSION", 3, 14)]
    [InlineData("Series S02E09 HDTV x264-2HD [eztv]-[rarbg.com]", 2, 9)]
    [InlineData("7s-Series-s02e01-720p.mkv", 2, 1)]
    [InlineData("The.Series.S09E13.720p.HEVC.x265-MeGusta-Pre", 9, 13)]
    [InlineData("Series Title - S01E01 - Episode Title [RlsGroup]", 1, 1)]
    [InlineData("Series.Title.S01E05.The-Aniversary.WEBDL-1080p.mkv", 1, 5)]
    [InlineData("Series.Title.S01E05.The-Aniversary.HDTV-1080p.mkv", 1, 5)]
    [InlineData("The.Series.S03E04.2160p.Amazon.WEBRip.DTS-HD.MA.5.1.x264", 3, 4)]
    [InlineData("SomeShow.S20E13.1080p.BluRay.DTS-X.MA.5.1.x264", 20, 13)]
    [InlineData("SomeShow.S20E13.1080p.BluRay.DTS-MA.5.1.x264", 20, 13)]
    [InlineData("SomeShow.S20E13.1080p.BluRay.DTS-ES.5.1.x264", 20, 13)]
    [InlineData("SomeShow.S20E13.1080p.Blu-Ray.DTS-ES.5.1.x264", 20, 13)]
    [InlineData("SomeShow.S20E13.1080p.Blu-Ray.DTS-ES.5.1.x264-ROUGH [PublicHD]", 20, 13)]
    [InlineData("SomeShow S01E168 1080p WEB-DL AAC 2.0 x264-Erai-raws", 1, 168)]
    [InlineData("The.Good.Series.S05E03.Series.of.Intelligence.1080p.10bit.AMZN.WEB-DL.DDP5.1.HEVC-Vyndros", 5, 3)]
    [InlineData("Series.Title.S01E09.1080p.DSNP.WEB-DL.DDP2.0.H.264-VARYG", 1, 9)]
    [InlineData("Series (1997) - S01E01-02 - Children of the Gods (Showtime) (1080p.BD.DD5.1.x265-TheSickle[TAoE])", 1, 1)]
    [InlineData("Series S01E01 VOSTFR 1080p WEB x265 EAC3 -Hveðrungr", 1, 1)]
    [InlineData("Series.S01E06.1080p.AMZN.WEB-DL.JPN.DDP2.0.H.264-ToonsHub.mkv-[N-Z-B]-xpost", 1, 6)]
    [InlineData("Show.Name.S01E01.1080p.WEB-DL.H264.Fight-BB.mkv", 1, 1)]
    [InlineData("The Show S08E09 The Series.1080p.AMZN.WEB-DL.x265.10bit.EAC3.6.0-Qman[UTR]", 8, 9)]
    [InlineData("The Show S03E07 Fire and Series[1080p x265 10bit S87 Joy]", 3, 7)]
    [InlineData("The Show (2016) - S02E01 - Soul Series #1 (1080p NF WEBRip x265 ImE)", 2, 1)]
    [InlineData("The Show (2020) - S02E03 - Fighting His Series(1080p ATVP WEB-DL x265 t3nzin)", 2, 3)]
    [InlineData("Series.Title.S01E09.1080p.DSNP.WEB-DL.DDP2.0.H.264-VARYG (Blue Lock, Multi-Subs)", 1, 9)]
    [InlineData("Series.Title (2014) S09E10 (1080p AMZN WEB-DL x265 HEVC 10bit DDP 5.1 Vyndros)", 9, 10)]
    [InlineData("Series Title S02E03 Title 4k to 1080p DSNP WEBrip x265 DDP 5 1 Releaser[SEV]", 2, 3)]
    [InlineData("Series Title (2005) - S01E01 - A Hard Day's Night (1080p DSNP WEB-DL x265 Garshasp).mkv", 1, 1)]
    [InlineData("Series Title (2015) - S02E04 - Smoke & Mirrors (1080p BluRay x265 Kappa).mkv", 2, 4)]
    [InlineData("Series Title (2020) - S02E03 - A Great Odyssey (1080p BluRay x265 Kappa).mkv", 2, 3)]
    [InlineData("Series Title (2019) - S01E01 - Episode 1 (1080p WEB-DL x265 Natty).mkv", 1, 1)]
    [InlineData("Series Title (1999) - S03E01-E02 - Mermaid Man and Barnacle Boy IV & Doing Time (1080p AMZN WEB-DL x265 RCVR).mkv", 3, 1)]
    [InlineData("Series Title (2021) - S01E02 - Here Goes Nothing (1080p WEB-DL x265 SAMPA).mkv", 1, 2)]
    [InlineData("Series Title (2021) - S01E01 - Aftermath (1080p DSNP WEB-DL x265 YOGI).mkv", 1, 1)]
    [InlineData("Series Title (2012) - S01E01 - Episode 1 (1080p BluRay x265 r00t).mkv", 1, 1)]
    [InlineData("Series Title - S01E01 - Girls Gone Wild Exposed (720p x265 EDGE2020).mkv", 1, 1)]
    [InlineData("Series.Title.S01E02.1080p.BluRay.Remux.AVC.FLAC.2.0-E.N.D", 1, 2)]
    [InlineData("Example (2013) S01E01 (1080p iP WEBRip x265 SDR AAC 2.0 English - DarQ)", 1, 1)]
    [InlineData("Series.Title.S08E03.720p.WEB.DL.AAC2.0.H.264.KCRT", 8, 3)]
    [InlineData("Series Title S02E05 2160p WEB-DL DV HDR ENG DDP5.1 Atmos H265 MP4-BEN THE MEN", 2, 5)]
    [InlineData("Series Title S02E05 2160p AMZN WEB-DL DV HDR10 PLUS DDP5 1 Atmos H265 MKV-BEN THE MEN-xpost", 2, 5)]
    [InlineData("Series.S01E05.1080p.WEB-DL.DDP5.1.H264-BEN.THE.MEN", 1, 5)]
    [InlineData("Series (2005) S21E12 (1080p AMZN WEB-DL x265 SDR DDP 5.1 English - Goki TAoE)", 21, 12)]
    [InlineData("Series (2022) S03E12 (1080p AMZN Webrip x265 10 bit EAC3 5 1 - Ainz)[TAoE]", 3, 12)]
    [InlineData("The.Series.1989.S00E65.1080p.DSNP.Webrip.x265.10bit.EAC3.5.1.Goki.TAoE", 0, 65)]
    [InlineData("Series (2008) S05E01 (1080p BDRip DDP2.0 x265) - Vialle", 5, 1)]
    [InlineData("The Series (2023) S03E01 (1080p DS4K NF Webrip DV HDR DDP5.1 x265) - Vialle.mkv", 3, 1)]
    [InlineData("The Show (2020) - S02E03 - Fighting His Series(1080p ATVP WEB-DL x265 t3nzinFAKE)", 2, 3)]
    [InlineData("The Series (2023) S03E01 (1080p DS4K NF Webrip DV HDR DDP5.1 x265) - VialleFAKE.mkv", 3, 1)]
    [InlineData("The Series (2025) S03E01 (1080p AMZN WEB-DL x265 SDR DDP 5.1 English - Celdra QxR)", 3, 1)]
    [InlineData("The Series (2025) - S03E01 - We're Already Here (1080p AMZN WEB-DL x265 Celdra)", 3, 1)]
    [InlineData("Series.Title.S02E04.720p.WEBRip.x264-SKGTV English", 2, 4)]
    [InlineData("Series.Title.S02E04.720p.WEBRip.x264-SKGTV_English", 2, 4)]
    [InlineData("Series.Title.S02E04.720p.WEBRip.x264-SKGTV.English", 2, 4)]
    [InlineData("Series.Title.S02E04.720p.WEB-DL.AAC2.0.H.264-EVL-RP", 2, 4)]
    [InlineData("Series.Title.S02E04.720p.WEB-DL.AAC2.0.H.264-EVL-RP-RP", 2, 4)]
    [InlineData("Series.Title.S02E04.720p.WEB-DL.AAC2.0.H.264-EVL-Obfuscated", 2, 4)]
    [InlineData("Series.Title.S04E04.720p.BluRay.x264-xHD-NZBgeek", 4, 4)]
    [InlineData("Series.Title.S05E11.720p.HDTV.X264-DIMENSION-NZBgeek", 5, 11)]
    [InlineData("Series.Title.S04E04.720p.BluRay.x264-xHD-1", 4, 4)]
    [InlineData("Series.Title.S05E11.720p.HDTV.X264-DIMENSION-1", 5, 11)]
    [InlineData("series.title.s40e11.jane.doe_sia.720p.hdtv.x264-w4f-sample.mkv", 40, 11)]
    [InlineData("The.Series.2017.S05E02.1080p.WEB-DL.DD5.1.H264-EVL-Scrambled", 5, 2)]
    [InlineData("Series.S01E08.Haunted.Hayride.720p.AMZN.WEBRip.DDP5.1.x264-NTb-postbot", 1, 8)]
    [InlineData("Series.S01E08.Haunted.Hayride.720p.AMZN.WEBRip.DDP5.1.x264-NTb-xpost", 1, 8)]
    [InlineData("Series.Title.S08E05.The.Forgotten.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb-Rakuv", 8, 5)]
    [InlineData("The.Series.S30E01.Devs.Not.Dead.1080p.AMZN.WEB-DL.DDP5.1.H264-QOQ-Rakuv02", 30, 1)]
    [InlineData("Lie.To.Developers.S01E13.720p.BluRay.x264-SiNNERS-Rakuvfinhel", 1, 13)]
    [InlineData("Who.is.Sonarr.S01E01.INTERNAL.720p.HDTV.x264-aAF-RakuvUS-Obfuscated", 1, 1)]
    [InlineData("Deadly.Development.S01E10.Sink.With.Code.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTG-WhiteRev", 1, 10)]
    [InlineData("The.Sonarr.Series.S09E12.Developers.REPACK.1080p.AMZN.WEB-DL.DD.5.1.H.264-CasStudio-BUYMORE", 9, 12)]
    [InlineData("2.Tired.Developers.S02E24.1080p.AMZN.WEBRip.DD5.1.x264-CasStudio-AsRequested", 2, 24)]
    [InlineData("Series.S04E11.Lines.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb-AlternativeToRequested", 4, 11)]
    [InlineData("Series.S16E04.Third.Wheel.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb-GEROV", 16, 4)]
    [InlineData("Series.and.Title.S10E06.Dev.n.Play.1080p.AMZN.WEB-DL.DDP5.1.H.264-NTb-Z0iDS3N", 10, 6)]
    [InlineData("Absolute.Series.S02E06.The.House.of.Sonarr.DVDRip.x264-MaG-Chamele0n", 2, 6)]
    [InlineData("The.Series.Title.S08E08.1080p.BluRay.x264-ROVERS-4P", 8, 8)]
    [InlineData("Series.Title.S01E02.720p.BluRay.X264-REWARD-4Planet", 1, 2)]
    [InlineData("Series.S01E01.Rites.of.Passage.1080p.BluRay.x264-DON-AlteZachen", 1, 1)]
    [InlineData("Series.Title.S04E06.Episode.Name.720p.WEB-DL.DD5.1.H.264-HarrHD-RePACKPOST", 4, 6)]
    [InlineData("[FFF] Series Title!! - S01E11 - Someday, With Sonarr", 1, 11)]
    [InlineData("[HorribleSubs] Series Title!! - S01E12 - Sonarr Going Well!!", 1, 12)]
    [InlineData("[Anime-Koi] Series Title - S01E06 - Guys From Sonarr", 1, 6)]
    [InlineData("[Anime-Koi] Series Title - S01E07 - A High-Grade Sonarr", 1, 7)]
    [InlineData("Some TV (1979) - S02E10 - Hart-Shaped Murder [SDTV][AAC 2.0][x264]", 2, 10)]
    [InlineData("Some TV (1979) - S02E10 - Hart-Shaped Murder [HDTV-480p][AAC 2.0][x264]", 2, 10)]
    [InlineData("Some TV (1979) - S02E10 - Hart-Shaped Murder [480p-HDTV][AAC 2.0][x264]", 2, 10)]
    public void Parses_sonarr_release_names_as_episodes(string release, int season, int episode)
    {
        var parsed = ReleaseParser.Parse(release);
        new ReleaseExpectation(release, parsed)
            .Equal("IsEpisode", true, parsed?.IsEpisode)
            .Equal("SeasonNumber", season, parsed?.SeasonNumber)
            .Equal("Contains episode", true, parsed?.EpisodeNumbers.Contains(episode))
            .Verify();
        ReleaseExpectation.ExpectNull(release, ReleaseParser.ParseMovie(release));
    }

    [Theory]
    [InlineData("Series S03E14 720p HDTV X264-DIMENSION", "Series", 0)]
    [InlineData("Series.S03E14.720p.HDTV.X264-DIMENSION", "Series", 0)]
    [InlineData("Series-S03E14-720p-HDTV-X264-DIMENSION", "Series", 0)]
    [InlineData("Series_S03E14_720p_HDTV_X264-DIMENSION", "Series", 0)]
    [InlineData("Series 2022 S03E14 720p HDTV X264-DIMENSION", "Series", 2022)]
    [InlineData("Series (2022) S03E14 720p HDTV X264-DIMENSION", "Series", 2022)]
    [InlineData("Series.2022.S03E14.720p.HDTV.X264-DIMENSION", "Series", 2022)]
    [InlineData("Series-2022-S03E14-720p-HDTV-X264-DIMENSION", "Series", 2022)]
    [InlineData("Series_2022_S03E14_720p_HDTV_X264-DIMENSION", "Series", 2022)]
    [InlineData("1234 S03E14 720p HDTV X264-DIMENSION", "1234", 0)]
    [InlineData("1234.S03E14.720p.HDTV.X264-DIMENSION", "1234", 0)]
    [InlineData("1234-S03E14-720p-HDTV-X264-DIMENSION", "1234", 0)]
    [InlineData("1234_S03E14_720p_HDTV_X264-DIMENSION", "1234", 0)]
    [InlineData("1234 2022 S03E14 720p HDTV X264-DIMENSION", "1234", 2022)]
    [InlineData("1234 (2022) S03E14 720p HDTV X264-DIMENSION", "1234", 2022)]
    [InlineData("1234.2022.S03E14.720p.HDTV.X264-DIMENSION", "1234", 2022)]
    [InlineData("1234-2022-S03E14-720p-HDTV-X264-DIMENSION", "1234", 2022)]
    [InlineData("1234_2022_S03E14_720p_HDTV_X264-DIMENSION", "1234", 2022)]
    [InlineData("[scnzbefnet][509103] 2.Developers.Series.S03E18.720p.HDTV.X264-DIMENSION", "2 Developers Series", 0)]
    public void Parses_series_title_and_year(string release, string title, int year)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        new ReleaseExpectation(release, parsed)
            .Equal("Title", title, parsed?.Title)
            .Equal("Year", year, parsed?.Year)
            .Verify();
    }

    [Theory]
    [InlineData("Series Title - 4x05 - Title", "seriestitle")]
    [InlineData("Series & Title - 4x05 - Title", "seriestitle")]
    [InlineData("Bad Format", "badformat")]
    [InlineData("Mad Series - Season 1 [Bluray720p]", "madseries")]
    [InlineData("Mad Series - Season 1 [Bluray1080p]", "madseries")]
    [InlineData("The Daily Series -", "thedailyseries")]
    [InlineData("The Series Bros. (2006)", "theseriesbros2006")]
    [InlineData("Series (2011)", "series2011")]
    [InlineData("Series Time S02 720p HDTV x264 CRON", "seriestime")]
    [InlineData("Series Title 0", "seriestitle0")]
    [InlineData("Series of the Day", "seriesday")]
    [InlineData("Series of the Day 2", "seriesday2")]
    [InlineData("[ www.Torrenting.com ] - Series.S03E14.720p.HDTV.X264-DIMENSION", "series")]
    [InlineData("www.Torrenting.com - Series.S03E14.720p.HDTV.X264-DIMENSION", "series")]
    [InlineData("Series S02E09 HDTV x264-2HD [eztv]-[rarbg.com]", "series")]
    [InlineData("Series.911.S01.DVDRip.DD2.0.x264-DEEP", "series 911")]
    [InlineData("www.Torrenting.org - Series.S03E14.720p.HDTV.X264-DIMENSION", "series")]
    public void Clean_title_matches_the_series_name(string release, string seriesName)
    {
        var parsed = ReleaseParser.ParseEpisode(release);
        var cleanTitle = parsed?.CleanTitle ?? TitleNormalizer.Clean(release);
        if (cleanTitle != TitleNormalizer.Clean(seriesName))
        {
            Assert.Fail($"Release <<{release}>>: CleanTitle expected <{TitleNormalizer.Clean(seriesName)}> but was <{cleanTitle}>");
        }
    }

    [Theory]
    [InlineData("76El6LcgLzqb426WoVFg1vVVVGx4uCYopQkfjmLe")]
    [InlineData("Vrq6e1Aba3U amCjuEgV5R2QvdsLEGYF3YQAQkw8")]
    [InlineData("TDAsqTea7k4o6iofVx3MQGuDK116FSjPobMuh8oB")]
    [InlineData("yp4nFodAAzoeoRc467HRh1mzuT17qeekmuJ3zFnL")]
    [InlineData("oxXo8S2272KE1 lfppvxo3iwEJBrBmhlQVK1gqGc")]
    [InlineData("dPBAtu681Ycy3A4NpJDH6kNVQooLxqtnsW1Umfiv")]
    [InlineData("password - \"bdc435cb-93c4-4902-97ea-ca00568c3887.337\" yEnc")]
    [InlineData("185d86a343e39f3341e35c4dad3f9959")]
    [InlineData("ba27283b17c00d01193eacc02a8ba98eeb523a76")]
    [InlineData("45a55debe3856da318cc35882ad07e43cd32fd15")]
    [InlineData("86420f8ee425340d8894bf3bc636b66404b95f18")]
    [InlineData("ce39afb7da6cf7c04eba3090f0a309f609883862")]
    [InlineData("THIS SHOULD NEVER PARSE")]
    [InlineData("Vh1FvU3bJXw6zs8EEUX4bMo5vbbMdHghxHirc.mkv")]
    [InlineData("0e895c37245186812cb08aab1529cf8ee389dd05.mkv")]
    [InlineData("08bbc153931ce3ca5fcafe1b92d3297285feb061.mkv")]
    [InlineData("185d86a343e39f3341e35c4dad3ff159")]
    [InlineData("ah63jka93jf0jh26ahjas961.mkv")]
    [InlineData("qrdSD3rYzWb7cPdVIGSn4E7")]
    [InlineData("QZC4HDl7ncmzyUj9amucWe1ddKU1oFMZDd8r0dEDUsTd")]
    [InlineData("abc.xyz.af6021c37f7852")]
    [InlineData("e096aeb3c2c0483a96f5b32fc6d10ff5")]
    [InlineData("_unpack e096aeb3c2c0483a96f5b32fc6d10ff5.mkv")]
    [InlineData("theseriestitle1618finale")]
    [InlineData("Series Title (2018) Complete 360p HDTV AAC H.264-NEXT")]
    [InlineData("Specials/Series - Episode Title (part 1)")]
    public void Does_not_parse_crap(string release)
    {
        ReleaseExpectation.ExpectNull(release, ReleaseParser.ParseEpisode(release));
    }

    [Theory]
    [InlineData("علم نف) أ.دعادل الأبيض ٢٠٢٤ ٣ ٣")]
    [InlineData("ror-240618_1007-1022-")]
    public void Parses_unknown_formats(string release)
    {
        new ReleaseExpectation(release, ReleaseParser.ParseEpisode(release)).Verify();
    }

    [Theory]
    [InlineData(32)]
    [InlineData(40)]
    public void Does_not_parse_random_hashes(int length)
    {
        const string charset = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var random = new Random(length);

        for (var i = 0; i < 500; i++)
        {
            var hash = new string(Enumerable.Range(0, length).Select(_ => charset[random.Next(charset.Length)]).ToArray());
            Assert.Null(ReleaseParser.ParseEpisode(hash));
        }
    }

    [Fact]
    public void Does_not_parse_md5_hashes()
    {
        var hash = "CRAPPY TEST SEED";

        for (var i = 0; i < 100; i++)
        {
            hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(hash)));
            Assert.Null(ReleaseParser.ParseEpisode(hash));
        }
    }
}

/// <summary>Collects every mismatch for one release so a failing case names the release and all wrong fields at once.</summary>
internal sealed class ReleaseExpectation(string release, ParsedRelease? parsed)
{
    private readonly List<string> _problems = parsed is null ? ["parser returned null"] : [];

    public ReleaseExpectation Equal<T>(string field, T expected, T actual)
    {
        if (parsed is not null && !EqualityComparer<T>.Default.Equals(expected, actual))
        {
            _problems.Add($"{field}: expected <{expected}> but was <{actual}>");
        }

        return this;
    }

    public ReleaseExpectation Sequence(string field, IEnumerable<int> expected, IEnumerable<int>? actual)
    {
        var expectedList = expected.ToList();
        var actualList = actual?.ToList() ?? [];

        if (parsed is not null && !expectedList.SequenceEqual(actualList))
        {
            _problems.Add($"{field}: expected [{string.Join(",", expectedList)}] but was [{string.Join(",", actualList)}]");
        }

        return this;
    }

    public static void ExpectNull(string release, ParsedRelease? parsed)
    {
        if (parsed is not null)
        {
            Assert.Fail($"Release <<{release}>>: expected no result but got {parsed}");
        }
    }

    public void Verify()
    {
        if (_problems.Count > 0)
        {
            Assert.Fail($"Release <<{release}>>: {string.Join("; ", _problems)}");
        }
    }
}
