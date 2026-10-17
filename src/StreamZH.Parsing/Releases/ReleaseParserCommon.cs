// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Parsing.Releases;

/// <summary>Helpers shared by the movie and episode parsers (Radarr/Sonarr ParserCommon, FileExtensions, ValidateBeforeParsing).</summary>
internal static partial class ReleaseParserCommon
{
    private static readonly HashSet<string> RemovableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webm", ".m4v", ".3gp", ".nsv", ".ty", ".strm", ".rm", ".rmvb", ".m3u", ".ifo", ".mov", ".qt", ".divx",
        ".xvid", ".bivx", ".nrg", ".pva", ".wmv", ".asf", ".asx", ".ogm", ".ogv", ".m2v", ".avi", ".bin", ".dat",
        ".dvr-ms", ".mpg", ".mpeg", ".mp4", ".avc", ".vp3", ".svq3", ".nuv", ".viv", ".dv", ".fli", ".flv", ".wpl",
        ".img", ".iso", ".vob", ".mkv", ".mk3d", ".ts", ".wtv", ".m2ts",
        ".par2", ".nzb",
    };

    /// <summary>Strips a trailing media or usenet file extension (".mkv", ".nzb"); other dotted suffixes stay.</summary>
    public static string RemoveFileExtension(string title) =>
        FileExtensionRegex().Replace(title, m => RemovableExtensions.Contains(m.Value) ? string.Empty : m.Value);

    /// <summary>Some posters reverse the whole name ("p027.10E10S.emoS"). Turns it back around.</summary>
    public static string UnreverseTitle(string title, bool includeEpisodeMarker)
    {
        var reversed = includeEpisodeMarker ? ReversedEpisodeTitleRegex().IsMatch(title) : ReversedMovieTitleRegex().IsMatch(title);
        if (!reversed)
        {
            return title;
        }

        var withoutExtension = RemoveFileExtension(title);
        var chars = withoutExtension.ToCharArray();
        Array.Reverse(chars);
        return string.Concat(new string(chars), title.AsSpan(withoutExtension.Length));
    }

    /// <summary>Removes "www.site.com - " prefixes, "[site.com]" postfixes and "[rarbg]"-style torrent suffixes.</summary>
    public static string StripWebsites(string title)
    {
        title = WebsitePrefixRegex().Replace(title, string.Empty);
        title = WebsitePostfixRegex().Replace(title, string.Empty);
        return CleanTorrentSuffixRegex().Replace(title, string.Empty);
    }

    /// <summary>Removes a trailing bracket that only holds quality info, e.g. "[HDTV-720p]" or "[Bluray]".</summary>
    public static string StripTrailingQualityBracket(string title) =>
        CleanQualityBracketsRegex().Replace(title, m => QualityWordRegex().IsMatch(m.Value) ? string.Empty : m.Value);

    /// <summary>Radarr/Sonarr ValidateBeforeParsing: rejects password/yEnc posts, names without letters or digits and obfuscated hashes.</summary>
    public static bool IsParsable(string title, bool sonarrRules)
    {
        if (title.Contains("password", StringComparison.OrdinalIgnoreCase) && title.Contains("yenc", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!title.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        var withoutExtension = RemoveFileExtension(title);

        if (RejectHashedReleaseRegexes.Any(r => r.IsMatch(withoutExtension)))
        {
            return false;
        }

        if (sonarrRules && (SonarrRejectHashedReleaseRegexes.Any(r => r.IsMatch(withoutExtension)) || SeasonFolderRegex().IsMatch(withoutExtension)))
        {
            return false;
        }

        return true;
    }

    /// <summary>Runs <paramref name="regex"/> and treats a timeout as "no match".</summary>
    public static MatchCollection? SafeMatches(Regex regex, string input)
    {
        try
        {
            var matches = regex.Matches(input);
            return matches.Count == 0 ? null : matches;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    public static Match SafeMatch(Regex regex, string input)
    {
        try
        {
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return Match.Empty;
        }
    }

    public static string SafeReplace(Regex regex, string input, string replacement)
    {
        try
        {
            return regex.Replace(input, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    private static readonly Regex[] RejectHashedReleaseRegexes =
    [
        Md5HashRegex(),
        ShortLowerCaseHashRegex(),
        NzbGeekUpperHashRegex(),
        NzbGeekLowerHashRegex(),
        BackupFileRegex(),
        Only123Regex(),
        OnlyAbcRegex(),
        AbcXyzRegex(),
        B00bsRegex(),
    ];

    private static readonly Regex[] SonarrRejectHashedReleaseRegexes =
    [
        SixDigitUnderscoreRegex(),
        MixedCaseHash30Regex(),
        MixedCaseHash26Regex(),
        MixedCaseHash39Regex(),
        MixedCaseHash24Regex(),
    ];

    [GeneratedRegex(@"\.[a-z0-9]{2,4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex FileExtensionRegex();

    [GeneratedRegex(@"(?:^|[-._ ])(p027|p0801)[-._ ]", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex ReversedMovieTitleRegex();

    [GeneratedRegex(@"(?:^|[-._ ])(p027|p0801|\d{2,3}E-?\d{2}S)[-._ ]", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex ReversedEpisodeTitleRegex();

    // Valid TLDs http://data.iana.org/TLD/tlds-alpha-by-domain.txt
    [GeneratedRegex(@"^(?:(?:\[|\()\s*)?(?:www\.)?[-a-z0-9-]{1,256}\.(?<!Naruto-Kun\.)(?:[a-z]{2,6}\.[a-z]{2,6}|xn--[a-z0-9-]{4,}|[a-z]{2,})\b(?:\s*(?:\]|\))|[ -]{2,})[ -]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex WebsitePrefixRegex();

    [GeneratedRegex(@"(?:\[\s*)?(?:www\.)?[-a-z0-9-]{1,256}\.(?:xn--[a-z0-9-]{4,}|[a-z]{2,6})\b(?:\s*\])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex WebsitePostfixRegex();

    [GeneratedRegex(@"\[(?:ettv|rartv|rarbg|cttv|publichd)\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex CleanTorrentSuffixRegex();

    [GeneratedRegex(@"\[[a-z0-9 ._-]+\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex CleanQualityBracketsRegex();

    // Stand-in for Radarr/Sonarr's QualityParser.ParseQualityName(bracket) != Unknown, so the parser does not depend on the quality parser.
    [GeneratedRegex(@"\b(?:(?:480|540|576|720|1080|1440|2160)[ip]|HDTV|SDTV|PDTV|DSR|WEB(?:[-_. ]?(?:DL|Rip))?|Blu-?Ray|BD(?:Rip)?|BRRip|DVD(?:Rip)?|Remux|HD|SD|UHD|4K)(?![a-z])",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex QualityWordRegex();

    [GeneratedRegex(@"^(Season[ ._-]*\d+|Specials)$", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex SeasonFolderRegex();

    // Generic match for md5 and mixed-case hashes.
    [GeneratedRegex(@"^[0-9a-zA-Z]{32}", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Md5HashRegex();

    // Generic match for shorter lower-case hashes.
    [GeneratedRegex(@"^[a-z0-9]{24}$", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex ShortLowerCaseHashRegex();

    // Format seen on some NZBGeek releases. Be very strict with these, they are very close to the valid 101 episode numbering.
    [GeneratedRegex(@"^[A-Z]{11}\d{3}$", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex NzbGeekUpperHashRegex();

    [GeneratedRegex(@"^[a-z]{12}\d{3}$", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex NzbGeekLowerHashRegex();

    // Backup filename (unknown origins)
    [GeneratedRegex(@"^Backup_\d{5,}S\d{2}-\d{2}$", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex BackupFileRegex();

    // 123 - started appearing December 2014
    [GeneratedRegex(@"^123$", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Only123Regex();

    // abc - started appearing January 2015
    [GeneratedRegex(@"^abc$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex OnlyAbcRegex();

    // abc - started appearing 2020
    [GeneratedRegex(@"^abc[-_. ]xyz", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex AbcXyzRegex();

    // b00bs - started appearing January 2015
    [GeneratedRegex(@"^b00bs$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex B00bsRegex();

    // 170424_26 - started appearing August 2018 (Sonarr only)
    [GeneratedRegex(@"^\d{6}_\d{2}$", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex SixDigitUnderscoreRegex();

    // Additional generic matches for mixed-case hashes, started appearing Dec 2020 / Jan 2021 (Sonarr only)
    [GeneratedRegex(@"^[0-9a-zA-Z]{30}", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex MixedCaseHash30Regex();

    [GeneratedRegex(@"^[0-9a-zA-Z]{26}", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex MixedCaseHash26Regex();

    [GeneratedRegex(@"^[0-9a-zA-Z]{39}", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex MixedCaseHash39Regex();

    [GeneratedRegex(@"^[0-9a-zA-Z]{24}", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex MixedCaseHash24Regex();
}
