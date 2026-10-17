// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.Downloads;
using StreamZH.Tests.PostProcessing;

namespace StreamZH.Tests.Library.Downloads;

public sealed class RarInspectorTests : IDisposable
{
    private readonly TempFolder _folder = new();

    [Fact]
    public void Rar4_headers_tell_whether_a_password_is_needed()
    {
        var encrypted = DownloadSwitchesTests.EncryptedRar4();
        Assert.True(RarInspector.IsEncrypted(encrypted));

        var plain = (byte[])encrypted.Clone();
        plain[23] = 0x00; // file header flags without "password"
        Assert.False(RarInspector.IsEncrypted(plain));

        var lockedHeaders = (byte[])plain.Clone();
        lockedHeaders[10] = 0x80; // main header: block headers encrypted
        Assert.True(RarInspector.IsEncrypted(lockedHeaders));

        Assert.Null(RarInspector.IsEncrypted("not a rar file"u8));
        Assert.Null(RarInspector.IsEncrypted(encrypted.AsSpan(0, 12)));
        Assert.False(RarInspector.IsRar("PK\u0003\u0004"u8));
    }

    [RequiresToolsFact("rar")]
    public void Rar5_archives_made_by_rar()
    {
        _folder.Write("src/movie.mkv", TestData.Random(200_000, 1));
        var plain = Make("plain.rar");
        var files = Make("files.rar", "-psecret");
        var headers = Make("headers.rar", "-hpsecret");
        var volumes = Make("volumes.rar", "-psecret", "-v60k");

        Assert.False(RarInspector.IsEncrypted(FirstArticle(plain)));
        Assert.True(RarInspector.IsEncrypted(FirstArticle(files)));
        Assert.True(RarInspector.IsEncrypted(FirstArticle(headers)));

        // Every volume carries the headers: the first article of the second volume is enough, too.
        Assert.True(RarInspector.IsEncrypted(FirstArticle(volumes.Replace("part1", "part2", StringComparison.Ordinal))));
    }

    private string Make(string name, params string[] options)
    {
        var archive = _folder.Combine("out", name);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        InstalledTools.Run(InstalledTools.Rar!, _folder.Combine("src"), ["a", "-m0", "-idq", .. options, archive, "movie.mkv"]);
        return File.Exists(archive) ? archive : archive.Replace(".rar", ".part1.rar", StringComparison.Ordinal);
    }

    /// <summary>The first 4000 bytes, about what the first article of a volume holds.</summary>
    private static byte[] FirstArticle(string path) => File.ReadAllBytes(path)[..4000];

    public void Dispose() => _folder.Dispose();
}
