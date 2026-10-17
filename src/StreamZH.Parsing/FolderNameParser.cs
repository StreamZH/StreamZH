// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;

namespace StreamZH.Parsing;

public sealed record ParsedFolderName(string Title, int? Year, int? TmdbId);

/// <summary>
/// Reads title, year and an optional TMDB id from library folder names such as
/// "Blade Runner 2049 (2017)", "The.Matrix.1999.1080p" or "Dune (2021) {tmdb-438631}".
/// </summary>
public static partial class FolderNameParser
{
    public static ParsedFolderName Parse(string folderName)
    {
        var name = folderName.Trim();

        int? tmdbId = null;
        var idMatch = TmdbIdRegex().Match(name);
        if (idMatch.Success)
        {
            tmdbId = int.Parse(idMatch.Groups["id"].Value, CultureInfo.InvariantCulture);
        }

        // Drop {tmdb-1}, [imdbid-tt1], {tvdb-1} and similar tags.
        name = IdTagRegex().Replace(name, " ");

        // Scene style: dots or underscores instead of spaces.
        if (!name.Contains(' ', StringComparison.Ordinal))
        {
            name = name.Replace('.', ' ').Replace('_', ' ');
        }

        int? year = null;
        var yearMatch = YearRegex().Match(name);
        if (yearMatch.Success && yearMatch.Index > 0)
        {
            year = int.Parse(yearMatch.Groups["year"].Value, CultureInfo.InvariantCulture);
            name = name[..yearMatch.Index];
        }
        else
        {
            var junk = QualityRegex().Match(name);
            if (junk.Success && junk.Index > 0)
            {
                name = name[..junk.Index];
            }
        }

        var title = SpacesRegex().Replace(name, " ").Trim(' ', '-', '(', '[', '.');
        return new ParsedFolderName(title.Length > 0 ? title : folderName.Trim(), year, tmdbId);
    }

    [GeneratedRegex(@"[\{\[]tmdb(?:id)?[-=](?<id>\d+)[\}\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbIdRegex();

    [GeneratedRegex(@"[\{\[](?:tmdb|tmdbid|imdb|imdbid|tvdb|tvdbid)[-=][^\}\]]+[\}\]]", RegexOptions.IgnoreCase)]
    private static partial Regex IdTagRegex();

    // A year in brackets or followed by end/space/quality junk; the last plausible one wins ("2001 A Space Odyssey (1968)").
    [GeneratedRegex(@"[\s\(\[](?<year>(?:19|20)\d{2})(?:[\)\]]|\s|$)(?!.*[\s\(\[](?:19|20)\d{2}(?:[\)\]]|\s|$))")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\s(?:2160p|1080p|720p|480p|4k|uhd|bluray|web-?dl|webrip|hdtv|remux|x264|x265|h264|h265|hevc)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();
}