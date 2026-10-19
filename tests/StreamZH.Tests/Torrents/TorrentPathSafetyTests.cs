// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using StreamZH.Core.Downloads;
using StreamZH.Torrent;

namespace StreamZH.Tests.Torrents;

/// <summary>Security review finding S2-1: names from a torrent must never lead outside the download folder.</summary>
public sealed class TorrentPathSafetyTests : IAsyncLifetime
{
    private readonly TorrentEngineTestBed _bed = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _bed.DisposeAsync();

    [Theory]
    [InlineData("../.bashrc")]
    [InlineData("..\\evil.exe")]
    [InlineData("/tmp/abs_single.txt")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("C:\\Users\\Public\\Startup\\x.bat")]
    [InlineData("C:x")]
    [InlineData("folder/../../x")]
    [InlineData("NUL")]
    [InlineData("con.txt")]
    [InlineData("a\u0000b")]
    public void Unsafe_names_are_refused(string name) => Assert.NotNull(TorrentPathSafety.NameProblem(name));

    [Theory]
    [InlineData("Some.Movie.2024.1080p.mkv")]
    [InlineData("Season 01/Show.S01E01.mkv")]
    [InlineData("..hidden name..txt")]
    [InlineData("Console.log")]
    public void Normal_names_pass(string name) => Assert.Null(TorrentPathSafety.NameProblem(name));

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/tmp/streamzh-evil.txt")]
    public async Task A_torrent_with_an_unsafe_name_is_not_added_and_writes_nothing(string name)
    {
        await using var engine = _bed.CreateEngine();
        var bytes = SingleFileTorrent(name, 5);
        var error = await Assert.ThrowsAsync<DownloadClientException>(() =>
            engine.AddAsync(new TorrentAddRequest("Evil", bytes, null, "movies"), CancellationToken.None));
        Assert.Contains("not added", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_bed.DownloadFolder, "movies", name))));
        Assert.Empty(await engine.GetTorrentsAsync(null, CancellationToken.None));
    }

    [Fact]
    public void Paths_inside_and_outside_the_folder_are_told_apart()
    {
        var root = Path.Combine(Path.GetTempPath(), "streamzh-root");
        Assert.True(TorrentPathSafety.IsInside(root, Path.Combine(root, "a", "b.mkv")));
        Assert.False(TorrentPathSafety.IsInside(root, Path.Combine(root, "..", "x")));
        Assert.False(TorrentPathSafety.IsInside(root, root + "-other" + Path.DirectorySeparatorChar + "x"));
        Assert.False(TorrentPathSafety.IsInside(root, root));
    }

    /// <summary>A minimal bencoded single-file torrent whose name is taken as given.</summary>
    private static byte[] SingleFileTorrent(string name, int length)
    {
        var pieces = SHA1.HashData(new byte[length]);
        using var ms = new MemoryStream();
        void Write(string text) => ms.Write(Encoding.UTF8.GetBytes(text));
        void Bytes(byte[] value)
        {
            Write($"{value.Length}:");
            ms.Write(value);
        }

        var nameBytes = Encoding.UTF8.GetBytes(name);
        Write("d4:infod6:lengthi" + length + "e4:name");
        Bytes(nameBytes);
        Write("12:piece lengthi16384e6:pieces");
        Bytes(pieces);
        Write("ee");
        return ms.ToArray();
    }
}
