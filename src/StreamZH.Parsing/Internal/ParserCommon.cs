// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Parsing.Internal;

/// <summary>Clean-up helpers shared by the name parsers.</summary>
internal static partial class ParserCommon
{
    /// <summary>Removes a leading website tag such as "[ www.Torrenting.com ] - ".</summary>
    public static string RemoveWebsitePrefix(string title) => WebsitePrefixRegex().Replace(title, string.Empty);

    /// <summary>Removes a trailing tracker tag such as "[rarbg]".</summary>
    public static string RemoveTorrentSuffix(string title) => CleanTorrentSuffixRegex().Replace(title, string.Empty);

    // Valid TLDs http://data.iana.org/TLD/tlds-alpha-by-domain.txt
    [GeneratedRegex(@"^(?:(?:\[|\()\s*)?(?:www\.)?[-a-z0-9-]{1,256}\.(?<!Naruto-Kun\.)(?:[a-z]{2,6}\.[a-z]{2,6}|xn--[a-z0-9-]{4,}|[a-z]{2,})\b(?:\s*(?:\]|\))|[ -]{2,})[ -]*", RegexOptions.IgnoreCase)]
    private static partial Regex WebsitePrefixRegex();

    [GeneratedRegex(@"\[(?:ettv|rartv|rarbg|cttv|publichd)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex CleanTorrentSuffixRegex();
}
