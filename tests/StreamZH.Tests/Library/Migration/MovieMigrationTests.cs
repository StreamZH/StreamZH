// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Arr.Library;

namespace StreamZH.Tests.Library.Migration;

public sealed class MovieMigrationTests : IDisposable
{
    private readonly LibraryMigrationTestBed _bed = new();

    [Fact]
    public async Task Preview_lists_every_movie_with_what_happens_and_changes_nothing()
    {
        var context = _bed.Context(MigrationSources.Radarr);

        var plan = await _bed.MoviesSection.PreviewAsync(context, CancellationToken.None);

        Assert.Equal(["Arrival (2016)", "Blade Runner 2049 (2017)", "Dune: Part Two (2024)", "Freitag der 13. (2009)", "Unbekannter Film (1999)"], plan.Items.Select(i => i.Label));
        var freitag = plan.Items.Single(i => i.Key == "movies:13207");
        Assert.Equal(MigrationAction.Create, freitag.Action);
        Assert.Equal($"Bluray-1080p · 9.2 GB · {Path.Combine(_bed.MoviesFolder, "Freitag der 13. (2009)")}", freitag.Detail);
        Assert.Null(freitag.Warning);

        // Radarr's "German Remux" profile has no match in StreamZH; Arrival's file is not on disk.
        var arrival = plan.Items.Single(i => i.Key == "movies:329865");
        Assert.Contains("'German Remux' is not in StreamZH; 'Deutsch 1080p' is used", arrival.Warning, StringComparison.Ordinal);
        Assert.Contains("Arrival.2016.German.DL.1080p.BluRay.x264-SAViOUR.mkv was not found", arrival.Warning, StringComparison.Ordinal);
        Assert.StartsWith("No file", plan.Items.Single(i => i.Key == "movies:335984").Detail, StringComparison.Ordinal);
        var unknown = plan.Items.Single(i => i.Label.StartsWith("Unbekannter", StringComparison.Ordinal));
        Assert.Equal((MigrationAction.Skip, "Radarr has no TMDB id for this movie."), (unknown.Action, unknown.Reason));

        Assert.Contains("StreamZH has no tags yet; Radarr's movie tags are not taken over.", plan.Warnings!);
        Assert.Contains(plan.Warnings!, w => w.Contains("minimum availability", StringComparison.Ordinal));
        Assert.Equal([new MigrationPathCheck("/movies", _bed.MoviesFolder, true, true)], context.Paths.Checks());

        // Preview: Radarr only, no TMDB, no writes.
        Assert.Empty(_bed.Tmdb.Calls);
        Assert.Empty(_bed.Movies.All());
        Assert.All(_bed.Radarr.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Run_adds_the_movies_and_links_the_files_where_they_are()
    {
        var report = await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr));

        Assert.Equal((4, 0, 0, 1, 0), (report.Created, report.Updated, report.Linked, report.Skipped, report.Failed));
        var movies = _bed.Movies.All().ToDictionary(m => m.TmdbId);
        Assert.Equal(4, movies.Count);

        var freitag = movies[13207];
        Assert.Equal("Freitag der 13.", freitag.Title);
        Assert.Equal(Path.Combine(_bed.MoviesFolder, "Freitag der 13. (2009)"), freitag.FolderPath);
        Assert.Equal("Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS.mkv", freitag.File?.RelativePath);
        Assert.Equal(4096, freitag.File?.Size);
        Assert.True(freitag.Monitored);
        Assert.Equal(2, freitag.QualityProfileId); // Radarr's "HD-1080p" (id 4) → StreamZH's "HD-1080p" (id 2), by name.
        Assert.Equal(new DateTime(2023, 5, 1, 20, 0, 0, DateTimeKind.Utc), freitag.Added);

        var root = Assert.Single(new RootFolderRepository(_bed.Database).All());
        Assert.Equal((_bed.MoviesFolder, MediaType.Movie), (root.Path, root.MediaType));
        Assert.All(movies.Values, m => Assert.Equal(root.Id, m.RootFolderId));

        var details = _bed.Details.Find(_bed.MovieFile("Freitag der 13. (2009)", "Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS.mkv"))!;
        Assert.Equal(Quality.Bluray1080p.Id, details.QualityId);
        Assert.Equal([Language.German.Id, Language.English.Id], details.LanguageIds);
        Assert.Equal(("DETAiLS", "Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS"), (details.ReleaseGroup, details.SceneName));
        Assert.Equal(new DateTime(2023, 5, 1, 20, 15, 0, DateTimeKind.Utc), details.DateAdded);
        Assert.Equal("x264", details.MediaInfo?.Video?.Codec);
        Assert.Equal(1080, details.MediaInfo?.Video?.Resolution);
        Assert.Equal([Language.German, Language.English], details.MediaInfo?.AudioLanguages);
        Assert.Equal(0, details.MediaInfo?.Revision); // ffprobe analyses it again when it can.
        Assert.Null(details.MediaInfoUpdated);

        // The REPACK keeps its revision; Dolby Vision from the media info.
        var dune = _bed.Details.Find(_bed.MovieFile("Dune Part Two (2024)", "Dune.Part.Two.2024.German.DL.2160p.WEB.H265-W4K.mkv"))!;
        Assert.Equal((Quality.WebDl2160p.Id, 2, true), (dune.QualityId, dune.RevisionVersion, dune.IsRepack));
        Assert.Equal(MediaHdrType.DolbyVisionHdr10, dune.MediaInfo?.Video?.Hdr);
        Assert.Equal(3, movies[693134].QualityProfileId); // Ultra-HD

        // Arrival: the file is not on disk → missing, and the unknown profile falls back to the default.
        Assert.Null(movies[329865].File);
        Assert.False(movies[329865].Monitored);
        Assert.Equal(QualityDefaults.DefaultProfileId, movies[329865].QualityProfileId);
        Assert.Contains(report.Messages, m => m.Level == MigrationMessageLevel.Warn && m.Text.Contains("Arrival.2016.German.DL.1080p.BluRay.x264-SAViOUR.mkv was not found", StringComparison.Ordinal));
        Assert.Contains(report.Messages, m => m.Level == MigrationMessageLevel.Warn && m.Text.Contains("'German Remux'", StringComparison.Ordinal));
        Assert.Null(movies[335984].File);

        // Files stay where they are; Radarr was only read.
        Assert.True(File.Exists(_bed.MovieFile("Freitag der 13. (2009)", "Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS.mkv")));
        Assert.All(_bed.Radarr.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task A_second_run_links_instead_of_adding_twice()
    {
        await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr));
        var first = _bed.Movies.All().ToDictionary(m => m.TmdbId, m => (m.Id, m.File, m.QualityProfileId));
        var tmdbCalls = _bed.Tmdb.Calls.Count;

        var preview = await _bed.MoviesSection.PreviewAsync(_bed.Context(MigrationSources.Radarr), CancellationToken.None);
        Assert.Equal(4, preview.Items.Count(i => i.Action == MigrationAction.Link));

        var report = await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr));

        Assert.Equal((0, 0, 4, 1), (report.Created, report.Updated, report.Linked, report.Skipped));
        Assert.Equal(first, _bed.Movies.All().ToDictionary(m => m.TmdbId, m => (m.Id, m.File, m.QualityProfileId)));
        Assert.Single(new RootFolderRepository(_bed.Database).All());
        Assert.Equal(tmdbCalls, _bed.Tmdb.Calls.Count);
    }

    [Fact]
    public async Task Left_out_movies_are_skipped()
    {
        var report = await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr, exclude: ["movies:693134", "movies:329865"]));

        Assert.Equal((2, 3), (report.Created, report.Skipped));
        Assert.Equal([13207, 335984], _bed.Movies.All().Select(m => m.TmdbId).Order());
        Assert.Contains(report.Messages, m => m.Item == "Dune: Part Two (2024)" && m.Text == "Left out in the wizard.");
    }

    [Fact]
    public async Task Monitor_none_adds_every_movie_unmonitored_and_overwrite_updates_existing_movies()
    {
        await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr, new Dictionary<string, string> { ["movies.monitor"] = "none" }));
        Assert.All(_bed.Movies.All(), m => Assert.False(m.Monitored));

        // Without "overwrite" StreamZH's data stays.
        var freitag = _bed.Movies.FindByTmdbId(13207)!;
        freitag.QualityProfileId = 5;
        _bed.Movies.Update(freitag);
        await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr));
        Assert.Equal((false, 5), (_bed.Movies.FindByTmdbId(13207)!.Monitored, _bed.Movies.FindByTmdbId(13207)!.QualityProfileId));

        var report = await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr, new Dictionary<string, string> { ["movies.overwrite"] = "true" }));

        Assert.Equal(4, report.Updated);
        Assert.Equal((true, 2), (_bed.Movies.FindByTmdbId(13207)!.Monitored, _bed.Movies.FindByTmdbId(13207)!.QualityProfileId));
        Assert.False(_bed.Movies.FindByTmdbId(329865)!.Monitored);
    }

    [Fact]
    public async Task Existing_movies_keep_their_data_and_get_the_file_linked()
    {
        var rootId = new RootFolderRepository(_bed.Database).Add(_bed.MoviesFolder, MediaType.Movie);
        var folder = Path.Combine(_bed.MoviesFolder, "Freitag der 13. (2009)");
        _bed.Movies.Add(new Movie
        {
            TmdbId = 13207, Title = "Friday the 13th", OriginalTitle = "Friday the 13th", SortTitle = "friday the 13th", Year = 2009,
            Monitored = false, QualityProfileId = 1, RootFolderId = rootId, FolderPath = folder, Added = DateTime.UtcNow,
        });

        var report = await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr));

        Assert.Equal(1, report.Linked);
        var movie = _bed.Movies.FindByTmdbId(13207)!;
        Assert.Equal(("Friday the 13th", false, 1), (movie.Title, movie.Monitored, movie.QualityProfileId));
        Assert.Equal("Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS.mkv", movie.File?.RelativePath);
        Assert.Equal(Quality.Bluray1080p.Id, _bed.Details.Find(Path.Combine(folder, movie.File!.RelativePath))?.QualityId);
    }

    [Fact]
    public async Task Profile_and_root_folder_mappings_of_the_settings_sections_win()
    {
        var otherRoot = Path.Combine(_bed.Root, "elsewhere");
        Directory.CreateDirectory(otherRoot);
        var rootId = new RootFolderRepository(_bed.Database).Add(otherRoot, MediaType.Movie);
        var context = _bed.Context(MigrationSources.Radarr);
        context.Ids.Set("qualityProfile", 7, 5);
        context.Ids.Set("rootFolder", 1, rootId);

        await _bed.RunAsync(_bed.MoviesSection, context);

        var arrival = _bed.Movies.FindByTmdbId(329865)!;
        Assert.Equal((5, rootId), (arrival.QualityProfileId, arrival.RootFolderId));
        Assert.Equal(Path.Combine(_bed.MoviesFolder, "Arrival (2016)"), arrival.FolderPath);
        Assert.Equal(_bed.Movies.FindByTmdbId(13207)!.Id, context.Ids.Get("movie", 1));
    }

    [Fact]
    public async Task Without_a_path_mapping_the_preview_warns()
    {
        new RootFolderRepository(_bed.Database).Add(_bed.MoviesFolder, MediaType.Movie);
        var context = _bed.Context(MigrationSources.Radarr, mappings: []);
        var plan = await _bed.MoviesSection.PreviewAsync(context, CancellationToken.None);

        var freitag = plan.Items.Single(i => i.Key == "movies:13207");
        Assert.Equal(MigrationAction.Create, freitag.Action);
        Assert.Contains("StreamZH cannot see the folder /movies/Freitag der 13. (2009); add a path mapping.", freitag.Warning, StringComparison.Ordinal);
        Assert.Contains("Radarr's root folder /movies has no match in StreamZH", freitag.Warning, StringComparison.Ordinal);
        Assert.Contains(context.Paths.Checks(), c => c is { Remote: "/movies", Exists: false, Mapped: false });
    }

    [Fact]
    public async Task Without_TMDB_the_preview_warns_and_the_run_stops_before_changing_anything()
    {
        _bed.Tmdb.IsConfigured = false;

        var plan = await _bed.MoviesSection.PreviewAsync(_bed.Context(MigrationSources.Radarr), CancellationToken.None);
        var report = await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr));

        Assert.Contains(plan.Warnings!, w => w.StartsWith("TMDB is not set up", StringComparison.Ordinal));
        Assert.Equal(1, report.Failed);
        Assert.Contains("TMDB is not set up", Assert.Single(report.Messages).Text, StringComparison.Ordinal);
        Assert.Empty(_bed.Movies.All());
        Assert.Empty(new RootFolderRepository(_bed.Database).All());
    }

    [Fact]
    public void Qualities_and_languages_map_by_name_for_sonarr_and_by_id_for_radarr()
    {
        Assert.Equal(Quality.Remux1080p, ArrLibraryMapping.QualityOf(MigrationSources.Sonarr, new ArrQuality(20, "Bluray-1080p Remux", "blurayRaw", 1080)));
        Assert.Equal(Quality.Bluray480p, ArrLibraryMapping.QualityOf(MigrationSources.Radarr, new ArrQuality(20, "Bluray-480p", "bluray", 480, "none")));
        Assert.Equal(Quality.WebRip1080p, ArrLibraryMapping.QualityOf(MigrationSources.Sonarr, new ArrQuality(15, "WEBRip-1080p", "webRip", 1080)));
        Assert.Equal(Quality.WebDl720p, ArrLibraryMapping.QualityOf(MigrationSources.Radarr, new ArrQuality(5, "Renamed in Radarr", "webdl", 720)));
        Assert.Equal(Quality.Remux2160p, ArrLibraryMapping.QualityOf(MigrationSources.Sonarr, new ArrQuality(99, "Something", "blurayRaw", 2160)));
        Assert.Equal(
            [Language.German, Language.PortugueseBrazil],
            ArrLibraryMapping.LanguagesOf([new ArrLanguage(4, "German"), new ArrLanguage(30, "Portuguese (Brazil)"), new ArrLanguage(-2, "Original")]));
    }

    public void Dispose() => _bed.Dispose();
}
