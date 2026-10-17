// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Organize;

namespace StreamZH.Tests.Library.Organize;

public sealed class MassEditTests : IDisposable
{
    private readonly OrganizeTestBed _bed = new();

    [Fact]
    public async Task Monitored_and_quality_profile_change_for_all_selected()
    {
        var other = _bed.Import.Bed.Profiles.All().First(p => p.Id != QualityDefaults.DefaultProfileId);

        var result = await _bed.MassEdit().EditAsync(MediaType.Movie, [_bed.Import.Dune.Id], new MassEditChanges(Monitored: false, QualityProfileId: other.Id));

        Assert.Equal(1, result.Updated);
        Assert.Empty(result.ToMove);
        Assert.False(_bed.Import.Dune.Monitored);
        Assert.Equal(other.Id, _bed.Import.Dune.QualityProfileId);

        var series = await _bed.MassEdit().EditAsync(MediaType.Series, [_bed.Import.Show.Id], new MassEditChanges(Monitored: false));
        Assert.Equal(1, series.Updated);
        Assert.False(_bed.Import.Show.Monitored);

        // Nothing to change: nothing updated.
        Assert.Equal(0, (await _bed.MassEdit().EditAsync(MediaType.Movie, [_bed.Import.Dune.Id], new MassEditChanges(Monitored: false))).Updated);
    }

    [Fact]
    public async Task Unknown_profile_or_root_folder_of_another_type_is_refused()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _bed.MassEdit().EditAsync(MediaType.Movie, [_bed.Import.Dune.Id], new MassEditChanges(QualityProfileId: 999)));
        var seriesRoot = _bed.RootFolders.Add(Path.Combine(_bed.Media, "tv"), MediaType.Series);
        var error = await Assert.ThrowsAsync<LibraryException>(() =>
            _bed.MassEdit().EditAsync(MediaType.Movie, [_bed.Import.Dune.Id], new MassEditChanges(RootFolderId: seriesRoot)));
        Assert.Equal(LibraryError.Invalid, error.Error);
    }

    [Fact]
    public async Task New_root_folder_without_moving_only_changes_the_library_path()
    {
        _bed.ReleaseMovie("Dune (2021)", "Dune (2021) Bluray-1080p.mkv");
        var second = Path.Combine(_bed.Import.Bed.DataDirectory, "media2");
        var rootId = _bed.RootFolders.Add(second, MediaType.Movie);

        var result = await _bed.MassEdit().EditAsync(MediaType.Movie, [_bed.Import.Dune.Id], new MassEditChanges(RootFolderId: rootId));

        Assert.Equal(1, result.Updated);
        Assert.Equal(Path.Combine(second, "Dune (2021)"), _bed.Import.Dune.FolderPath);
        Assert.Equal(rootId, _bed.Import.Dune.RootFolderId);
        Assert.True(File.Exists(Path.Combine(_bed.Media, "Dune (2021)", "Dune (2021) Bluray-1080p.mkv")));
    }

    [Fact]
    public async Task Moving_to_another_root_folder_moves_the_folder_and_the_stored_details()
    {
        _bed.ReleaseMovie("Dune (2021)", "Dune (2021) Bluray-1080p.mkv", "poster.jpg");
        var video = Path.Combine(_bed.Media, "Dune (2021)", "Dune (2021) Bluray-1080p.mkv");
        _bed.Import.Details.Save(new MediaFileDetails(video, StreamZH.Core.Quality.Quality.Bluray1080p.Id, 1, 0, false, [], "GRP", null, null, null, null, DateTime.UtcNow));
        var second = Path.Combine(_bed.Import.Bed.DataDirectory, "media2");
        var rootId = _bed.RootFolders.Add(second, MediaType.Movie);

        var edit = await _bed.MassEdit().EditAsync(MediaType.Movie, [_bed.Import.Dune.Id], new MassEditChanges(RootFolderId: rootId, MoveFiles: true));
        Assert.Equal([_bed.Import.Dune.Id], edit.ToMove);
        Assert.Equal(_bed.Media, Path.GetDirectoryName(_bed.Import.Dune.FolderPath));

        var result = await _bed.MassEdit().MoveAsync(MediaType.Movie, edit.ToMove, rootId);

        Assert.Equal((1, 0), (result.Done, result.Failed));
        var moved = Path.Combine(second, "Dune (2021)");
        Assert.Equal(["Dune (2021) Bluray-1080p.mkv", "poster.jpg"], OrganizeTestBed.Tree(moved));
        Assert.False(Directory.Exists(Path.Combine(_bed.Media, "Dune (2021)")));
        Assert.Equal(moved, _bed.Import.Dune.FolderPath);
        Assert.Equal(rootId, _bed.Import.Dune.RootFolderId);
        Assert.NotNull(_bed.Import.Details.Find(Path.Combine(moved, "Dune (2021) Bluray-1080p.mkv")));
        Assert.Single(_bed.Import.Renamed);
    }

    [Fact]
    public async Task A_move_into_an_existing_folder_fails_for_that_item_only()
    {
        _bed.ReleaseMovie("Dune (2021)", "Dune (2021) Bluray-1080p.mkv");
        var second = Path.Combine(_bed.Import.Bed.DataDirectory, "media2");
        Directory.CreateDirectory(Path.Combine(second, "Dune (2021)"));
        var rootId = _bed.RootFolders.Add(second, MediaType.Movie);

        var result = await _bed.MassEdit().MoveAsync(MediaType.Movie, [_bed.Import.Dune.Id], rootId);

        Assert.Equal(1, result.Failed);
        Assert.Contains("already exists", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_bed.Media, "Dune (2021)"), _bed.Import.Dune.FolderPath);
    }

    [Fact]
    public async Task Removing_keeps_the_files_unless_asked()
    {
        _bed.ReleaseMovie("Dune (2021)", "Dune (2021) Bluray-1080p.mkv");

        var result = await _bed.MassEdit().DeleteAsync(MediaType.Movie, [_bed.Import.Dune.Id], deleteFiles: false);

        Assert.False(Assert.Single(result.Removed).FilesDeleted);
        Assert.Null(_bed.Import.Bed.Movies.Find(_bed.Import.Bed.Dune.Id));
        Assert.True(File.Exists(Path.Combine(_bed.Media, "Dune (2021)", "Dune (2021) Bluray-1080p.mkv")));
    }

    [Fact]
    public async Task Removing_with_files_uses_the_recycle_bin()
    {
        _bed.ReleaseMovie("Dune (2021)", "Dune (2021) Bluray-1080p.mkv", "Subs/en.srt");
        var bin = Path.Combine(_bed.Import.Bed.DataDirectory, "recycle");
        _bed.Import.Configure(v => v with { RecycleBin = bin });
        Directory.CreateDirectory(Path.Combine(bin, "Dune (2021)"));

        var result = await _bed.MassEdit().DeleteAsync(MediaType.Movie, [_bed.Import.Dune.Id], deleteFiles: true);

        var removed = Assert.Single(result.Removed);
        Assert.Equal(Path.Combine(bin, "Dune (2021) (2)"), removed.RecycledTo);
        Assert.Equal(["Dune (2021) Bluray-1080p.mkv", "Subs/en.srt"], OrganizeTestBed.Tree(removed.RecycledTo!));
        Assert.False(Directory.Exists(Path.Combine(_bed.Media, "Dune (2021)")));
        Assert.Equal(_bed.Import.Bed.Time.Now.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(removed.RecycledTo!, "Subs", "en.srt")));
    }

    [Fact]
    public async Task Removing_with_files_without_recycle_bin_deletes_them_but_never_a_root_folder()
    {
        _bed.ReleaseMovie("Dune (2021)", "Dune (2021) Bluray-1080p.mkv");
        var series = _bed.Import.Show;
        series.FolderPath = _bed.Media;
        _bed.Import.Bed.Series.Update(series);

        var result = await _bed.MassEdit().DeleteAsync(MediaType.Movie, [_bed.Import.Dune.Id], deleteFiles: true);
        Assert.Null(Assert.Single(result.Removed).RecycledTo);
        Assert.False(Directory.Exists(Path.Combine(_bed.Media, "Dune (2021)")));

        var refused = await _bed.MassEdit().DeleteAsync(MediaType.Series, [series.Id], deleteFiles: true);
        Assert.Empty(refused.Removed);
        Assert.Contains("root folder", Assert.Single(refused.Errors).Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(_bed.Media));
        Assert.NotNull(_bed.Import.Bed.Series.Find(series.Id));
    }

    public void Dispose() => _bed.Dispose();
}
