// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Library;
using StreamZH.Library.Import;
using StreamZH.Library.Trash;

namespace StreamZH.Tests.Trash;

public sealed class RecyclarrImporterTests : IDisposable
{
    /// <summary>A realistic recyclarr.yml (v7/v8 syntax): templates, guide-backed and manual profiles, formats, groups, sizes, naming.</summary>
    public const string Sample = """
        # yaml-language-server: $schema=https://raw.githubusercontent.com/recyclarr/recyclarr/master/schemas/config-schema.json
        radarr:
          movies:
            base_url: http://radarr:7878
            api_key: !secret radarr_apikey
            delete_old_custom_formats: true
            replace_existing_custom_formats: true
            include:
              - template: radarr-quality-definition-movie
              - template: radarr-quality-profile-hd-bluray-web-german
              - template: radarr-custom-formats-hd-bluray-web-german
              - template: radarr-quality-profile-anime-remux-1080p
              - config: shared-formats.yml
            quality_definition:
              type: movie
              preferred_ratio: 0.5
            quality_profiles:
              - trash_id: 2b90e905c99490edc7c7a5787443748b # [German] HD Bluray + WEB
                name: Deutsch HD
                min_format_score: 100
                upgrade:
                  allowed: true
                  until_quality: Merged QPs
                  until_score: 20000
              - name: Kids
                reset_unmatched_scores:
                  enabled: true
                upgrade:
                  allowed: false
                  until_quality: WEB 1080p
                qualities:
                  - name: WEB 1080p
                    qualities:
                      - WEBDL-1080p
                      - WEBRip-1080p
                  - name: HDTV-1080p
                  - name: Bluray-720p
                    enabled: false
            custom_formats:
              - trash_ids:
                  - ee425f921dd740621523343bffe82fa3 # German Subbed
                  - 7357cf5161efbf8c4d5d0c30b4815ee2 # Obfuscated
                assign_scores_to:
                  - name: Deutsch HD
                    score: -500
                  - name: Kids
              - trash_ids:
                  - 8608a2ed20c636b8a62de108e9147713 # German Remux Tier 01, only created
              - trash_ids:
                  - ffffffffffffffffffffffffffffffff
            custom_format_groups:
              add:
                - trash_id: bc85e56ee3bd0f01467866d5f1261543 # [Release Groups] German
                  assign_scores_to:
                    - name: Deutsch HD
            media_naming:
              folder: plex-tmdb
              movie:
                rename: true
                standard: plex-tmdb

        sonarr:
          series:
            base_url: http://sonarr:8989
            api_key: 0123456789abcdef
            include:
              - template: sonarr-quality-definition-series
              - template: sonarr-v4-quality-profile-web-1080p
              - template: sonarr-v4-custom-formats-web-1080p
            quality_profiles:
              - trash_id: dca7e5e9e99c703bcbdaaa471dd40e98 # [German] HD Bluray + WEB
                name: Deutsch HD
            custom_format_groups:
              skip:
                - 6f0872eebfc95b1f93474b7ac866ced0 # [Unwanted] Unwanted Formats German
            media_naming:
              series: default
              season: default
              episodes:
                rename: true
                standard: default
        """;

    private readonly TrashTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private int Score(QualityProfile profile, string name, MediaType mediaType)
    {
        var format = _bed.Formats.FindByName(name, mediaType);
        return format is null ? 0 : profile.FormatItems.FirstOrDefault(i => i.CustomFormatId == format.Id)?.Score ?? 0;
    }

    [Fact]
    public void Preview_lists_the_profiles_and_changes_nothing()
    {
        var result = _bed.Recyclarr.Run(Sample, applyChanges: false, TrashTestBed.Guides);

        Assert.False(result.Applied);
        Assert.Equal(["Deutsch HD", "Kids", "WEB-1080p"], result.Profiles.Select(p => p.Name));
        var deutsch = result.Profiles[0];
        Assert.Equal("[German] HD Bluray + WEB", deutsch.Template);
        Assert.Equal(TrashTestBed.MovieGerman, deutsch.MovieTemplate);
        Assert.Equal(TrashTestBed.SeriesGerman, deutsch.SeriesTemplate);
        Assert.Equal(2, deutsch.Overrides); // German Subbed and Obfuscated at -500
        Assert.True(deutsch.Formats > 40);
        Assert.False(deutsch.Exists);
        Assert.Null(result.Profiles[1].Template);
        Assert.Equal(2, result.Profiles[1].Formats);
        Assert.Equal("72dae194fc92bf828f32cde7744e51a1", result.Profiles[2].SeriesTemplate);

        Assert.Contains(result.Warnings, w => w.Contains("anime-remux-1080p", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("shared-formats.yml", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("ffffffffffffffffffffffffffffffff", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("media_naming.season", StringComparison.Ordinal));

        Assert.Empty(_bed.Formats.All());
        Assert.Null(_bed.Profiles.FindByName("Deutsch HD"));
    }

    [Fact]
    public void Apply_creates_the_profiles_formats_sizes_and_naming()
    {
        var result = _bed.Recyclarr.Run(Sample, applyChanges: true, TrashTestBed.Guides);

        Assert.True(result.Applied);
        Assert.True(result.Created > 50);

        var deutsch = _bed.Profiles.FindByName("Deutsch HD")!;
        Assert.Equal(TrashTestBed.MovieGerman, deutsch.TrashMovieTemplate);
        Assert.Equal(TrashTestBed.SeriesGerman, deutsch.TrashSeriesTemplate);
        Assert.Equal(100, deutsch.MinFormatScore);
        Assert.Equal(20000, deutsch.CutoffFormatScore);
        Assert.Equal(Quality.Bluray1080p.Id, deutsch.CutoffQualityId);
        Assert.Equal(-500, Score(deutsch, "German Subbed", MediaType.Movie));
        Assert.Equal(11000, Score(deutsch, "German DL", MediaType.Movie));
        Assert.True(Score(deutsch, "German Remux Tier 01", MediaType.Movie) > 0); // added group without defaults
        Assert.NotEqual(0, Score(deutsch, "AV1", MediaType.Movie));
        Assert.Equal(0, Score(deutsch, "AV1", MediaType.Series)); // Sonarr skipped the unwanted group
        Assert.Equal(11000, Score(deutsch, "German DL", MediaType.Series));

        var kids = _bed.Profiles.FindByName("Kids")!;
        Assert.Null(kids.TrashMovieTemplate);
        Assert.False(kids.UpgradeAllowed);
        Assert.Equal(Quality.WebDl1080p.Id, kids.CutoffQualityId);
        Assert.Equal(["WEB 1080p", "WEB 1080p"], kids.Items.Where(i => i.Allowed && i.Group is not null).Select(i => i.Group));
        Assert.True(kids.IsAllowed(Quality.Hdtv1080p.Id));
        Assert.False(kids.IsAllowed(Quality.Bluray720p.Id));
        Assert.True(Score(kids, "Obfuscated", MediaType.Movie) < 0); // guide score
        Assert.True(kids.FormatItems.Count <= 2);

        var web = _bed.Profiles.FindByName("WEB-1080p")!;
        Assert.Equal("72dae194fc92bf828f32cde7744e51a1", web.TrashSeriesTemplate);

        Assert.NotNull(_bed.Formats.FindByTrashId("8608a2ed20c636b8a62de108e9147713", MediaType.Movie));
        Assert.Equal(50.4, _bed.Definitions.Find(Quality.Bluray1080p.Id)!.MinSize);
        Assert.Null(_bed.Definitions.Find(Quality.Bluray1080p.Id)!.PreferredSize); // Radarr's ratio, but Sonarr's sizes have no preferred limit
        Assert.Equal("movie:movie:0.5,series:series", _bed.Config.Get(StreamZH.Data.ConfigKeys.TrashQualitySizes));
        Assert.Equal("{Movie CleanTitle} ({Release Year}) {tmdb-{TmdbId}}", _bed.Media.Current.MovieFolderFormat);

        // Variants with tokens StreamZH's renamer does not know yet stay as they were, with a warning.
        Assert.Equal(MediaManagementValues.DefaultSeriesFolderFormat, _bed.Media.Current.SeriesFolderFormat);
        Assert.Equal(MediaManagementValues.DefaultMovieFormat, _bed.Media.Current.StandardMovieFormat);
        Assert.Contains(result.Warnings, w => w.StartsWith("Naming (seriesFolder)", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("Naming (movieFile)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Yaml_scores_survive_the_sync_and_applying_twice_is_stable()
    {
        await _bed.UseLocalCopyAsync();
        _bed.Recyclarr.Run(Sample, applyChanges: true, TrashTestBed.Guides);
        var before = _bed.Profiles.FindByName("Deutsch HD")!;

        var again = _bed.Recyclarr.Run(Sample, applyChanges: true, TrashTestBed.Guides);
        Assert.Equal(2, _bed.Sync.Update().ProfilesUpdated);

        var after = _bed.Profiles.FindByName("Deutsch HD")!;
        Assert.Equal(0, again.Created);
        Assert.Equal(before.FormatItems, after.FormatItems);
        Assert.Equal(-500, Score(after, "German Subbed", MediaType.Movie));
        Assert.Equal(100, after.MinFormatScore);
        Assert.True(Score(after, "German Remux Tier 01", MediaType.Movie) > 0);
    }

    [Fact]
    public void Merge_keys_and_aliases_are_understood()
    {
        const string yaml = """
            radarr:
              base: &base
                quality_profiles:
                  - trash_id: d1d67249d3890e49bc12e275d989a7e9
              movies:
                <<: *base
                base_url: http://radarr:7878
            """;

        var result = _bed.Recyclarr.Run(yaml, applyChanges: false, TrashTestBed.Guides);

        Assert.Equal(["HD Bluray + WEB"], result.Profiles.Select(p => p.Name).Distinct());
    }

    [Theory]
    [InlineData("radarr: [", "yaml")]
    [InlineData("", "yaml")]
    [InlineData("lidarr:\n  music:\n    base_url: x", "yaml")]
    public void Unreadable_yaml_is_a_field_error(string yaml, string field)
    {
        var error = Assert.Throws<ValidationFailedException>(() => _bed.Recyclarr.Run(yaml, applyChanges: false, TrashTestBed.Guides));
        Assert.Contains(field, error.Errors.Keys);
    }

    [Theory]
    [InlineData("hd-bluray-web-german", "german-hd-bluray-web")]
    [InlineData("german-hd-bluray-web", "german-hd-bluray-web")]
    [InlineData("hd-bluray-web", "hd-bluray-web")]
    [InlineData("web-hd-bluray", "hd-bluray-web")]
    public void Templates_resolve_by_slug_words(string slug, string expected) =>
        Assert.Equal(expected, RecyclarrImporter.FindBySlug(TrashTestBed.Guides.Movie, slug)?.Slug);
}
