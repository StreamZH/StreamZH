// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.PostProcessing;

namespace StreamZH.Tests.PostProcessing;

/// <summary>The native PAR2 reader and verifier with hand-built packets: no par2 binary needed.</summary>
public sealed class Par2NativeTests : IDisposable
{
    private const int SliceSize = 4096;
    private readonly TempFolder _folder = new();

    [Fact]
    public void Crc32_matches_the_reference_implementation()
    {
        foreach (var length in new[] { 0, 1, 7, 8, 9, 63, 4096, 100_003 })
        {
            var data = TestData.Random(length, length);
            Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(data), Crc32.Compute(data));

            // Appending in pieces gives the same result.
            var crc = Crc32.Append(Crc32.Initial, data.AsSpan(0, length / 3));
            crc = Crc32.Append(crc, data.AsSpan(length / 3));
            Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(data), Crc32.Finish(crc));
        }
    }

    [Fact]
    public void Reads_main_file_descriptions_slice_checksums_creator_and_recovery_blocks()
    {
        var movie = TestData.Random((SliceSize * 5) + 123, 1);
        var subs = TestData.Random(1000, 2);
        var fixture = Par2Builder.Create([("movie.mkv", movie), ("movie.srt", subs)], SliceSize, recoveryBlocks: 3);
        var index = _folder.Write("set.par2", fixture.Index);
        var volume = _folder.Write("set.vol0+3.par2", fixture.Volume(0, 3));

        Assert.True(Par2Reader.IsPar2File(index));
        Assert.False(Par2Reader.IsPar2File(_folder.Write("movie.mkv", movie)));

        var catalog = Par2Reader.ReadFiles([index, volume], verifyRecoveryData: true);

        var set = Assert.Single(catalog.Sets.Values);
        Assert.Equal(fixture.SetId, set.SetId);
        Assert.Equal(SliceSize, set.SliceSize);
        Assert.Equal(2, set.RecoveryFileIds.Count);
        Assert.Equal(["movie.mkv", "movie.srt"], set.RecoverableFiles.Select(f => f.Name).Order());
        var description = set.Files.Values.Single(f => f.Name == "movie.mkv");
        Assert.Equal(movie.Length, description.Length);
        Assert.Equal(6, set.SliceCount(description));
        Assert.Equal(6, set.SliceChecksums[description.FileId].Length);
        Assert.Equal("StreamZH tests", set.Creator);
        Assert.Equal(3, set.RecoveryBlocks);
        Assert.Equal([index, volume], set.Par2Files);
    }

    [Fact]
    public void Skips_damaged_packets_and_garbage_and_finds_the_rest()
    {
        var fixture = Par2Builder.Create([("a.bin", TestData.Random(SliceSize * 3, 3))], SliceSize, recoveryBlocks: 4);

        // Garbage in front, one recovery packet with a broken byte, and a cut-off packet at the end.
        var broken = fixture.Recovery[1].ToArray();
        broken[200] ^= 0x55;
        byte[] data = [.. TestData.Random(333, 4), .. fixture.Index, .. fixture.Recovery[0], .. broken, .. fixture.Recovery[2], .. fixture.Recovery[3].AsSpan(0, 500)];
        var path = _folder.Write("damaged.par2", data);

        var verified = Par2Reader.ReadFiles([path], verifyRecoveryData: true).Sets[fixture.SetId];
        Assert.Equal(SliceSize, verified.SliceSize);
        Assert.Single(verified.Files);
        Assert.Equal([0u, 2u], verified.RecoveryExponents.Order());

        // Without checking the data the damaged packet is counted (fast path), the cut-off one is not.
        var quick = Par2Reader.ReadFiles([path], verifyRecoveryData: false).Sets[fixture.SetId];
        Assert.Equal([0u, 1u, 2u], quick.RecoveryExponents.Order());
    }

    [Fact]
    public void Unicode_name_packets_win_over_the_ascii_name()
    {
        var fixture = Par2Builder.Create([("Amélie (2001).mkv", TestData.Random(5000, 5))], SliceSize, 0, unicodeNames: true);
        var path = _folder.Write("set.par2", fixture.Index);

        var set = Par2Reader.ReadFiles([path], verifyRecoveryData: false).Sets[fixture.SetId];

        Assert.Equal("Amélie (2001).mkv", Assert.Single(set.Files.Values).Name);
    }

    [Fact]
    public void Clean_files_verify_without_repair()
    {
        var (set, candidates) = Prepare(out _, ("movie.mkv", TestData.Random((SliceSize * 4) + 10, 6)), ("movie.nfo", TestData.Random(300, 7)));
        var reports = new List<double>();

        var result = Par2Verifier.Verify(_folder.Path, set, candidates, new SyncProgress(reports.Add));

        Assert.False(result.NeedsRepair);
        Assert.All(result.Files, f => Assert.Equal(Par2FileState.Ok, f.State));
        Assert.Equal(0, result.BlocksNeeded);
        Assert.Equal(1, reports[^1]);
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.First <= p.Second), "progress only goes up");
    }

    [Fact]
    public void Damaged_file_reports_the_number_of_bad_blocks()
    {
        var (set, candidates) = Prepare(out var paths, ("movie.mkv", TestData.Random((SliceSize * 8) + 100, 8)));
        TestData.Corrupt(paths["movie.mkv"], (SliceSize * 2) + 5);
        TestData.Corrupt(paths["movie.mkv"], (SliceSize * 6) + 99, 16);

        var result = Par2Verifier.Verify(_folder.Path, set, candidates);

        var file = Assert.Single(result.Files);
        Assert.Equal(Par2FileState.Damaged, file.State);
        Assert.Equal(9, file.TotalSlices);
        Assert.Equal(2, file.DamagedSlices);
        Assert.Equal(2, result.BlocksNeeded);
        Assert.Equal(2, result.BlocksAvailable);
        Assert.True(result.CanRepair);
    }

    [Fact]
    public void Damaged_last_partial_slice_is_found()
    {
        var (set, candidates) = Prepare(out var paths, ("movie.mkv", TestData.Random((SliceSize * 3) + 100, 9)));
        TestData.Corrupt(paths["movie.mkv"], (SliceSize * 3) + 50, 4);

        var file = Assert.Single(Par2Verifier.Verify(_folder.Path, set, candidates).Files);

        Assert.Equal(1, file.DamagedSlices);
    }

    [Fact]
    public void Truncated_and_missing_files_count_all_missing_blocks()
    {
        var (set, candidates) = Prepare(out var paths, ("a.rar", TestData.Random(SliceSize * 10, 10)), ("b.r00", TestData.Random(SliceSize * 4, 11)));

        // a.rar loses its last 3.5 slices, b.r00 is gone.
        using (var stream = new FileStream(paths["a.rar"], FileMode.Open))
        {
            stream.SetLength((SliceSize * 6) + (SliceSize / 2));
        }

        File.Delete(paths["b.r00"]);
        candidates = new Par2Candidates([paths["a.rar"]]);

        var result = Par2Verifier.Verify(_folder.Path, set, candidates);

        var a = result.Files.Single(f => f.Name == "a.rar");
        Assert.Equal(Par2FileState.Damaged, a.State);
        Assert.Equal(4, a.DamagedSlices);
        var b = result.Files.Single(f => f.Name == "b.r00");
        Assert.Equal(Par2FileState.Missing, b.State);
        Assert.Equal(4, b.DamagedSlices);
        Assert.Equal(_folder.Combine("b.r00"), b.Path);
        Assert.Equal(8, result.BlocksNeeded);
        Assert.False(result.CanRepair);
    }

    [Fact]
    public void Obfuscated_files_get_their_real_names_back()
    {
        var part1 = TestData.Random((SliceSize * 6) + 7, 12);
        var part2 = TestData.Random(SliceSize * 5, 13);
        var fixture = Par2Builder.Create([("Show.S01E01.part1.rar", part1), ("Show.S01E01.part2.rar", part2)], SliceSize, 1);

        // Typical obfuscated post: random names everywhere, the par2 file too, the second file also damaged.
        var par2 = _folder.Write("b8f1c0e0d7a64f3c", fixture.Index);
        var first = _folder.Write("a1b2c3d4e5f6a7b8c9d0", part1);
        var second = _folder.Write("ffeeddccbbaa99887766", part2);
        var unrelated = _folder.Write("readme.txt", TestData.Random(20000, 14));
        TestData.Corrupt(second, (SliceSize * 4) + 1);

        Assert.True(Par2Reader.IsPar2File(par2));
        var set = Par2Reader.ReadFiles([par2], verifyRecoveryData: false).Sets[fixture.SetId];
        var result = Par2Verifier.Verify(_folder.Path, set, new Par2Candidates([first, second, unrelated]));

        Assert.True(File.Exists(_folder.Combine("Show.S01E01.part1.rar")));
        Assert.True(File.Exists(_folder.Combine("Show.S01E01.part2.rar")));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(unrelated));

        var renamed1 = result.Files.Single(f => f.Name == "Show.S01E01.part1.rar");
        Assert.Equal("a1b2c3d4e5f6a7b8c9d0", renamed1.RenamedFrom);
        Assert.Equal(Par2FileState.Ok, renamed1.State);
        var renamed2 = result.Files.Single(f => f.Name == "Show.S01E01.part2.rar");
        Assert.Equal(Par2FileState.Damaged, renamed2.State);
        Assert.Equal(1, renamed2.DamagedSlices);
    }

    [Fact]
    public void Obfuscated_truncated_file_is_still_recognised_by_its_first_16k()
    {
        var data = TestData.Random(SliceSize * 10, 15);
        var fixture = Par2Builder.Create([("movie.mkv", data)], SliceSize, 2);
        var par2 = _folder.Write("movie.par2", fixture.Index);
        var obfuscated = _folder.Write("x7Yq2Lm9", data.AsSpan(0, SliceSize * 8).ToArray());

        var set = Par2Reader.ReadFiles([par2], false).Sets[fixture.SetId];
        var result = Par2Verifier.Verify(_folder.Path, set, new Par2Candidates([obfuscated]));

        var file = Assert.Single(result.Files);
        Assert.Equal("x7Yq2Lm9", file.RenamedFrom);
        Assert.Equal(2, file.DamagedSlices);
    }

    [Fact]
    public void Obfuscated_file_damaged_at_the_start_is_recognised_by_its_slices()
    {
        var first = TestData.Random(SliceSize * 8, 16);
        var second = TestData.Random(SliceSize * 8, 17);
        var fixture = Par2Builder.Create([("x.part1.rar", first), ("x.part2.rar", second)], SliceSize, 2);
        var par2 = _folder.Write("x.par2", fixture.Index);
        var a = _folder.Write("aaaa", first);
        var b = _folder.Write("bbbb", second);
        TestData.Corrupt(b, 10);

        var set = Par2Reader.ReadFiles([par2], false).Sets[fixture.SetId];
        var result = Par2Verifier.Verify(_folder.Path, set, new Par2Candidates([a, b]));

        Assert.Equal("aaaa", result.Files.Single(f => f.Name == "x.part1.rar").RenamedFrom);
        var damaged = result.Files.Single(f => f.Name == "x.part2.rar");
        Assert.Equal("bbbb", damaged.RenamedFrom);
        Assert.Equal(1, damaged.DamagedSlices);
    }

    [Fact]
    public void Same_size_file_with_other_content_is_not_taken()
    {
        var data = TestData.Random(SliceSize * 4, 18);
        var fixture = Par2Builder.Create([("movie.mkv", data)], SliceSize, 1);
        var par2 = _folder.Write("movie.par2", fixture.Index);
        var other = _folder.Write("other.bin", TestData.Random(SliceSize * 4, 19));

        var set = Par2Reader.ReadFiles([par2], false).Sets[fixture.SetId];
        var result = Par2Verifier.Verify(_folder.Path, set, new Par2Candidates([other]));

        Assert.Equal(Par2FileState.Missing, Assert.Single(result.Files).State);
        Assert.True(File.Exists(other));
    }

    [Theory]
    [InlineData("../evil.mkv", "evil.mkv")]
    [InlineData("sub/../../evil.mkv", "sub/evil.mkv")]
    [InlineData("/etc/passwd", "etc/passwd")]
    [InlineData("Subs\\English.srt", "Subs/English.srt")]
    public void Names_from_par2_files_cannot_leave_the_folder(string name, string expected)
    {
        var path = Par2Verifier.TargetPath(_folder.Path, name);

        Assert.Equal(_folder.Combine(expected.Split('/')), path);
    }

    private (Par2Set Set, Par2Candidates Candidates) Prepare(out Dictionary<string, string> paths, params (string Name, byte[] Data)[] files)
    {
        var fixture = Par2Builder.Create(files, SliceSize, recoveryBlocks: 2);
        var index = _folder.Write("set.par2", fixture.Index);
        var volume = _folder.Write("set.vol0+2.par2", fixture.Volume(0, 2));
        paths = files.ToDictionary(f => f.Name, f => _folder.Write(f.Name, f.Data));
        var set = Par2Reader.ReadFiles([index, volume], verifyRecoveryData: false).Sets[fixture.SetId];
        return (set, new Par2Candidates(paths.Values));
    }

    public void Dispose() => _folder.Dispose();

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
