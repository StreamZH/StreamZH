// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.Import;

namespace StreamZH.Tests.Library.MediaFiles;

/// <summary>The {MediaInfo …} tokens of Radarr and Sonarr from ffprobe's analysis, title lengths, ellipses and {{Edition Tags}}.</summary>
public sealed class MediaInfoNamingTests
{
    private static readonly Movie Dune = new() { Title = "Dune: Part Two", Year = 2024, TmdbId = 693134, ImdbId = "tt15239678" };
    private static readonly Series Show = new() { Title = "Crystal Lake", Year = 2025, TvdbId = 1, TmdbId = 2 };

    private static readonly NamingFileInfo DvFile = new()
    {
        Quality = new QualityModel(Quality.Remux2160p),
        Languages = [Language.German, Language.English],
        ReleaseGroup = "GRP",
        VideoCodec = "HEVC",
        AudioCodec = "TrueHD Atmos",
        SceneName = Path.GetFileNameWithoutExtension(FfprobeParserTests.DvRelease),
        MediaInfo = FfprobeParserTests.DolbyVision(),
    };

    private static string Movie(string format, NamingFileInfo file) =>
        new FileNameBuilder(new MediaManagementValues() with { StandardMovieFormat = format }).MovieFileName(Dune, file);

    [Theory]
    [InlineData("{MediaInfo Simple}", "HEVC TrueHD Atmos")]
    [InlineData("{MediaInfo Full}", "HEVC TrueHD Atmos [DE+EN] [DE+EN]")]
    [InlineData("{MediaInfo VideoCodec}", "HEVC")]
    [InlineData("{MediaInfo VideoBitDepth}", "10")]
    [InlineData("{MediaInfo VideoDynamicRange}", "HDR")]
    [InlineData("{MediaInfo VideoDynamicRangeType}", "DV HDR10")]
    [InlineData("{MediaInfo AudioCodec}", "TrueHD Atmos")]
    [InlineData("{MediaInfo AudioChannels}", "7.1")]
    [InlineData("{MediaInfo AudioLanguages}", "[DE+EN]")]
    [InlineData("{MediaInfo AudioLanguages:DE}", "[DE]")]
    [InlineData("{MediaInfo AudioLanguages:EN+DE}", "[EN+DE]")]
    [InlineData("{MediaInfo AudioLanguages:DE+}", "[DE+--]")]
    [InlineData("{MediaInfo AudioLanguages:FR}", "")]
    [InlineData("{MediaInfo AudioLanguages:-DE}", "")] // only English left, which AudioLanguages leaves out
    [InlineData("{MediaInfo AudioLanguagesAll:-DE}", "[EN]")]
    [InlineData("{[MediaInfo AudioLanguages]}", "[DE+EN]")] // one pair of brackets
    [InlineData("{MediaInfo SubtitleLanguages}", "[DE+EN]")]
    [InlineData("{MediaInfo SubtitleLanguages:EN}", "[EN]")]
    [InlineData("{MediaInfo 3D}", "")]
    [InlineData("{[MediaInfo VideoDynamicRangeType]}", "[DV HDR10]")]
    [InlineData("{Mediainfo.AudioCodec}", "TrueHD.Atmos")]
    public void Tokens_from_the_dolby_vision_remux(string token, string expected) =>
        Assert.Equal(expected, Movie(token, DvFile));

    [Fact]
    public void Trash_movie_naming_with_media_info()
    {
        const string standard = "{Movie CleanTitle} {(Release Year)} - {{Edition Tags}} {[MediaInfo 3D]}{[Custom Formats]}{[Quality Full]}{[Mediainfo AudioCodec}{ Mediainfo AudioChannels]}{[MediaInfo VideoDynamicRangeType]}{[Mediainfo VideoCodec]}{-Release Group}";
        Assert.Equal("Dune Part Two (2024) - [Remux-2160p][TrueHD Atmos 7.1][DV HDR10][HEVC]-GRP", Movie(standard, DvFile));
        Assert.Equal("Dune Part Two (2024) - {IMAX} [Remux-2160p][TrueHD Atmos 7.1][DV HDR10][HEVC]-GRP", Movie(standard, DvFile with { Edition = "IMAX" }));

        const string anime = "{Movie CleanTitle} {(Release Year)} - {[Quality Full]}{[Mediainfo AudioCodec}{ Mediainfo AudioChannels]}{MediaInfo AudioLanguages}{[MediaInfo VideoDynamicRangeType]}[{Mediainfo VideoCodec }{MediaInfo VideoBitDepth}bit]{-Release Group}";
        var animeFile = new NamingFileInfo { Quality = new QualityModel(Quality.Bluray1080p), ReleaseGroup = "GRP", MediaInfo = FfprobeParserTests.Anime() };
        Assert.Equal("Dune Part Two (2024) - [Bluray-1080p][FLAC 2.0][JA+DE][HEVC 10bit]-GRP", Movie(anime, animeFile with { SceneName = FfprobeParserTests.AnimeRelease }));
    }

    [Fact]
    public void Without_media_info_the_release_name_is_used()
    {
        var file = new NamingFileInfo
        {
            Quality = new QualityModel(Quality.WebDl1080p), Languages = [Language.German, Language.English], VideoCodec = "x265", AudioCodec = "EAC3", Hdr = HdrFormat.DolbyVisionHdr10,
        };

        Assert.Equal("x265 EAC3", Movie("{MediaInfo Simple}", file));
        Assert.Equal("DV HDR10", Movie("{MediaInfo VideoDynamicRangeType}", file));
        Assert.Equal("HDR", Movie("{MediaInfo VideoDynamicRange}", file));
        Assert.Equal("[DE+EN]", Movie("{MediaInfo AudioLanguages}", file));
        Assert.Equal(string.Empty, Movie("{MediaInfo AudioLanguages}", file with { Languages = [Language.English] }));
        Assert.Equal(string.Empty, Movie("{MediaInfo AudioChannels}", file));
        Assert.Equal(string.Empty, Movie("{MediaInfo VideoBitDepth}", file));
    }

    [Fact]
    public void X264_file_tokens()
    {
        var file = new NamingFileInfo { Quality = new QualityModel(Quality.Bluray1080p), MediaInfo = FfprobeParserTests.X264() };

        Assert.Equal("x264 AC3", Movie("{MediaInfo Simple}", file));
        Assert.Equal("5.1", Movie("{MediaInfo AudioChannels}", file));
        Assert.Equal("8", Movie("{MediaInfo VideoBitDepth}", file));
        Assert.Equal(string.Empty, Movie("{MediaInfo VideoDynamicRangeType}", file));
        Assert.Equal("[DE+EN]", Movie("{MediaInfo AudioLanguages}", file));
    }

    [Theory]
    [InlineData("{MediaInfo AudioLanguages:DE}", null)]
    [InlineData("{MediaInfo AudioLanguages:-DE+EN}", null)]
    [InlineData("{MediaInfo SubtitleLanguages:DE}", null)]
    [InlineData("{MediaInfo Full}", null)]
    [InlineData("{Episode CleanTitle:90}", null)]
    [InlineData("{Episode Title:-30}", null)]
    [InlineData("{MediaInfo VideoCodec:DE}", "Only number formats")]
    [InlineData("{Quality Full:30}", "Only number formats")]
    public void Filters_and_lengths_are_validated(string token, string? problem)
    {
        var result = FileNameBuilder.ValidateFormat("{Series Title} - S{season:00}E{episode:00} " + token, NamingFormatKind.StandardEpisode);
        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.StartsWith(problem, result);
        }
    }

    [Theory]
    [InlineData("Who Are You...", "Crystal Lake - S01E01 - Who Are You... [WEBDL-1080p]")]
    [InlineData("Who Are You…", "Crystal Lake - S01E01 - Who Are You... [WEBDL-1080p]")]
    [InlineData("...And Then There Were None", "Crystal Lake - S01E01 - ...And Then There Were None [WEBDL-1080p]")]
    [InlineData("Wait... What?", "Crystal Lake - S01E01 - Wait... What! [WEBDL-1080p]")]
    [InlineData("Who Are You?", "Crystal Lake - S01E01 - Who Are You! [WEBDL-1080p]")]
    public void Episode_titles_keep_their_ellipsis(string title, string expected)
    {
        var builder = new FileNameBuilder(new MediaManagementValues() with { StandardEpisodeFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title} [{Quality Full}]" });
        var episode = new Episode { SeasonNumber = 1, EpisodeNumber = 1, Title = title };

        Assert.Equal(expected, builder.EpisodeFileName(Show, [episode], new NamingFileInfo { Quality = new QualityModel(Quality.WebDl1080p) }));
    }

    [Fact]
    public void Ellipsis_at_the_end_of_the_name_and_with_dots_as_separators()
    {
        var episode = new Episode { SeasonNumber = 1, EpisodeNumber = 2, Title = "The Night He Came Home..." };
        var file = new NamingFileInfo { Quality = new QualityModel(Quality.WebDl1080p) };

        var plain = new FileNameBuilder(new MediaManagementValues() with { StandardEpisodeFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title}" });
        Assert.Equal("Crystal Lake - S01E02 - The Night He Came Home...", plain.EpisodeFileName(Show, [episode], file));

        var dotted = new FileNameBuilder(new MediaManagementValues() with { StandardEpisodeFormat = "{Series.Title}.S{season:00}E{episode:00}.{Episode.Title}.{Quality.Title}" });
        Assert.Equal("Crystal.Lake.S01E02.The.Night.He.Came.Home....WEBDL-1080p", dotted.EpisodeFileName(Show, [episode], file));
    }

    [Theory]
    [InlineData("{Episode Title:10}", "The Nig...")]
    [InlineData("{Episode Title:-10}", "...me Home")]
    [InlineData("{Episode Title:90}", "The Night He Came Home")]
    [InlineData("{Episode CleanTitle:15}", "The Night He...")]
    public void Titles_can_be_cut_to_a_length_like_sonarr(string token, string expected)
    {
        var builder = new FileNameBuilder(new MediaManagementValues() with { StandardEpisodeFormat = "S{season:00}E{episode:00} " + token });
        var episode = new Episode { SeasonNumber = 1, EpisodeNumber = 1, Title = "The Night He Came Home" };

        Assert.Equal("S01E01 " + expected, builder.EpisodeFileName(Show, [episode], new NamingFileInfo()));
    }

    [Fact]
    public void Series_title_without_year()
    {
        var show = new Series { Title = "The Office (US) (2005)", Year = 2005 };
        var builder = new FileNameBuilder(new MediaManagementValues() with { SeriesFolderFormat = "{Series CleanTitleWithoutYear} {(Series Year)}" });

        Assert.Equal("The Office US (2005)", builder.SeriesFolderName(show));
    }

    [Fact]
    public void A_name_never_starts_with_dots()
    {
        var movie = new Movie { Title = "...And Justice for All", Year = 1979 };
        var builder = new FileNameBuilder(new MediaManagementValues() with { StandardMovieFormat = "{Movie Title} ({Release Year})", MovieFolderFormat = "{Movie Title} ({Release Year})" });

        Assert.Equal("And Justice for All (1979)", builder.MovieFileName(movie, new NamingFileInfo()));
        Assert.Equal("And Justice for All (1979)", builder.MovieFolderName(movie));
    }

    [Fact]
    public void Literal_double_braces_stay()
    {
        var builder = new FileNameBuilder(new MediaManagementValues() with { StandardMovieFormat = "{Movie Title} {{x}}" });

        Assert.Equal("Dune - Part Two {x}", builder.MovieFileName(Dune, new NamingFileInfo()));
    }
}
