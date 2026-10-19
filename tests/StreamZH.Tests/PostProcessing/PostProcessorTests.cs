// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Library.PostProcessing;

namespace StreamZH.Tests.PostProcessing;

/// <summary>
/// The whole pipeline. Tests marked [RequiresToolsFact] create real par2 sets and archives with the installed tools and
/// are skipped (with the reason) where par2 / 7-Zip / rar are missing.
/// </summary>
public sealed class PostProcessorTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ProgressLog _progress = new();

    private string Work => _folder.Combine("work");

    private string Target => _folder.Combine("done", "Some.Movie");

    [Fact]
    public async Task Without_archives_media_files_are_moved_and_junk_samples_and_nfo_stay_out()
    {
        var movie = TestData.Random(300_000, 1);
        _folder.Write("work/Some.Movie.mkv", movie);
        _folder.Write("work/Some.Movie.nfo", TestData.Random(500, 2));
        _folder.Write("work/Some.Movie.sfv", TestData.Random(50, 3));
        _folder.Write("work/Sample/some.movie-sample.mkv", TestData.Random(1000, 4));
        _folder.Write("work/Subs/English.srt", TestData.Random(200, 5));
        _folder.Write("work/Some.Movie.Sample.mkv", TestData.Random(2000, 6));
        _folder.Write("work/setup.exe", TestData.Random(100, 7));

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.False(result.Repaired);
        Assert.Equal(Target, result.OutputFolder);
        Assert.Equal([Path.Combine(Target, "Some.Movie.mkv"), Path.Combine(Target, "Subs", "English.srt")], result.Files);
        Assert.Equal(movie, File.ReadAllBytes(Path.Combine(Target, "Some.Movie.mkv")));
        Assert.False(Directory.Exists(Work), "work folder is deleted after success");
        Assert.Equal(PostProcessStage.Cleaning, _progress.Items[^1].Stage);
        Assert.Equal(1, _progress.Items[^1].Progress);
    }

    [Fact]
    public async Task Keep_files_leaves_the_rest_of_the_download_folder()
    {
        var movie = TestData.Random(300_000, 1);
        _folder.Write("work/Some.Movie.mkv", movie);
        _folder.Write("work/Some.Movie.nfo", TestData.Random(500, 2));
        _folder.Write("work/Some.Movie.sfv", TestData.Random(50, 3));

        var job = PostProcessorFactory.Job(_folder) with { KeepFiles = true };
        var result = await PostProcessorFactory.Create(noTools: true).RunAsync(job, _progress, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(movie, File.ReadAllBytes(Path.Combine(Target, "Some.Movie.mkv")));
        Assert.False(File.Exists(Path.Combine(Work, "Some.Movie.mkv")), "the media file is moved, not copied");
        Assert.True(File.Exists(Path.Combine(Work, "Some.Movie.nfo")));
        Assert.True(File.Exists(Path.Combine(Work, "Some.Movie.sfv")));
        Assert.DoesNotContain(_progress.Items, p => p.Message == "Deleting download folder");
    }

    [Fact]
    public async Task Big_files_named_sample_are_kept()
    {
        var path = _folder.Write("work/The.Sample.2024.mkv", []);
        using (var stream = new FileStream(path, FileMode.Open))
        {
            stream.SetLength(PostProcessor.SampleMaxBytes + 1);
        }

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.Single(result.Files);
        Assert.True(PostProcessor.IsSample("Sample/x.mkv", 10));
        Assert.True(PostProcessor.IsSample("movie-sample.mkv", 10));
        Assert.False(PostProcessor.IsSample("Samples.of.Life.mkv", PostProcessor.SampleMaxBytes));
        Assert.False(PostProcessor.IsSample("Resampled.mkv", 10));
    }

    [Fact]
    public async Task Intact_par2_set_is_verified_natively_and_par2_files_are_dropped()
    {
        var movie = TestData.Random(200_000, 10);
        var fixture = Par2Builder.Create([("Some.Movie.mkv", movie)], 16384, 2);
        _folder.Write("work/Some.Movie.mkv", movie);
        _folder.Write("work/Some.Movie.par2", fixture.Index);
        _folder.Write("work/Some.Movie.vol0+2.par2", fixture.Volume(0, 2));

        var result = await Run(noTools: true);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Repaired);
        Assert.Equal([Path.Combine(Target, "Some.Movie.mkv")], result.Files);
        Assert.Contains(_progress.Items, p => p.Stage == PostProcessStage.Verifying && p.Progress == 1);
        Assert.DoesNotContain(_progress.Items, p => p.Stage == PostProcessStage.Repairing);
    }

    [Fact]
    public async Task Obfuscated_files_end_up_with_their_real_names_in_the_target()
    {
        var movie = TestData.Random(100_000, 11);
        var fixture = Par2Builder.Create([("Some.Movie.2024.1080p.mkv", movie)], 16384, 1);
        _folder.Write("work/0f3a9c1e7b2d", movie);
        _folder.Write("work/7c1d2e3f4a5b.par2", fixture.Index);

        var result = await Run(noTools: true);

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(Target, "Some.Movie.2024.1080p.mkv")], result.Files);
    }

    [Fact]
    public async Task Repair_without_par2_installed_fails_and_keeps_everything()
    {
        var movie = TestData.Random(100_000, 12);
        var fixture = Par2Builder.Create([("Some.Movie.mkv", movie)], 16384, 3);
        var file = _folder.Write("work/Some.Movie.mkv", movie);
        _folder.Write("work/Some.Movie.par2", fixture.Index);
        _folder.Write("work/Some.Movie.vol0+3.par2", fixture.Volume(0, 3));
        TestData.Corrupt(file, 20_000);

        var result = await Run(noTools: true);

        Assert.False(result.Success);
        Assert.Equal(PostProcessor.Par2MissingError, result.Error);
        Assert.Equal(Work, result.OutputFolder);
        Assert.Empty(result.Files);
        Assert.Equal(3, Directory.GetFiles(Work).Length);
        Assert.False(Directory.Exists(Target), "nothing is left behind in the target");
    }

    [Fact]
    public async Task Not_enough_recovery_blocks_fails_with_the_counts()
    {
        var movie = TestData.Random(100_000, 13);
        var fixture = Par2Builder.Create([("Some.Movie.mkv", movie)], 16384, 1);
        var file = _folder.Write("work/Some.Movie.mkv", movie);
        _folder.Write("work/Some.Movie.par2", fixture.Index);
        _folder.Write("work/Some.Movie.vol0+1.par2", fixture.Volume(0, 1));
        TestData.Corrupt(file, 1000);
        TestData.Corrupt(file, 40_000);

        var result = await Run(noTools: true);

        Assert.False(result.Success);
        Assert.Equal("Not enough recovery blocks to repair: 2 needed, 1 available.", result.Error);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Archives_without_7zip_installed_fail_with_a_hint()
    {
        _folder.Write("work/Some.Movie.rar", [.. "Rar!\u001A\u0007\u0001\u0000"u8, .. TestData.Random(100, 14)]);

        var result = await Run(noTools: true);

        Assert.False(result.Success);
        Assert.Equal(PostProcessor.SevenZipMissingError, result.Error);
        Assert.True(File.Exists(_folder.Combine("work", "Some.Movie.rar")));
    }

    [Fact]
    public async Task Missing_first_volume_fails()
    {
        _folder.Write("work/Some.Movie.part02.rar", TestData.Random(100, 15));

        var result = await Run(noTools: true);

        Assert.Equal("The first volume of Some.Movie is missing.", result.Error);
    }

    [Fact]
    public async Task Cancellation_stops_and_keeps_the_files()
    {
        var movie = TestData.Random(100_000, 16);
        var fixture = Par2Builder.Create([("Some.Movie.mkv", movie)], 16384, 1);
        _folder.Write("work/Some.Movie.mkv", movie);
        _folder.Write("work/Some.Movie.par2", fixture.Index);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PostProcessorFactory.Create(noTools: true).RunAsync(PostProcessorFactory.Job(_folder), _progress, new CancellationToken(canceled: true)));
        Assert.Equal(2, Directory.GetFiles(Work).Length);
    }

    [Theory]
    [InlineData("Movie.2024{{secret}}", null, "secret")]
    [InlineData("Movie {{old}} 2024 {{new pw}}", null, "new pw")]
    [InlineData("Movie.2024{{secret}}", "from-job", "from-job")]
    [InlineData("Movie.2024", null, null)]
    public void Password_comes_from_the_job_or_the_title(string title, string? password, string? expected)
    {
        Assert.Equal(expected, PostProcessor.PasswordFor(new PostProcessJob(1, title, "/w", "/t", password)));
    }

    [RequiresToolsFact("par2")]
    public async Task Damaged_file_is_repaired_with_par2()
    {
        var movie = TestData.Random(3_000_000, 20);
        var file = _folder.Write("work/Some.Movie.mkv", movie);
        _folder.Write("work/Some.Movie.nfo", TestData.Random(100, 21));
        InstalledTools.Run(InstalledTools.Par2!, Work, "c", "-q", "-r10", "-s65536", "Some.Movie.par2", "Some.Movie.mkv");
        TestData.Corrupt(file, 130_000, 4000);

        // The native verifier sees the same damage par2 would.
        var catalog = Par2Reader.ReadFiles(Directory.GetFiles(Work, "*.par2"), verifyRecoveryData: true);
        var check = Par2Verifier.Verify(Work, catalog.Sets.Values.Single(), new Par2Candidates([file]));
        Assert.Equal(2, check.BlocksNeeded);
        Assert.True(check.CanRepair);

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.True(result.Repaired);
        Assert.Equal([Path.Combine(Target, "Some.Movie.mkv")], result.Files);
        Assert.Equal(movie, File.ReadAllBytes(result.Files[0]));
        Assert.False(Directory.Exists(Work));

        var repairing = _progress.Items.Where(p => p.Stage == PostProcessStage.Repairing).ToList();
        Assert.Contains(repairing, p => p.Message == "Repairing 2 block(s)");
        Assert.Contains(repairing, p => p.Progress is > 0.15 and < 0.9);
        Assert.Equal(1, repairing[^1].Progress);
    }

    [RequiresToolsFact("par2")]
    public async Task Obfuscated_and_damaged_set_is_renamed_then_repaired()
    {
        var movie = TestData.Random(2_000_000, 22);
        _folder.Write("work/Some.Movie.mkv", movie);
        InstalledTools.Run(InstalledTools.Par2!, Work, "c", "-q", "-r5", "-s65536", "Some.Movie.par2", "Some.Movie.mkv");

        // Obfuscate: every file gets a random name without extension, the volumes too.
        var index = 0;
        foreach (var path in Directory.GetFiles(Work))
        {
            File.Move(path, Path.Combine(Work, $"{Guid.NewGuid():N}{index++}"));
        }

        var data = Directory.GetFiles(Work).Single(f => !Par2Reader.IsPar2File(f));
        TestData.Corrupt(data, 500_000, 100);

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.True(result.Repaired);
        Assert.Equal(movie, File.ReadAllBytes(Assert.Single(result.Files)));
        Assert.Equal("Some.Movie.mkv", Path.GetFileName(result.Files[0]));
    }

    [RequiresToolsFact("7z")]
    public async Task Single_7z_archive_is_extracted()
    {
        var movie = WriteSource("Some.Movie.mkv", 1_500_000, 30);
        SevenZip("a", "-mx=0", Path.Combine(Work, "Some.Movie.7z"), movie);

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(Target, "Some.Movie.mkv")], result.Files);
        Assert.Equal(File.ReadAllBytes(movie), File.ReadAllBytes(result.Files[0]));
        Assert.False(Directory.Exists(Work));
        Assert.Empty(Directory.GetDirectories(Target));
        Assert.Contains(_progress.Items, p => p.Stage == PostProcessStage.Extracting && p.Message == "Extracting Some.Movie");
        Assert.Equal(1, _progress.Items.Last(p => p.Stage == PostProcessStage.Extracting).Progress);
    }

    [RequiresToolsFact("7z")]
    public async Task Multi_volume_7z_archive_and_loose_subtitles_are_extracted_together()
    {
        var movie = WriteSource("Some.Movie.mkv", 3_000_000, 31);
        SevenZip("a", "-mx=0", "-v1m", Path.Combine(Work, "Some.Movie.7z"), movie);
        _folder.Write("work/Some.Movie.en.srt", TestData.Random(100, 32));
        _folder.Write("work/Some.Movie.nfo", TestData.Random(100, 33));
        Assert.True(File.Exists(Path.Combine(Work, "Some.Movie.7z.003")));

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(Target, "Some.Movie.en.srt"), Path.Combine(Target, "Some.Movie.mkv")], result.Files);
        Assert.Equal(File.ReadAllBytes(movie), File.ReadAllBytes(Path.Combine(Target, "Some.Movie.mkv")));
    }

    [RequiresToolsFact("rar", "7z")]
    public async Task Multi_volume_rar5_set_is_extracted()
    {
        var movie = WriteSource("Some.Movie.mkv", 3_000_000, 34);
        Directory.CreateDirectory(Work);
        InstalledTools.Run(InstalledTools.Rar!, _folder.Combine("src"), "a", "-m0", "-v1m", "-idq", Path.Combine(Work, "Some.Movie.rar"), "Some.Movie.mkv");
        Assert.True(File.Exists(Path.Combine(Work, "Some.Movie.part1.rar")));

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.Equal(File.ReadAllBytes(movie), File.ReadAllBytes(Assert.Single(result.Files)));
    }

    [RequiresToolsFact("7z")]
    public async Task Nested_archive_is_extracted_one_level_deep()
    {
        var movie = WriteSource("Some.Movie.mkv", 500_000, 35);
        var inner = _folder.Combine("src", "inner.zip");
        SevenZip("a", "-mx=0", inner, movie);
        SevenZip("a", "-mx=0", Path.Combine(Work, "Some.Movie.7z"), inner);

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(Target, "Some.Movie.mkv")], result.Files);
        Assert.Equal(File.ReadAllBytes(movie), File.ReadAllBytes(result.Files[0]));
    }

    [RequiresToolsFact("7z")]
    public async Task Encrypted_archive_without_password_fails_as_password_protected_and_keeps_the_download()
    {
        var movie = WriteSource("Some.Movie.mkv", 300_000, 36);
        SevenZip("a", "-mx=0", "-psecret", "-mhe=on", Path.Combine(Work, "Some.Movie.7z"), movie);

        var result = await Run();

        Assert.False(result.Success);
        Assert.StartsWith("Password protected", result.Error);
        Assert.True(File.Exists(Path.Combine(Work, "Some.Movie.7z")));
        Assert.False(Directory.Exists(Target));
    }

    [RequiresToolsFact("7z")]
    public async Task Wrong_password_is_reported()
    {
        var movie = WriteSource("Some.Movie.mkv", 300_000, 37);
        SevenZip("a", "-mx=0", "-psecret", Path.Combine(Work, "Some.Movie.7z"), movie);

        var result = await Run(password: "nope");

        Assert.False(result.Success);
        Assert.Equal("Password protected: the password is wrong.", result.Error);
        Assert.True(File.Exists(Path.Combine(Work, "Some.Movie.7z")));
        Assert.False(Directory.Exists(Target), "partly extracted files are removed");
    }

    [RequiresToolsFact("7z")]
    public async Task Password_from_the_title_opens_the_archive()
    {
        var movie = WriteSource("Some.Movie.mkv", 300_000, 38);
        SevenZip("a", "-mx=0", "-psecret pw", "-mhe=on", Path.Combine(Work, "Some.Movie.7z"), movie);

        var result = await Run(title: "Some.Movie.2024{{secret pw}}");

        Assert.True(result.Success, result.Error);
        Assert.Equal(File.ReadAllBytes(movie), File.ReadAllBytes(Assert.Single(result.Files)));
    }

    [RequiresToolsFact("7z")]
    public async Task Broken_archive_fails_and_keeps_everything_for_inspection()
    {
        var movie = WriteSource("Some.Movie.mkv", 1_000_000, 39);
        SevenZip("a", "-mx=0", "-v300k", Path.Combine(Work, "Some.Movie.7z"), movie);
        File.Delete(Path.Combine(Work, "Some.Movie.7z.002"));

        var result = await Run();

        Assert.False(result.Success);
        Assert.StartsWith("Extraction failed:", result.Error);
        Assert.Equal(3, Directory.GetFiles(Work).Length);
        Assert.False(Directory.Exists(Target));
    }

    [RequiresToolsFact("par2", "7z")]
    public async Task Obfuscated_damaged_multi_volume_release_is_repaired_and_extracted()
    {
        var movie = WriteSource("Some.Movie.2024.1080p.mkv", 4_000_000, 40);
        SevenZip("a", "-mx=0", "-v1m", Path.Combine(Work, "Some.Movie.7z"), movie);
        var volumes = Directory.GetFiles(Work).Select(Path.GetFileName).Order().ToArray();
        InstalledTools.Run(InstalledTools.Par2!, Work, ["c", "-q", "-r35", "-s32768", "Some.Movie.par2", .. volumes!]);
        TestData.Corrupt(Path.Combine(Work, "Some.Movie.7z.002"), 10_000, 2000);
        File.Delete(Path.Combine(Work, "Some.Movie.7z.004"));

        // The remaining archive volumes lose their names (par2 files keep theirs, like most obfuscated posts). The damage
        // in .002 is inside its first 16 KiB, so that one is only recognised by its slice checksums.
        foreach (var volume in Directory.GetFiles(Work, "Some.Movie.7z.*"))
        {
            File.Move(volume, Path.Combine(Work, Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())));
        }

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.True(result.Repaired);
        Assert.Equal(File.ReadAllBytes(movie), File.ReadAllBytes(Assert.Single(result.Files)));
        Assert.Equal(
            [PostProcessStage.Verifying, PostProcessStage.Repairing, PostProcessStage.Extracting, PostProcessStage.Cleaning],
            _progress.Items.Select(p => p.Stage).Distinct());
        Assert.All(_progress.Items, p => Assert.InRange(p.Progress, 0, 1));
        Assert.False(Directory.Exists(Work));
    }

    [Fact]
    public async Task A_directory_link_in_the_download_is_not_followed_when_moving()
    {
        // S2-5: a link to a folder outside the download must not make StreamZH move the files it points to.
        if (OperatingSystem.IsWindows())
        {
            return; // creating links needs developer mode or administrator rights there
        }

        var movie = TestData.Random(300_000, 1);
        _folder.Write("work/Some.Movie.mkv", movie);
        var outside = _folder.Write("outside/secret.txt", TestData.Random(100, 2));
        Directory.CreateSymbolicLink(Path.Combine(Work, "linked"), Path.GetDirectoryName(outside)!);
        File.CreateSymbolicLink(Path.Combine(Work, "passwd.mkv"), outside);

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(Target, "Some.Movie.mkv")], result.Files);
        Assert.True(File.Exists(outside), "the file behind the link stays where it is");
        Assert.False(Directory.Exists(Path.Combine(Target, "linked")));
    }

    [RequiresToolsFact("7z")]
    public async Task Links_from_an_archive_are_removed_after_extraction()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var source = _folder.Combine("src", "Some.Movie");
        _folder.Write("src/Some.Movie/Some.Movie.mkv", TestData.Random(300_000, 3));
        _folder.Write("src/Some.Movie/Subs/English.srt", TestData.Random(200, 4));
        File.CreateSymbolicLink(Path.Combine(source, "Other.mkv"), "Some.Movie.mkv");
        Directory.CreateSymbolicLink(Path.Combine(source, "SubsLink"), "Subs");
        SevenZip("a", "-snl", "-mx=0", Path.Combine(Work, "Some.Movie.7z"), source);

        var result = await Run();

        Assert.True(result.Success, result.Error);
        Assert.Equal([Path.Combine(Target, "Some.Movie", "Some.Movie.mkv"), Path.Combine(Target, "Some.Movie", "Subs", "English.srt")], result.Files);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Target, "*", SearchOption.AllDirectories), p => new FileInfo(p).LinkTarget is not null);
    }

    [Fact]
    public void Delete_links_removes_only_the_links()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var keep = _folder.Write("tree/a/keep.txt", TestData.Random(10, 5));
        var outside = _folder.Write("outside/target.txt", TestData.Random(10, 6));
        Directory.CreateSymbolicLink(_folder.Combine("tree", "a", "dir"), Path.GetDirectoryName(outside)!);
        File.CreateSymbolicLink(_folder.Combine("tree", "file"), outside);
        File.CreateSymbolicLink(_folder.Combine("tree", "dangling"), _folder.Combine("nowhere"));

        Assert.Equal(3, PostProcessor.DeleteLinks(_folder.Combine("tree")));

        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(outside));
        Assert.Equal([keep], Directory.EnumerateFileSystemEntries(_folder.Combine("tree"), "*", SearchOption.AllDirectories).Where(File.Exists));
    }

    private Task<PostProcessResult> Run(bool noTools = false, string? password = null, string title = "Some.Movie.2024.1080p.WEB-DL") =>
        PostProcessorFactory.Create(noTools).RunAsync(PostProcessorFactory.Job(_folder, title, password), _progress, CancellationToken.None);

    private string WriteSource(string name, int size, int seed) => _folder.Write(Path.Combine("src", name), TestData.Random(size, seed));

    private void SevenZip(params string[] arguments)
    {
        Directory.CreateDirectory(Work);

        // Archive entries without the source folder: run from the source folder with file names only.
        var source = Path.GetDirectoryName(arguments[^1])!;
        InstalledTools.Run(InstalledTools.SevenZip!, source, [.. arguments[..^1], Path.GetFileName(arguments[^1])]);
    }

    public void Dispose() => _folder.Dispose();
}
