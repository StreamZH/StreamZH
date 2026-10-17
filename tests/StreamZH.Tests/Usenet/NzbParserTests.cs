// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Usenet.Nzb;

namespace StreamZH.Tests.Usenet;

public class NzbParserTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE nzb PUBLIC "-//newzBin//DTD NZB 1.1//EN" "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd">
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <head>
            <meta type="title">John Doe Home Video</meta>
            <meta type="password">secret</meta>
          </head>
          <file poster="John Doe &lt;john@example.com&gt;" date="1760000000" subject="[1/3] - &quot;home.video.2026.1080p.part01.rar&quot; yEnc (1/2)">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              <segment bytes="768000" number="2">part2of2@example.com</segment>
              <segment bytes="768000" number="1">&lt;part1of2@example.com&gt;</segment>
            </segments>
          </file>
          <file poster="John Doe" date="1760000000" subject="[2/3] - &quot;home.video.2026.1080p.par2&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments><segment bytes="20000" number="1">par2index@example.com</segment></segments>
          </file>
          <file poster="John Doe" date="1760000000" subject="[3/3] - &quot;home.video.2026.1080p.vol00+01.par2&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments><segment bytes="400000" number="1">par2vol@example.com</segment></segments>
          </file>
        </nzb>
        """;

    [Fact]
    public void Parses_files_segments_and_meta()
    {
        var nzb = NzbParser.Parse(Sample);

        Assert.Equal(3, nzb.Files.Count);
        Assert.Equal("secret", nzb.Password);
        Assert.Equal(768000L * 2 + 20000 + 400000, nzb.TotalBytes);

        var rar = nzb.Files[0];
        Assert.Equal("home.video.2026.1080p.part01.rar", rar.FileName);
        Assert.Equal(NzbFileKind.Archive, rar.Kind);
        Assert.Equal(["part1of2@example.com", "part2of2@example.com"], rar.Segments.Select(s => s.MessageId));
        Assert.Equal("alt.binaries.test", Assert.Single(rar.Groups));
    }

    [Theory]
    [InlineData("show.s01e01.par2", NzbFileKind.Par2Index)]
    [InlineData("show.s01e01.vol03+04.par2", NzbFileKind.Par2Recovery)]
    [InlineData("show.s01e01.r07", NzbFileKind.Archive)]
    [InlineData("show.s01e01.7z.002", NzbFileKind.Archive)]
    [InlineData("show.s01e01.nfo", NzbFileKind.Nfo)]
    [InlineData("show.s01e01.mkv", NzbFileKind.Other)]
    public void Classifies_files(string fileName, NzbFileKind expected) =>
        Assert.Equal(expected, NzbFileClassifier.Classify(fileName));

    [Fact]
    public void Rejects_document_without_files() =>
        Assert.Throws<InvalidNzbException>(() => NzbParser.Parse("<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"></nzb>"));
}