// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library;

namespace StreamZH.Tests.Library;

public sealed class DiskScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("streamzh-scan-").FullName;

    [Fact]
    public void Movie_picks_largest_video_and_skips_samples_and_extras()
    {
        var folder = Path.Combine(_root, "Dune (2021)");
        Touch(folder, "Dune.2021.2160p.mkv", 900);
        Touch(folder, "dune-sample.mkv", 10);
        Touch(folder, "Extras/Making of.mkv", 2000);
        Touch(folder, "Dune.2021.nfo", 1);

        var file = DiskScanner.ScanMovie(folder);

        Assert.NotNull(file);
        Assert.Equal("Dune.2021.2160p.mkv", file.RelativePath);
        Assert.Equal(900L * 1024 * 1024, file.Size);
    }

    [Fact]
    public void Unpacked_bluray_counts_as_one_file()
    {
        var folder = Path.Combine(_root, "The Pope's Exorcist (2023)");
        Touch(folder, "Disc/BDMV/STREAM/00001.m2ts", 20000);
        Touch(folder, "Disc/BDMV/STREAM/00002.m2ts", 300);
        Touch(folder, "Disc/BDMV/index.bdmv", 1);

        var file = DiskScanner.ScanMovie(folder);

        Assert.NotNull(file);
        Assert.Equal(Path.Combine("Disc", "BDMV"), file.RelativePath);
        Assert.Equal(20301L * 1024 * 1024, file.Size);
        Assert.Equal((1, 20301L * 1024 * 1024), DiskScanner.SummarizeMovieFolder(folder));
    }

    [Fact]
    public void Iso_image_is_a_movie_file()
    {
        var folder = Path.Combine(_root, "Arrival (2016)");
        Touch(folder, "Arrival.2016.Complete.BluRay.iso", 40000);

        Assert.Equal("Arrival.2016.Complete.BluRay.iso", DiskScanner.ScanMovie(folder)!.RelativePath);
    }

    [Fact]
    public void Missing_folder_has_no_file() => Assert.Null(DiskScanner.ScanMovie(Path.Combine(_root, "nope")));

    [Fact]
    public void Series_maps_episodes_including_multi_episode_files()
    {
        var folder = Path.Combine(_root, "Show (2008)");
        Touch(folder, "Season 01/Show.S01E01.mkv", 400);
        Touch(folder, "Season 01/Show.S01E02E03.mkv", 800);
        Touch(folder, "Season 02/Show - 2x01 - Pilot.mp4", 400);
        Touch(folder, "Season 02/Show.S02E01.720p.mkv", 300);
        Touch(folder, "Show.trailer.mkv", 50);

        var files = DiskScanner.ScanSeries(folder);

        Assert.Equal(4, files.Count);
        Assert.Equal(files[(1, 2)], files[(1, 3)]);
        Assert.EndsWith("Pilot.mp4", files[(2, 1)].RelativePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Star Wars: Episode IV", 1977, "Star Wars - Episode IV (1977)")]
    [InlineData("What If...?", 2021, "What If... (2021)")]
    [InlineData("AC/DC: Live", 0, "ACDC - Live")]
    public void Folder_names_are_safe(string title, int year, string expected) =>
        Assert.Equal(expected, FolderNames.For(title, year));

    private void Touch(string folder, string relative, long megabytes)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(megabytes * 1024 * 1024); // sparse, no real disk usage
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}