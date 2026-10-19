// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Library;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Downloads;

public sealed class WatchedFolderTests : IDisposable
{
    private readonly DownloadTestBed _bed = new();
    private readonly ZonedClock _clock = new(DateTimeOffset.UtcNow.AddMinutes(1));
    private readonly string _watch;

    public WatchedFolderTests()
    {
        _watch = Path.Combine(_bed.DataDirectory, "downloads", "watch");
        _bed.Options.Time = _clock;
        _bed.Settings.Update(new DownloadSettingsChange { WatchFolder = _watch });
    }

    [Fact]
    public async Task Nzb_files_are_added_with_the_category_of_their_subfolder_and_removed()
    {
        var engine = await _bed.StartAsync();
        await engine.PauseAllAsync();
        var watcher = Watcher(engine);
        _bed.Categories.Create(new DownloadCategoryDefinition(0, "anime", string.Empty, null, PostProcessLevel.Full));
        Write("Top.Level.2024-GRP.nzb", _bed.Release("Top.Level.2024-GRP", ("a.mkv", 4000, 4000)).Nzb);
        Write("movies/A.Movie.2024-GRP.nzb", _bed.Release("A.Movie.2024-GRP", ("b.mkv", 4000, 4000)).Nzb);
        Write("anime/Show.S01E01{{secret}}.nzb", _bed.Release("Show.S01E01", ("c.mkv", 4000, 4000)).Nzb);
        Write("unknown/Ignored.nzb", _bed.Release("Ignored", ("d.mkv", 4000, 4000)).Nzb);
        Write("notes.txt", [1, 2, 3]);

        Assert.Equal(3, await watcher.ScanAsync());

        var items = engine.Active().ToDictionary(i => i.Title);
        Assert.Equal(("other", DownloadCategory.Other), (items["Top.Level.2024-GRP"].CategoryName, items["Top.Level.2024-GRP"].Category));
        Assert.Equal(("movies", DownloadCategory.Movie), (items["A.Movie.2024-GRP"].CategoryName, items["A.Movie.2024-GRP"].Category));
        Assert.Equal("anime", items["Show.S01E01"].CategoryName);
        Assert.Equal("secret", _bed.Repository.Find(items["Show.S01E01"].Id)!.Password);
        Assert.False(File.Exists(Path.Combine(_watch, "Top.Level.2024-GRP.nzb")));
        Assert.False(File.Exists(Path.Combine(_watch, "movies", "A.Movie.2024-GRP.nzb")));
        Assert.True(File.Exists(Path.Combine(_watch, "unknown", "Ignored.nzb")));
        Assert.True(File.Exists(Path.Combine(_watch, "notes.txt")));

        Assert.Equal(0, await watcher.ScanAsync());
    }

    [Fact]
    public async Task Files_still_being_written_wait_and_broken_ones_are_set_aside()
    {
        var engine = await _bed.StartAsync();
        await engine.PauseAllAsync();
        var watcher = Watcher(engine);
        var fresh = Write("Fresh.2024-GRP.nzb", _bed.Release("Fresh.2024-GRP", ("a.mkv", 4000, 4000)).Nzb);
        File.SetLastWriteTimeUtc(fresh, _clock.GetUtcNow().UtcDateTime.AddSeconds(-1));
        Write("Broken.nzb", "<html>not an nzb</html>"u8.ToArray());

        Assert.Equal(0, await watcher.ScanAsync());
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(Path.Combine(_watch, "Broken.nzb" + WatchedFolderService.FailedSuffix)));
        Assert.Empty(engine.Active());

        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(1, await watcher.ScanAsync());
        Assert.Equal("Fresh.2024-GRP", Assert.Single(engine.Active()).Title);
    }

    [Fact]
    public async Task Off_when_no_folder_is_set_and_the_folder_must_be_its_own()
    {
        _bed.Settings.Update(new DownloadSettingsChange { WatchFolder = string.Empty });
        var engine = await _bed.StartAsync();
        Assert.Null(_bed.Settings.WatchFolder);
        Assert.Equal(0, await Watcher(engine).ScanAsync());

        var same = Assert.Throws<ValidationFailedException>(() => _bed.Settings.Update(new DownloadSettingsChange { WatchFolder = _bed.Settings.CompleteFolder }));
        Assert.Contains("watchFolder", same.Errors.Keys);
        var relative = Assert.Throws<ValidationFailedException>(() => _bed.Settings.Update(new DownloadSettingsChange { WatchFolder = "watch" }));
        Assert.Contains("watchFolder", relative.Errors.Keys);
    }

    private WatchedFolderService Watcher(DownloadEngine engine) =>
        new(_bed.Settings, _bed.Categories, engine, _bed.Options, NullLogger<WatchedFolderService>.Instance);

    /// <summary>Writes a file that is old enough to be picked up.</summary>
    private string Write(string relative, byte[] content)
    {
        var path = Path.Combine(_watch, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5));
        return path;
    }

    public void Dispose() => _bed.Dispose();
}
