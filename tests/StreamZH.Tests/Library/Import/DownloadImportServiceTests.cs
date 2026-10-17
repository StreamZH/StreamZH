// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Import;

namespace StreamZH.Tests.Library.Import;

public sealed class DownloadImportServiceTests : IDisposable
{
    private const string DuneRelease = "Dune.2021.German.DL.1080p.BluRay.x264-DETAiLS";

    private readonly ImportTestBed _bed = new();

    [Fact]
    public async Task Movie_is_moved_renamed_and_the_library_and_history_are_updated()
    {
        var item = _bed.Completed(1, DuneRelease, _bed.MovieLink,
            ("details-dune-1080p.mkv", 120), ("Sample/details-dune-sample.mkv", 20), ("details-dune.nfo", 0), ("proof.jpg", 0));

        var result = await _bed.Service().ImportAsync(item.Id);

        var expected = Path.Combine(_bed.Media, "Dune (2021)", "Dune (2021) Bluray-1080p.mkv");
        Assert.Equal(ImportState.Imported, result.State);
        Assert.Equal([expected], result.Files);
        Assert.True(File.Exists(expected));
        Assert.Equal(120 * ImportTestBed.Mb, new FileInfo(expected).Length);

        // The download folder is gone (moved, nothing left worth keeping).
        Assert.False(Directory.Exists(item.OutputPath));

        var movie = _bed.Dune;
        Assert.Equal("Dune (2021) Bluray-1080p.mkv", movie.File!.RelativePath);
        var details = _bed.Details.Find(expected)!;
        Assert.Equal(StreamZH.Core.Quality.Quality.Bluray1080p.Id, details.QualityId);
        Assert.Equal("DETAiLS", details.ReleaseGroup);
        Assert.Equal(DuneRelease, details.SceneName);

        var history = _bed.Bed.History.Page(1, 10).Records;
        var imported = Assert.Single(history, h => h.EventType == HistoryEventType.Imported);
        Assert.Equal(_bed.Dune.Id, imported.MovieId);
        Assert.Equal(expected, imported.Data["importedPath"]);

        var published = Assert.Single(_bed.Imported);
        Assert.Equal(MediaType.Movie, published.MediaType);
        Assert.Equal(_bed.Dune.Id, published.MovieId);
        Assert.Equal([expected], published.Files);
        Assert.False(published.IsUpgrade);
        Assert.Equal(1, published.DownloadId);
        Assert.Equal(DuneRelease, published.ReleaseTitle);

        Assert.Equal(ImportState.Imported, _bed.Imports.Find(1)!.State);
    }

    [Fact]
    public async Task Without_renaming_the_original_file_name_is_kept()
    {
        _bed.Configure(v => v with { RenameMovies = false });
        var item = _bed.Completed(1, DuneRelease, _bed.MovieLink, ("details-dune-1080p.MKV", 50));

        await _bed.Service().ImportAsync(item.Id);

        Assert.True(File.Exists(Path.Combine(_bed.Media, "Dune (2021)", "details-dune-1080p.mkv")));
    }

    [Fact]
    public async Task An_upgrade_sends_the_old_file_to_the_recycle_bin()
    {
        var bin = Path.Combine(_bed.Bed.DataDirectory, "recycle");
        _bed.Configure(v => v with { RecycleBin = bin, ImportExtraFiles = true });
        var folder = Path.Combine(_bed.Media, "Dune (2021)");
        ImportTestBed.WriteFile(Path.Combine(folder, "Dune.2021.720p.WEB.H264-OLD.mkv"), 30);
        ImportTestBed.WriteFile(Path.Combine(folder, "Dune.2021.720p.WEB.H264-OLD.de.srt"), 0);
        var movie = _bed.Dune;
        movie.File = new MediaFile("Dune.2021.720p.WEB.H264-OLD.mkv", 30 * ImportTestBed.Mb, DateTime.UtcNow);
        _bed.Bed.Movies.Update(movie);

        var item = _bed.Completed(1, DuneRelease, _bed.MovieLink, ("dune.mkv", 80), ("dune.en.srt", 0));
        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        Assert.True(result.IsUpgrade);
        Assert.Equal(["Dune (2021) Bluray-1080p.en.srt", "Dune (2021) Bluray-1080p.mkv"], Names(folder));
        Assert.Equal(["Dune.2021.720p.WEB.H264-OLD.de.srt", "Dune.2021.720p.WEB.H264-OLD.mkv"], Names(Path.Combine(bin, "Dune (2021)")));
        Assert.Contains(_bed.Bed.History.Page(1, 10).Records, h => h.EventType == HistoryEventType.Upgraded);
        Assert.True(Assert.Single(_bed.Imported).IsUpgrade);
    }

    [Fact]
    public async Task Without_a_recycle_bin_the_old_file_is_deleted_even_with_the_same_name()
    {
        var folder = Path.Combine(_bed.Media, "Dune (2021)");
        ImportTestBed.WriteFile(Path.Combine(folder, "Dune (2021) Bluray-1080p.mkv"), 30);
        var movie = _bed.Dune;
        movie.File = new MediaFile("Dune (2021) Bluray-1080p.mkv", 30 * ImportTestBed.Mb, DateTime.UtcNow);
        _bed.Bed.Movies.Update(movie);

        var item = _bed.Completed(1, DuneRelease, _bed.MovieLink, ("dune.mkv", 80));
        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        Assert.Equal(["Dune (2021) Bluray-1080p.mkv"], Names(folder));
        Assert.Equal(80 * ImportTestBed.Mb, new FileInfo(Path.Combine(folder, "Dune (2021) Bluray-1080p.mkv")).Length);
    }

    [Fact]
    public async Task A_worse_release_is_not_imported_and_the_download_stays()
    {
        var folder = Path.Combine(_bed.Media, "Dune (2021)");
        ImportTestBed.WriteFile(Path.Combine(folder, "Dune.2021.2160p.BluRay.x265-BEST.mkv"), 30);
        var movie = _bed.Dune;
        movie.File = new MediaFile("Dune.2021.2160p.BluRay.x265-BEST.mkv", 30 * ImportTestBed.Mb, DateTime.UtcNow);
        _bed.Bed.Movies.Update(movie);

        var item = _bed.Completed(1, "Dune.2021.German.DL.720p.WEB.H264-WORSE", _bed.MovieLink, ("dune.mkv", 80));
        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Failed, result.State);
        Assert.Contains("Not an upgrade", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(item.OutputPath!, "dune.mkv")));
        Assert.Equal(["Dune.2021.2160p.BluRay.x265-BEST.mkv"], Names(folder));
        Assert.Contains(_bed.Bed.History.Page(1, 10).Records, h => h.EventType == HistoryEventType.ImportFailed);
        Assert.Empty(_bed.Imported);
    }

    [Fact]
    public async Task A_manual_import_may_replace_a_better_file()
    {
        var folder = Path.Combine(_bed.Media, "Dune (2021)");
        ImportTestBed.WriteFile(Path.Combine(folder, "Dune.2021.2160p.BluRay.x265-BEST.mkv"), 30);
        var movie = _bed.Dune;
        movie.File = new MediaFile("Dune.2021.2160p.BluRay.x265-BEST.mkv", 30 * ImportTestBed.Mb, DateTime.UtcNow);
        _bed.Bed.Movies.Update(movie);

        var item = _bed.Completed(1, "Some.Upload.720p.WEB.H264-WORSE", MediaLink.None, ("dune.mkv", 80), ("other.mkv", 40));
        var result = await _bed.Service().ImportAsync(item.Id, new ManualImportRequest(MediaType.Movie, _bed.Dune.Id, null, [new ManualImportFile("other.mkv", [])]));

        Assert.Equal(ImportState.Imported, result.State);
        Assert.Equal(["Dune (2021) WEBDL-720p.mkv"], Names(folder));

        // The other video file is still there, so the download folder is kept.
        Assert.True(File.Exists(Path.Combine(item.OutputPath!, "dune.mkv")));

        // A retry uses the manual choice again.
        Assert.NotNull(_bed.Imports.Find(1)!.Manual);
    }

    [Fact]
    public async Task A_failed_import_keeps_the_files_and_explains_why()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            // Permissions cannot be taken away from root; the message itself is tested below.
            Assert.Contains("access denied", ImportFileSystem.Describe(new UnauthorizedAccessException("x"), "/media/movies"), StringComparison.Ordinal);
            return;
        }

        Directory.CreateDirectory(_bed.Media);
        File.SetUnixFileMode(_bed.Media, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var item = _bed.Completed(1, DuneRelease, _bed.MovieLink, ("dune.mkv", 80));
            var result = await _bed.Service().ImportAsync(item.Id);

            Assert.Equal(ImportState.Failed, result.State);
            Assert.Contains("access denied", result.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(item.OutputPath!, "dune.mkv")));
        }
        finally
        {
            File.SetUnixFileMode(_bed.Media, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Errors_name_the_folder_and_what_to_do()
    {
        var message = ImportFileSystem.Describe(new UnauthorizedAccessException("Access to the path is denied."), @"C:\Users\john\Movies");
        Assert.StartsWith(@"Could not write to C:\Users\john\Movies: access denied.", message, StringComparison.Ordinal);
        Assert.Contains(OperatingSystem.IsWindows() ? "Controlled folder access" : "user running StreamZH", message, StringComparison.Ordinal);
        Assert.Contains("disk is full", ImportFileSystem.Describe(new IOException("No space left on device", 28), "/media"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_download_folder_fails_clearly()
    {
        var item = _bed.Completed(1, DuneRelease, _bed.MovieLink, ("dune.mkv", 80));
        Directory.Delete(item.OutputPath!, recursive: true);

        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Failed, result.State);
        Assert.Contains("no longer exists", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_samples_means_no_video_file()
    {
        var item = _bed.Completed(1, DuneRelease, _bed.MovieLink, ("dune-sample.mkv", 20), ("readme.txt", 0));

        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Failed, result.State);
        Assert.Contains("No video file found", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_enough_free_space_fails_before_anything_moves()
    {
        _bed.Configure(v => v with { MinimumFreeSpaceMb = MediaManagementSettings.MaxMinimumFreeSpaceMb, ImportMode = ImportMode.Copy, CopyUsingHardlinks = false });
        var item = _bed.Completed(1, DuneRelease, _bed.MovieLink, ("dune.mkv", 80));

        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Failed, result.State);
        Assert.Contains("Not enough free space", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_bed.Media, "Dune (2021)")));

        _bed.Configure(v => v with { SkipFreeSpaceCheck = true });
        Assert.Equal(ImportState.Imported, (await _bed.Service().ImportAsync(item.Id)).State);
    }

    [Fact]
    public async Task Copy_mode_hard_links_and_keeps_the_download()
    {
        _bed.Configure(v => v with { ImportMode = ImportMode.Copy, CopyUsingHardlinks = true });
        var item = _bed.Completed(1, DuneRelease, _bed.MovieLink, ("dune.mkv", 1));
        await File.WriteAllTextAsync(Path.Combine(item.OutputPath!, "dune.mkv"), "content");

        var result = await _bed.Service().ImportAsync(item.Id);

        var target = Assert.Single(result.Files);
        Assert.Equal("content", await File.ReadAllTextAsync(target));
        Assert.True(File.Exists(Path.Combine(item.OutputPath!, "dune.mkv")));

        // A hard link is the same file: writing one changes the other.
        await File.AppendAllTextAsync(Path.Combine(item.OutputPath!, "dune.mkv"), "+more");
        Assert.Equal("content+more", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Copy_mode_without_hard_links_makes_an_independent_copy()
    {
        _bed.Configure(v => v with { ImportMode = ImportMode.Copy, CopyUsingHardlinks = false });
        var item = _bed.Completed(1, DuneRelease, _bed.MovieLink, ("dune.mkv", 1));
        await File.WriteAllTextAsync(Path.Combine(item.OutputPath!, "dune.mkv"), "content");

        var target = Assert.Single((await _bed.Service().ImportAsync(item.Id)).Files);
        await File.AppendAllTextAsync(Path.Combine(item.OutputPath!, "dune.mkv"), "+more");

        Assert.Equal("content", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.partial~"));
    }

    [Fact]
    public async Task Keep_files_from_the_download_settings_keeps_the_download_folder()
    {
        _bed.Bed.Downloads.Update(null, null, null, null, null, keepFiles: true);
        var item = _bed.Completed(1, DuneRelease, _bed.MovieLink, ("dune.mkv", 80), ("dune.nfo", 0));

        await _bed.Service().ImportAsync(item.Id);

        Assert.True(File.Exists(Path.Combine(item.OutputPath!, "dune.nfo")));
    }

    [Fact]
    public async Task Season_pack_goes_into_the_season_folder_with_episode_names()
    {
        var link = _bed.EpisodeLink([.. Enumerable.Range(1, 7).Select(e => (1, e))]);
        var files = Enumerable.Range(1, 7).Select(e => ($"Breaking.Bad.S01E{e:00}.1080p.BluRay.x264-ROVERS.mkv", 40L))
            .Append(("Breaking.Bad.S01E01.sample.mkv", 5L))
            .Append(("Breaking.Bad.S01E02.1080p.BluRay.x264-ROVERS.eng.srt", 0L))
            .ToArray();
        _bed.Configure(v => v with { ImportExtraFiles = true });
        var item = _bed.Completed(5, "Breaking.Bad.S01.1080p.BluRay.x264-ROVERS", link, files);

        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        Assert.Null(result.Message);
        var season = Path.Combine(_bed.Media, "Breaking Bad (2008)", "Season 1");
        Assert.Equal(
            [
                "Breaking Bad - S01E01 - Episode 1 Bluray-1080p.mkv",
                "Breaking Bad - S01E02 - Episode 2 Bluray-1080p.eng.srt",
                "Breaking Bad - S01E02 - Episode 2 Bluray-1080p.mkv",
                .. Enumerable.Range(3, 5).Select(e => $"Breaking Bad - S01E{e:00} - Episode {e} Bluray-1080p.mkv"),
            ],
            Names(season));
        Assert.All(_bed.Bed.Episodes.Where(e => e.SeasonNumber == 1), e => Assert.NotNull(e.File));
        Assert.Equal(Path.Combine("Season 1", "Breaking Bad - S01E03 - Episode 3 Bluray-1080p.mkv"), _bed.Bed.Episode(1, 3).File!.RelativePath);
        Assert.False(Directory.Exists(item.OutputPath));
        Assert.Equal(7, _bed.Bed.History.Page(1, 20).Records.Count(h => h.EventType == HistoryEventType.Imported));
        var published = Assert.Single(_bed.Imported);
        Assert.Equal(7, published.Files.Count);
        Assert.Equal(7, published.EpisodeIds.Count);
        Assert.Equal(_bed.Bed.Show.Id, published.SeriesId);
    }

    [Fact]
    public async Task Multi_episode_file_belongs_to_both_episodes()
    {
        var link = _bed.EpisodeLink((2, 1), (2, 2));
        var item = _bed.Completed(6, "Breaking.Bad.S02E01E02.720p.HDTV.x264-CTU", link, ("bb.s02e01e02.720p.mkv", 40));

        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        var expected = Path.Combine("Season 2", "Breaking Bad - S02E01-E02 - Episode 1 + Episode 2 HDTV-720p.mkv");
        Assert.Equal(expected, _bed.Bed.Episode(2, 1).File!.RelativePath);
        Assert.Equal(expected, _bed.Bed.Episode(2, 2).File!.RelativePath);
        Assert.Null(_bed.Bed.Episode(2, 3).File);
    }

    [Fact]
    public async Task Episodes_that_were_not_grabbed_are_skipped_but_the_rest_is_imported()
    {
        var link = _bed.EpisodeLink((1, 1));
        var item = _bed.Completed(7, "Breaking.Bad.S01E01.720p.HDTV.x264-CTU", link,
            ("Breaking.Bad.S01E01.720p.HDTV.x264-CTU.mkv", 40), ("Breaking.Bad.S01E05.720p.HDTV.x264-CTU.mkv", 40));

        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        Assert.Contains("S01E05 was not grabbed with this release", result.Message, StringComparison.Ordinal);
        Assert.NotNull(_bed.Bed.Episode(1, 1).File);
        Assert.Null(_bed.Bed.Episode(1, 5).File);

        // Not everything was imported: the download folder stays with the rest.
        Assert.True(File.Exists(Path.Combine(item.OutputPath!, "Breaking.Bad.S01E05.720p.HDTV.x264-CTU.mkv")));
    }

    [Fact]
    public async Task An_obfuscated_single_file_uses_the_grabbed_episode()
    {
        var link = _bed.EpisodeLink((1, 3));
        var item = _bed.Completed(8, "Breaking.Bad.S01E03.720p.HDTV.x264-CTU", link, ("a8f3b2c1d9e7.mkv", 40));

        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        Assert.Equal(Path.Combine("Season 1", "Breaking Bad - S01E03 - Episode 3 HDTV-720p.mkv"), _bed.Bed.Episode(1, 3).File!.RelativePath);
    }

    [Fact]
    public async Task A_missing_episode_title_holds_the_import_back_for_48_hours()
    {
        _bed.Bed.Series.UpsertEpisodes(_bed.Bed.Show.Id,
            [.. _bed.Bed.Episodes.Select(e => e.SeasonNumber == 2 && e.EpisodeNumber == 1
                ? new Episode { SeasonNumber = 2, EpisodeNumber = 1, Title = "TBA", AirDate = "2026-10-16" }
                : e)],
            monitorNew: true);
        var item = _bed.Completed(9, "Breaking.Bad.S02E01.720p.HDTV.x264-CTU", _bed.EpisodeLink((2, 1)), ("bb.s02e01.mkv", 40));

        var result = await _bed.Service().ImportAsync(item.Id);
        Assert.Equal(ImportState.Waiting, result.State);
        Assert.Contains("S02E01 is not known yet", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(item.OutputPath!, "bb.s02e01.mkv")));

        // Two days after the air date it is imported anyway (Sonarr does the same).
        _bed.Bed.Time.Now += TimeSpan.FromHours(48);
        Assert.Equal(ImportState.Imported, (await _bed.Service().ImportAsync(item.Id)).State);
    }

    [Fact]
    public async Task Episode_title_required_never_imports_at_once()
    {
        _bed.Bed.Series.UpsertEpisodes(_bed.Bed.Show.Id,
            [.. _bed.Bed.Episodes.Select(e => e.SeasonNumber == 2 && e.EpisodeNumber == 1
                ? new Episode { SeasonNumber = 2, EpisodeNumber = 1, Title = "TBA", AirDate = "2026-10-16" }
                : e)],
            monitorNew: true);
        _bed.Configure(v => v with { EpisodeTitleRequired = EpisodeTitleRequired.Never });
        var item = _bed.Completed(9, "Breaking.Bad.S02E01.720p.HDTV.x264-CTU", _bed.EpisodeLink((2, 1)), ("bb.s02e01.mkv", 40));

        Assert.Equal(ImportState.Imported, (await _bed.Service().ImportAsync(item.Id)).State);
        Assert.Equal(Path.Combine("Season 2", "Breaking Bad - S02E01 - TBA HDTV-720p.mkv"), _bed.Bed.Episode(2, 1).File!.RelativePath);
    }

    [Fact]
    public async Task Episode_upgrade_replaces_the_old_file_but_keeps_files_other_episodes_use()
    {
        var show = _bed.Show;
        var multi = Path.Combine("Season 1", "Breaking.Bad.S01E01E02.SDTV.XviD-OLD.avi");
        ImportTestBed.WriteFile(Path.Combine(show.FolderPath, multi), 10);
        _bed.EpisodeFiles.SetFile([_bed.Bed.Episode(1, 1).Id, _bed.Bed.Episode(1, 2).Id], new MediaFile(multi, 10 * ImportTestBed.Mb, DateTime.UtcNow));

        var item = _bed.Completed(10, "Breaking.Bad.S01E01.1080p.BluRay.x264-ROVERS", _bed.EpisodeLink((1, 1)), ("bb.s01e01.mkv", 40));
        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        Assert.True(result.IsUpgrade);
        Assert.True(File.Exists(Path.Combine(show.FolderPath, multi)));
        Assert.Equal(multi, _bed.Bed.Episode(1, 2).File!.RelativePath);
        Assert.Equal(Path.Combine("Season 1", "Breaking Bad - S01E01 - Episode 1 Bluray-1080p.mkv"), _bed.Bed.Episode(1, 1).File!.RelativePath);
    }

    [Fact]
    public async Task Manual_import_maps_files_to_chosen_episodes()
    {
        var item = _bed.Completed(11, "random.upload", MediaLink.None, ("one.mkv", 40), ("two.mkv", 40));
        var service = _bed.Service();

        var candidates = service.Candidates(item.Id, _bed.Bed.Show.Id);
        Assert.Equal(["one.mkv", "two.mkv"], candidates.Select(c => c.RelativePath));
        Assert.All(candidates, c => Assert.NotNull(c.Problem));

        var request = new ManualImportRequest(MediaType.Series, null, _bed.Bed.Show.Id,
        [
            new ManualImportFile("one.mkv", [_bed.Bed.Episode(2, 4).Id]),
            new ManualImportFile("two.mkv", [_bed.Bed.Episode(2, 5).Id, _bed.Bed.Episode(2, 6).Id]),
        ]);
        var result = await service.ImportAsync(item.Id, request);

        Assert.Equal(ImportState.Imported, result.State);
        Assert.Equal(2, result.Files.Count);
        Assert.NotNull(_bed.Bed.Episode(2, 4).File);
        Assert.Equal(_bed.Bed.Episode(2, 5).File, _bed.Bed.Episode(2, 6).File);
        Assert.False(Directory.Exists(item.OutputPath));
    }

    [Fact]
    public async Task A_download_without_a_link_needs_a_choice()
    {
        var item = _bed.Completed(12, "random.upload", MediaLink.None, ("one.mkv", 40));
        var error = await Assert.ThrowsAsync<LibraryException>(() => _bed.Service().ImportAsync(item.Id));
        Assert.Equal(LibraryError.Invalid, error.Error);
    }

    [Fact]
    public async Task Permissions_and_file_dates_are_applied()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _bed.Configure(v => v with { SetPermissions = true, ChmodFolder = "750", ChangeFileDate = FileDateMode.ReleaseDate });
        var item = _bed.Completed(13, "Breaking.Bad.S01E04.720p.HDTV.x264-CTU", _bed.EpisodeLink((1, 4)), ("bb.s01e04.mkv", 40));

        var target = Assert.Single((await _bed.Service().ImportAsync(item.Id)).Files);

        Assert.Equal((UnixFileMode)Convert.ToInt32("640", 8), File.GetUnixFileMode(target));
        Assert.Equal((UnixFileMode)Convert.ToInt32("750", 8), File.GetUnixFileMode(Path.GetDirectoryName(target)!));
        Assert.Equal(new DateTime(2008, 1, 23, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public async Task Do_not_prefer_propers_ignores_the_revision_on_disk()
    {
        var folder = Path.Combine(_bed.Media, "Dune (2021)");
        ImportTestBed.WriteFile(Path.Combine(folder, "Dune.2021.1080p.BluRay.PROPER.x264-GRP.mkv"), 30);
        var movie = _bed.Dune;
        movie.File = new MediaFile("Dune.2021.1080p.BluRay.PROPER.x264-GRP.mkv", 30 * ImportTestBed.Mb, DateTime.UtcNow);
        _bed.Bed.Movies.Update(movie);

        var item = _bed.Completed(14, DuneRelease, _bed.MovieLink, ("dune.mkv", 80));
        Assert.Equal(ImportState.Failed, (await _bed.Service().ImportAsync(item.Id)).State);

        _bed.Configure(v => v with { PropersAndRepacks = ProperHandling.DoNotPrefer });
        Assert.Equal(ImportState.Imported, (await _bed.Service().ImportAsync(item.Id)).State);
    }

    private static string[] Names(string folder) =>
        [.. Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    public void Dispose() => _bed.Dispose();
}
