// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Library.Import;

namespace StreamZH.Tests.Library;

/// <summary>V-02: no feature writes into the data folder (keys, tools, database) except its downloads/ and Backups/ folders.</summary>
public sealed class DataFolderGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("streamzh-guard-").FullName;

    private string Data => Path.Combine(_root, "data");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Inside_the_data_folder_only_downloads_or_backups_are_allowed()
    {
        Assert.NotNull(DataFolderGuard.Problem(Data, Data));
        Assert.NotNull(DataFolderGuard.Problem(Data, Path.Combine(Data, "tools", "par2")));
        Assert.NotNull(DataFolderGuard.Problem(Data, Path.Combine(Data, "keys")));
        Assert.NotNull(DataFolderGuard.Problem(Data, Path.Combine(Data, "downloads", "..", "keys")));
        Assert.NotNull(DataFolderGuard.Problem(Data, _root));
        Assert.Null(DataFolderGuard.Problem(Data, Path.Combine(Data, "downloads", "complete")));
        Assert.Null(DataFolderGuard.Problem(Data, Path.Combine(_root, "media")));
        Assert.Null(DataFolderGuard.Problem(Data, Data + "-other"));
        Assert.NotNull(DataFolderGuard.Problem(Data, Path.Combine(Data, "downloads"), DataFolderUse.Backups));
        Assert.Null(DataFolderGuard.Problem(Data, Path.Combine(Data, "Backups"), DataFolderUse.Backups));
        Assert.NotNull(DataFolderGuard.Problem(Data, Path.GetPathRoot(Data)));
    }

    [Fact]
    public void A_link_into_the_data_folder_is_followed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(Data, "tools"));
        var link = Path.Combine(_root, "innocent");
        Directory.CreateSymbolicLink(link, Path.Combine(Data, "tools"));

        Assert.NotNull(DataFolderGuard.Problem(Data, link));
        Assert.NotNull(DataFolderGuard.Problem(Data, Path.Combine(link, "par2")));
    }

    [Fact]
    public void Imports_renames_and_the_recycle_bin_do_not_write_into_the_data_folder()
    {
        var files = new ImportFileSystem(NullLogger<ImportFileSystem>.Instance, new AppPaths(Data));
        var source = Path.Combine(_root, "release.mkv");
        File.WriteAllText(source, "video");
        var target = Path.Combine(Data, "tools", "7-Zip", "7z.dll");

        Assert.Throws<ImportException>(() => files.Transfer(source, target, ImportMode.Copy, useHardlinks: false));
        Assert.Throws<ImportException>(() => files.Recycle(source, Path.Combine(Data, "tools"), "x"));
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(source));

        var ok = Path.Combine(_root, "media", "release.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(ok)!);
        Assert.Equal(TransferKind.Copied, files.Transfer(source, ok, ImportMode.Copy, useHardlinks: false));
    }
}
