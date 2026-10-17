// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;

namespace StreamZH.Parsing;

/// <summary>
/// Finds season and episode numbers in file names: S01E02, S01E02E03, S01E02-E03, 1x02.
/// Interim parser for 0.2; the full release parser ported from Sonarr follows in 0.3.
/// </summary>
public static partial class EpisodeFileParser
{
    public static (int Season, IReadOnlyList<int> Episodes)? Parse(string fileName)
    {
        var match = SxxEyyRegex().Match(fileName);
        if (match.Success)
        {
            var season = int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture);
            var first = int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture);
            var episodes = new List<int> { first };

            foreach (Capture capture in match.Groups["more"].Captures)
            {
                episodes.Add(int.Parse(capture.Value, CultureInfo.InvariantCulture));
            }

            // S01E01-E04 means a range.
            if (match.Groups["dash"].Success && episodes.Count == 2 && episodes[1] > first)
            {
                episodes = [.. Enumerable.Range(first, episodes[1] - first + 1)];
            }

            return (season, episodes.Distinct().ToList());
        }

        var x = NxNNRegex().Match(fileName);
        if (x.Success)
        {
            return (int.Parse(x.Groups["season"].Value, CultureInfo.InvariantCulture),
                [int.Parse(x.Groups["episode"].Value, CultureInfo.InvariantCulture)]);
        }

        return null;
    }

    [GeneratedRegex(@"\bS(?<season>\d{1,2})[ ._-]?E(?<episode>\d{1,3})(?:(?<dash>-)?E?(?<more>\d{1,3}))*(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SxxEyyRegex();

    [GeneratedRegex(@"(?<![\dx])(?<season>\d{1,2})x(?<episode>\d{2,3})(?![\dp])", RegexOptions.IgnoreCase)]
    private static partial Regex NxNNRegex();
}