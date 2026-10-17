// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StreamZH.Usenet.Nzb;

/// <summary>Reads NZB files (newzbin DTD 1.1). Namespace-agnostic and safe against external entities.</summary>
public static partial class NzbParser
{
    public static NzbDocument Parse(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
        };

        using var reader = XmlReader.Create(stream, settings);
        var root = XDocument.Load(reader).Root ?? throw new InvalidNzbException("NZB has no root element.");

        if (root.Name.LocalName != "nzb")
        {
            throw new InvalidNzbException($"Unexpected root element '{root.Name.LocalName}'.");
        }

        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Children(root, "head").SelectMany(h => Children(h, "meta")))
        {
            var type = (string?)entry.Attribute("type");
            if (!string.IsNullOrWhiteSpace(type))
            {
                meta[type] = entry.Value.Trim();
            }
        }

        var files = Children(root, "file").Select(ParseFile).Where(f => f.Segments.Count > 0).ToList();
        if (files.Count == 0)
        {
            throw new InvalidNzbException("NZB contains no files with segments.");
        }

        return new NzbDocument(meta, files);
    }

    public static NzbDocument Parse(string xml)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return Parse(stream);
    }

    private static NzbFile ParseFile(XElement file)
    {
        var subject = (string?)file.Attribute("subject") ?? string.Empty;
        var poster = (string?)file.Attribute("poster") ?? string.Empty;
        var date = long.TryParse((string?)file.Attribute("date"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : DateTimeOffset.MinValue;

        var groups = Children(file, "groups").SelectMany(g => Children(g, "group")).Select(g => g.Value.Trim()).ToList();

        var segments = Children(file, "segments")
            .SelectMany(s => Children(s, "segment"))
            .Select(s => new NzbSegment(
                int.TryParse((string?)s.Attribute("number"), out var n) ? n : 0,
                int.TryParse((string?)s.Attribute("bytes"), out var b) ? b : 0,
                s.Value.Trim().Trim('<', '>')))
            .Where(s => s.MessageId.Length > 0)
            .GroupBy(s => s.Number)
            .Select(g => g.First())
            .OrderBy(s => s.Number)
            .ToList();

        return new NzbFile(subject, ExtractFileName(subject), poster, date, groups, segments);
    }

    /// <summary>Most posters put the file name in quotes inside the subject.</summary>
    public static string ExtractFileName(string subject)
    {
        var quoted = QuotedNameRegex().Match(subject);
        if (quoted.Success)
        {
            return quoted.Groups[1].Value.Trim();
        }

        var withExtension = NameWithExtensionRegex().Match(subject);
        return withExtension.Success ? withExtension.Value.Trim() : subject.Trim();
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedNameRegex();

    [GeneratedRegex(@"[\w.\-\[\]() ]+\.(par2|rar|r\d{2,3}|7z|zip|\d{3}|nfo|sfv|mkv|mp4|avi)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NameWithExtensionRegex();
}

public sealed class InvalidNzbException(string message) : Exception(message);