// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Core.Decisions;

/// <summary>
/// Detects hardcoded (burned-in) subtitles in a release name, like Radarr's Parser.ParseHardcodeSubs:
/// "KORSUB", "NLSUBS", "HARDSUB" (anything ending in SUB/SUBS except SOFTSUB and MULTISUB) or "HC"/"SUBBED".
/// </summary>
public static partial class HardcodedSubs
{
    public const string Generic = "Generic Hardcoded Subs";

    /// <summary>The tag found ("KORSUB"), <see cref="Generic"/> for "HC"/"SUBBED", or null when there is none.</summary>
    public static string? Find(string releaseTitle)
    {
        ArgumentNullException.ThrowIfNull(releaseTitle);
        try
        {
            var match = Pattern().Matches(releaseTitle).LastOrDefault();
            if (match is null)
            {
                return null;
            }

            return match.Groups["hcsub"].Success ? match.Groups["hcsub"].Value : Generic;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>True when the found tag contains one of the whitelisted tags (case-insensitive, like Radarr).</summary>
    public static bool IsWhitelisted(string found, IEnumerable<string> whitelist) =>
        whitelist.Any(t => !string.IsNullOrWhiteSpace(t) && found.Contains(t.Trim(), StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\b((?<hcsub>(\w+(?<!SOFT|MULTI)SUBS?)\b)|(?<hc>(HC|SUBBED)))\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}
