// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.Trash;

namespace StreamZH.Tests.TrashSync;

public sealed class TrashGuideParserTests
{
    private static TrashGuides Guides => TrashTestBed.Guides;

    [Fact]
    public void Reads_both_apps_from_the_paths_in_metadata_json()
    {
        Assert.Empty(Guides.Warnings);
        Assert.Equal(MediaType.Movie, Guides.Movie.MediaType);
        Assert.True(Guides.Movie.CustomFormats.Count > 50);
        Assert.True(Guides.Series.CustomFormats.Count > 50);
        Assert.Equal(2, Guides.Movie.Profiles.Count);
        Assert.Equal(2, Guides.Series.Profiles.Count);
    }

    [Fact]
    public void Custom_format_with_score_sets_and_specifications()
    {
        var germanDl = Guides.Movie.Format("f845be10da4f442654c13e1f2c3d6cd5");
        Assert.NotNull(germanDl);
        Assert.Equal("German DL", germanDl.Name);
        Assert.Equal("german-dl", germanDl.Slug);
        Assert.True(germanDl.IncludeInRename);
        Assert.Equal(11000, germanDl.ScoreFor("german"));
        Assert.Equal(-10000, germanDl.ScoreFor("default"));
        Assert.Equal(-10000, germanDl.ScoreFor("sqp-1-1080p")); // unknown set → default

        Assert.Equal(2, germanDl.Specifications.Count);
        var spec = germanDl.Specifications[1];
        Assert.Equal(CustomFormatImplementations.Language, spec.Implementation);
        Assert.True(spec.Required);
        Assert.Equal(-2, spec.Fields["value"].GetInt32());
    }

    [Fact]
    public void Quality_profile_with_merged_group_cutoff_and_formats()
    {
        var profile = Guides.Movie.Profile(TrashTestBed.MovieGerman);
        Assert.NotNull(profile);
        Assert.Equal("[German] HD Bluray + WEB", profile.Name);
        Assert.Equal("german-hd-bluray-web", profile.Slug);
        Assert.Equal("German", profile.Group);
        Assert.Equal("german", profile.ScoreSet);
        Assert.Equal("Merged QPs", profile.Cutoff);
        Assert.Equal(35000, profile.CutoffFormatScore);
        Assert.Equal(1, profile.MinUpgradeFormatScore);
        Assert.Equal("Any", profile.Language);
        Assert.Equal("Merged QPs", profile.Items[0].Name);
        Assert.True(profile.Items[0].Allowed);
        Assert.Equal(6, profile.Items[0].Qualities.Count);
        Assert.Equal(23, profile.FormatItems.Count);
        Assert.Equal("German 1080p Booster", profile.FormatItems[0].Key);
    }

    [Fact]
    public void Cf_groups_read_default_flags_written_as_text_or_bool()
    {
        var unwanted = Guides.Movie.Group("0ca61b4b233178d07113082a7acff72d");
        Assert.NotNull(unwanted);
        Assert.True(unwanted.Default); // "default": "true"
        Assert.True(unwanted.AppliesTo(TrashTestBed.MovieGerman));
        Assert.False(unwanted.AppliesTo("e8c5acb741363a0dbda67d3978f4912f"));
        Assert.Contains(unwanted.Formats, f => f.Name == "3D" && f.Default && !f.Required);
        Assert.Contains(unwanted.Formats, f => f.Name == "No-RlsGroup" && !f.Default);

        var releaseGroups = Guides.Movie.Group("bc85e56ee3bd0f01467866d5f1261543");
        Assert.NotNull(releaseGroups);
        Assert.False(releaseGroups.Default);
    }

    [Fact]
    public void Quality_sizes_and_naming_variants()
    {
        var movie = Guides.Movie.QualitySize("movie");
        Assert.NotNull(movie);
        Assert.Contains(movie.Qualities, q => q.Quality == "Bluray-1080p" && q.Min == 50.8 && q.Max == 2000);
        var series = Guides.Series.QualitySize("series");
        Assert.NotNull(series);
        Assert.Contains(series.Qualities, q => q.Quality == "Bluray-1080p Remux");

        Assert.Equal("{Movie CleanTitle} ({Release Year})", Guides.Movie.Naming["folder"]["default"]);
        Assert.Contains("plex-tmdb", Guides.Movie.Naming["file"].Keys);
        Assert.StartsWith("{Series CleanTitleWithoutYear}", Guides.Series.Naming["episodes.standard"]["default"], StringComparison.Ordinal);
        Assert.True(Guides.Series.Naming.ContainsKey("episodes.daily"));
    }

    [Fact]
    public void Categories_come_from_the_collection_pages()
    {
        Assert.Equal("Repacks/Proper (Required)", Guides.Movie.Format("e7718d7a3ce595f289bfee26adc178f5")?.Category);
        Assert.Equal("Resolution (Optional)", Guides.Movie.Format("820b09bb9acbfde9c35c71e0e565dad8")?.Category);
        Assert.Contains(Guides.Series.CustomFormats, f => f.Category is not null);
    }

    [Fact]
    public void Broken_files_are_skipped_with_a_warning()
    {
        var root = Path.Combine(Path.GetTempPath(), $"streamzh-trash-broken-{Guid.NewGuid():N}");
        var cf = Path.Combine(root, "json", "radarr", "cf");
        Directory.CreateDirectory(cf);
        try
        {
            File.WriteAllText(Path.Combine(root, "metadata.json"), """{ "json_paths": { "radarr": { "custom_formats": ["json/radarr/cf"], "naming": ["json/radarr/missing"] } } }""");
            File.WriteAllText(Path.Combine(cf, "good.json"), """{ "trash_id": "a1", "name": "Good", "specifications": [{ "name": "x", "implementation": "ReleaseTitleSpecification", "fields": [{ "name": "value", "value": "x" }] }] }""");
            File.WriteAllText(Path.Combine(cf, "broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(cf, "noid.json"), """{ "name": "No id" }""");

            var guides = TrashGuideParser.Parse(root);
            var good = Assert.Single(guides.Movie.CustomFormats);
            Assert.Equal(JsonValueKind.String, good.Specifications[0].Fields["value"].ValueKind); // Radarr export shape
            Assert.Equal(2, guides.Warnings.Count);
            Assert.Empty(guides.Series.CustomFormats);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_metadata_is_an_error() =>
        Assert.Throws<FormatException>(() => TrashGuideParser.Parse(Path.Combine(Path.GetTempPath(), $"streamzh-none-{Guid.NewGuid():N}")));

    [Theory]
    [InlineData("Bluray-1080p Remux", MediaType.Series, 30)]
    [InlineData("Bluray-2160p Remux", MediaType.Series, 31)]
    [InlineData("WEBDL-1080p", MediaType.Series, 3)]
    [InlineData("HDTV-1080p", MediaType.Series, 9)]
    [InlineData("Raw-HD", MediaType.Series, 10)]
    [InlineData("Remux-1080p", MediaType.Movie, 30)]
    [InlineData("BR-DISK", MediaType.Movie, 22)]
    [InlineData("webrip-720p", MediaType.Movie, 14)]
    [InlineData("Bluray-2160p Remux", MediaType.Movie, 31)]
    public void Quality_names_map_to_streamzh_qualities(string name, MediaType mediaType, int id) =>
        Assert.Equal(id, TrashQualityNames.Find(name, mediaType)?.Id);

    [Fact]
    public void Unknown_quality_names_are_null()
    {
        Assert.Null(TrashQualityNames.Find("Bluray-4320p", MediaType.Movie));
        Assert.Null(TrashQualityNames.Find(" ", MediaType.Series));
        Assert.Equal(Quality.Unknown, TrashQualityNames.Find("Unknown", MediaType.Series));
    }
}
