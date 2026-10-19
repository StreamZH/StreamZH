// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Arr.Settings;

namespace StreamZH.Tests.Library.Migration;

/// <summary>Quality, language and profile mapping of Radarr and Sonarr (no database).</summary>
public sealed class ArrSettingsMappingTests
{
    /// <summary>Every Sonarr quality (Sonarr's NzbDrone.Core.Qualities.Quality): id, name, source, resolution → StreamZH's quality.</summary>
    public static TheoryData<int, string, string, int, string> SonarrQualities => new()
    {
        { 0, "Unknown", "unknown", 0, "Unknown" },
        { 1, "SDTV", "television", 480, "SDTV" },
        { 2, "DVD", "dvd", 480, "DVD" },
        { 3, "WEBDL-1080p", "web", 1080, "WEBDL-1080p" },
        { 4, "HDTV-720p", "television", 720, "HDTV-720p" },
        { 5, "WEBDL-720p", "web", 720, "WEBDL-720p" },
        { 6, "Bluray-720p", "bluray", 720, "Bluray-720p" },
        { 7, "Bluray-1080p", "bluray", 1080, "Bluray-1080p" },
        { 8, "WEBDL-480p", "web", 480, "WEBDL-480p" },
        { 9, "HDTV-1080p", "television", 1080, "HDTV-1080p" },
        { 10, "Raw-HD", "televisionRaw", 1080, "Raw-HD" },
        { 12, "WEBRip-480p", "webRip", 480, "WEBRip-480p" },
        { 13, "Bluray-480p", "bluray", 480, "Bluray-480p" },
        { 14, "WEBRip-720p", "webRip", 720, "WEBRip-720p" },
        { 15, "WEBRip-1080p", "webRip", 1080, "WEBRip-1080p" },
        { 16, "HDTV-2160p", "television", 2160, "HDTV-2160p" },
        { 17, "WEBRip-2160p", "webRip", 2160, "WEBRip-2160p" },
        { 18, "WEBDL-2160p", "web", 2160, "WEBDL-2160p" },
        { 19, "Bluray-2160p", "bluray", 2160, "Bluray-2160p" },
        { 20, "Bluray-1080p Remux", "blurayRaw", 1080, "Remux-1080p" },
        { 21, "Bluray-2160p Remux", "blurayRaw", 2160, "Remux-2160p" },
        { 22, "Bluray-576p", "bluray", 576, "Bluray-576p" },
    };

    [Theory]
    [MemberData(nameof(SonarrQualities))]
    public void Every_sonarr_quality_maps_by_name_by_id_and_by_source(int id, string name, string source, int resolution, string expected)
    {
        Assert.Equal(expected, ArrQualities.Map(MigrationSources.Sonarr, Json(new { id, name, source, resolution }))?.Name);
        Assert.Equal(expected, ArrQualities.Map(MigrationSources.Sonarr, Json(new { id }))?.Name);
        Assert.Equal(expected, ArrQualities.Map(MigrationSources.Sonarr, Json(new { name }))?.Name);
        Assert.Equal(expected, ArrQualities.FromSonarrSource(source, resolution)?.Name);
    }

    [Fact]
    public void Every_sonarr_quality_id_is_covered_and_maps_to_a_distinct_streamzh_quality()
    {
        Assert.Equal(22, ArrQualities.SonarrIds.Count);
        Assert.Equal(22, ArrQualities.SonarrIds.Values.Distinct().Count());
        Assert.All(ArrQualities.SonarrIds.Values, id => Assert.Equal(id, Quality.FindById(id).Id));
    }

    [Fact]
    public void Radarr_qualities_keep_their_ids()
    {
        foreach (var quality in Quality.All)
        {
            Assert.Same(quality, ArrQualities.Map(MigrationSources.Radarr, Json(new { id = quality.Id, name = quality.Name })));
        }

        Assert.Null(ArrQualities.Map(MigrationSources.Radarr, Json(new { id = 99, name = "Holo-8K" })));
    }

    [Theory]
    [InlineData("radarr", 31, "Arabic", "Arabic")]
    [InlineData("sonarr", 26, "Arabic", "Arabic")]
    [InlineData("sonarr", 33, "Portuguese (Brazil)", "Portuguese (Brazil)")]
    [InlineData("radarr", -1, "Any", "Any")]
    [InlineData("sonarr", -2, "Original", "Original")]
    [InlineData("radarr", 4, "German", "German")]
    public void Languages_map_by_id_for_radarr_and_by_name_for_sonarr(string source, int id, string name, string expected) =>
        Assert.Equal(expected, ArrQualities.MapLanguage(source, Json(new { id, name }))?.Name);

    [Fact]
    public void A_profile_with_groups_keeps_them_and_a_group_cutoff_becomes_its_first_quality()
    {
        var profile = Json(new
        {
            cutoff = 1002,
            items = new object[]
            {
                new { quality = new { id = 4, name = "HDTV-720p" }, items = Array.Empty<object>(), allowed = false },
                new { id = 1002, name = "WEB 1080p", allowed = true, items = new object[] { new { quality = new { id = 15, name = "WEBRip-1080p" }, allowed = true }, new { quality = new { id = 3, name = "WEBDL-1080p" }, allowed = true } } },
                new { quality = new { id = 20, name = "Bluray-1080p Remux" }, items = Array.Empty<object>(), allowed = true },
            },
        });

        var result = ArrProfiles.Qualities(MigrationSources.Sonarr, profile);

        Assert.Equal(Quality.WebRip1080p.Id, result.CutoffQualityId);
        Assert.Equal(Quality.All.Count, result.Items.Count);
        Assert.Contains(new QualityProfileItem(Quality.WebDl1080p.Id, true, "WEB 1080p"), result.Items);
        Assert.Contains(new QualityProfileItem(Quality.Remux1080p.Id, true), result.Items);
        Assert.Contains(new QualityProfileItem(Quality.Cam.Id, false), result.Items);
        var web = result.Items.Select((item, index) => (item, index)).Where(x => x.item.Group == "WEB 1080p").Select(x => x.index).ToList();
        Assert.Equal(web[0] + 1, web[1]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_sonarr_v3_language_profile_with_a_fallback_asks_for_the_cutoff_language_and_prefers_dual_language()
    {
        var choice = ArrProfiles.LanguageProfile(Json(new
        {
            name = "Deutsch",
            cutoff = new { id = 4, name = "German" },
            languages = new object[]
            {
                new { language = new { id = 4, name = "German" }, allowed = true },
                new { language = new { id = 1, name = "English" }, allowed = true },
                new { language = new { id = 26, name = "Arabic" }, allowed = false },
            },
        }));

        Assert.Equal(Language.German, choice.Language);
        Assert.True(choice.PreferDualLanguage);
        Assert.Contains("English", choice.Warning, StringComparison.Ordinal);

        var single = ArrProfiles.LanguageProfile(Json(new { cutoff = new { id = 8, name = "Japanese" }, languages = new object[] { new { language = new { id = 8, name = "Japanese" }, allowed = true } } }));
        Assert.Equal(new ArrLanguageChoice(Language.Japanese, false, null), single);
    }

    [Theory]
    [InlineData("{Series Title} {[Preferred Words]}", "{Series Title} {[Custom Formats]}")]
    [InlineData("{Series Title} {Preferred.Words}", "{Series Title} {Custom.Formats}")]
    [InlineData("{Series Title} {Quality Full}", "{Series Title} {Quality Full}")]
    public void Sonarr_v3_preferred_words_become_custom_formats_in_naming(string format, string expected) =>
        Assert.Equal(expected, NamingSection.Translate(format).Format);

    [Fact]
    public void Widened_sizes_cover_both_apps()
    {
        var widened = QualityDefinitionsSection.Widen(new QualityDefinition(3, "WEBDL-1080p", 12.5, 400, 380), new QualityDefinition(3, "WEBDL-1080p", 10, 300, 290));
        Assert.Equal(new QualityDefinition(3, "WEBDL-1080p", 10, 400, 380), widened);
        Assert.Null(QualityDefinitionsSection.Widen(new QualityDefinition(7, "x", 0, null, null), new QualityDefinition(7, "x", 1, 100, 95)).MaxSize);
    }

    [Theory]
    [InlineData("5.26.2.10099", "radarr", 5)]
    [InlineData("4.0.15.2941", "sonarr", 4)]
    [InlineData("3.0.10.1567", "sonarr", 3)]
    [InlineData("unknown", "sonarr", 4)]
    [InlineData(null, "radarr", 5)]
    public void Major_version_comes_from_the_status(string? version, string source, int expected) =>
        Assert.Equal(expected, ArrReader.MajorVersion(source, version));

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
