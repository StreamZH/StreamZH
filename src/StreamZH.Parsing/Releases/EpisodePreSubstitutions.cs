// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Parsing.Releases;

/// <summary>Rewrites applied to episode release names before matching, ported from Sonarr's ParserCommon.PreSubstitutionRegex (mostly Chinese anime naming).</summary>
internal static partial class EpisodePreSubstitutions
{
    internal static readonly (Regex Regex, string Replacement)[] All =
    [
        (Substitution00(), ".S01E$1.$2"),
        (Substitution01(), "[${subgroup}] ${title} - ${episode} - "),
        (Substitution02(), "[${subgroup}][${title}][${episode}]["),
        (Substitution03(), "[${subgroup}] ${title} S${season} - ${episode} "),
        (Substitution04(), "[${subgroup}] ${title} - ${episode} "),
        (Substitution05(), "${title}S${season}E${episode}.${language}${rest}"),
        (Substitution06(), "${title}S${season}.${language}${rest}"),
        (Substitution07(), "[${subgroup}] ${title} - ${episode} "),
        (Substitution08(), "[${subgroup}] ${title} S${season} - ${episode} "),
        (Substitution09(), "[${subgroup}] ${title} - ${episode} "),
        (Substitution10(), "[${subgroup}] ${title} - ${episode} "),
        (Substitution11(), "[${subgroup}] ${title} - ${episode} "),
        (Substitution12(), "[${subgroup}] ${title} - ${episode} "),
        (Substitution13(), "${title} (${year}) - ${info} "),
    ];

    // Korean series without season number, replace with S01Exxx and remove airdate
    [GeneratedRegex(@"\.E(\d{2,4})\.\d{6}\.(.*-(F1RST|NEXT))$", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution00();

    // Some Chinese anime releases contain both English and Chinese titles, remove the Chinese title and replace with normal anime pattern
    [GeneratedRegex(@"^\[(?:(?<subgroup>[^\]]+?)(?:[\u4E00-\u9FCC]+)?)\]\[(?<title>[^\]]+?)(?:\s(?<chinesetitle>[\u4E00-\u9FCC][^\]]*?))\]\[(?:(?:[\u4E00-\u9FCC]+?)?(?<episode>\d{1,4})(?:[\u4E00-\u9FCC]+?)?)\]", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution01();

    // Chinese LoliHouse/ZERO/Lilith-Raws/Skymoon-Raws/orion origin releases don't use the expected brackets, normalize using brackets
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]*?(?:LoliHouse|ZERO|Lilith-Raws|Skymoon-Raws|orion origin)[^\]]*?)\](?<title>[^\[\]]+?)(?: - (?<episode>[0-9-]+)\s*|\[第?(?<episode>[0-9]+(?:-[0-9]+)?)话?(?:END|完)?\])\[", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution02();

    // Most Chinese anime releases contain additional brackets/separators for chinese and non-chinese titles, remove junk first and if it has S0x as season number, convert it to Sx
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+)\](?:\s?★[^\[ -]+\s?)?\[?(?:(?<chinesetitle>(?=[^\]]*?[\u4E00-\u9FCC])[^\]]*?)(?:\]\[|\s*[_/·]\s*)){0,2}(?<title>[^\[\]]+?)(?:\s(?:S?(?<!\d+)((0)(?<season>\d)|(?<season>[1-9]\d))(?!\d+)))\]?(?:\[\d{4}\])?\[第?(?<episode>[0-9]+(?:-[0-9]+)?)(?:话|集)?(?: ?END|完| ?Fin)?\]", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution03();

    // Some Chinese releases don't include a separation between Chinese and English titles within the same bracketed group
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+)\]\[(?<chinesetitle>(?<![^a-zA-Z0-9])[^a-zA-Z0-9]+)(?<title>[^\]]+?)\](?:\[\d{4}\])?\[第?(?<episode>[0-9]+(?:-[0-9]+)?)(?:话|集)?(?: ?END|完| ?Fin)?\]", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution04();

    // Chinese season packs, that may not actually have the full season so treated as multi-episode
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+)\](?:\s?★[^\[ -]+\s?)?\[?(?:(?<chinesetitle>(?=[^\]]*?[\u4E00-\u9FCC])[^\]]*?)(?:\]\[|\s*[_/·]\s*)){0,2}(?<title>[^\]]+?)\]?(?:\[\d{4}\])?\[(?:第|全)?(?<episode>[0-9]{1,4}-[0-9]{1,4})(?:话|集)?\](?:\[(?<language>.+?)\+.+?\])[_. ](?<title>.+?)S(?<season>[0-9]{1,2})(?<rest>.+?)$", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution05();

    // Chinese season packs, that actually have the full season so treated as multi-episode
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+)\](?:\s?★[^\[ -]+\s?)?\[?(?:(?<chinesetitle>(?=[^\]]*?[\u4E00-\u9FCC])[^\]]*?)(?:\]\[|\s*[_/·]\s*)){0,2}(?<title>[^\]]+?)\]?(?:\[\d{4}\])?\[(?:全)?(?<episode>[0-9]{1,4})(?:话|集)?\](?:\[(?<language>.+?)\+.+?\])[_. ](?<title>.+?)S(?<season>[0-9]{1,2})(?<rest>.+?)$", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution06();

    // Most Chinese anime releases contain additional brackets/separators for chinese and non-chinese titles, remove junk and replace with normal anime pattern
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+)\](?:\s?★[^\[ -]+\s?)?\[?(?:(?<chinesetitle>(?=[^\]]*?[\u4E00-\u9FCC])[^\]]*?)(?:\]\[|\s*[_/·]\s*)){0,2}(?<title>[^\]]+?)\]?(?:\[\d{4}\])?\[第?(?<episode>[0-9]{1,4}(?:-[0-9]{1,4})?)(?:话|集)?(?: ?END|完| ?Fin)?\]", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution07();

    // Some Chinese anime releases contain both Chinese and English titles, remove the Chinese title first and if it has S0x as season number, convert it to Sx
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+)\](?:\s)(?:(?<chinesetitle>(?=[^\]]*?[\u4E00-\u9FCC])[^\]]*?)(?:\s/\s))(?<title>[^\[\]]+?)(?:\s(?:S?(?<!\d+)((0)(?<season>\d)|(?<season>[1-9]\d))(?!\d+)))(?:[- ]+)(?<episode>[0-9]+(?:-[0-9]+)?)话?(?:END|完)?", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution08();

    // Some Chinese anime releases contain both English and Chinese titles, remove the Chinese title and replace with normal anime pattern
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+)\](?:\s)(?:(?<title>[^\]]+?)(?:\s/\s))(?<chinesetitle>(?=[^\]]*?[\u4E00-\u9FCC])[^\]]*?)(?:[- ]+)(?<episode>[0-9]+(?:-[0-9]+)?(?![a-z]))话?(?:END|完)?", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution09();

    // Some Chinese anime releases contain both Chinese and English titles, remove the Chinese title and replace with normal anime pattern
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+)\](?:\s)(?:(?<chinesetitle>(?=[^\]]*?[\u4E00-\u9FCC])[^\]]*?)(?:\s/\s))(?<title>[^\]]+?)(?:[- ]+)(?<episode>[0-9]+(?:-[0-9]+)?(?![a-z]))话?(?:END|完)?", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution10();

    // GM-Team releases with lots of square brackets
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+)\](?:(?<chinesubgroup>\[(?=[^\]]*?[\u4E00-\u9FCC])[^\]]*\])+)\[(?<title>[^\]]+?)\](?<junk>\[[^\]]+\])*\[(?<episode>[0-9]+(?:-[0-9]+)?)( END| Fin)?\]", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution11();

    // Some Chinese anime releases contain both Chinese and English titles separated by | instead of /, remove the Chinese title and replace with normal anime pattern
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+)\](?:\s)(?:(?<chinesetitle>(?=[^\]]*?[\u4E00-\u9FCC])[^\]]*?)(?:\s\|\s))(?<title>[^\]]+?)(?:[- ]+)(?<episode>[0-9]+(?:-[0-9]+)?(?![a-z]))话?(?:END|完)?", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution12();

    // Spanish releases with information in brackets
    [GeneratedRegex(@"^(?<title>.+?(?=[ ._-]\()).+?\((?<year>\d{4})\/(?<info>S[^\/]+)", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex Substitution13();
}
