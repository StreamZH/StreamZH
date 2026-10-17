// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using StreamZH.Parsing.Internal;

namespace StreamZH.Parsing;

/// <summary>Reads the release group ("-LiNEUP" → "LiNEUP"). Port of Radarr's ReleaseGroupParser.</summary>
public static partial class ReleaseGroupParser
{
    public static string? ParseReleaseGroup(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        title = title.Trim();
        title = MediaFileExtensions.RemoveFileExtension(title);
        title = ParserCommon.RemoveWebsitePrefix(title);
        title = ParserCommon.RemoveTorrentSuffix(title);

        var animeMatch = AnimeReleaseGroupRegex().Match(title);
        if (animeMatch.Success)
        {
            return animeMatch.Groups["subgroup"].Value;
        }

        title = CleanReleaseGroupRegex().Replace(title, string.Empty);

        var exceptionExactMatches = ExceptionReleaseGroupRegexExact().Matches(title);
        if (exceptionExactMatches.Count != 0)
        {
            return exceptionExactMatches[^1].Groups["releasegroup"].Value;
        }

        var exceptionMatches = ExceptionReleaseGroupRegex().Matches(title);
        if (exceptionMatches.Count != 0)
        {
            return exceptionMatches[^1].Groups["releasegroup"].Value;
        }

        var matches = ReleaseGroupRegex().Matches(title);
        if (matches.Count != 0)
        {
            var group = matches[^1].Groups["releasegroup"].Value;

            if (int.TryParse(group, out _) || InvalidReleaseGroupRegex().IsMatch(group))
            {
                return null;
            }

            return group;
        }

        // StreamZH addition: "Title.2023.BluRay.-.GROUP" (dash surrounded by dots at the very end).
        var dottedDash = DottedDashReleaseGroupRegex().Match(title);
        if (dottedDash.Success && !int.TryParse(dottedDash.Groups["releasegroup"].Value, out _))
        {
            return dottedDash.Groups["releasegroup"].Value;
        }

        return null;
    }

    // Radarr's pattern with Sonarr's additions: accented letters in group names, HDTV/SDTV next to the resolutions.
    [GeneratedRegex(@"-(?<releasegroup>[A-Za-zÀ-ÖØ-öø-ÿ0-9]+(?<part2>-[A-Za-zÀ-ÖØ-öø-ÿ0-9]+)?(?!.+?(?:HDTV|SDTV|480p|576p|720p|1080p|2160p)))(?<!(?:HDTV|SDTV|WEB-(DL|Rip)|Blu-Ray|480p|576p|720p|1080p|2160p|DTS-HD|DTS-X|DTS-MA|DTS-ES|-ES|-EN|-CAT|-ENG|-JAP|-GER|-FRA|-FRE|-ITA|-HDRip|\d{1,2}-bit|[ ._]\d{4}-\d{2}|-\d{2}|tmdb(id)?-(?<tmdbid>\d+)|(?<imdbid>tt\d{7,8}))(?:\k<part2>)?)(?:\b|[-._ ]|$)|[-._ ]\[(?<releasegroup>[A-Za-zÀ-ÖØ-öø-ÿ0-9]+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseGroupRegex();

    [GeneratedRegex(@"^([se]\d+|[0-9a-f]{8})$", RegexOptions.IgnoreCase)]
    private static partial Regex InvalidReleaseGroupRegex();

    [GeneratedRegex(@"^(?:\[(?<subgroup>(?!\s).+?(?<!\s))\](?:_|-|\s|\.)?)", RegexOptions.IgnoreCase)]
    private static partial Regex AnimeReleaseGroupRegex();

    // Handle exception release groups that don't follow -RlsGrp; manual list.
    // Name only, be very careful with this one: high chance of false positives.
    [GeneratedRegex(@"\b(?<releasegroup>KRaLiMaRKo|E\.N\.D|D\-Z0N3|Koten_Gars|BluDragon|ZØNEHD|HQMUX|VARYG|YIFY|YTS(.(MX|LT|AG))?|TMd|Eml HDTeam|LMain|DarQ|BEN[_. ]THE[_. ]MEN|TAoE|QxR|126811|Fight-BB|KCRT|Vialle)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionReleaseGroupRegexExact();

    // Groups whose releases end with RlsGroup) or RlsGroup]
    [GeneratedRegex(@"(?<=[._ \[])(?<releasegroup>(Silence|afm72|Panda|Ghost|MONOLITH|Tigole|Joy|ImE|UTR|t3nzin|Anime Time|Project Angel|Hakata Ramen|HONE|GiLG|Vyndros|SEV|Garshasp|Kappa|Natty|RCVR|SAMPA|YOGI|r00t|EDGE2020|RZeroX|FreetheFish|Anna|Bandi|Qman|theincognito|HDO|DusIctv|DHD|CtrlHD|-ZR-|ADC|XZVN|RH|Kametsu|Celdra)(?=\]|\)))", RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionReleaseGroupRegex();

    // Sonarr: drop the series title and SxxEyy first so dashes in titles are not taken for groups.
    [GeneratedRegex(@"^(.*?[-._ ](S\d+E\d+)[-._ ])|(-(RP|1|NZBGeek|\[N-Z-B\]|Obfuscated|Obfuscation|Scrambled|sample|Pre|postbot|xpost|Rakuv[a-z0-9]*|WhiteRev|BUYMORE|AsRequested|AlternativeToRequested|GEROV|Z0iDS3N|Chamele0n|4P|4Planet|AlteZachen|RePACKPOST))+$", RegexOptions.IgnoreCase)]
    private static partial Regex CleanReleaseGroupRegex();

    [GeneratedRegex(@"\.-\.(?<releasegroup>[a-z0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DottedDashReleaseGroupRegex();
}
