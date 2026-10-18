// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Library;
using StreamZH.Library.Import;
using StreamZH.Library.Trash;

namespace StreamZH.Tests.TrashSync;

public sealed class TrashApplyTests : IDisposable
{
    private readonly TrashTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private int Score(QualityProfile profile, string name, MediaType mediaType)
    {
        var format = _bed.Formats.FindByName(name, mediaType) ?? throw new InvalidOperationException($"{name} not installed");
        return profile.FormatItems.FirstOrDefault(i => i.CustomFormatId == format.Id)?.Score ?? 0;
    }

    [Fact]
    public void German_movie_and_series_template_create_one_profile()
    {
        var result = _bed.ApplyGerman();

        Assert.True(result.ProfileCreated);
        var profile = result.Profile;
        Assert.Equal("[German] HD Bluray + WEB", profile.Name);
        Assert.Equal(TrashTestBed.MovieGerman, profile.TrashMovieTemplate);
        Assert.Equal(TrashTestBed.SeriesGerman, profile.TrashSeriesTemplate);

        // Qualities from the movie template: the six merged qualities allowed and ranked equally.
        var merged = profile.Items.Where(i => i.Group == "Merged QPs").ToList();
        Assert.Equal(6, merged.Count);
        Assert.All(merged, i => Assert.True(i.Allowed));
        Assert.Equal(
            new[] { Quality.WebDl720p.Id, Quality.WebRip720p.Id, Quality.Bluray720p.Id, Quality.WebDl1080p.Id, Quality.WebRip1080p.Id, Quality.Bluray1080p.Id }.Order(),
            merged.Select(i => i.QualityId).Order());
        Assert.Equal(profile.RankOf(Quality.WebDl720p.Id), profile.RankOf(Quality.Bluray1080p.Id));
        Assert.True(profile.RankOf(Quality.Bluray1080p.Id) > profile.RankOf(Quality.Remux2160p.Id)); // the guide ranks the merged group first
        Assert.Equal(Quality.All.Count, profile.Items.Count);
        Assert.Equal(Quality.Bluray1080p.Id, profile.CutoffQualityId);
        Assert.True(profile.IsAllowed(profile.CutoffQualityId));
        Assert.False(profile.IsAllowed(Quality.Remux1080p.Id));
        Assert.Equal(Language.Any.Id, profile.LanguageId);
        Assert.Equal(35000, profile.CutoffFormatScore);
        Assert.Equal(0, profile.MinFormatScore);
        Assert.Equal(1, profile.MinUpgradeFormatScore);

        // Movie and series formats with the "german" score set; groups of the template by default.
        Assert.Equal(11000, Score(profile, "German DL", MediaType.Movie));
        Assert.Equal(11000, Score(profile, "German DL", MediaType.Series));
        Assert.Equal(-35000, Score(profile, "Not German or English", MediaType.Movie));
        Assert.Equal(2900, Score(profile, "German Bluray Tier 01", MediaType.Series));
        Assert.Equal(-35000, Score(profile, "3D", MediaType.Movie)); // [Unwanted] Unwanted Formats German, default
        Assert.Null(_bed.Formats.FindByName("No-RlsGroup", MediaType.Movie)); // in the group, but not default
        Assert.NotNull(_bed.Formats.FindByName("x265 (HD)", MediaType.Movie)); // installed, german score 0 = not scored
        Assert.Equal(0, Score(profile, "x265 (HD)", MediaType.Movie));

        var formats = _bed.Formats.All();
        Assert.All(formats, f => Assert.NotNull(f.TrashId));
        Assert.Equal(result.Created, formats.Count);
        var germanDl = _bed.Formats.FindByTrashId("f845be10da4f442654c13e1f2c3d6cd5", MediaType.Movie);
        Assert.NotNull(germanDl);
        Assert.True(germanDl.IncludeInRename);
        Assert.Equal(-2, germanDl.Specifications[1].Fields["value"].GetInt32());
    }

    [Fact]
    public void Applying_again_changes_nothing()
    {
        var first = _bed.ApplyGerman();
        var formatsBefore = _bed.Formats.All();

        var second = _bed.ApplyGerman(first.Profile.Id);

        Assert.False(second.ProfileCreated);
        Assert.Equal(0, second.Created);
        Assert.Equal(0, second.Updated);
        Assert.Equal(first.Profile.FormatItems, second.Profile.FormatItems);
        Assert.Equal(first.Profile.Items, second.Profile.Items);
        Assert.Equal(formatsBefore.Select(f => (f.Id, f.Name)), _bed.Formats.All().Select(f => (f.Id, f.Name)));
        Assert.Single(_bed.Profiles.All(), p => p.Name == "[German] HD Bluray + WEB");
    }

    [Fact]
    public void Series_template_alone_brings_its_qualities()
    {
        var result = _bed.Apply.Apply(new TrashApplyRequest { SeriesTemplate = TrashTestBed.SeriesGerman, Name = "Series only" }, TrashTestBed.Guides);

        Assert.Null(result.Profile.TrashMovieTemplate);
        Assert.Equal(TrashTestBed.SeriesGerman, result.Profile.TrashSeriesTemplate);
        Assert.Equal(6, result.Profile.Items.Count(i => i.Allowed));
        Assert.All(_bed.Formats.All(), f => Assert.Equal(MediaType.Series, f.MediaType));
        Assert.False(result.Profile.IsAllowed(Quality.Remux1080p.Id)); // "Bluray-1080p Remux" mapped, not allowed
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Unknown quality", StringComparison.Ordinal));
    }

    [Fact]
    public void Existing_format_with_the_same_name_is_linked_not_duplicated()
    {
        var own = _bed.Formats.Add(new CustomFormat { Name = "German DL", MediaType = MediaType.Movie, Specifications = [] });

        var result = _bed.ApplyGerman();

        var format = _bed.Formats.Find(own)!;
        Assert.Equal("f845be10da4f442654c13e1f2c3d6cd5", format.TrashId);
        Assert.Equal(2, format.Specifications.Count);
        Assert.Equal(1, result.Updated);
        Assert.Single(_bed.Formats.All(), f => f.Name == "German DL" && f.MediaType == MediaType.Movie);
    }

    [Fact]
    public void Existing_profile_keeps_its_name_and_own_scores()
    {
        var own = _bed.Formats.Add(new CustomFormat { Name = "My format", MediaType = MediaType.Movie, Specifications = [] });
        var deutsch = _bed.Profiles.FindByName("Deutsch 1080p")!;
        _bed.Profiles.Update(deutsch with { FormatItems = [new ProfileFormatItem(own, 42)] });

        var result = _bed.ApplyGerman(deutsch.Id);

        Assert.Equal("Deutsch 1080p", result.Profile.Name);
        Assert.Equal(42, Score(result.Profile, "My format", MediaType.Movie));
        Assert.Equal(11000, Score(result.Profile, "German DL", MediaType.Movie));
        Assert.True(result.Profile.PreferDualLanguage);
    }

    [Fact]
    public async Task Extra_group_without_defaults_adds_all_its_formats_and_survives_the_sync()
    {
        await _bed.UseLocalCopyAsync();
        var result = _bed.Apply.Apply(
            new TrashApplyRequest
            {
                MovieTemplate = TrashTestBed.MovieGerman,
                Options = new TrashProfileOptions
                {
                    Groups =
                    [
                        new TrashGroupSelection { TrashId = "bc85e56ee3bd0f01467866d5f1261543" }, // [Release Groups] German: no default flags
                        new TrashGroupSelection { TrashId = "0ca61b4b233178d07113082a7acff72d", Select = ["ae9b7c9ebde1f3bd336a8cbd1ec4c5e5"] }, // + No-RlsGroup
                    ],
                },
            },
            TrashTestBed.Guides);

        Assert.True(Score(result.Profile, "German Remux Tier 01", MediaType.Movie) > 0);
        Assert.Equal(-35000, Score(result.Profile, "No-RlsGroup", MediaType.Movie));
        Assert.Null(_bed.Formats.FindByName("Obfuscated", MediaType.Movie));

        Assert.Equal(1, _bed.Sync.Update().ProfilesUpdated);
        var synced = _bed.Profiles.Find(result.Profile.Id)!;
        Assert.Equal(result.Profile.FormatItems, synced.FormatItems);
    }

    [Fact]
    public async Task Score_changed_in_the_profile_editor_is_kept_by_the_sync()
    {
        await _bed.UseLocalCopyAsync();
        var profile = _bed.ApplyGerman().Profile;
        var germanDl = _bed.Formats.FindByTrashId("f845be10da4f442654c13e1f2c3d6cd5", MediaType.Movie)!;
        var own = _bed.Formats.Add(new CustomFormat { Name = "Own", MediaType = MediaType.Movie, Specifications = [] });
        var edited = profile with
        {
            FormatItems = [.. profile.FormatItems.Where(i => i.CustomFormatId != germanDl.Id), new ProfileFormatItem(germanDl.Id, 500), new ProfileFormatItem(own, 7)],
            MinFormatScore = 100,
        };

        _bed.ProfileService.Update(profile.Id, edited);
        Assert.Equal(500, _bed.Trash.Overrides(profile.Id)[germanDl.Id]);
        Assert.False(_bed.Trash.Overrides(profile.Id).ContainsKey(own)); // own formats are never touched anyway

        var sync = _bed.Sync.Update();
        Assert.True(sync.ProfilesUpdated == 1, string.Join(" | ", sync.Problems));
        var synced = _bed.Profiles.Find(profile.Id)!;
        Assert.Equal(500, Score(synced, "German DL", MediaType.Movie));
        Assert.Equal(7, Score(synced, "Own", MediaType.Movie));
        Assert.Equal(100, synced.MinFormatScore);
        Assert.Equal(11000, Score(synced, "German DL", MediaType.Series));

        // Without "keep score overrides" the guide wins again.
        _bed.Settings.Save(TrashSettings.Defaults with { KeepScoreOverrides = false });
        _bed.Sync.Update();
        synced = _bed.Profiles.Find(profile.Id)!;
        Assert.Equal(11000, Score(synced, "German DL", MediaType.Movie));
        Assert.Empty(_bed.Trash.Overrides(profile.Id));
        Assert.Equal(7, Score(synced, "Own", MediaType.Movie));
    }

    [Fact]
    public void Format_that_drops_out_of_the_template_loses_its_score()
    {
        var profile = _bed.ApplyGerman().Profile;
        var guides = TrashTestBed.Guides;
        var movie = guides.Movie.Profile(TrashTestBed.MovieGerman)!;
        var trimmed = guides with
        {
            Movie = guides.Movie with
            {
                Profiles = [movie with { FormatItems = [.. movie.FormatItems.Where(f => f.Key != "German 1080p Booster")] }],
            },
        };

        var result = _bed.ApplyGerman(profile.Id, guides: trimmed);

        Assert.Equal(0, Score(result.Profile, "German 1080p Booster", MediaType.Movie));
        Assert.NotEqual(0, Score(result.Profile, "German 1080p Booster", MediaType.Series));
    }

    [Fact]
    public void Unlinking_a_profile_forgets_its_trash_state_and_deleting_too()
    {
        var profile = _bed.ApplyGerman().Profile;
        Assert.NotNull(_bed.Trash.Options(profile.Id));

        _bed.ProfileService.Update(profile.Id, profile with { TrashMovieTemplate = null, TrashSeriesTemplate = null });
        Assert.Null(_bed.Trash.Options(profile.Id));

        var second = _bed.ApplyGerman(name: "Second").Profile;
        _bed.ProfileService.Delete(second.Id);
        Assert.Null(_bed.Trash.Options(second.Id));
    }

    [Fact]
    public void Invalid_requests_change_nothing()
    {
        var unknown = Assert.Throws<ValidationFailedException>(() =>
            _bed.Apply.Apply(new TrashApplyRequest { MovieTemplate = "nope" }, TrashTestBed.Guides));
        Assert.Contains("movieTemplate", unknown.Errors.Keys);

        var none = Assert.Throws<ValidationFailedException>(() => _bed.Apply.Apply(new TrashApplyRequest(), TrashTestBed.Guides));
        Assert.Contains("movieTemplate", none.Errors.Keys);

        var taken = Assert.Throws<ValidationFailedException>(() => _bed.ApplyGerman(name: "Deutsch 1080p"));
        Assert.Contains("name", taken.Errors.Keys);

        var missing = Assert.Throws<ValidationFailedException>(() => _bed.ApplyGerman(profileId: 999));
        Assert.Contains("profileId", missing.Errors.Keys);
        Assert.Empty(_bed.Formats.All());
    }

    [Fact]
    public void Managed_formats_follow_the_guides()
    {
        _bed.ApplyGerman();
        var germanDl = _bed.Formats.FindByTrashId("f845be10da4f442654c13e1f2c3d6cd5", MediaType.Movie)!;
        _bed.Formats.Update(germanDl with { Name = "Old name", Specifications = [] });

        var warnings = new List<string>();
        Assert.Equal(1, _bed.Apply.UpdateManagedFormats(TrashTestBed.Guides, warnings));
        var updated = _bed.Formats.Find(germanDl.Id)!;
        Assert.Equal("German DL", updated.Name);
        Assert.Equal(2, updated.Specifications.Count);
        Assert.Equal(0, _bed.Apply.UpdateManagedFormats(TrashTestBed.Guides, warnings));
    }

    [Fact]
    public void Quality_sizes_of_movie_and_series_are_combined()
    {
        var warnings = new List<string>();
        var count = _bed.Apply.ApplyQualitySizes(
            [new TrashSizeChoice(MediaType.Movie, "movie"), new TrashSizeChoice(MediaType.Series, "series")], TrashTestBed.Guides, warnings);

        Assert.Empty(warnings);
        Assert.Equal(14, count);
        var bluray = _bed.Definitions.Find(Quality.Bluray1080p.Id)!;
        Assert.Equal(50.4, bluray.MinSize); // series 50.4 < movie 50.8
        Assert.Null(bluray.MaxSize); // slider end = unlimited
        Assert.Null(bluray.PreferredSize);
        Assert.Equal(69.1, _bed.Definitions.Find(Quality.Remux1080p.Id)!.MinSize); // "Bluray-1080p Remux"
        Assert.Equal(0, _bed.Definitions.Find(Quality.Dvd.Id)!.MinSize); // not in the guide: unchanged
        Assert.Equal(2, _bed.Apply.RememberedSizeChoices().Count);
    }

    [Fact]
    public void Quality_sizes_with_a_preferred_ratio()
    {
        _bed.Apply.ApplyQualitySizes([new TrashSizeChoice(MediaType.Movie, "movie", 0.5)], TrashTestBed.Guides, []);

        var webdl = _bed.Definitions.Find(Quality.WebDl1080p.Id)!;
        Assert.Equal(12.5, webdl.MinSize);
        Assert.Equal(Math.Round(12.5 + ((2000 - 12.5) * 0.5), 1), webdl.PreferredSize);
        Assert.Equal("movie:movie:0.5", _bed.Config.Get(StreamZH.Data.ConfigKeys.TrashQualitySizes));
    }

    [Fact]
    public void Apply_with_quality_sizes_and_naming()
    {
        var result = _bed.Apply.ApplyWithExtras(
            new TrashApplyRequest { MovieTemplate = TrashTestBed.MovieGerman, SeriesTemplate = TrashTestBed.SeriesGerman },
            applyQualitySizes: true,
            new TrashNaming(MovieFolder: "plex-tmdb", MovieFile: "original"),
            TrashTestBed.Guides);

        Assert.True(result.ProfileCreated);
        Assert.Equal(50.4, _bed.Definitions.Find(Quality.Bluray1080p.Id)!.MinSize);
        Assert.Equal("{Movie CleanTitle} ({Release Year}) {tmdb-{TmdbId}}", _bed.Media.Current.MovieFolderFormat);
        Assert.Equal("{Original Title}", _bed.Media.Current.StandardMovieFormat);
    }

    [Fact]
    public void Naming_variant_with_tokens_streamzh_lacks_is_skipped_with_a_warning()
    {
        var anime = TrashTestBed.Guides.Series.Naming["episodes.anime"]["default"];
        var animeProblem = FileNameBuilder.ValidateFormat(anime, NamingFormatKind.AnimeEpisode);
        var result = _bed.Apply.ApplyWithExtras(
            new TrashApplyRequest { MovieTemplate = TrashTestBed.MovieGerman },
            applyQualitySizes: false,
            new TrashNaming(MovieFolder: "plex-imdb", SeriesFolder: "default", SeriesFile: "default"),
            TrashTestBed.Guides);

        // Standard and daily apply; the anime variant only when StreamZH knows all its tokens ({absolute} for absolute numbering).
        Assert.Equal(TrashTestBed.Guides.Series.Naming["episodes.standard"]["default"], _bed.Media.Current.StandardEpisodeFormat);
        Assert.Equal(TrashTestBed.Guides.Series.Naming["episodes.daily"]["default"], _bed.Media.Current.DailyEpisodeFormat);
        if (animeProblem is null)
        {
            Assert.Equal(anime, _bed.Media.Current.AnimeEpisodeFormat);
        }
        else
        {
            Assert.Single(result.Warnings, w => w.StartsWith("Naming seriesFileAnime 'default' not applied: Unknown token", StringComparison.Ordinal));
            Assert.Equal(MediaManagementValues.DefaultAnimeFormat, _bed.Media.Current.AnimeEpisodeFormat);
        }

        Assert.Equal("{Series CleanTitleWithoutYear} {(Series Year)}", _bed.Media.Current.SeriesFolderFormat);
        Assert.Equal("{Movie CleanTitle} ({Release Year}) {imdb-{ImdbId}}", _bed.Media.Current.MovieFolderFormat);
    }

    /// <summary>Every naming variant of the guides fixture is usable now that the renamer has the media info tokens (anime: see above).</summary>
    [Fact]
    public void All_trash_naming_variants_validate()
    {
        var guides = TrashTestBed.Guides;
        var kinds = new Dictionary<string, NamingFormatKind>
        {
            ["folder"] = NamingFormatKind.MovieFolder,
            ["file"] = NamingFormatKind.MovieFile,
        };
        foreach (var (section, kind) in kinds)
        {
            foreach (var (name, format) in guides.Movie.Naming[section])
            {
                Assert.True(FileNameBuilder.ValidateFormat(format, kind) is null, $"radarr {section} {name}: {FileNameBuilder.ValidateFormat(format, kind)}");
            }
        }

        var seriesKinds = new Dictionary<string, NamingFormatKind>
        {
            ["series"] = NamingFormatKind.SeriesFolder,
            ["season"] = NamingFormatKind.SeasonFolder,
            ["episodes.standard"] = NamingFormatKind.StandardEpisode,
            ["episodes.daily"] = NamingFormatKind.DailyEpisode,
        };
        foreach (var (section, kind) in seriesKinds)
        {
            foreach (var (name, format) in guides.Series.Naming[section])
            {
                Assert.True(FileNameBuilder.ValidateFormat(format, kind) is null, $"sonarr {section} {name}: {FileNameBuilder.ValidateFormat(format, kind)}");
            }
        }
    }

    [Fact]
    public void Unknown_naming_variant_is_a_field_error_and_nothing_is_applied()
    {
        var error = Assert.Throws<ValidationFailedException>(() => _bed.Apply.ApplyWithExtras(
            new TrashApplyRequest { MovieTemplate = TrashTestBed.MovieGerman },
            applyQualitySizes: true,
            new TrashNaming(MovieFile: "fancy"),
            TrashTestBed.Guides));

        Assert.Contains("naming.movieFile", error.Errors.Keys);
        Assert.Empty(_bed.Formats.All());
        Assert.Null(_bed.Profiles.FindByName("[German] HD Bluray + WEB"));
    }

    [Fact]
    public void Template_items_order_worst_first_with_unlisted_qualities_not_allowed()
    {
        var warnings = new List<string>();
        var template = TrashTestBed.Guides.Series.Profile(TrashTestBed.SeriesGerman)!;
        var items = TrashApplyService.BuildItems(template, MediaType.Series, warnings);

        Assert.Empty(warnings);
        Assert.Equal(Quality.All.Count, items.Count);
        Assert.Equal(items.Count, items.Select(i => i.QualityId).Distinct().Count());
        Assert.Contains(items, i => i.QualityId == Quality.Cam.Id && !i.Allowed); // Sonarr has no CAM
        var web2160 = items.Where(i => i.Group == "WEB 2160p").Select(i => i.QualityId).ToList();
        Assert.Equal([Quality.WebDl2160p.Id, Quality.WebRip2160p.Id], web2160);
        Assert.True(items.ToList().FindIndex(i => i.QualityId == Quality.Remux2160p.Id) > items.ToList().FindIndex(i => i.QualityId == Quality.Sdtv.Id));
    }
}
