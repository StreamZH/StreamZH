// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Import;

namespace StreamZH.Tests.Library.Import;

public sealed class RenameAndSettingsTests : IDisposable
{
    private readonly ImportTestBed _bed = new();

    [Fact]
    public async Task Rename_movie_previews_and_renames_with_subtitles()
    {
        var folder = _bed.Dune.FolderPath;
        ImportTestBed.WriteFile(Path.Combine(folder, "Dune.2021.German.DL.1080p.BluRay.x264-GRP.mkv"), 1);
        ImportTestBed.WriteFile(Path.Combine(folder, "Dune.2021.German.DL.1080p.BluRay.x264-GRP.de.srt"), 0);
        var movie = _bed.Dune;
        movie.File = new MediaFile("Dune.2021.German.DL.1080p.BluRay.x264-GRP.mkv", 1, DateTime.UtcNow);
        _bed.Bed.Movies.Update(movie);
        var rename = _bed.Rename();

        var preview = Assert.Single(rename.PreviewMovie(movie.Id));
        Assert.Equal("Dune.2021.German.DL.1080p.BluRay.x264-GRP.mkv", preview.ExistingPath);
        Assert.Equal("Dune (2021) Bluray-1080p.mkv", preview.NewPath);

        Assert.Equal(1, await rename.RenameMovieAsync(movie.Id));
        Assert.Equal(["Dune (2021) Bluray-1080p.de.srt", "Dune (2021) Bluray-1080p.mkv"], Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("Dune (2021) Bluray-1080p.mkv", _bed.Dune.File!.RelativePath);
        Assert.Empty(rename.PreviewMovie(movie.Id));
        Assert.Equal(MediaType.Movie, Assert.Single(_bed.Renamed).MediaType);
        Assert.Contains(_bed.Bed.History.Page(1, 10).Records, h => h.EventType == HistoryEventType.Renamed);

        // Renaming off: nothing to rename.
        _bed.Configure(v => v with { RenameMovies = false, StandardMovieFormat = "{Movie Title}" });
        Assert.Empty(rename.PreviewMovie(movie.Id));
    }

    [Fact]
    public async Task Renamed_files_keep_the_quality_the_import_stored()
    {
        var folder = _bed.Dune.FolderPath;
        var path = Path.Combine(folder, "Dune (2021) Bluray-1080p.mkv");
        ImportTestBed.WriteFile(path, 1);
        _bed.Details.Save(new MediaFileDetails(path, StreamZH.Core.Quality.Quality.Bluray1080p.Id, 1, 0, false, [4, 1], "GRP", "Dune.2021", null, null, null, DateTime.UtcNow));
        var movie = _bed.Dune;
        movie.File = new MediaFile("Dune (2021) Bluray-1080p.mkv", 1, DateTime.UtcNow);
        _bed.Bed.Movies.Update(movie);

        _bed.Configure(v => v with { StandardMovieFormat = "{Movie CleanTitle} {Release Year} {Quality Title} {[MediaInfo AudioLanguages]}-{Release Group}" });
        await _bed.Rename().RenameMovieAsync(movie.Id);

        var renamed = Path.Combine(folder, "Dune 2021 Bluray-1080p [DE+EN]-GRP.mkv");
        Assert.True(File.Exists(renamed));
        Assert.NotNull(_bed.Details.Find(renamed));
        Assert.Null(_bed.Details.Find(path));
    }

    [Fact]
    public async Task Rename_series_moves_files_into_season_folders()
    {
        var show = _bed.Show;
        ImportTestBed.WriteFile(Path.Combine(show.FolderPath, "Breaking.Bad.S01E01.720p.HDTV.x264-CTU.mkv"), 1);
        ImportTestBed.WriteFile(Path.Combine(show.FolderPath, "old", "Breaking.Bad.S01E02E03.720p.HDTV.x264-CTU.mkv"), 1);
        _bed.EpisodeFiles.SetFile([_bed.Bed.Episode(1, 1).Id], new MediaFile("Breaking.Bad.S01E01.720p.HDTV.x264-CTU.mkv", 1, DateTime.UtcNow));
        _bed.EpisodeFiles.SetFile([_bed.Bed.Episode(1, 2).Id, _bed.Bed.Episode(1, 3).Id],
            new MediaFile(Path.Combine("old", "Breaking.Bad.S01E02E03.720p.HDTV.x264-CTU.mkv"), 1, DateTime.UtcNow));
        _bed.Configure(v => v with { DeleteEmptyFolders = true });
        var rename = _bed.Rename();

        var preview = rename.PreviewSeries(show.Id);
        Assert.Equal(
            [
                Path.Combine("Season 1", "Breaking Bad - S01E01 - Episode 1 HDTV-720p.mkv"),
                Path.Combine("Season 1", "Breaking Bad - S01E02-E03 - Episode 2 + Episode 3 HDTV-720p.mkv"),
            ],
            preview.Select(p => p.NewPath));
        Assert.Empty(rename.PreviewSeries(show.Id, seasonNumber: 2));

        Assert.Equal(2, await rename.RenameSeriesAsync(show.Id));
        Assert.Equal(Path.Combine("Season 1", "Breaking Bad - S01E01 - Episode 1 HDTV-720p.mkv"), _bed.Bed.Episode(1, 1).File!.RelativePath);
        Assert.Equal(_bed.Bed.Episode(1, 2).File, _bed.Bed.Episode(1, 3).File);
        Assert.False(Directory.Exists(Path.Combine(show.FolderPath, "old")));
        Assert.Empty(rename.PreviewSeries(show.Id));
        Assert.Equal(_bed.Bed.Show.Id, Assert.Single(_bed.Renamed).SeriesId);
    }

    [Fact]
    public void Settings_have_defaults_and_are_saved()
    {
        var current = _bed.Settings.Current;
        Assert.True(current.RenameMovies);
        Assert.Equal(MediaManagementValues.DefaultMovieFormat, current.StandardMovieFormat);
        Assert.Equal(ColonReplacement.Smart, current.ColonReplacement);
        Assert.Equal(ImportMode.Move, current.ImportMode);
        Assert.True(current.CompletedDownloadHandling);
        Assert.True(current.RedownloadFailed);
        Assert.Equal(ProperHandling.PreferAndUpgrade, current.PropersAndRepacks);

        var bin = Path.Combine(_bed.Bed.DataDirectory, "bin") + Path.DirectorySeparatorChar;
        _bed.Settings.Update(current with
        {
            ColonReplacement = ColonReplacement.SpaceDashSpace,
            ExtraFileExtensions = " .SRT, ass ,nfo",
            RecycleBin = bin,
            MultiEpisodeStyle = MultiEpisodeStyle.Range,
            PropersAndRepacks = ProperHandling.DoNotPrefer,
        });

        var reloaded = new MediaManagementSettings(_bed.Bed.Config).Current;
        Assert.Equal(ColonReplacement.SpaceDashSpace, reloaded.ColonReplacement);
        Assert.Equal("srt,ass,nfo", reloaded.ExtraFileExtensions);
        Assert.Equal(Path.TrimEndingDirectorySeparator(bin), reloaded.RecycleBin);
        Assert.Equal(MultiEpisodeStyle.Range, reloaded.MultiEpisodeStyle);
        Assert.Equal(ProperHandling.DoNotPrefer, reloaded.PropersAndRepacks);
        Assert.Equal("doNotPrefer", _bed.Bed.Config.Get(ConfigKeys.PropersAndRepacks));
    }

    [Fact]
    public void Invalid_settings_are_rejected_field_by_field()
    {
        var error = Assert.Throws<ValidationFailedException>(() => _bed.Settings.Update(_bed.Settings.Current with
        {
            StandardMovieFormat = "{Quality Full}",
            SeasonFolderFormat = "Season",
            MinimumFreeSpaceMb = -1,
            ExtraFileExtensions = "srt,mkv",
            RecycleBin = "relative/bin",
            RecycleBinCleanupDays = 99999,
            ChmodFolder = "999",
            ChownGroup = "bad group!",
        }));

        Assert.Equal(
            ["chmodFolder", "chownGroup", "extraFileExtensions", "minimumFreeSpaceMb", "recycleBin", "recycleBinCleanupDays", "seasonFolderFormat", "standardMovieFormat"],
            error.Errors.Keys.Order(StringComparer.Ordinal));

        // Nothing was saved.
        Assert.Equal(MediaManagementValues.DefaultMovieFormat, new MediaManagementSettings(_bed.Bed.Config).Current.StandardMovieFormat);
    }

    [Fact]
    public void Recycle_bin_cleanup_deletes_old_files_only()
    {
        var bin = Path.Combine(_bed.Bed.DataDirectory, "bin");
        _bed.Configure(v => v with { RecycleBin = bin, RecycleBinCleanupDays = 7 });
        ImportTestBed.WriteFile(Path.Combine(bin, "Dune (2021)", "old.mkv"), 1);
        ImportTestBed.WriteFile(Path.Combine(bin, "Arrival (2016)", "new.mkv"), 1);
        File.SetLastWriteTimeUtc(Path.Combine(bin, "Dune (2021)", "old.mkv"), _bed.Bed.Time.Now.UtcDateTime.AddDays(-8));
        File.SetLastWriteTimeUtc(Path.Combine(bin, "Arrival (2016)", "new.mkv"), _bed.Bed.Time.Now.UtcDateTime.AddDays(-2));

        var task = new RecycleBinCleanupTask(_bed.Settings, _bed.Files, NullLogger<RecycleBinCleanupTask>.Instance, _bed.Bed.Time);
        Assert.Equal(TimeSpan.FromHours(24), task.Interval);
        Assert.Equal(1, task.Clean());

        Assert.False(Directory.Exists(Path.Combine(bin, "Dune (2021)")));
        Assert.True(File.Exists(Path.Combine(bin, "Arrival (2016)", "new.mkv")));

        _bed.Configure(v => v with { RecycleBinCleanupDays = 0 });
        Assert.Null(task.Interval);
    }

    [Fact]
    public void Subtitle_names_keep_language_and_flags()
    {
        Assert.Equal("New.de.forced.srt", ExtraFiles.TargetName("/d/Movie.2021.German.forced.srt", "Other", "New"));
        Assert.Equal("New.en.srt", ExtraFiles.TargetName("/d/Subs/2_English.srt", "Movie", "New"));
        Assert.Equal("New.en.sdh.srt", ExtraFiles.TargetName("/d/movie.en.sdh.srt", "movie", "New"));
        Assert.Equal("New.srt", ExtraFiles.TargetName("/d/random.srt", "movie", "New"));
        Assert.Equal("New.nfo-orig", ExtraFiles.TargetName("/d/movie.nfo", "movie", "New"));
    }

    public void Dispose() => _bed.Dispose();
}
