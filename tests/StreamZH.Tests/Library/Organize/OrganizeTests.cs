// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Import;
using StreamZH.Library.Organize;
using StreamZH.Tests.Library.Import;

namespace StreamZH.Tests.Library.Organize;

/// <summary>The import test bed plus organize, the mass editor and the rename log, with real folders in a temp directory.</summary>
public sealed class OrganizeTestBed : IDisposable
{
    public OrganizeTestBed()
    {
        Log = new OrganizeLogRepository(Import.Bed.Database);
        Tracker = new LibraryRunTracker(Import.Bed.Events, Import.Bed.Time);
        Mover = new FolderMover(Import.Files, NullLogger<FolderMover>.Instance, Import.Bed.Time);
        RootFolders = new RootFolderRepository(Import.Bed.Database);
    }

    public ImportTestBed Import { get; } = new();
    public OrganizeLogRepository Log { get; }
    public LibraryRunTracker Tracker { get; }
    public FolderMover Mover { get; }
    public RootFolderRepository RootFolders { get; }
    public string Media => Import.Media;

    public OrganizePlanner Planner(bool windows = false) =>
        new(Import.Settings, Import.Bed.Movies, Import.Bed.Series, Import.Rename()) { WindowsPathRules = windows };

    public ItemRelocator Relocator() => new(
        Import.Settings, Import.Bed.Movies, Import.Bed.Series, Import.EpisodeFiles, Import.Details, Mover, Import.Files, NullLogger<ItemRelocator>.Instance);

    public OrganizeService Organize(bool windows = false) => new(
        Planner(windows), Relocator(), Log, Import.Bed.History, Import.Bed.Movies, Import.Bed.Series, Tracker, Import.Bed.Events,
        NullLogger<OrganizeService>.Instance, Import.Bed.Time);

    public MassEditService MassEdit() => new(
        Import.Bed.Movies, Import.Bed.Series, RootFolders, new QualityProfileService(Import.Bed.Profiles, Import.Bed.Config, NullLogger<QualityProfileService>.Instance), Import.Details,
        Import.Settings, Relocator(), Mover, Import.Bed.History, Tracker, Import.Bed.Events, NullLogger<MassEditService>.Instance, Import.Bed.Time);

    /// <summary>Puts Dune into a release-named folder with the given files (relative paths); the first one is the movie file.</summary>
    public Movie ReleaseMovie(string folderName, params string[] files)
    {
        var movie = Import.Dune;
        movie.FolderPath = Path.Combine(Media, folderName);
        foreach (var file in files)
        {
            ImportTestBed.WriteFile(Path.Combine(movie.FolderPath, file), 1);
        }

        movie.File = files.Length == 0 ? null : new MediaFile(files[0], ImportTestBed.Mb, DateTime.UtcNow);
        Import.Bed.Movies.Update(movie);
        return Import.Dune;
    }

    public static string[] Tree(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    public void Dispose() => Import.Dispose();
}

public sealed class OrganizeTests : IDisposable
{
    private const string Release = "Dune.2021.German.DL.1080p.BluRay.x264-GRP";
    private readonly OrganizeTestBed _bed = new();

    [Fact]
    public void Preview_shows_the_new_folder_files_subtitles_and_side_files()
    {
        _bed.ReleaseMovie(Release, $"{Release}.mkv", $"{Release}.de.forced.srt", $"{Release}.nfo", "Subs/2_English.srt", "movie.nfo", "poster.jpg");

        var item = Assert.Single(_bed.Planner().Plan(MediaType.Movie, [_bed.Import.Dune.Id]));

        Assert.True(item.FolderChanged);
        Assert.False(item.CaseOnly);
        Assert.Equal(Path.Combine(_bed.Media, "Dune (2021)"), item.NewFolder);
        Assert.Equal(
            [
                ($"{Release}.mkv", "Dune (2021) Bluray-1080p.mkv", OrganizeFileKind.Video),
                ($"{Release}.de.forced.srt", "Dune (2021) Bluray-1080p.de.forced.srt", OrganizeFileKind.Subtitle),
                ($"{Release}.nfo", "Dune (2021) Bluray-1080p.nfo", OrganizeFileKind.Metadata),
                (Path.Combine("Subs", "2_English.srt"), "Dune (2021) Bluray-1080p.en.srt", OrganizeFileKind.Subtitle),
            ],
            item.Files.Select(f => (f.ExistingPath, f.NewPath, f.Kind)));
        Assert.Empty(item.Conflicts);
        Assert.Equal(2, item.OtherFileCount);
        Assert.Equal(["movie.nfo", "poster.jpg"], item.OtherFiles);
        Assert.True(item.CanApply);
    }

    [Fact]
    public async Task Apply_renames_folder_and_files_updates_the_library_and_can_be_undone()
    {
        _bed.ReleaseMovie(Release, $"{Release}.mkv", $"{Release}.de.srt", "Subs/English.srt", "poster.jpg");
        var oldFolder = _bed.Import.Dune.FolderPath;
        var oldVideo = Path.Combine(oldFolder, $"{Release}.mkv");
        _bed.Import.Details.Save(new MediaFileDetails(oldVideo, StreamZH.Core.Quality.Quality.Bluray1080p.Id, 1, 0, false, [4], "GRP", Release, null, null, null, DateTime.UtcNow));
        var organize = _bed.Organize();

        var result = await organize.ApplyAsync(MediaType.Movie, [_bed.Import.Dune.Id]);

        Assert.Equal((1, 0, 0, 0), (result.Done, result.Unchanged, result.Skipped, result.Failed));
        var newFolder = Path.Combine(_bed.Media, "Dune (2021)");
        Assert.False(Directory.Exists(oldFolder));
        Assert.Equal(
            ["Dune (2021) Bluray-1080p.de.srt", "Dune (2021) Bluray-1080p.en.srt", "Dune (2021) Bluray-1080p.mkv", "poster.jpg"],
            OrganizeTestBed.Tree(newFolder));
        var movie = _bed.Import.Dune;
        Assert.Equal(newFolder, movie.FolderPath);
        Assert.Equal("Dune (2021) Bluray-1080p.mkv", movie.File!.RelativePath);
        Assert.NotNull(_bed.Import.Details.Find(Path.Combine(newFolder, "Dune (2021) Bluray-1080p.mkv")));
        Assert.Null(_bed.Import.Details.Find(oldVideo));
        Assert.Equal(newFolder, Assert.Single(_bed.Import.Renamed).Folder);
        var history = Assert.Single(_bed.Import.Bed.History.Page(1, 10).Records);
        Assert.Equal(HistoryEventType.Renamed, history.EventType);
        Assert.Equal(Release, history.SourceTitle);
        Assert.DoesNotContain(_bed.Planner().Plan(MediaType.Movie, [movie.Id]), p => p.HasChanges);

        // Undo: everything back where it was.
        var run = organize.Undoable();
        Assert.NotNull(run);
        Assert.Equal(result.RunId, run.Id);
        var undo = await organize.UndoAsync(run.Id);
        Assert.Equal(1, undo.Done);
        Assert.False(Directory.Exists(newFolder));
        Assert.Equal([$"{Release}.de.srt", $"{Release}.mkv", "Subs/English.srt", "poster.jpg"], OrganizeTestBed.Tree(oldFolder));
        Assert.Equal(oldFolder, _bed.Import.Dune.FolderPath);
        Assert.Equal($"{Release}.mkv", _bed.Import.Dune.File!.RelativePath);
        Assert.NotNull(_bed.Import.Details.Find(oldVideo));
        Assert.Null(organize.Undoable());
        await Assert.ThrowsAsync<LibraryException>(() => organize.UndoAsync(run.Id));
    }

    [Fact]
    public async Task Undo_is_offered_for_a_day_only()
    {
        _bed.ReleaseMovie(Release, $"{Release}.mkv");
        var organize = _bed.Organize();
        await organize.ApplyAsync(MediaType.Movie, [_bed.Import.Dune.Id]);
        Assert.NotNull(organize.Undoable());

        _bed.Import.Bed.Time.Now += OrganizeService.UndoWindow + TimeSpan.FromMinutes(1);
        Assert.Null(organize.Undoable());
    }

    [Fact]
    public void Quality_of_files_without_stored_details_comes_from_the_file_or_folder_name()
    {
        _bed.ReleaseMovie("Dune.2021.GERMAN.TELESYNC.1080p.x264-LiNEUP", "lnp-dune.mkv");

        var item = Assert.Single(_bed.Planner().Plan(MediaType.Movie, [_bed.Import.Dune.Id]));

        var video = Assert.Single(item.Files);
        Assert.Equal("lnp-dune.mkv", video.ExistingPath);
        Assert.StartsWith("Dune (2021) TELESYNC", video.NewPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_with_a_conflict_are_skipped_and_nothing_moves()
    {
        _bed.ReleaseMovie(Release, $"{Release}.mkv");
        Directory.CreateDirectory(Path.Combine(_bed.Media, "Dune (2021)"));

        var item = Assert.Single(_bed.Planner().Plan(MediaType.Movie, [_bed.Import.Dune.Id]));
        Assert.Contains("already exists", Assert.Single(item.Conflicts), StringComparison.Ordinal);
        Assert.False(item.CanApply);

        var result = await _bed.Organize().ApplyAsync(MediaType.Movie, [_bed.Import.Dune.Id]);
        Assert.Equal((0, 1), (result.Done, result.Skipped));
        Assert.Contains("Skipped", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_bed.Media, Release, $"{Release}.mkv")));
        Assert.Null(result.RunId);
    }

    [Fact]
    public void A_file_whose_new_name_is_taken_is_a_conflict()
    {
        _bed.ReleaseMovie(Release, $"{Release}.mkv", "Dune (2021) Bluray-1080p.mkv");

        var item = Assert.Single(_bed.Planner().Plan(MediaType.Movie, [_bed.Import.Dune.Id]));

        Assert.Contains(item.Conflicts, c => c == "Dune (2021) Bluray-1080p.mkv already exists.");
    }

    [Fact]
    public void Two_selected_items_cannot_get_the_same_folder_and_another_item_may_not_own_it()
    {
        _bed.ReleaseMovie(Release, $"{Release}.mkv");
        var other = new Movie
        {
            TmdbId = 1, Title = "Dune", OriginalTitle = "Dune", SortTitle = "dune", Year = 2021, QualityProfileId = QualityDefaults.GermanProfileId,
            RootFolderId = _bed.Import.Dune.RootFolderId, FolderPath = Path.Combine(_bed.Media, "Dune.2021.Other"), Added = DateTime.UtcNow,
        };
        _bed.Import.Bed.Movies.Add(other);

        var plans = _bed.Planner().Plan(MediaType.Movie, [_bed.Import.Dune.Id, other.Id]);
        Assert.Empty(plans[0].Conflicts);
        Assert.Contains("Another selected item", Assert.Single(plans[1].Conflicts), StringComparison.Ordinal);

        // Not selected, but already in the folder.
        var owner = _bed.Import.Dune;
        owner.FolderPath = Path.Combine(_bed.Media, "Dune (2021)");
        _bed.Import.Bed.Movies.Update(owner);
        Assert.Contains("Another item in the library", Assert.Single(_bed.Planner().Plan(MediaType.Movie, [other.Id])[0].Conflicts), StringComparison.Ordinal);
    }

    [Fact]
    public void Too_long_paths_are_conflicts_with_the_Windows_rules()
    {
        _bed.Import.Configure(v => v with { StandardMovieFormat = "{Movie Title} " + new string('x', 200) + " {Quality Full}" });
        _bed.ReleaseMovie(Release, $"{Release}.mkv");

        Assert.Empty(_bed.Planner(windows: false).Plan(MediaType.Movie, [_bed.Import.Dune.Id])[0].Conflicts);
        Assert.Contains(_bed.Planner(windows: true).Plan(MediaType.Movie, [_bed.Import.Dune.Id])[0].Conflicts, c => c.Contains("Windows allows 259", StringComparison.Ordinal));
        Assert.NotNull(FolderMover.PathProblem("/media/" + new string('a', 256), windows: false));
        Assert.Null(FolderMover.PathProblem("/media/" + new string('a', 255), windows: false));
    }

    [Fact]
    public async Task A_case_only_change_is_renamed_in_two_steps()
    {
        _bed.ReleaseMovie("dune (2021)", "Dune (2021) Bluray-1080p.mkv");

        var item = Assert.Single(_bed.Planner().Plan(MediaType.Movie, [_bed.Import.Dune.Id]));
        Assert.True(item.CaseOnly);
        Assert.Empty(item.Conflicts);
        Assert.Empty(item.Files);

        var result = await _bed.Organize().ApplyAsync(MediaType.Movie, [_bed.Import.Dune.Id]);

        Assert.Equal(1, result.Done);
        Assert.Equal(["Dune (2021)"], Directory.GetDirectories(_bed.Media).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(_bed.Media, "*.organize-*"));
        Assert.Equal(Path.Combine(_bed.Media, "Dune (2021)"), _bed.Import.Dune.FolderPath);
    }

    [Fact]
    public void Same_folder_in_another_case_is_only_a_conflict_on_a_case_sensitive_file_system()
    {
        var lower = Path.Combine(_bed.Media, "dune (2021)");
        var upper = Path.Combine(_bed.Media, "Dune (2021)");
        Directory.CreateDirectory(lower);
        Assert.False(FolderMover.TargetTaken(lower, upper));

        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(upper);
            Assert.True(FolderMover.TargetTaken(lower, upper));
        }
    }

    [Fact]
    public async Task Items_without_changes_are_counted_as_unchanged()
    {
        _bed.ReleaseMovie("Dune (2021)", "Dune (2021) Bluray-1080p.mkv");
        var item = Assert.Single(_bed.Planner().Plan(MediaType.Movie, [_bed.Import.Dune.Id]));
        Assert.False(item.HasChanges);

        var result = await _bed.Organize().ApplyAsync(MediaType.Movie, [_bed.Import.Dune.Id]);
        Assert.Equal((0, 1), (result.Done, result.Unchanged));
        Assert.Null(_bed.Organize().Undoable());
    }

    [Fact]
    public void A_movie_without_folder_on_disk_only_changes_the_library_path()
    {
        _bed.ReleaseMovie(Release);

        var item = Assert.Single(_bed.Planner().Plan(MediaType.Movie, [_bed.Import.Dune.Id]));

        Assert.True(item.FolderChanged);
        Assert.False(item.FolderExists);
        Assert.Contains(item.Notes, n => n.Contains("only the library path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Series_folder_episodes_and_their_side_files_are_organized()
    {
        var show = _bed.Import.Show;
        show.FolderPath = Path.Combine(_bed.Media, "Breaking.Bad.S01.720p.HDTV.x264-CTU");
        _bed.Import.Bed.Series.Update(show);
        foreach (var file in new[]
        {
            "Breaking.Bad.S01E01.720p.HDTV.x264-CTU.mkv", "Breaking.Bad.S01E01.720p.HDTV.x264-CTU.nfo", "Breaking.Bad.S01E01.720p.HDTV.x264-CTU-thumb.jpg",
            "Breaking.Bad.S01E02.720p.HDTV.x264-CTU.mkv", "Breaking.Bad.S01E02.720p.HDTV.x264-CTU.en.srt", "tvshow.nfo",
        })
        {
            ImportTestBed.WriteFile(Path.Combine(show.FolderPath, file), 1);
        }

        _bed.Import.EpisodeFiles.SetFile([_bed.Import.Bed.Episode(1, 1).Id], new MediaFile("Breaking.Bad.S01E01.720p.HDTV.x264-CTU.mkv", 1, DateTime.UtcNow));
        _bed.Import.EpisodeFiles.SetFile([_bed.Import.Bed.Episode(1, 2).Id], new MediaFile("Breaking.Bad.S01E02.720p.HDTV.x264-CTU.mkv", 1, DateTime.UtcNow));

        var plan = Assert.Single(_bed.Planner().Plan(MediaType.Series, [show.Id]));
        Assert.Equal(Path.Combine(_bed.Media, "Breaking Bad (2008)"), plan.NewFolder);
        Assert.Equal(5, plan.Files.Count);
        Assert.Equal(1, plan.OtherFileCount);

        var result = await _bed.Organize().ApplyAsync(MediaType.Series, [show.Id]);

        Assert.Equal(1, result.Done);
        var folder = Path.Combine(_bed.Media, "Breaking Bad (2008)");
        Assert.Equal(
            [
                "Season 1/Breaking Bad - S01E01 - Episode 1 HDTV-720p-thumb.jpg",
                "Season 1/Breaking Bad - S01E01 - Episode 1 HDTV-720p.mkv",
                "Season 1/Breaking Bad - S01E01 - Episode 1 HDTV-720p.nfo",
                "Season 1/Breaking Bad - S01E02 - Episode 2 HDTV-720p.en.srt",
                "Season 1/Breaking Bad - S01E02 - Episode 2 HDTV-720p.mkv",
                "tvshow.nfo",
            ],
            OrganizeTestBed.Tree(folder));
        Assert.Equal(folder, _bed.Import.Show.FolderPath);
        Assert.Equal(Path.Combine("Season 1", "Breaking Bad - S01E02 - Episode 2 HDTV-720p.mkv"), _bed.Import.Bed.Episode(1, 2).File!.RelativePath);

        await _bed.Organize().UndoAsync(result.RunId!.Value);
        Assert.Equal("Breaking.Bad.S01E02.720p.HDTV.x264-CTU.mkv", _bed.Import.Bed.Episode(1, 2).File!.RelativePath);
        Assert.Equal(6, OrganizeTestBed.Tree(show.FolderPath).Length);
    }

    [Fact]
    public void A_failing_step_reverses_what_was_done()
    {
        _bed.ReleaseMovie(Release, $"{Release}.mkv");
        var oldFolder = _bed.Import.Dune.FolderPath;
        var newFolder = Path.Combine(_bed.Media, "Dune (2021)");

        var error = Assert.Throws<LibraryException>(() => _bed.Relocator().Relocate(
            MediaType.Movie, _bed.Import.Dune.Id, oldFolder, newFolder,
            [new FileMove($"{Release}.mkv", "Dune.mkv", true), new FileMove("missing.srt", "Dune.srt", false)]));

        Assert.Contains("no longer exists", error.Message, StringComparison.Ordinal);
        Assert.Equal([$"{Release}.mkv"], OrganizeTestBed.Tree(oldFolder));
        Assert.False(Directory.Exists(newFolder));
        Assert.Equal(oldFolder, _bed.Import.Dune.FolderPath);
    }

    [Fact]
    public async Task Organize_reports_progress_and_frees_the_slot()
    {
        _bed.ReleaseMovie(Release, $"{Release}.mkv");
        Assert.True(_bed.Tracker.TryQueue(LibraryRunKind.Organize, MediaType.Movie, "Organize 1 movie", 1));
        Assert.False(_bed.Tracker.TryQueue(LibraryRunKind.Move, MediaType.Movie, "Move 1 movie", 1));

        await _bed.Organize().ApplyAsync(MediaType.Movie, [_bed.Import.Dune.Id]);

        Assert.Null(_bed.Tracker.Current);
        Assert.Equal(LibraryRunKind.Organize, _bed.Tracker.Last!.Kind);
        Assert.True(_bed.Tracker.TryQueue(LibraryRunKind.Move, MediaType.Movie, "Move 1 movie", 1));
    }

    public void Dispose() => _bed.Dispose();
}
