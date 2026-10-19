// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.Security;

namespace StreamZH.Tests.Host;

/// <summary>SV4 Y-01: a link in place of a secret folder of the data folder is reported.</summary>
public sealed class DataFolderLinkTests
{
    [Fact]
    public void Links_in_place_of_secret_folders_are_reported()
    {
        Assert.Null(DataFolderAcl.LinkProblem(@"C:\ProgramData\StreamZH", []));
        Assert.Null(DataFolderAcl.LinkProblem(@"C:\ProgramData\StreamZH", ["downloads"]));
        Assert.Contains("keys is a link", DataFolderAcl.LinkProblem(@"C:\ProgramData\StreamZH", ["keys"]), StringComparison.Ordinal);
        Assert.Contains("keys, Backups are links", DataFolderAcl.LinkProblem(@"C:\ProgramData\StreamZH", ["keys", "Backups", "logs"]), StringComparison.Ordinal);
    }
}
