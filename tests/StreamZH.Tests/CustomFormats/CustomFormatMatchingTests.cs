// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using static StreamZH.Tests.CustomFormats.CustomFormatTestData;
using Impl = StreamZH.Core.CustomFormats.CustomFormatImplementations;

namespace StreamZH.Tests.CustomFormats;

/// <summary>Custom format matching per condition type, with real TRaSH formats where they exist (Radarr/Sonarr semantics).</summary>
public sealed class CustomFormatMatchingTests
{
    private const long Gb = 1024L * 1024 * 1024;

    // ------------------------------------------------------------------ TRaSH formats

    [Theory]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-ZeroTwo", true)]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.WEB.H264-GRP", true)]
    [InlineData("Dune.Part.Two.2024.German.1080p.BluRay.x264-ZeroTwo", false)]
    [InlineData("Dune.Part.Two.2024.1080p.BluRay.x264-GRP", false)]
    public void German_DL_needs_German_and_the_original_language(string title, bool expected)
    {
        var format = Trash("radarr", "german-dl.json", 1);
        Assert.Equal(expected, Matches(format, MovieRelease(title)));
    }

    [Fact]
    public void German_DL_of_a_German_movie_needs_the_original_language_which_is_German()
    {
        var format = Trash("radarr", "german-dl.json", 1);

        // Original German: "German" alone is both conditions.
        Assert.True(Matches(format, MovieRelease("Das.Boot.1981.German.1080p.BluRay.x264-GRP", Language.German)));
    }

    [Theory]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-ZeroTwo", true)]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-TvR", true)]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.BluRay.REMUX.AVC-ZeroTwo", false)]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.WEB.H264-ZeroTwo", false)]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-OTHER", false)]
    public void German_Bluray_Tier_01_needs_a_listed_group_a_Bluray_source_and_no_remux(string title, bool expected)
    {
        var format = Trash("radarr", "german-bluray-tier-01.json", 2);
        Assert.Equal(expected, Matches(format, MovieRelease(title)));
    }

    [Theory]
    [InlineData("Dune.Part.Two.2024.PROPER.1080p.WEB.H264-GRP", true)]
    [InlineData("Dune.Part.Two.2024.REPACK.1080p.WEB.H264-GRP", true)]
    [InlineData("Dune.Part.Two.2024.REPACK2.1080p.WEB.H264-GRP", false)]
    [InlineData("Dune.Part.Two.2024.REAL.PROPER.1080p.WEB.H264-GRP", false)]
    [InlineData("Dune.Part.Two.2024.1080p.WEB.H264-GRP", false)]
    public void Repack_Proper_excludes_higher_repack_versions(string title, bool expected)
    {
        var format = Trash("radarr", "repack-proper.json", 3);
        Assert.Equal(expected, Matches(format, MovieRelease(title)));
    }

    [Theory]
    [InlineData("Dune.Part.Two.2024.1080p.WEB.x265-GRP", true)]
    [InlineData("Dune.Part.Two.2024.1080p.BluRay.HEVC-GRP", true)]
    [InlineData("Dune.Part.Two.2024.2160p.WEB.x265-GRP", false)]
    [InlineData("Dune.Part.Two.2024.1080p.WEB.x264-GRP", false)]
    public void X265_HD_matches_x265_below_2160p(string title, bool expected)
    {
        var format = Trash("radarr", "x265-hd.json", 4);
        Assert.Equal(expected, Matches(format, MovieRelease(title)));
    }

    [Theory]
    [InlineData("Dune.Part.Two.2024.2160p.WEB-DL.DV.H.265-GRP", true)]
    [InlineData("Dune.Part.Two.2024.2160p.WEBRip.DoVi.x265-GRP", true)]
    [InlineData("Dune.Part.Two.2024.2160p.WEB-DL.DV.HDR.H.265-GRP", false)]
    [InlineData("Dune.Part.Two.2024.2160p.BluRay.DV.x265-GRP", false)]
    [InlineData("Dune.Part.Two.2024.2160p.WEB-DL.DV.H.265-Flights", false)]
    [InlineData("Dune.Part.Two.2024.2160p.WEB-DL.H.265-GRP", false)]
    public void DV_without_HDR_fallback_combines_required_negated_and_optional_conditions(string title, bool expected)
    {
        var format = Trash("radarr", "dv-wo-hdr-fallback.json", 5);
        Assert.Equal(expected, Matches(format, MovieRelease(title)));
    }

    [Theory]
    [InlineData("Dune.Part.Two.2024.German.1080p.WEB.H264-GRP", true)]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.WEB.H264-GRP", false)]
    [InlineData("Dune.Part.Two.2024.1080p.WEB.H264-GRP", false)]
    public void Wrong_Language_matches_releases_without_the_original_language(string title, bool expected)
    {
        var format = Trash("radarr", "wrong-language.json", 6);
        Assert.Equal(expected, Matches(format, MovieRelease(title)));
    }

    [Theory]
    [InlineData("Show.S01.1080p.WEB-DL.H264-GRP", true, false)]
    [InlineData("Show.S01E01E02.1080p.WEB-DL.H264-GRP", false, true)]
    [InlineData("Show.S01E01.1080p.WEB-DL.H264-GRP", false, false)]
    public void Sonarr_release_type_formats(string title, bool seasonPack, bool multiEpisode)
    {
        var input = EpisodeRelease(title);
        Assert.Equal(seasonPack, Matches(Trash("sonarr", "season-pack.json", 10), input));
        Assert.Equal(multiEpisode, Matches(Trash("sonarr", "multi-episode.json", 11), input));
    }

    [Fact]
    public void Sonarr_formats_use_Sonarr_values()
    {
        var germanDl = Trash("sonarr", "german-dl.json", 12);
        var x265 = Trash("sonarr", "x265-hd.json", 13);

        Assert.True(Matches(germanDl, EpisodeRelease("Show.S01E01.German.DL.1080p.WEB.H264-GRP")));
        Assert.True(Matches(x265, EpisodeRelease("Show.S01E01.1080p.WEB.x265-GRP")));
        Assert.False(Matches(x265, EpisodeRelease("Show.S01E01.2160p.WEB.x265-GRP")));

        // A movie format never scores an episode, a series format never a movie.
        Assert.Empty(CustomFormatCalculator.Matching([Trash("radarr", "x265-hd.json", 4)], EpisodeRelease("Show.S01E01.1080p.WEB.x265-GRP")));
        Assert.Empty(CustomFormatCalculator.Matching([x265], MovieRelease("Dune.2021.1080p.WEB.x265-GRP")));
    }

    // ------------------------------------------------------------------ required / negate per group

    [Theory]
    [InlineData("Movie.2020.1080p.WEB.H264-AAA", true)]
    [InlineData("Movie.2020.1080p.WEB.H264-BBB", true)]
    [InlineData("Movie.2020.1080p.WEB.H264-CCC", false)]
    [InlineData("Movie.2020.720p.WEB.H264-AAA", false)]
    public void One_condition_per_type_is_enough_but_required_ones_must_match(string title, bool expected)
    {
        var format = Movie("Groups at 1080p", 1,
            Title("-AAA$"),
            Title("-BBB$"),
            Spec(Impl.Resolution, 1080, required: true));
        Assert.Equal(expected, Matches(format, MovieRelease(title)));
    }

    [Theory]
    [InlineData("Movie.2020.1080p.WEB.H264-AAA", false)]
    [InlineData("Movie.2020.1080p.WEB.H264-CCC", true)]
    public void A_negated_condition_matches_when_its_pattern_does_not(string title, bool expected)
    {
        var format = Movie("Not AAA", 1, Title("-AAA$", negate: true));
        Assert.Equal(expected, Matches(format, MovieRelease(title)));
    }

    [Fact]
    public void A_failing_required_condition_fails_the_format_even_when_another_of_its_type_matches()
    {
        var format = Movie("x", 1, Title("WEB"), Title("HDR", required: true));
        Assert.False(Matches(format, MovieRelease("Movie.2020.1080p.WEB.H264-AAA")));
        Assert.True(Matches(format, MovieRelease("Movie.2020.1080p.WEB.HDR.H265-AAA")));

        var evaluation = CustomFormatCalculator.Evaluate(format, MovieRelease("Movie.2020.1080p.WEB.H264-AAA"));
        Assert.Equal([true, false], evaluation.Specifications.Select(s => s.Matched));
    }

    [Fact]
    public void Regexes_ignore_case_invalid_ones_never_match_and_the_file_name_counts_too()
    {
        Assert.True(Matches(Movie("x", 1, Title(@"\bweb\b")), MovieRelease("Movie.2020.1080p.WEB.H264-AAA")));
        Assert.False(Matches(Movie("x", 1, Title("([")), MovieRelease("Movie.2020.1080p.WEB.H264-AAA")));
        Assert.True(Matches(Movie("x", 1, Title("([", negate: true)), MovieRelease("Movie.2020.1080p.WEB.H264-AAA")));

        var file = CustomFormatInput.ForFile(
            MediaType.Movie, "Movie (2020) WEBDL-1080p [HDR].mkv", "Movie.2020.1080p.WEB.H264-AAA", new QualityModel(Quality.WebDl1080p), [],
            Language.English, "AAA", null, 2020, 4 * Gb);
        Assert.True(Matches(Movie("x", 1, Title(@"\[HDR\]")), file));
        Assert.True(Matches(Movie("x", 1, Title(@"\.H264-AAA$")), file));
    }

    // ------------------------------------------------------------------ the other condition types

    [Fact]
    public void Release_group_and_edition()
    {
        var input = MovieRelease("Movie.2020.Directors.Cut.1080p.BluRay.x264-SPARKS");
        Assert.True(Matches(Movie("g", 1, Spec(Impl.ReleaseGroup, "^SPARKS$")), input));
        Assert.False(Matches(Movie("g", 1, Spec(Impl.ReleaseGroup, "^OTHER$")), input));
        Assert.True(Matches(Movie("e", 1, Spec(Impl.Edition, "director")), input));
        Assert.False(Matches(Movie("e", 1, Spec(Impl.Edition, "imax")), input));
        Assert.False(Matches(Movie("e", 1, Spec(Impl.Edition, "director")), MovieRelease("Movie.2020.1080p.BluRay.x264-SPARKS")));
    }

    [Theory]
    [InlineData("Movie.2020.1080p.BluRay.x264-GRP", 9, 0, 1080)]
    [InlineData("Movie.2020.1080p.BluRay.REMUX.AVC-GRP", 9, 5, 1080)]
    [InlineData("Movie.2020.1080p.WEB-DL.H264-GRP", 7, 0, 1080)]
    [InlineData("Movie.2020.2160p.WEBRip.x265-GRP", 8, 0, 2160)]
    [InlineData("Movie.2020.HDTV.720p.x264-GRP", 6, 0, 720)]
    public void Movie_source_modifier_and_resolution_use_Radarr_values(string title, int source, int modifier, int resolution)
    {
        var input = MovieRelease(title);
        Assert.True(Matches(Movie("s", 1, Spec(Impl.Source, source)), input));
        Assert.True(Matches(Movie("m", 1, Spec(Impl.QualityModifier, modifier)), input));
        Assert.True(Matches(Movie("r", 1, Spec(Impl.Resolution, resolution)), input));
        Assert.False(Matches(Movie("s", 1, Spec(Impl.Source, source == 9 ? 7 : 9)), input));
    }

    [Theory]
    [InlineData("Show.S01E01.1080p.WEB-DL.H264-GRP", 3)]
    [InlineData("Show.S01E01.1080p.WEBRip.x264-GRP", 4)]
    [InlineData("Show.S01E01.720p.HDTV.x264-GRP", 1)]
    [InlineData("Show.S01E01.1080p.BluRay.x264-GRP", 6)]
    [InlineData("Show.S01E01.1080p.BluRay.REMUX.AVC-GRP", 7)]
    public void Series_source_uses_Sonarr_values(string title, int source)
    {
        var format = Format(MediaType.Series, "s", 1, Spec(Impl.Source, source));
        Assert.True(Matches(format, EpisodeRelease(title)));
    }

    [Fact]
    public void Sonarr_gives_DVD_480_lines()
    {
        Assert.Equal(480, CustomFormatValues.Resolution(MediaType.Series, Quality.Dvd));
        Assert.Equal(0, CustomFormatValues.Resolution(MediaType.Movie, Quality.Dvd));
        Assert.Equal(2, CustomFormatValues.Source(MediaType.Series, Quality.RawHd));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(1, false)]
    [InlineData(10.5, false)]
    [InlineData(0, false)]
    public void Size_is_above_the_minimum_and_at_most_the_maximum(double sizeGb, bool expected)
    {
        var format = Movie("1-10 GB", 1, Range(Impl.Size, 1, 10));
        Assert.Equal(expected, Matches(format, MovieRelease("Movie.2020.1080p.WEB.H264-GRP", sizeBytes: (long)(sizeGb * Gb))));
    }

    [Fact]
    public void Year_uses_the_release_year_else_the_movie_year()
    {
        var format = Movie("2000s", 1, Range(Impl.Year, 2000, 2009));
        Assert.True(Matches(format, MovieRelease("Movie.2005.1080p.WEB.H264-GRP")));
        Assert.True(Matches(format, MovieRelease("Movie.2009.1080p.WEB.H264-GRP")));
        Assert.False(Matches(format, MovieRelease("Movie.2010.1080p.WEB.H264-GRP")));
        Assert.True(Matches(format, CustomFormatInput.ForRelease(
            new ParsedRelease { ReleaseTitle = "Movie 1080p", Title = "Movie", CleanTitle = "movie" }, 0, MediaType.Movie, Language.English, 2003)));
    }

    [Fact]
    public void Indexer_flags_match_when_the_flag_is_set()
    {
        var format = Movie("Internal", 1, Spec(Impl.IndexerFlag, 32));
        Assert.False(Matches(format, MovieRelease("Movie.2020.1080p.WEB.H264-GRP")));
        Assert.True(Matches(format, MovieRelease("Movie.2020.1080p.WEB.H264-GRP", indexerFlags: 32 | 1)));
        Assert.False(Matches(format, MovieRelease("Movie.2020.1080p.WEB.H264-GRP", indexerFlags: 1)));
    }

    [Fact]
    public void Language_except_and_original_rules()
    {
        var input = MovieRelease("Movie.2020.German.DL.1080p.WEB.H264-GRP");
        var exceptEnglish = Spec(Impl.Language, 1) with
        {
            Fields = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["value"] = System.Text.Json.JsonSerializer.SerializeToElement(1),
                ["exceptLanguage"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
            },
        };
        Assert.True(Matches(Movie("not only English", 1, exceptEnglish), input));
        Assert.False(Matches(Movie("not only English", 1, exceptEnglish), MovieRelease("Movie.2020.1080p.WEB.H264-GRP")));

        // "Original" is the movie's original language; when that is unknown, English (like the engine's "DL" rule).
        var original = Movie("Original", 1, Spec(Impl.Language, -2));
        Assert.True(Matches(original, input));
        Assert.True(Matches(original, MovieRelease("Movie.2020.German.DL.1080p.WEB.H264-GRP", Language.Unknown)));
        Assert.True(Matches(original, MovieRelease("Movie.2020.German.DL.1080p.WEB.H264-GRP", Language.French)));
        Assert.False(Matches(original, MovieRelease("Movie.2020.German.1080p.WEB.H264-GRP", Language.French)));
        var germanOnly = CustomFormatInput.ForRelease(
            new ParsedRelease { ReleaseTitle = "x", Title = "x", CleanTitle = "x", Languages = [Language.German] }, 0, MediaType.Movie, Language.Unknown);
        Assert.False(Matches(original, germanOnly));
    }

    [Fact]
    public void Series_language_values_are_Sonarr_ids()
    {
        // Sonarr 26 = Arabic (Radarr 31), Sonarr 27 = Hindi (Radarr 26).
        var arabic = Format(MediaType.Series, "Arabic", 1, Spec(Impl.Language, 26));
        var input = CustomFormatInput.ForRelease(
            new ParsedRelease { ReleaseTitle = "Show.S01E01", Title = "Show", CleanTitle = "show", SeasonNumber = 1, EpisodeNumbers = [1], Languages = [Language.Arabic] },
            0, MediaType.Series, Language.English);
        Assert.True(Matches(arabic, input));
        Assert.False(Matches(Format(MediaType.Series, "Hindi", 2, Spec(Impl.Language, 27)), input));
        Assert.Equal(Language.Hindi.Id, CustomFormatValues.LanguageId(MediaType.Series, 27));
        Assert.Null(CustomFormatValues.LanguageId(MediaType.Series, 47));
    }

    // ------------------------------------------------------------------ calculator

    [Fact]
    public void Calculator_returns_the_matching_formats_by_name_and_their_profile_score()
    {
        var formats = new[]
        {
            Trash("radarr", "x265-hd.json", 4),
            Trash("radarr", "german-dl.json", 1),
            Trash("radarr", "repack-proper.json", 3),
        };
        var profile = new QualityProfile
        {
            Name = "p",
            Items = [],
            CutoffQualityId = 0,
            LanguageId = -1,
            FormatItems = [new ProfileFormatItem(1, 11000), new ProfileFormatItem(3, 5), new ProfileFormatItem(4, -10000)],
        };

        var result = CustomFormatCalculator.Calculate(profile, formats, MovieRelease("Dune.Part.Two.2024.German.DL.PROPER.1080p.WEB.H264-GRP"));
        Assert.Equal(["German DL", "Repack/Proper"], result.Formats.Select(f => f.Name));
        Assert.Equal(11005, result.Score);
        Assert.Equal(0, CustomFormatCalculator.Calculate(null, formats, MovieRelease("Dune.Part.Two.2024.German.DL.PROPER.1080p.WEB.H264-GRP")).Score);
    }

    [Fact]
    public void A_runaway_pattern_times_out_as_no_match()
    {
        var evil = Movie("evil", 1, Title("^(a+)+$"));
        var input = CustomFormatInput.ForFile(MediaType.Movie, new string('a', 40) + "!", null, QualityModel.Unknown, [], Language.English, null, null, 0, 0);
        Assert.False(Matches(evil, input));
    }
}
