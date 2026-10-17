// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using StreamZH.Core.Quality;
using StreamZH.Parsing.Internal;

namespace StreamZH.Parsing;

/// <summary>
/// Reads audio languages from a release name. Port of Radarr's LanguageParser.
/// <list type="bullet">
/// <item>No language tag gives <see cref="Language.Unknown"/> (like Radarr; the caller decides the fallback, usually the original language).</item>
/// <item>"German.DL" gives German plus <see cref="Language.Original"/>, "German.ML" gives German, Original and English (Radarr behaviour).</item>
/// <item>"GermanSubbed" / "German.Subbed" is original audio with German subtitles: German is not reported.</item>
/// </list>
/// </summary>
public static partial class LanguageParser
{
    private static readonly (string Word, Language Language)[] PlainWords =
    [
        ("english", Language.English),
        ("spanish", Language.Spanish),
        ("danish", Language.Danish),
        ("dutch", Language.Dutch),
        ("japanese", Language.Japanese),
        ("icelandic", Language.Icelandic),
        ("mandarin", Language.Chinese),
        ("cantonese", Language.Chinese),
        ("chinese", Language.Chinese),
        ("korean", Language.Korean),
        ("russian", Language.Russian),
        ("romanian", Language.Romanian),
        ("hindi", Language.Hindi),
        ("arabic", Language.Arabic),
        ("thai", Language.Thai),
        ("bulgarian", Language.Bulgarian),
        ("polish", Language.Polish),
        ("vietnamese", Language.Vietnamese),
        ("swedish", Language.Swedish),
        ("norwegian", Language.Norwegian),
        ("finnish", Language.Finnish),
        ("turkish", Language.Turkish),
        ("portuguese", Language.Portuguese),
        ("brazilian", Language.PortugueseBrazil),
        ("hungarian", Language.Hungarian),
        ("hebrew", Language.Hebrew),
        ("ukrainian", Language.Ukrainian),
        ("persian", Language.Persian),
        ("bengali", Language.Bengali),
        ("slovak", Language.Slovak),
        ("latvian", Language.Latvian),
        ("latino", Language.SpanishLatino),
        ("tamil", Language.Tamil),
        ("telugu", Language.Telugu),
        ("malayalam", Language.Malayalam),
        ("kannada", Language.Kannada),
        ("albanian", Language.Albanian),
        ("afrikaans", Language.Afrikaans),
        ("marathi", Language.Marathi),
        ("tagalog", Language.Tagalog),
    ];

    private static readonly (string Group, Language Language)[] CaseSensitiveGroups =
    [
        ("english", Language.English),
        ("lithuanian", Language.Lithuanian),
        ("czech", Language.Czech),
        ("polish", Language.Polish),
        ("bulgarian", Language.Bulgarian),
        ("slovak", Language.Slovak),
        ("spanish", Language.Spanish),
        ("german", Language.German),
    ];

    private static readonly (string Group, Language Language)[] RegexGroups =
    [
        ("english", Language.English),
        ("italian", Language.Italian),
        ("german", Language.German),
        ("flemish", Language.Flemish),
        ("greek", Language.Greek),
        ("french", Language.French),
        ("russian", Language.Russian),
        ("bulgarian", Language.Bulgarian),
        ("brazilian", Language.PortugueseBrazil),
        ("hungarian", Language.Hungarian),
        ("hebrew", Language.Hebrew),
        ("polish", Language.Polish),
        ("chinese", Language.Chinese),
        ("spanish", Language.Spanish),
        ("catalan", Language.Catalan),
        ("ukrainian", Language.Ukrainian),
        ("latvian", Language.Latvian),
        ("romanian", Language.Romanian),
        ("telugu", Language.Telugu),
        ("vietnamese", Language.Vietnamese),
        ("japanese", Language.Japanese),
        ("korean", Language.Korean),
        ("urdu", Language.Urdu),
        ("romansh", Language.Romansh),
        ("mongolian", Language.Mongolian),
        ("georgian", Language.Georgian),
        ("turkish", Language.Turkish),
        ("portuguese", Language.Portuguese),
        ("original", Language.Original),
    ];

    public static IReadOnlyList<Language> ParseLanguages(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [Language.Unknown];
        }

        title = PrepareTitle(title.Trim());

        var lowerTitle = title.ToLowerInvariant();
        var languages = new List<Language>();

        foreach (var (word, language) in PlainWords)
        {
            if (lowerTitle.Contains(word, StringComparison.Ordinal))
            {
                languages.Add(language);
            }
        }

        foreach (Match match in CaseSensitiveLanguageRegex().Matches(title))
        {
            foreach (var (group, language) in CaseSensitiveGroups)
            {
                if (match.Groups[group].Success)
                {
                    languages.Add(language);
                }
            }
        }

        foreach (Match match in LanguageRegex().Matches(title))
        {
            foreach (var (group, language) in RegexGroups)
            {
                if (match.Groups[group].Success)
                {
                    languages.Add(language);
                }
            }
        }

        languages = languages.DistinctBy(l => l.Id).ToList();

        if (languages.Count == 0)
        {
            languages.Add(Language.Unknown);
        }

        // Radarr checks this before removing duplicates, so "German ... GER" lost its DL; StreamZH removes them first.
        if (languages.Count == 1 && languages[0] == Language.German)
        {
            if (GermanDualLanguageRegex().IsMatch(title))
            {
                // German DL: German plus the original language.
                languages.Add(Language.Original);
            }
            else if (GermanMultiLanguageRegex().IsMatch(title))
            {
                // German ML: German, the original language and English.
                languages.Add(Language.Original);
                languages.Add(Language.English);
            }
        }

        return languages;
    }

    /// <summary>
    /// Cuts away what is not release information, like Radarr and Sonarr do before parsing languages:
    /// the file extension, a trailing "-GROUP", and the series/movie title in front of SxxEyy or the year.
    /// Underscores become dots so word boundaries work.
    /// </summary>
    private static string PrepareTitle(string title)
    {
        title = MediaFileExtensions.RemoveFileExtension(title);

        // Only a group at the very end ("-GRP", "[GRP]"), so "RU-KA.WEB-DL" keeps its languages.
        var group = ReleaseGroupParser.ParseReleaseGroup(title);
        var groupIndex = string.IsNullOrEmpty(group) ? -1 : title.LastIndexOf(group, StringComparison.Ordinal);
        if (groupIndex > 0 && groupIndex + group!.Length >= title.Length - 1)
        {
            title = string.Concat(title.AsSpan(0, groupIndex), "RlsGrp", title.AsSpan(groupIndex + group.Length));
        }

        title = title.Replace('_', '.');

        var episodeMatch = SeriesTitleRegex().Match(title);
        if (episodeMatch.Success)
        {
            return "A.Series" + title[episodeMatch.Index..];
        }

        var yearMatch = YearRegex().Matches(title).LastOrDefault(m => m.Index > 0);
        return yearMatch is null ? title : "A.Movie." + title[yearMatch.Index..];
    }

    // Radarr's language patterns plus Sonarr's additions (ing/esp/spa/cat/tur/por/JPN/(JA)/国语音轨).
    private const string LanguagesOnly =
        @"(?<english>\b(?:ing|eng)\b)|" +
        @"(?<italian>\b(?:ita|italian)\b)|" +
        @"(?<german>(?:swiss)?german\b|videomann|ger[. ]dub|\bger\b)|" +
        @"(?<flemish>flemish)|" +
        @"(?<bulgarian>bgaudio)|" +
        @"(?<romanian>rodubbed)|" +
        @"(?<brazilian>\b(?:dublado|pt-BR)\b)|" +
        @"(?<greek>greek)|" +
        @"(?<french>\b(?:FR|VO|VF|VFF|VFQ|VFI|VF2|TRUEFRENCH|FRENCH|FRE|FRA)\b)|" +
        @"(?<russian>\b(?:rus|ru)\b)|" +
        @"(?<hungarian>\b(?:HUNDUB|HUN)\b)|" +
        @"(?<hebrew>\b(?:HebDub|HebDubbed)\b)|" +
        @"(?<polish>\b(?:PL\W?DUB|DUB\W?PL|LEK\W?PL|PL\W?LEK)\b)|" +
        @"(?<chinese>\[(?:CH[ST]|BIG5|GB)\]|简|繁|字幕|国语音轨[.+])|" +
        @"(?<ukrainian>(?:(?:\dx)?UKR))|" +
        @"(?<spanish>\b(?:español|castellano|esp|spa(?!\(Latino\)))\b)|" +
        @"(?<catalan>\b(?:catalan?|catalán|català)\b|[-,. ]cat[. ](?:DD|subs))|" +
        @"(?<latvian>\b(?:lat|lav|lv)\b)|" +
        @"(?<telugu>\btel\b)|" +
        @"(?<vietnamese>\bVIE\b)|" +
        @"(?<japanese>\b(?:JAP|JPN)\b|\(JA\))|" +
        @"(?<korean>\bKOR\b)|" +
        @"(?<turkish>\btur\b)|" +
        @"(?<portuguese>[_. ]por[_. ])|" +
        @"(?<urdu>\burdu\b)|" +
        @"(?<romansh>\b(?:romansh|rumantsch|romansch)\b)|" +
        @"(?<mongolian>\b(?:mongolian|khalkha)\b)|" +
        @"(?<georgian>\b(?:georgian|geo|ka|kat)\b)|" +
        @"(?<original>\b(?:orig|original)\b)";

    // Sonarr: a language directly followed by (more languages and) "Sub"/"Subs"/"Subbed" is a subtitle language, not audio.
    [GeneratedRegex("(?:" + LanguagesOnly + @")(?!(?:[-_. ](?:" + LanguagesOnly + @"))*[-_. ]subs?)", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageRegex();

    [GeneratedRegex(@"(?:(?i)(?<!SUB[\W|_|^]))(?:(?<english>\bEN\b)|(?<lithuanian>\bLT\b)|(?<czech>\bCZ\b)|(?<polish>\bPL\b)|(?<bulgarian>\bBG\b)|(?<slovak>\bSK\b)|(?<german>\bDE\b)|(?<spanish>\b(?<!DTS[._ -])ES\b))(?:(?i)(?![\W|_|^]SUB))")]
    private static partial Regex CaseSensitiveLanguageRegex();

    [GeneratedRegex(@"(?<!WEB[-_. ]?)\bDL\b", RegexOptions.IgnoreCase)]
    private static partial Regex GermanDualLanguageRegex();

    [GeneratedRegex(@"\bML\b", RegexOptions.IgnoreCase)]
    private static partial Regex GermanMultiLanguageRegex();

    // Sonarr: everything in front of SxxEyy is the series title.
    [GeneratedRegex(@"[. ]S\d{2}(?:E\d{2,4})*[. ]", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesTitleRegex();

    // Movie year; the title ends in front of it.
    [GeneratedRegex(@"(?<=[-. (\[])(?:19|20)\d{2}(?=[-. )\]]|$)")]
    private static partial Regex YearRegex();
}
