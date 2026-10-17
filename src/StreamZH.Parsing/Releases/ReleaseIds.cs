// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;

namespace StreamZH.Parsing.Releases;

/// <summary>IMDb and TMDB ids embedded in release names ("[tt1234567]", "{tmdb-438631}", "{tmdbid-438631}").</summary>
internal static partial class ReleaseIds
{
    public static string? ParseImdbId(string title)
    {
        var match = ReleaseParserCommon.SafeMatch(ImdbIdRegex(), title);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups["imdbid"].Value;
        return id.Length is 9 or 10 ? id.ToLowerInvariant() : null;
    }

    public static int? ParseTmdbId(string title)
    {
        var match = ReleaseParserCommon.SafeMatch(TmdbIdRegex(), title);
        return match.Success && int.TryParse(match.Groups["tmdbid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;
    }

    [GeneratedRegex(@"(?<imdbid>tt\d{7,8})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex ImdbIdRegex();

    [GeneratedRegex(@"tmdb(id)?[-=](?<tmdbid>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex TmdbIdRegex();
}
