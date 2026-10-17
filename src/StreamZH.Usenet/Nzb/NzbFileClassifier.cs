// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Usenet.Nzb;

public static partial class NzbFileClassifier
{
    public static NzbFileKind Classify(string fileName)
    {
        var name = fileName.ToLowerInvariant();

        if (name.EndsWith(".par2", StringComparison.Ordinal))
        {
            return Par2VolumeRegex().IsMatch(name) ? NzbFileKind.Par2Recovery : NzbFileKind.Par2Index;
        }

        if (ArchiveRegex().IsMatch(name))
        {
            return NzbFileKind.Archive;
        }

        if (name.EndsWith(".nfo", StringComparison.Ordinal))
        {
            return NzbFileKind.Nfo;
        }

        return name.EndsWith(".sfv", StringComparison.Ordinal) ? NzbFileKind.Sfv : NzbFileKind.Other;
    }

    [GeneratedRegex(@"\.vol\d+[+-]\d+\.par2$")]
    private static partial Regex Par2VolumeRegex();

    // .rar, .r00-.r99, .part01.rar, .7z, .7z.001, .zip, .001
    [GeneratedRegex(@"(\.rar|\.r\d{2,3}|\.7z(\.\d{3})?|\.zip|\.\d{3})$")]
    private static partial Regex ArchiveRegex();
}