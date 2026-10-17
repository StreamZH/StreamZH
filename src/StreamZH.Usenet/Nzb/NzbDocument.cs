// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Usenet.Nzb;

public sealed record NzbDocument(IReadOnlyDictionary<string, string> Meta, IReadOnlyList<NzbFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Bytes);

    /// <summary>Optional password from the &lt;meta type="password"&gt; head entry.</summary>
    public string? Password => Meta.TryGetValue("password", out var value) ? value : null;
}

public sealed record NzbFile(
    string Subject,
    string FileName,
    string Poster,
    DateTimeOffset Date,
    IReadOnlyList<string> Groups,
    IReadOnlyList<NzbSegment> Segments)
{
    public long Bytes => Segments.Sum(s => (long)s.Bytes);

    public NzbFileKind Kind => NzbFileClassifier.Classify(FileName);
}

public sealed record NzbSegment(int Number, int Bytes, string MessageId);

public enum NzbFileKind
{
    Other,
    Par2Index,
    Par2Recovery,
    Archive,
    Nfo,
    Sfv,
}