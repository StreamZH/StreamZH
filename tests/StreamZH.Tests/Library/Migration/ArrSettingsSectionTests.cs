// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.CustomFormats;
using StreamZH.Library.Import;
using StreamZH.Library.Migration;
using StreamZH.Library.Notifications;

namespace StreamZH.Tests.Library.Migration;

/// <summary>The Radarr v5, Sonarr v4 and Sonarr v3 settings sections against the fixtures: preview, run, second run, left-out items.</summary>
public sealed class ArrSettingsSectionTests : IDisposable
{
    private const string Radarr = MigrationSources.Radarr;
    private const string Sonarr = MigrationSources.Sonarr;

    /// <summary>The settings sections; the library, indexer and download client sections of the same sources are other parts.</summary>
    private static readonly string[] SettingsKeys =
        ["rootFolders", "qualityDefinitions", "tags", "customFormats", "qualityProfiles", "releaseProfiles", "naming", "mediaManagement", "notifications", "importListExclusions"];

    private readonly ArrSettingsTestBed _bed = new();

    [Fact]
    public void Both_sources_list_all_settings_sections_in_order()
    {
        foreach (var source in new[] { Radarr, Sonarr })
        {
            var sections = _bed.Get<MigrationService>().Sources().Single(s => s.Key == source).Sections;
            Assert.Equal(SettingsKeys, sections.Select(s => s.Key).Where(SettingsKeys.Contains));
            Assert.False(sections.Single(s => s.Key == "notifications").DefaultSelected);
            Assert.True(sections.Single(s => s.Key == "qualityDefinitions").Advanced);
        }
    }

    // ------------------------------------------------------------------ root folders

    [Fact]
    public async Task Root_folders_are_created_at_the_mapped_path_and_unmapped_ones_skipped()
    {
        var context = _bed.Context(Radarr, "radarr5");
        var plan = await _bed.Section(Radarr, "rootFolders").PreviewAsync(context, CancellationToken.None);

        var movies = Assert.Single(plan.Items, i => i.Key == "rootFolders:1");
        Assert.Equal(MigrationAction.Create, movies.Action);
        Assert.Equal($"/movies → {_bed.MoviesFolder}", movies.Detail);
        var uhd = Assert.Single(plan.Items, i => i.Key == "rootFolders:2");
        Assert.Equal(MigrationAction.Skip, uhd.Action);
        Assert.Contains("path mapping", uhd.Warning, StringComparison.Ordinal);
        Assert.Equal(["/movies", "/movies-uhd"], context.Paths.Checks().Select(c => c.Remote));
        Assert.Empty(_bed.Get<RootFolderRepository>().All());

        var report = await _bed.RunAsync(Radarr, "radarr5", "rootFolders");
        Assert.Equal((1, 1), (report.Created, report.Skipped));
        var root = Assert.Single(_bed.Get<RootFolderRepository>().All());
        Assert.Equal((_bed.MoviesFolder, MediaType.Movie), (root.Path, root.MediaType));

        var again = await _bed.RunAsync(Radarr, "radarr5", "rootFolders");
        Assert.Equal((0, 1, 1), (again.Created, again.Linked, again.Skipped));
        Assert.Single(_bed.Get<RootFolderRepository>().All());

        var series = await _bed.RunAsync(Sonarr, "sonarr4", "rootFolders");
        Assert.Equal(1, series.Created);
        Assert.Contains(_bed.Get<RootFolderRepository>().All(), r => r.Path == _bed.TvFolder && r.MediaType == MediaType.Series);
    }

    // ------------------------------------------------------------------ quality sizes

    [Fact]
    public async Task Quality_sizes_take_radarrs_values_and_widen_for_sonarr()
    {
        var preview = await _bed.PreviewAsync(Radarr, "radarr5", "qualityDefinitions");
        Assert.Equal(2, preview.Items.Count(i => i.Action == MigrationAction.Update));
        Assert.Equal("0 / 95 / 100 MB/min → 12.5 / 380 / 400 MB/min", preview.Items.Single(i => i.Label == "WEBDL-1080p").Detail);

        var radarr = await _bed.RunAsync(Radarr, "radarr5", "qualityDefinitions");
        Assert.Equal(2, radarr.Updated);
        var definitions = _bed.Get<QualityDefinitionRepository>();
        Assert.Equal((12.5, 400d, 380d), Sizes(definitions, Quality.WebDl1080p));
        Assert.Equal((50.4, 1000d, 950d), Sizes(definitions, Quality.Bluray1080p));

        // Sonarr: the WEB-1080p range is widened; untouched qualities take Sonarr's sizes.
        var sonarr = await _bed.RunAsync(Sonarr, "sonarr4", "qualityDefinitions");
        Assert.Equal((10d, 400d, 380d), Sizes(definitions, Quality.WebDl1080p));
        Assert.Equal((4d, 1000d, 950d), Sizes(definitions, Quality.Bluray1080p));
        Assert.Equal((3d, 125d, 95d), Sizes(definitions, Quality.Hdtv720p));
        Assert.Equal((35d, null, 95d), Sizes(definitions, Quality.Remux1080p));
        Assert.Contains(sonarr.Messages, m => m.Level == MigrationMessageLevel.Warn && m.Text.Contains("wider range", StringComparison.Ordinal));

        var again = await _bed.RunAsync(Sonarr, "sonarr4", "qualityDefinitions");
        Assert.Equal(0, again.Updated);
    }

    // ------------------------------------------------------------------ tags

    [Fact]
    public async Task Tags_are_listed_with_what_uses_them_and_never_taken_over()
    {
        var plan = await _bed.PreviewAsync(Radarr, "radarr5", "tags");
        var anime = plan.Items.Single(i => i.Label == "anime");
        Assert.Equal(MigrationAction.Skip, anime.Action);
        Assert.Equal("Used by 3 movies, 1 release profile, 1 connection", anime.Detail);
        Assert.Equal("Not used", plan.Items.Single(i => i.Label == "kids").Detail);

        var report = await _bed.RunAsync(Sonarr, "sonarr4", "tags");
        Assert.Equal((0, 1), (report.Created, report.Skipped));
    }

    // ------------------------------------------------------------------ custom formats

    [Fact]
    public async Task Custom_formats_are_created_by_name_and_updated_when_their_conditions_differ()
    {
        var preview = await _bed.PreviewAsync(Radarr, "radarr5", "customFormats");
        Assert.Equal(6, preview.Items.Count(i => i.Action == MigrationAction.Create));
        var broken = preview.Items.Single(i => i.Label == "Broken Regex");
        Assert.Equal(MigrationAction.Skip, broken.Action);
        Assert.Contains("not a valid regular expression", broken.Reason, StringComparison.Ordinal);
        Assert.Equal("2 conditions · in file names", preview.Items.Single(i => i.Label == "German DL").Detail);

        var report = await _bed.RunAsync(Radarr, "radarr5", "customFormats");
        Assert.Equal((6, 1), (report.Created, report.Skipped));
        var formats = _bed.Get<CustomFormatRepository>();
        var german = formats.FindByName("German DL", MediaType.Movie)!;
        Assert.True(german.IncludeInRename);
        Assert.Equal(4, german.Specifications[0].Fields["value"].GetInt32());
        Assert.False(german.Specifications[0].Fields["exceptLanguage"].GetBoolean());
        var size = formats.FindByName("Too Big", MediaType.Movie)!.Specifications[0];
        Assert.Equal((40d, 400d), (size.Fields["min"].GetDouble(), size.Fields["max"].GetDouble()));

        var again = await _bed.RunAsync(Radarr, "radarr5", "customFormats");
        Assert.Equal((0, 0, 6), (again.Created, again.Updated, again.Linked));

        // Changed in StreamZH: the next run puts Radarr's conditions back.
        var lq = formats.FindByName("LQ", MediaType.Movie)!;
        formats.Update(lq with { Specifications = [lq.Specifications[0]] });
        var third = await _bed.RunAsync(Radarr, "radarr5", "customFormats");
        Assert.Equal(1, third.Updated);
        Assert.Equal(2, formats.FindByName("LQ", MediaType.Movie)!.Specifications.Count);
        Assert.Equal(6, formats.ForMediaType(MediaType.Movie).Count);
    }

    [Fact]
    public async Task A_format_that_follows_the_trash_guides_keeps_its_conditions()
    {
        var service = _bed.Get<CustomFormatService>();
        var trash = service.Create(new CustomFormat
        {
            Name = "x265 (HD)",
            MediaType = MediaType.Series,
            TrashId = "47435ece6b99a0b477caf360e79ba0bb",
            Specifications = [new CustomFormatSpecification { Name = "x265", Implementation = CustomFormatImplementations.ReleaseTitle, Fields = new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement("x265") } }],
        });

        var preview = await _bed.PreviewAsync(Sonarr, "sonarr4", "customFormats");
        var item = preview.Items.Single(i => i.Label == "x265 (HD)");
        Assert.Equal(MigrationAction.Link, item.Action);
        Assert.Contains("TRaSH", item.Warning, StringComparison.Ordinal);

        var reports = await _bed.RunAsync(Sonarr, "sonarr4", null, "customFormats", "qualityProfiles");
        Assert.Equal(4, reports[0].Created);
        Assert.Single(_bed.Get<CustomFormatRepository>().FindByName("x265 (HD)", MediaType.Series)!.Specifications);

        // Its score still comes over with the profile.
        var profile = _bed.Get<QualityProfileRepository>().FindByName("Deutsch 1080p")!;
        Assert.Contains(new ProfileFormatItem(trash.Id, -10000), profile.FormatItems);
    }

    [Fact]
    public async Task Sonarr_v3_has_no_custom_formats()
    {
        var plan = await _bed.PreviewAsync(Sonarr, "sonarr3", "customFormats");
        Assert.Empty(plan.Items);
        Assert.Contains(plan.Warnings!, w => w.Contains("Sonarr v3", StringComparison.Ordinal));
        Assert.DoesNotContain(_bed.Handler.Requests, r => r.Path.EndsWith("/customformat", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ quality profiles

    [Fact]
    public async Task Radarr_profiles_replace_the_seed_profile_and_bring_movie_scores()
    {
        var preview = await _bed.PreviewAsync(Radarr, "radarr5", "qualityProfiles");
        var deutsch = preview.Items.Single(i => i.Key == "qualityProfiles:4");
        Assert.Equal(MigrationAction.Update, deutsch.Action);

        // Formats not in StreamZH yet are counted: they come with the custom formats part.
        Assert.Equal("German · 6 qualities · cutoff WEBDL-1080p · 5 formats", deutsch.Detail);
        Assert.Equal(MigrationAction.Create, preview.Items.Single(i => i.Key == "qualityProfiles:7").Action);

        var reports = await _bed.RunAsync(Radarr, "radarr5", null, "customFormats", "qualityProfiles");
        var report = reports[1];
        Assert.Equal((1, 1), (report.Created, report.Updated));
        Assert.Contains(report.Messages, m => m.Text.Contains("'Broken Regex' is left out", StringComparison.Ordinal));

        var profiles = _bed.Get<QualityProfileRepository>();
        var formats = _bed.Get<CustomFormatRepository>();
        var profile = profiles.FindByName("Deutsch 1080p")!;
        Assert.Equal(Language.German.Id, profile.LanguageId);
        Assert.True(profile.PreferDualLanguage);
        Assert.Equal(Quality.WebDl1080p.Id, profile.CutoffQualityId);
        Assert.Equal(25000, profile.CutoffFormatScore);
        Assert.Equal(
            [Quality.WebDl720p.Id, Quality.WebRip720p.Id, Quality.Bluray720p.Id, Quality.WebDl1080p.Id, Quality.WebRip1080p.Id, Quality.Bluray1080p.Id],
            profile.Items.Where(i => i.Allowed).Select(i => i.QualityId));
        Assert.Equal("WEB 1080p", profile.Items.Single(i => i.QualityId == Quality.WebRip1080p.Id).Group);
        Assert.Equal(
            new[] { ("German DL", 11000), ("LQ", -10000), ("Too Big", -50), ("x265 (HD)", -10000) },
            profile.FormatItems.Select(i => (formats.Find(i.CustomFormatId)!.Name, i.Score)).OrderBy(x => x.Name));

        var anime = profiles.FindByName("Anime")!;
        Assert.Equal((Language.Original.Id, Quality.Remux1080p.Id, 10), (anime.LanguageId, anime.CutoffQualityId, anime.MinUpgradeFormatScore));

        var again = await _bed.RunAsync(Radarr, "radarr5", "qualityProfiles");
        Assert.Equal((0, 0, 2), (again.Created, again.Updated, again.Linked));
    }

    [Fact]
    public async Task Sonarr_after_radarr_joins_the_same_profiles_instead_of_duplicating_them()
    {
        await _bed.RunAsync(Radarr, "radarr5", null, "customFormats", "qualityProfiles");
        var profiles = _bed.Get<QualityProfileRepository>();
        var count = profiles.Count();
        var movieScores = profiles.FindByName("Deutsch 1080p")!.FormatItems.ToHashSet();

        var preview = await _bed.PreviewAsync(Sonarr, "sonarr4", "qualityProfiles");
        Assert.EndsWith("joins the Radarr part", preview.Items.Single(i => i.Key == "qualityProfiles:4").Detail, StringComparison.Ordinal);

        var reports = await _bed.RunAsync(Sonarr, "sonarr4", null, "customFormats", "qualityProfiles");
        var report = reports[1];
        Assert.Equal((1, 2), (report.Created, report.Updated));
        Assert.Equal(count + 1, profiles.Count());

        var formats = _bed.Get<CustomFormatRepository>();
        var deutsch = profiles.FindByName("Deutsch 1080p")!;
        Assert.Superset(movieScores, deutsch.FormatItems.ToHashSet());
        Assert.Contains(new ProfileFormatItem(formats.FindByName("German DL", MediaType.Series)!.Id, 11000), deutsch.FormatItems);
        Assert.Contains(new ProfileFormatItem(formats.FindByName("x265 (HD)", MediaType.Series)!.Id, -10000), deutsch.FormatItems);

        // Qualities of both apps allowed; Radarr's cutoff, language and limits stay, with a warning.
        Assert.True(deutsch.IsAllowed(Quality.Hdtv720p.Id));
        Assert.True(deutsch.IsAllowed(Quality.Hdtv1080p.Id));
        Assert.True(deutsch.IsAllowed(Quality.Bluray720p.Id));
        Assert.Equal((Quality.WebDl1080p.Id, Language.German.Id), (deutsch.CutoffQualityId, deutsch.LanguageId));
        Assert.Contains(report.Messages, m => m.Level == MigrationMessageLevel.Warn && m.Text.Contains("The cutoff stays WEBDL-1080p (from Radarr)", StringComparison.Ordinal));

        var anime = profiles.FindByName("Anime")!;
        Assert.Contains(new ProfileFormatItem(formats.FindByName("Season Pack", MediaType.Series)!.Id, 10), anime.FormatItems);
        Assert.Contains(anime.FormatItems, i => formats.Find(i.CustomFormatId)!.Name == "Anime Dual Audio");

        var seriesOnly = profiles.FindByName("Nur Serien SD")!;
        Assert.Equal((Language.Any.Id, false, Quality.Sdtv.Id), (seriesOnly.LanguageId, seriesOnly.UpgradeAllowed, seriesOnly.CutoffQualityId));
        Assert.True(seriesOnly.IsAllowed(Quality.Bluray576p.Id));
        Assert.True(seriesOnly.IsAllowed(Quality.Bluray480p.Id));

        // Both apps again: nothing changes any more.
        var sonarrAgain = await _bed.RunAsync(Sonarr, "sonarr4", "qualityProfiles");
        Assert.Equal((0, 0, 3), (sonarrAgain.Created, sonarrAgain.Updated, sonarrAgain.Linked));
        var radarrAgain = await _bed.RunAsync(Radarr, "radarr5", "qualityProfiles");
        Assert.Equal((0, 0, 2), (radarrAgain.Created, radarrAgain.Updated, radarrAgain.Linked));
        Assert.Equal(count + 1, profiles.Count());
    }

    [Fact]
    public async Task Sonarr_v3_language_profiles_set_the_profile_language()
    {
        // HD-1080p is one of StreamZH's starting profiles: it is updated.
        var report = await _bed.RunAsync(Sonarr, "sonarr3", "qualityProfiles");
        Assert.Equal((1, 1), (report.Created, report.Updated));
        var profiles = _bed.Get<QualityProfileRepository>();

        var hd = profiles.FindByName("HD-1080p")!;
        Assert.Equal((Language.German.Id, true), (hd.LanguageId, hd.PreferDualLanguage));
        Assert.Empty(hd.FormatItems);
        Assert.Contains(report.Messages, m => m.Text.Contains("several language profiles", StringComparison.Ordinal));

        var anime = profiles.FindByName("Anime")!;
        Assert.Equal((Language.Japanese.Id, false, Quality.Remux1080p.Id), (anime.LanguageId, anime.PreferDualLanguage, anime.CutoffQualityId));
    }

    // ------------------------------------------------------------------ release profiles

    [Fact]
    public async Task Radarr_release_profiles_come_over_by_name()
    {
        var plan = await _bed.PreviewAsync(Radarr, "radarr5", "releaseProfiles");
        var unnamed = plan.Items.Single(i => i.Key == "releaseProfiles:2");
        Assert.Equal("Radarr release profile 2", unnamed.Label);
        Assert.Equal("must contain 1 · off", unnamed.Detail);
        Assert.Contains("NZBgeek", unnamed.Warning, StringComparison.Ordinal);
        Assert.Contains("tagged anime", unnamed.Warning, StringComparison.Ordinal);
        Assert.Contains("also apply to series", unnamed.Warning, StringComparison.Ordinal);

        var report = await _bed.RunAsync(Radarr, "radarr5", "releaseProfiles");
        Assert.Equal(2, report.Created);
        var stored = _bed.Get<ReleaseProfileRepository>().All();
        var hardsubs = stored.Single(p => p.Name == "Keine Hardsubs");
        Assert.Equal(["hardsub", "/\\b(HC|hardcoded)\\b/i"], hardsubs.Ignored);
        Assert.False(stored.Single(p => p.Name == "Radarr release profile 2").Enabled);

        var again = await _bed.RunAsync(Radarr, "radarr5", "releaseProfiles");
        Assert.Equal((0, 0, 2), (again.Created, again.Updated, again.Linked));
        Assert.Equal(2, _bed.Get<ReleaseProfileRepository>().All().Count);
    }

    [Fact]
    public async Task Sonarr_v3_preferred_words_become_preferred_terms()
    {
        var report = await _bed.RunAsync(Sonarr, "sonarr3", "releaseProfiles");
        Assert.Equal(2, report.Created);
        var stored = _bed.Get<ReleaseProfileRepository>().All();

        var first = stored.Single(p => p.Name == "Sonarr release profile 1");
        Assert.Empty(first.Required);
        Assert.Equal(["dubbed", "/\\bsubbed\\b/i"], first.Ignored);
        Assert.Equal([new("/\\b(German\\.DL)\\b/i", 100), new("x265", -50)], first.Preferred);

        var groups = stored.Single(p => p.Name == "Nur 1080p Gruppen");
        Assert.Equal(10_000, groups.Preferred.Single().Score);
        Assert.Contains(report.Messages, m => m.Text.Contains("limited to 10000", StringComparison.Ordinal));
        Assert.Contains(report.Messages, m => m.Text.Contains("{Custom Formats}", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ naming

    [Fact]
    public async Task Radarr_naming_keeps_streamzh_formats_it_cannot_use()
    {
        var plan = await _bed.PreviewAsync(Radarr, "radarr5", "naming");
        var folder = plan.Items.Single(i => i.Key == "naming:movieFolderFormat");
        Assert.Equal(MigrationAction.Skip, folder.Action);
        Assert.Contains("{Movie Collection}", folder.Warning, StringComparison.Ordinal);
        Assert.Equal(MigrationAction.Update, plan.Items.Single(i => i.Key == "naming:standardMovieFormat").Action);
        Assert.Equal("smart → dash", plan.Items.Single(i => i.Key == "naming:colonReplacement").Detail);

        var report = await _bed.RunAsync(Radarr, "radarr5", "naming");
        Assert.Equal(2, report.Updated);
        var values = _bed.Get<MediaManagementSettings>().Current;
        Assert.Equal(ColonReplacement.Dash, values.ColonReplacement);
        Assert.StartsWith("{Movie CleanTitle} {(Release Year)} {edition-{Edition Tags}}", values.StandardMovieFormat, StringComparison.Ordinal);
        Assert.Equal(MediaManagementValues.DefaultMovieFolderFormat, values.MovieFolderFormat);
        Assert.Contains(report.Messages, m => m.Level == MigrationMessageLevel.Warn && m.Text.Contains("{Movie Collection}", StringComparison.Ordinal));

        // Sonarr: anime naming; the colon replacement is one value for both apps.
        var sonarr = await _bed.RunAsync(Sonarr, "sonarr4", "naming");
        values = _bed.Get<MediaManagementSettings>().Current;
        Assert.Equal(ColonReplacement.Smart, values.ColonReplacement);
        Assert.Contains("{absolute:000}", values.AnimeEpisodeFormat, StringComparison.Ordinal);
        Assert.Equal("{Series TitleYear} [tvdbid-{TvdbId}]", values.SeriesFolderFormat);
        Assert.Equal("Season {season:00}", values.SeasonFolderFormat);
        Assert.Equal(MultiEpisodeStyle.PrefixedRange, values.MultiEpisodeStyle);
        Assert.Contains(sonarr.Messages, m => m.Text.Contains("one value for movies and series", StringComparison.Ordinal));

        var again = await _bed.RunAsync(Sonarr, "sonarr4", "naming");
        Assert.Equal(0, again.Updated);
    }

    [Fact]
    public async Task Sonarr_v3_naming_translates_preferred_words()
    {
        await _bed.RunAsync(Sonarr, "sonarr3", "naming");
        var values = _bed.Get<MediaManagementSettings>().Current;
        Assert.EndsWith("{[Custom Formats]}", values.StandardEpisodeFormat, StringComparison.Ordinal);
        Assert.Equal(MultiEpisodeStyle.Extend, values.MultiEpisodeStyle);
    }

    // ------------------------------------------------------------------ media management

    [Fact]
    public async Task Media_management_takes_over_what_streamzh_has()
    {
        var plan = await _bed.PreviewAsync(Radarr, "radarr5", "mediaManagement");
        Assert.Equal("off → on", plan.Items.Single(i => i.Key == "mediaManagement:setPermissions").Detail);
        Assert.Equal(MigrationAction.Skip, plan.Items.Single(i => i.Key == "mediaManagement:copyUsingHardlinks").Action);

        var report = await _bed.RunAsync(Radarr, "radarr5", "mediaManagement");
        Assert.Equal(0, report.Failed);
        var values = _bed.Get<MediaManagementSettings>().Current;
        Assert.Equal(
            (true, "775", "media", 500, true, "srt,nfo", FileDateMode.ReleaseDate, RescanAfterRefresh.AfterManualRefresh, true, false),
            (values.SetPermissions, values.ChmodFolder, values.ChownGroup, values.MinimumFreeSpaceMb, values.ImportExtraFiles, values.ExtraFileExtensions,
                values.ChangeFileDate, values.RescanAfterRefresh, values.DeleteEmptyFolders, values.RedownloadFailed));
        Assert.Equal(Core.Decisions.ProperHandling.DoNotPrefer, values.PropersAndRepacks);

        var again = await _bed.RunAsync(Radarr, "radarr5", "mediaManagement");
        Assert.Equal(0, again.Updated);

        await _bed.RunAsync(Sonarr, "sonarr4", "mediaManagement");
        values = _bed.Get<MediaManagementSettings>().Current;
        Assert.Equal((EpisodeTitleRequired.BulkSeasonReleases, "srt,ass", true), (values.EpisodeTitleRequired, values.ExtraFileExtensions, values.RedownloadFailed));
    }

    [Fact]
    public async Task A_recycle_bin_streamzh_cannot_write_to_keeps_streamzhs()
    {
        var missing = Path.Combine(_bed.DataDirectory, "no-such-folder", "recycle");
        _bed.Handler.Overrides["/config/mediamanagement"] = JsonSerializer.Serialize(new { recycleBin = missing, recycleBinCleanupDays = 14 });
        var plan = await _bed.PreviewAsync(Radarr, "radarr5", "mediaManagement");
        var bin = plan.Items.Single(i => i.Key == "mediaManagement:recycleBin");
        Assert.Equal(MigrationAction.Skip, bin.Action);
        Assert.NotNull(bin.Warning);

        await _bed.RunAsync(Radarr, "radarr5", "mediaManagement");
        var values = _bed.Get<MediaManagementSettings>().Current;
        Assert.Equal((string.Empty, 14), (values.RecycleBin, values.RecycleBinCleanupDays));
        Assert.False(Directory.Exists(missing));
    }

    // ------------------------------------------------------------------ connections

    [Fact]
    public async Task Connections_with_masked_secrets_ask_for_them_and_stay_off_without()
    {
        var plan = await _bed.PreviewAsync(Radarr, "radarr5", "notifications");
        var plex = plan.Items.Single(i => i.Key == "notifications:1");
        Assert.True(plex.NeedsSecret);
        Assert.Equal("Plex Media Server · import, upgrade, rename", plex.Detail);
        Assert.True(plan.Items.Single(i => i.Key == "notifications:2").NeedsSecret);
        Assert.Equal(MigrationAction.Skip, plan.Items.Single(i => i.Key == "notifications:3").Action);
        Assert.False(plan.Items.Single(i => i.Key == "notifications:4").NeedsSecret);

        var report = await _bed.RunAsync(Radarr, "radarr5", "notifications");
        Assert.Equal((3, 1, 0), (report.Created, report.Skipped, report.Failed));
        var repository = _bed.Get<ConnectionRepository>();
        var storedPlex = repository.FindByName("Plex")!;
        Assert.False(storedPlex.Enable);
        Assert.Equal(_bed.MoviesFolder, new ConnectionSettings(storedPlex.Settings).Text("mapFrom"));
        Assert.Equal("/data/movies", new ConnectionSettings(storedPlex.Settings).Text("mapTo"));
        Assert.Equal(new ConnectionTriggers(OnImport: true, OnUpgrade: true, OnRename: true), storedPlex.Triggers);

        var discord = repository.FindByName("Discord Filme")!;
        Assert.False(discord.Enable);
        Assert.Equal(["overview", "genres", "quality", "size", "links", "poster", "indexer"], new ConnectionSettings(discord.Settings).List("grabFields"));
        Assert.Equal(new ConnectionTriggers(OnGrab: true, OnImport: true, OnUpgrade: true, OnHealthIssue: true), discord.Triggers);

        var webhook = repository.FindByName("Webhook")!;
        Assert.True(webhook.Enable);
        Assert.Equal("POST", new ConnectionSettings(webhook.Settings).Text("method"));

        // With the token typed in the wizard: Plex is switched on.
        var request = _bed.Request(Radarr) with { Secrets = new Dictionary<string, string> { ["notifications:1"] = "plex-token-123" } };
        var second = await _bed.RunAsync(Radarr, "radarr5", "notifications", request);
        Assert.Equal(0, second.Created);
        storedPlex = repository.FindByName("Plex")!;
        Assert.True(storedPlex.Enable);
        Assert.Equal("plex-token-123", new ConnectionSettings(storedPlex.Settings).Text("authToken"));
        Assert.Equal(3, repository.All().Count);

        // Third run without the token: the stored one is kept, nothing changes.
        var third = await _bed.PreviewAsync(Radarr, "radarr5", "notifications");
        Assert.Equal(MigrationAction.Link, third.Items.Single(i => i.Key == "notifications:1").Action);
        Assert.False(third.Items.Single(i => i.Key == "notifications:1").NeedsSecret);
    }

    [Fact]
    public async Task Sonarr_connections_map_jellyfin_and_several_secrets()
    {
        var request = _bed.Request(Sonarr) with
        {
            Secrets = new Dictionary<string, string> { ["notifications:3"] = "app-token", ["notifications:3.userKey"] = "user-key" },
        };
        var report = await _bed.RunAsync(Sonarr, "sonarr4", "notifications", request);
        Assert.Equal(3, report.Created);
        var repository = _bed.Get<ConnectionRepository>();

        var jellyfin = repository.FindByName("Jellyfin")!;
        Assert.Equal((ConnectCatalog.Jellyfin, false), (jellyfin.Implementation, jellyfin.Enable));
        Assert.Equal("http://jellyfin:8096", new ConnectionSettings(jellyfin.Settings).Text("url"));
        Assert.Equal(_bed.TvFolder, new ConnectionSettings(jellyfin.Settings).Text("mapFrom"));

        var pushover = repository.FindByName("Pushover")!;
        Assert.True(pushover.Enable);
        var settings = new ConnectionSettings(pushover.Settings);
        Assert.Equal(("app-token", "user-key", "1", "phone", "magic"), (settings.Text("apiKey"), settings.Text("userKey"), settings.Text("priority"), settings.Text("devices"), settings.Text("sound")));
        Assert.Equal(new ConnectionTriggers(OnImport: true, OnHealthIssue: true, OnHealthRestored: true, IncludeHealthWarnings: true), pushover.Triggers);
    }

    // ------------------------------------------------------------------ exclusions

    [Fact]
    public async Task Radarr_exclusions_are_taken_over_once()
    {
        var report = await _bed.RunAsync(Radarr, "radarr5", "importListExclusions");
        Assert.Equal(2, report.Created);
        var exclusions = _bed.Get<ImportListRepository>().Exclusions();
        Assert.Contains(exclusions, e => e.TmdbId == 242 && e.Title == "Der Pate - Teil III" && e.Year == 1990 && e.MediaType == MediaType.Movie);

        var again = await _bed.RunAsync(Radarr, "radarr5", "importListExclusions");
        Assert.Equal((0, 2), (again.Created, again.Linked));
        Assert.Equal(2, _bed.Get<ImportListRepository>().Exclusions().Count);
    }

    [Fact]
    public async Task Sonarr_exclusions_need_the_series_in_streamzh()
    {
        var root = _bed.Get<RootFolderRepository>().Add(_bed.TvFolder, MediaType.Series);
        _bed.Get<SeriesRepository>().Add(new Series
        {
            TmdbId = 1396, TvdbId = 81189, Title = "Breaking Bad", SortTitle = "breaking bad", Year = 2008, QualityProfileId = 1, RootFolderId = root,
            FolderPath = Path.Combine(_bed.TvFolder, "Breaking Bad (2008)"), Added = DateTime.UtcNow,
        });

        var plan = await _bed.PreviewAsync(Sonarr, "sonarr4", "importListExclusions");
        Assert.Equal(MigrationAction.Create, plan.Items.Single(i => i.Key == "importListExclusions:81189").Action);
        var thrones = plan.Items.Single(i => i.Key == "importListExclusions:121361");
        Assert.Equal(MigrationAction.Skip, thrones.Action);
        Assert.Contains("follows with the series", thrones.Reason, StringComparison.Ordinal);

        var report = await _bed.RunAsync(Sonarr, "sonarr4", "importListExclusions");
        Assert.Equal((1, 1), (report.Created, report.Skipped));
        var exclusion = Assert.Single(_bed.Get<ImportListRepository>().Exclusions());
        Assert.Equal((MediaType.Series, 1396), (exclusion.MediaType, exclusion.TmdbId));
    }

    // ------------------------------------------------------------------ left out, GET only, whole run

    [Fact]
    public async Task Items_left_out_in_the_wizard_are_not_taken_over()
    {
        var request = _bed.Request(Radarr) with { Exclude = ["customFormats:1", "importListExclusions:242", "naming:colonReplacement"] };
        var reports = await _bed.RunAsync(Radarr, "radarr5", request, "customFormats", "naming", "importListExclusions");

        Assert.Null(_bed.Get<CustomFormatRepository>().FindByName("German DL", MediaType.Movie));
        Assert.Contains(reports[0].Messages, m => m.Item == "German DL" && m.Text == "Left out in the wizard.");
        Assert.Equal(ColonReplacement.Smart, _bed.Get<MediaManagementSettings>().Current.ColonReplacement);
        Assert.DoesNotContain(_bed.Get<ImportListRepository>().Exclusions(), e => e.TmdbId == 242);
    }

    [Fact]
    public async Task A_whole_run_through_the_migration_service_only_reads_the_old_app()
    {
        var service = _bed.Get<MigrationService>();
        var request = _bed.Request(Sonarr);
        _bed.Handler.App = "sonarr4";

        var preview = await service.PreviewAsync(Sonarr, request, CancellationToken.None);
        Assert.Equal("4.0.15.2941", preview.App.Version);
        Assert.All(preview.Sections.Where(s => SettingsKeys.Contains(s.Key)), s => Assert.DoesNotContain(s.Warnings, w => w.StartsWith("Could not read", StringComparison.Ordinal)));
        Assert.Contains(preview.Paths, p => p.Remote == "/tv" && p.Exists && p.Mapped);

        var run = await service.StartAsync(Sonarr, request with { Sections = ["rootFolders", "customFormats", "qualityProfiles", "releaseProfiles", "naming", "mediaManagement", "notifications", "importListExclusions"] }, "johndoe", CancellationToken.None);
        MigrationRun done = run;
        for (var i = 0; i < 400 && done.Finished is null; i++)
        {
            await Task.Delay(25, CancellationToken.None);
            done = service.Find(run.Id)!;
        }

        Assert.Equal(MigrationRunStatus.Done, done.Status);
        Assert.All(done.Report.Sections, s => Assert.Equal(0, s.Failed));
        Assert.Equal(8, done.Report.Sections.Count);
        Assert.All(_bed.Handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Contains(_bed.Get<QualityProfileRepository>().All(), p => p.Name == "Nur Serien SD");
    }

    [Fact]
    public async Task An_endpoint_the_old_version_lacks_is_a_warning_of_that_part()
    {
        // The Sonarr v3 fixtures have no root folders: that part warns, the others still preview.
        _bed.Handler.App = "sonarr3";
        var preview = await _bed.Get<MigrationService>().PreviewAsync(Sonarr, _bed.Request(Sonarr), CancellationToken.None);

        var roots = preview.Sections.Single(s => s.Key == "rootFolders");
        Assert.Contains(roots.Warnings, w => w.Contains("Sonarr 3.0.10.1567 does not have /api/v3/rootfolder", StringComparison.Ordinal));
        Assert.Equal(2, preview.Sections.Single(s => s.Key == "qualityProfiles").Items.Count);
        Assert.Equal(2, preview.Sections.Single(s => s.Key == "releaseProfiles").Items.Count);
    }

    public void Dispose() => _bed.Dispose();

    private static (double Min, double? Max, double? Preferred) Sizes(QualityDefinitionRepository repository, Quality quality)
    {
        var d = repository.Find(quality.Id)!;
        return (d.MinSize, d.MaxSize, d.PreferredSize);
    }
}
