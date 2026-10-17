// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.PostProcessing;

namespace StreamZH.Tests.PostProcessing;

public sealed class PostProcessToolsTests
{
    private static PostProcessTools Tools(FixedToolSettings settings, bool windows, string path, params string[] existing)
    {
        var files = new HashSet<string>(existing, StringComparer.Ordinal);
        var environment = new ToolEnvironment(windows ? @"C:\Tools\StreamZH" : "/opt/streamzh", path, windows, windows ? [@"C:\Program Files", @"C:\Program Files (x86)"] : [])
        {
            FileExists = files.Contains,
        };
        return new PostProcessTools(settings, new ToolProcessRunner(), environment);
    }

    [Fact]
    public void Configured_path_wins_over_everything()
    {
        var tools = Tools(new FixedToolSettings(par2: "/custom/par2"), false, "/usr/bin", "/custom/par2", "/opt/streamzh/tools/par2", "/usr/bin/par2");

        var found = tools.Find(PostProcessTool.Par2);

        Assert.Equal("/custom/par2", found.Path);
        Assert.Equal(ToolSource.Configured, found.Source);
        Assert.Null(found.Problem);
    }

    [Fact]
    public void Configured_folder_is_searched_for_the_executable()
    {
        var tools = Tools(new FixedToolSettings(sevenZip: @"D:\Apps\7-Zip"), true, string.Empty, Path.Combine(@"D:\Apps\7-Zip", "7z.exe"));

        Assert.Equal(Path.Combine(@"D:\Apps\7-Zip", "7z.exe"), tools.Find(PostProcessTool.SevenZip).Path);
    }

    [Fact]
    public void Bundled_tools_folder_comes_before_path()
    {
        var bundled = Path.Combine("/opt/streamzh", "tools", "par2");
        var tools = Tools(new FixedToolSettings(), false, "/usr/local/bin:/usr/bin", bundled, "/usr/bin/par2");

        var found = tools.Find(PostProcessTool.Par2);

        Assert.Equal(bundled, found.Path);
        Assert.Equal(ToolSource.Bundled, found.Source);
    }

    [Fact]
    public void Path_is_searched_in_order_and_a_missing_configured_path_falls_back_with_a_problem()
    {
        var tools = Tools(new FixedToolSettings(sevenZip: "/gone/7z"), false, "/usr/local/bin:/usr/bin", "/usr/bin/7z", Path.Combine("/usr/local/bin", "7zz"));

        var found = tools.Find(PostProcessTool.SevenZip);

        // 7z is preferred over 7zz, wherever it is on PATH.
        Assert.Equal(Path.Combine("/usr/bin", "7z"), found.Path);
        Assert.Equal(ToolSource.Path, found.Source);
        Assert.Equal("/gone/7z", found.ConfiguredPath);
        Assert.Contains("/gone/7z", found.Problem);
    }

    [Fact]
    public void Windows_falls_back_to_the_7zip_install_folder()
    {
        var installed = Path.Combine(@"C:\Program Files", "7-Zip", "7z.exe");
        var tools = Tools(new FixedToolSettings(), true, @"C:\Windows\system32;C:\Windows", installed);

        var sevenZip = tools.Find(PostProcessTool.SevenZip);
        var par2 = tools.Find(PostProcessTool.Par2);

        Assert.Equal(installed, sevenZip.Path);
        Assert.Equal(ToolSource.WindowsDefault, sevenZip.Source);
        Assert.False(par2.Found);
        Assert.Equal(ToolSource.NotFound, par2.Source);
    }

    [Fact]
    public void Windows_looks_for_exe_names()
    {
        var tools = Tools(new FixedToolSettings(), true, @"C:\bin");

        Assert.Equal(["par2.exe"], tools.ExecutableNames(PostProcessTool.Par2));
        Assert.Equal(["7z.exe", "7za.exe"], tools.ExecutableNames(PostProcessTool.SevenZip));
    }

    [Theory]
    [InlineData("\n7-Zip 23.01 (x64) : Copyright (c) 1999-2023 Igor Pavlov : 2023-06-20", "7-Zip 23.01")]
    [InlineData("7-Zip [64] 16.02 : Copyright (c) 1999-2016 Igor Pavlov : 2016-05-21", "7-Zip 16.02")]
    [InlineData("7-Zip (z) 24.08 (x64) : Copyright (c) 1999-2024 Igor Pavlov", "7-Zip 24.08")]
    [InlineData("par2cmdline version 0.8.1\nCopyright (C) 2003-2019 Peter Brian Clements.", "par2cmdline 0.8.1")]
    [InlineData("par2cmdline-turbo version 1.1.1", "par2cmdline-turbo 1.1.1")]
    [InlineData("something else", null)]
    public void Parses_tool_versions(string output, string? expected)
    {
        Assert.Equal(expected, PostProcessTools.ParseVersion(output));
    }

    [RequiresToolsFact("par2", "7z")]
    public async Task Reads_versions_of_the_installed_tools()
    {
        var tools = new PostProcessTools(new FixedToolSettings(), new ToolProcessRunner(), ToolEnvironment.Current);

        var par2 = tools.Find(PostProcessTool.Par2);
        var sevenZip = tools.Find(PostProcessTool.SevenZip);

        Assert.StartsWith("par2", await tools.GetVersionAsync(par2.Path!, CancellationToken.None));
        Assert.StartsWith("7-Zip ", await tools.GetVersionAsync(sevenZip.Path!, CancellationToken.None));
    }

    [Fact]
    public void Archive_sets_are_grouped_with_their_first_volume()
    {
        string[] files =
        [
            "/w/Movie.2024.part01.rar", "/w/Movie.2024.part02.rar", "/w/Movie.2024.part10.rar",
            "/w/old.rar", "/w/old.r00", "/w/old.r01", "/w/old.s00",
            "/w/pack.7z.001", "/w/pack.7z.002",
            "/w/single.7z",
            "/w/subs.zip", "/w/subs.z01",
            "/w/movie.mkv.001", "/w/movie.mkv.002",
            "/w/orphan.r00",
            "/w/movie.mkv", "/w/movie.nfo", "/w/movie.mkv.1",
        ];

        var sets = ArchiveSets.Detect(files).ToDictionary(s => s.Name);

        Assert.Equal(7, sets.Count);
        Assert.Equal("/w/Movie.2024.part01.rar", sets["Movie.2024"].FirstVolume);
        Assert.Equal(3, sets["Movie.2024"].Volumes.Count);
        Assert.Equal(ArchiveKind.Rar, sets["Movie.2024"].Kind);
        Assert.Equal("/w/old.rar", sets["old"].FirstVolume);
        Assert.Equal(["/w/old.rar", "/w/old.r00", "/w/old.r01", "/w/old.s00"], sets["old"].Volumes);
        Assert.Equal("/w/pack.7z.001", sets["pack"].FirstVolume);
        Assert.Equal("/w/single.7z", sets["single"].FirstVolume);
        Assert.Equal(ArchiveKind.Zip, sets["subs"].Kind);
        Assert.Equal(2, sets["subs"].Volumes.Count);
        Assert.Equal("/w/movie.mkv.001", sets["movie.mkv"].FirstVolume);
        Assert.Equal(ArchiveKind.Split, sets["movie.mkv"].Kind);
        Assert.Null(sets["orphan"].FirstVolume);
    }

    [Fact]
    public void Rar5_set_without_its_first_volume_has_no_first_volume()
    {
        var set = Assert.Single(ArchiveSets.Detect(["/w/x.part02.rar", "/w/x.part03.rar"]));

        Assert.Null(set.FirstVolume);
    }

    [Fact]
    public void Archives_without_extension_are_recognised_by_signature()
    {
        using var folder = new TempFolder();
        var rar = folder.Write("q8w7e6r5t4", [.. "Rar!\u001A\u0007\u0001\u0000"u8, .. TestData.Random(100, 1)]);
        var zip = folder.Write("zz11", [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);
        var other = folder.Write("random", TestData.Random(100, 2));
        var mkv = folder.Write("video.mkv", [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);

        var sets = ArchiveSets.Detect([rar, zip, other, mkv]);

        Assert.Equal(2, sets.Count);
        Assert.Equal(ArchiveKind.Rar, sets.Single(s => s.FirstVolume == rar).Kind);
        Assert.Equal(ArchiveKind.Zip, sets.Single(s => s.FirstVolume == zip).Kind);
    }

    [Fact]
    public void Seven_zip_output_is_interpreted()
    {
        var ok = SevenZipExtractor.Interpret(new ToolProcessResult(0, "Everything is Ok"), null);
        Assert.True(ok.Success);

        const string encryptedHeaders = "ERROR: enc.7z.001\nCannot open encrypted archive. Wrong password?\nERRORS:\nHeaders Error";
        var missing = SevenZipExtractor.Interpret(new ToolProcessResult(2, encryptedHeaders), null);
        Assert.Equal(ExtractFailure.PasswordMissing, missing.Failure);
        Assert.StartsWith("Password protected", missing.Error);

        var wrong = SevenZipExtractor.Interpret(new ToolProcessResult(2, "ERROR: Wrong password : movie.mkv"), "secret");
        Assert.Equal(ExtractFailure.WrongPassword, wrong.Failure);
        Assert.Equal("Password protected: the password is wrong.", wrong.Error);

        var broken = SevenZipExtractor.Interpret(new ToolProcessResult(2, "ERROR: Data Error : movie.mkv\nSub items Errors: 1"), null);
        Assert.Equal(ExtractFailure.Other, broken.Failure);
        Assert.Contains("Data Error", broken.Error);
    }

    [Fact]
    public void Par2_output_is_turned_into_progress()
    {
        var parser = new Par2RepairProgress();
        string[] lines =
        [
            "Loading \"set.par2\".", "Loading: 50.0%", "Loaded 6 new packets", "Verifying source files:", "Scanning: 50.0%",
            "Target: \"a.bin\" - damaged. Found 305 of 306 data blocks.", "Repair is possible.", "Repairing: 0.0%", "Repairing: 50.0%",
            "Repairing: 100.0%", "Verifying repaired files:", "Scanning: 100.0%", "Repair complete.",
        ];

        var values = lines.Select(parser.Update).OfType<double>().ToList();

        Assert.Equal([0.025, 0.10, 0.15, 0.525, 0.90, 1.0], values.Select(v => Math.Round(v, 3)));
    }

    [Theory]
    [InlineData("  45% 3 - movie.part03.rar", 0.45)]
    [InlineData("Repairing: 12.5%", 0.125)]
    [InlineData("no percent here", null)]
    public void Percentages_are_read_from_tool_output(string text, double? expected)
    {
        Assert.Equal(expected, ToolOutput.Percent(text));
    }
}
