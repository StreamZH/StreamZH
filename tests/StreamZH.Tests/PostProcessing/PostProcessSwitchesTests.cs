// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Library.Downloads;
using StreamZH.Library.PostProcessing;

namespace StreamZH.Tests.PostProcessing;

/// <summary>The post-processing switches of Settings → Downloads and the per-category post-processing level.</summary>
public sealed class PostProcessSwitchesTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ProgressLog _progress = new();

    private string Work => _folder.Combine("work");

    private string Target => _folder.Combine("done", "Some.Movie");

    [Fact]
    public async Task Level_none_moves_everything_as_it_is_except_executables()
    {
        _folder.Write("work/Some.Movie.part1.rar", TestData.Random(1000, 1));
        _folder.Write("work/Some.Movie.par2", TestData.Random(300, 2));
        _folder.Write("work/Some.Movie.nfo", TestData.Random(100, 3));
        _folder.Write("work/Sample/sample.mkv", TestData.Random(100, 4));
        _folder.Write("work/setup.exe", TestData.Random(100, 5));

        var result = await Run(level: PostProcessLevel.None, noTools: true);

        Assert.True(result.Success, result.Error);
        Assert.Equal(
            ["Sample/sample.mkv", "Some.Movie.nfo", "Some.Movie.par2", "Some.Movie.part1.rar"],
            result.Files.Select(f => Path.GetRelativePath(Target, f).Replace('\\', '/')).ToList());
        Assert.False(File.Exists(Path.Combine(Target, "setup.exe")));
    }

    [RequiresToolsFact("7z")]
    public async Task Level_repair_does_not_unpack()
    {
        var movie = WriteSource("Some.Movie.mkv", 200_000, 6);
        SevenZip("a", "-mx=0", Path.Combine(Work, "Some.Movie.7z"), movie);

        var result = await Run(level: PostProcessLevel.Repair);

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(Target, "Some.Movie.7z")], result.Files);
        Assert.DoesNotContain(_progress.Items, p => p.Stage == PostProcessStage.Extracting);
    }

    [RequiresToolsFact("7z")]
    public async Task Level_unpack_keeps_the_archives_next_to_the_unpacked_files()
    {
        var movie = WriteSource("Some.Movie.mkv", 200_000, 7);
        SevenZip("a", "-mx=0", Path.Combine(Work, "Some.Movie.7z"), movie);
        _folder.Write("work/Some.Movie.nfo", TestData.Random(100, 8));

        var result = await Run(level: PostProcessLevel.Unpack);

        Assert.True(result.Success, result.Error);
        Assert.Equal(
            [Path.Combine(Target, "Some.Movie.7z"), Path.Combine(Target, "Some.Movie.mkv"), Path.Combine(Target, "Some.Movie.nfo")],
            result.Files);
        Assert.Equal(File.ReadAllBytes(movie), File.ReadAllBytes(Path.Combine(Target, "Some.Movie.mkv")));
    }

    [Fact]
    public async Task Cleanup_list_decides_what_is_left_out_and_samples_can_be_kept()
    {
        _folder.Write("work/Some.Movie.mkv", TestData.Random(5000, 9));
        _folder.Write("work/Some.Movie.nfo", TestData.Random(100, 10));
        _folder.Write("work/readme.txt", TestData.Random(100, 11));
        _folder.Write("work/Some.Movie.Sample.mkv", TestData.Random(100, 12));
        _folder.Write("work/run.bat", TestData.Random(100, 13));

        var switches = PostProcessSwitches.Default with
        {
            CleanupExtensions = new HashSet<string>([".txt"], StringComparer.OrdinalIgnoreCase),
            IgnoreSamples = false,
        };
        var result = await Run(switches: switches, noTools: true);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["Some.Movie.Sample.mkv", "Some.Movie.mkv", "Some.Movie.nfo"], result.Files.Select(Path.GetFileName).ToList());
    }

    [Fact]
    public async Task Deobfuscate_gives_the_biggest_file_and_its_companions_the_download_name()
    {
        _folder.Write("work/a8f3k2l9d0s8f7g6h5j4.mkv", TestData.Random(50_000, 14));
        _folder.Write("work/a8f3k2l9d0s8f7g6h5j4.en.srt", TestData.Random(100, 15));
        _folder.Write("work/poster.jpg", TestData.Random(100, 16));

        var off = await Run(noTools: true);
        Assert.Contains(Path.Combine(Target, "a8f3k2l9d0s8f7g6h5j4.mkv"), off.Files);

        Directory.Delete(Target, recursive: true);
        _folder.Write("work/a8f3k2l9d0s8f7g6h5j4.mkv", TestData.Random(50_000, 14));
        _folder.Write("work/a8f3k2l9d0s8f7g6h5j4.en.srt", TestData.Random(100, 15));
        var on = await Run(switches: PostProcessSwitches.Default with { Deobfuscate = true }, noTools: true);

        Assert.True(on.Success, on.Error);
        Assert.Equal(
            ["Some.Movie.2024.1080p.WEB-DL.en.srt", "Some.Movie.2024.1080p.WEB-DL.mkv"],
            on.Files.Select(Path.GetFileName).ToList());
        Assert.True(PostProcessor.IsObfuscated("4c2f9a8b7e6d5c4b3a29.mkv"));
        Assert.False(PostProcessor.IsObfuscated("Some.Movie.2024.mkv"));
        Assert.False(PostProcessor.IsObfuscated("movie.mkv"));
    }

    [RequiresToolsFact("7z")]
    public async Task Unwanted_files_in_an_archive_fail_the_download_or_give_a_warning()
    {
        var movie = WriteSource("Some.Movie.mkv", 100_000, 17);
        WriteSource("virus.scr", 100, 18);
        Directory.CreateDirectory(Work);
        InstalledTools.Run(InstalledTools.SevenZip!, _folder.Combine("src"), "a", "-mx=0", Path.Combine(Work, "Some.Movie.7z"), "Some.Movie.mkv", "virus.scr");
        var unwanted = new HashSet<string>([".scr"], StringComparer.OrdinalIgnoreCase);

        var failed = await Run(switches: PostProcessSwitches.Default with { UnwantedExtensions = unwanted, UnwantedAction = UnwantedAction.Fail });
        Assert.False(failed.Success);
        Assert.Equal("Unwanted files in the download: virus.scr.", failed.Error);
        Assert.True(File.Exists(Path.Combine(Work, "Some.Movie.7z")));

        var warned = await Run(switches: PostProcessSwitches.Default with { UnwantedExtensions = unwanted, UnwantedAction = UnwantedAction.Warn });
        Assert.True(warned.Success, warned.Error);
        Assert.Equal("Unwanted files: virus.scr.", warned.Warning);
        Assert.Equal([Path.Combine(Target, "Some.Movie.mkv")], warned.Files);
        Assert.Equal(File.ReadAllBytes(movie), File.ReadAllBytes(warned.Files[0]));
    }

    [RequiresToolsFact("7z")]
    public async Task Recursive_unpacking_off_leaves_archives_inside_archives()
    {
        var movie = WriteSource("Some.Movie.mkv", 100_000, 19);
        var inner = _folder.Combine("src", "inner.zip");
        SevenZip("a", "-mx=0", inner, movie);
        SevenZip("a", "-mx=0", Path.Combine(Work, "Some.Movie.7z"), inner);

        var result = await Run(switches: PostProcessSwitches.Default with { RecursiveUnpack = false });

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(Target, "inner.zip")], result.Files);
    }

    [RequiresToolsFact("7z")]
    public async Task Archives_inside_archives_are_unpacked_more_than_one_level_deep()
    {
        var movie = WriteSource("Some.Movie.mkv", 100_000, 20);
        var inner = _folder.Combine("src", "inner.zip");
        var middle = _folder.Combine("src", "middle.zip");
        SevenZip("a", "-mx=0", inner, movie);
        SevenZip("a", "-mx=0", middle, inner);
        SevenZip("a", "-mx=0", Path.Combine(Work, "Some.Movie.7z"), middle);

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(Target, "Some.Movie.mkv")], result.Files);
    }

    [RequiresToolsFact("par2")]
    public async Task Unverified_downloads_are_unpacked_anyway_when_post_processing_only_verified_jobs_is_off()
    {
        var movie = _folder.Write("work/Some.Movie.mkv", TestData.Random(400_000, 21));
        InstalledTools.Run(InstalledTools.Par2!, Work, "c", "-q", "-r1", "-s65536", "Some.Movie.par2", "Some.Movie.mkv");
        foreach (var volume in Directory.GetFiles(Work, "*.vol*.par2"))
        {
            File.Delete(volume);
        }

        TestData.Corrupt(movie, 1000);
        TestData.Corrupt(movie, 200_000);

        var strict = await Run(noTools: true);
        Assert.False(strict.Success);
        Assert.StartsWith("Not enough recovery blocks", strict.Error);

        var job = PostProcessorFactory.Job(_folder) with { ContinueWhenUnverified = true };
        var lenient = await Create(PostProcessSwitches.Default, noTools: true).RunAsync(job, _progress, CancellationToken.None);
        Assert.True(lenient.Success, lenient.Error);
        Assert.StartsWith("Not verified: Not enough recovery blocks", lenient.Warning);
        Assert.Equal([Path.Combine(Target, "Some.Movie.mkv")], lenient.Files);
    }

    [Fact]
    public async Task Extra_tool_parameters_are_passed_as_single_arguments()
    {
        var runner = new RecordingRunner();
        var tools = new PostProcessTools(new FixedToolSettings("/usr/bin/par2", "/usr/bin/7z"), runner, ToolEnvironment.Current with { FileExists = _ => true });
        _folder.Write("work/Some.Movie.7z", TestData.Random(100, 22));
        var switches = PostProcessSwitches.Default with { SevenZipArguments = ["-mmt2", "-bb1"] };

        await new PostProcessor(tools, runner, NullLogger<PostProcessor>.Instance, new FixedPostProcessSwitches(switches))
            .RunAsync(PostProcessorFactory.Job(_folder), _progress, CancellationToken.None);

        var call = Assert.Single(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "x");
        Assert.Equal(["-mmt2", "-bb1", "--", Path.Combine(Work, "Some.Movie.7z")], call.Arguments.TakeLast(4));

        var repair = await new Par2Repairer(runner).RepairAsync("/usr/bin/par2", Work, "main.par2", ["a.vol0.par2"], ["-t2"], new Progress<double>(), CancellationToken.None);
        Assert.Equal(["r", "-t2", "main.par2", "a.vol0.par2"], runner.Calls[^1].Arguments);
        Assert.True(repair.Success);
    }

    [Fact]
    public void Extra_parameters_must_be_options_and_cannot_change_output_or_password()
    {
        Assert.Equal(["-mmt4", "-bb1"], ToolArguments.Parse("-mmt4  -bb1", sevenZip: true, out _));
        Assert.Equal(["-t4", "-m512"], ToolArguments.Parse("-t4 -m512", sevenZip: false, out _));
        Assert.Empty(ToolArguments.Parse("  ", sevenZip: true, out _)!);
        Assert.Null(ToolArguments.Parse("-o/tmp/evil", sevenZip: true, out var output));
        Assert.Contains("-o", output);
        Assert.Null(ToolArguments.Parse("-psecret", sevenZip: true, out _));
        Assert.Null(ToolArguments.Parse("; rm -rf /", sevenZip: true, out var notOption));
        Assert.Contains("not an option", notOption);
        Assert.Null(ToolArguments.Parse("\"-x!*.nfo\"", sevenZip: true, out _));
        Assert.Null(ToolArguments.Parse("-B/", sevenZip: false, out _));
        Assert.Null(ToolArguments.Parse(string.Join(' ', Enumerable.Repeat("-q", 11)), sevenZip: false, out _));
    }

    [Fact]
    public async Task Tool_priority_lowers_the_nice_value_of_the_tools()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var normal = await new ToolProcessRunner().RunAsync("/bin/sh", ["-c", "sleep 0.3; nice"], null, null, TimeSpan.FromSeconds(10), CancellationToken.None);
        var idle = await new ToolProcessRunner(new FixedPostProcessSwitches(PostProcessSwitches.Default with { ToolPriority = ToolPriority.Idle }))
            .RunAsync("/bin/sh", ["-c", "sleep 0.3; nice"], null, null, TimeSpan.FromSeconds(10), CancellationToken.None);
        var below = await new ToolProcessRunner(new FixedPostProcessSwitches(PostProcessSwitches.Default with { ToolPriority = ToolPriority.BelowNormal }))
            .RunAsync("/bin/sh", ["-c", "sleep 0.3; nice"], null, null, TimeSpan.FromSeconds(10), CancellationToken.None);

        var baseline = int.Parse(normal.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(int.Parse(idle.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture) > baseline, idle.Output);
        Assert.True(int.Parse(below.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture) > baseline, below.Output);
    }

    private static PostProcessor Create(PostProcessSwitches switches, bool noTools = false)
    {
        var runner = new ToolProcessRunner();
        var environment = noTools
            ? new ToolEnvironment("/nonexistent-app", string.Empty, OperatingSystem.IsWindows(), [])
            : ToolEnvironment.Current;
        var tools = new PostProcessTools(new FixedToolSettings(), runner, environment);
        return new PostProcessor(tools, runner, NullLogger<PostProcessor>.Instance, new FixedPostProcessSwitches(switches));
    }

    private Task<PostProcessResult> Run(PostProcessLevel level = PostProcessLevel.Full, PostProcessSwitches? switches = null, bool noTools = false) =>
        Create(switches ?? PostProcessSwitches.Default, noTools).RunAsync(PostProcessorFactory.Job(_folder) with { Level = level }, _progress, CancellationToken.None);

    private string WriteSource(string name, int size, int seed) => _folder.Write(Path.Combine("src", name), TestData.Random(size, seed));

    private void SevenZip(params string[] arguments)
    {
        Directory.CreateDirectory(Work);
        var source = Path.GetDirectoryName(arguments[^1])!;
        InstalledTools.Run(InstalledTools.SevenZip!, source, [.. arguments[..^1], Path.GetFileName(arguments[^1])]);
    }

    public void Dispose() => _folder.Dispose();

    /// <summary>Records calls and answers with success, without running anything.</summary>
    private sealed class RecordingRunner : IToolProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<ToolProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? workingDirectory, Action<string>? onOutput, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add((fileName, [.. arguments]));
            }

            return Task.FromResult(new ToolProcessResult(0, "Everything is Ok"));
        }
    }
}
