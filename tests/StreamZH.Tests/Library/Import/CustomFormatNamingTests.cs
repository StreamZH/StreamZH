// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.CustomFormats;
using StreamZH.Library.Import;
using static StreamZH.Tests.CustomFormats.CustomFormatTestData;

namespace StreamZH.Tests.Library.Import;

/// <summary>The {Custom Formats} naming token, formatted like Radarr's.</summary>
public sealed class CustomFormatNamingTests
{
    private static readonly Movie Dune = new() { Title = "Dune: Part Two", Year = 2024 };

    private static readonly NamingFileInfo File = new()
    {
        Quality = new QualityModel(Quality.WebDl2160p),
        Languages = [Language.German, Language.English],
        ReleaseGroup = "GRP",
        SceneName = "Dune.Part.Two.2024.German.DL.2160p.WEB.DV.HDR.H265-GRP",
        CustomFormats =
        [
            Movie("DV", 1, Title(@"\bDV\b")),
            Movie("German DL", 2, Title("German")),
            Movie("HDR", 3, Title("HDR")) with { IncludeInRename = false },
            Movie("x265", 4, Title("H265")),
        ],
    };

    private static string Name(string format) =>
        new FileNameBuilder(new MediaManagementValues() with { StandardMovieFormat = format }).MovieFileName(Dune, File);

    [Theory]
    [InlineData("{Movie Title} {Custom Formats}", "Dune - Part Two DV German DL x265")]
    [InlineData("{Movie Title} {[Custom Formats]}", "Dune - Part Two [DV German DL x265]")]
    [InlineData("{Movie.Title}.{Custom.Formats}", "Dune.-.Part.Two.DV.German.DL.x265")]
    [InlineData("{Movie Title} {Custom Formats:-German DL,x265}", "Dune - Part Two DV")]
    [InlineData("{Movie Title} {Custom Formats:DV,x265}", "Dune - Part Two DV x265")]
    [InlineData("{Movie Title} {Custom Format:x265}", "Dune - Part Two x265")]
    [InlineData("{Movie Title} {Custom Format:HDR}", "Dune - Part Two")]
    [InlineData("{Movie Title} {CUSTOM FORMATS}", "Dune - Part Two DV GERMAN DL X265")]
    public void Custom_formats_token(string format, string expected) => Assert.Equal(expected, Name(format));

    [Fact]
    public void An_empty_list_leaves_no_trace()
    {
        var name = new FileNameBuilder(new MediaManagementValues() with { StandardMovieFormat = "{Movie Title} {[Custom Formats]} {Quality Full}" })
            .MovieFileName(Dune, File with { CustomFormats = [] });
        Assert.Equal("Dune - Part Two WEBDL-2160p", name);
    }

    [Fact]
    public void The_token_is_allowed_in_file_formats_only_and_filters_only_on_it()
    {
        Assert.Null(FileNameBuilder.ValidateFormat("{Movie Title} {Custom Formats:-x265}", NamingFormatKind.MovieFile));
        Assert.Null(FileNameBuilder.ValidateFormat("{Series Title} S{season:00}E{episode:00} {Custom Formats}", NamingFormatKind.StandardEpisode));
        Assert.NotNull(FileNameBuilder.ValidateFormat("{Movie Title} {Custom Formats}", NamingFormatKind.MovieFolder));
        Assert.NotNull(FileNameBuilder.ValidateFormat("{Movie Title} {Release Group:-x}", NamingFormatKind.MovieFile));
    }

    [Fact]
    public void File_custom_formats_come_from_the_release_name_and_the_stored_facts()
    {
        var formats = new[]
        {
            Movie("DV", 1, Title(@"\bDV\b")),
            Movie("Group GRP", 2, Spec(CustomFormatImplementations.ReleaseGroup, "^GRP$")),
            Movie("2160p", 3, Spec(CustomFormatImplementations.Resolution, 2160)),
            Movie("Bluray", 4, Spec(CustomFormatImplementations.Source, 9)),
        };

        var info = (File with { CustomFormats = [] }).WithCustomFormats(formats, MediaType.Movie, Language.English, 2024, "Dune - Part Two (2024).mkv", 20L << 30);
        Assert.Equal(["2160p", "DV", "Group GRP"], info.CustomFormats.Select(f => f.Name));
    }
}
