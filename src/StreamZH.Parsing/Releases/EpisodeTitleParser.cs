// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StreamZH.Core.Quality;

namespace StreamZH.Parsing.Releases;

/// <summary>
/// Port of Sonarr's Parser.ParseTitle / ParseMatchCollection. Series lookups, scene mappings and path parsing are left out.
/// </summary>
internal static partial class EpisodeTitleParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly string[] Numbers = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    private static readonly Dictionary<string, int> ShortMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6,
        ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12,
    };

    /// <summary>
    /// Parses an episode release. <paramref name="confidence"/> tells how sure the match is against a movie reading of the same name.
    /// </summary>
    public static ParsedRelease? Parse(string title, out EpisodeConfidence confidence)
    {
        confidence = EpisodeConfidence.Weak;
        var originalTitle = title;

        if (!ReleaseParserCommon.IsParsable(title, sonarrRules: true))
        {
            return null;
        }

        title = ReleaseParserCommon.UnreverseTitle(title, includeEpisodeMarker: true);

        var releaseTitle = ReleaseParserCommon.RemoveFileExtension(title);
        releaseTitle = releaseTitle.Replace('【', '[').Replace('】', ']');

        foreach (var (regex, replacement) in EpisodePreSubstitutions.All)
        {
            releaseTitle = ReleaseParserCommon.SafeReplace(regex, releaseTitle, replacement);
        }

        var simpleTitle = ReleaseParserCommon.SafeReplace(SimpleTitleRegex(), releaseTitle, string.Empty);
        simpleTitle = ReleaseParserCommon.StripWebsites(simpleTitle);
        simpleTitle = ReleaseParserCommon.StripTrailingQualityBracket(simpleTitle);

        var sixDigitAirDate = ReleaseParserCommon.SafeMatch(SixDigitAirDateRegex(), simpleTitle);
        if (sixDigitAirDate.Success)
        {
            var airMonth = sixDigitAirDate.Groups["airmonth"].Value;
            var airDay = sixDigitAirDate.Groups["airday"].Value;

            if (airMonth != "00" || airDay != "00")
            {
                var fixedDate = $"20{sixDigitAirDate.Groups["airyear"].Value}.{airMonth}.{airDay}";
                simpleTitle = simpleTitle.Replace(sixDigitAirDate.Groups["airdate"].Value, fixedDate, StringComparison.Ordinal);
            }
        }

        foreach (var (regex, kind) in EpisodeTitleRegexes.All)
        {
            var matches = ReleaseParserCommon.SafeMatches(regex, simpleTitle);
            if (matches is null)
            {
                continue;
            }

            var outcome = ParseMatchCollection(matches, releaseTitle, out var info);

            if (outcome == MatchOutcome.InvalidDate)
            {
                // Sonarr stops at an invalid or ambiguous date instead of trying the remaining patterns.
                return null;
            }

            if (outcome == MatchOutcome.NoResult || info is null)
            {
                continue;
            }

            if (info.FullSeason && info.ReleaseTokens.Contains("Special", StringComparison.OrdinalIgnoreCase))
            {
                info.FullSeason = false;
                info.Special = true;
            }

            var subGroup = matches[0].Groups["subgroup"];
            var hasSubGroup = subGroup.Success && !string.IsNullOrWhiteSpace(subGroup.Value);
            var releaseHash = GetReleaseHash(matches[0]);

            confidence = Classify(kind, matches[0], info, hasSubGroup || releaseHash is not null);

            return ToParsedRelease(info, originalTitle, title, releaseTitle, hasSubGroup ? subGroup.Value : null, releaseHash);
        }

        return null;
    }

    // Not part of Sonarr: Sonarr never has to decide between a movie and an episode, StreamZH's ReleaseParser.Parse does.
    private static EpisodeConfidence Classify(EpisodePatternKind kind, Match match, EpisodeInfo info, bool hasAnimeMarkers)
    {
        if (kind == EpisodePatternKind.Ambiguous)
        {
            return EpisodeConfidence.Weak;
        }

        if (kind == EpisodePatternKind.SeasonOnly && IsLoneSeasonLetter(match))
        {
            return EpisodeConfidence.Weak;
        }

        if (info.AirDate is null && !info.SeasonCaptured && info.AbsoluteCaptured && info.EpisodeNumbers.Count == 0)
        {
            return hasAnimeMarkers || kind == EpisodePatternKind.ExplicitAbsolute ? EpisodeConfidence.AbsoluteOnly : EpisodeConfidence.Weak;
        }

        return EpisodeConfidence.Strong;
    }

    // "V.H.S.2.2013": the "S" before the season number is a separate one-letter token, most likely part of an acronym.
    private static bool IsLoneSeasonLetter(Match match)
    {
        var season = match.Groups["season"];
        if (!season.Success)
        {
            return false;
        }

        var text = match.Value;
        var i = season.Index - match.Index - 1;

        if (i >= 0 && IsSeparator(text[i]))
        {
            i--;
        }

        // text[i] is the season letter; it must be preceded by "<separator><one letter><separator or start>".
        return i >= 2 &&
               (text[i] == 'S' || text[i] == 's') &&
               IsSeparator(text[i - 1]) &&
               char.IsLetter(text[i - 2]) &&
               (i == 2 || IsSeparator(text[i - 3]));
    }

    private static bool IsSeparator(char c) => c is '.' or ' ' or '_' or '-';

    private static ParsedRelease ToParsedRelease(EpisodeInfo info, string originalTitle, string title, string releaseTitle, string? subGroup, string? releaseHash)
    {
        var (titleWithoutYear, year) = SplitYear(info.SeriesTitle);

        int? seasonNumber;
        if (info.AirDate is not null)
        {
            seasonNumber = null;
        }
        else if (info.Seasons.Count > 0)
        {
            seasonNumber = info.Seasons[0];
        }
        else if (info.AbsoluteEpisodeNumbers.Count > 0)
        {
            // Absolute numbering without a season: the season comes from the series' episode list later.
            seasonNumber = null;
        }
        else
        {
            // Sonarr's default for releases without a season, e.g. anime specials ("[Group] Title OVA").
            seasonNumber = 0;
        }

        return new ParsedRelease
        {
            ReleaseTitle = originalTitle,
            Title = titleWithoutYear,
            CleanTitle = TitleNormalizer.Clean(titleWithoutYear),
            Year = year,
            SeasonNumber = seasonNumber,
            EpisodeNumbers = info.EpisodeNumbers,
            AbsoluteEpisodeNumbers = info.AbsoluteEpisodeNumbers,
            AirDate = info.AirDate,
            FullSeason = info.FullSeason,
            MultiSeason = info.Seasons.Count > 1,
            IsSpecial = info.Special || (info.SeasonCaptured && seasonNumber == 0),
            ReleaseHash = releaseHash,
            ImdbId = ReleaseIds.ParseImdbId(releaseTitle),
            TmdbId = ReleaseIds.ParseTmdbId(releaseTitle),
            ReleaseGroup = subGroup ?? ReleaseGroupParser.ParseReleaseGroup(releaseTitle),
            Languages = LanguagesOf(info.ReleaseTokens, subGroup),
            Quality = QualityParser.ParseQuality(title),
            Hdr = QualityParser.ParseHdr(title),
            VideoCodec = CodecParser.ParseVideoCodec(info.ReleaseTokens),
            AudioCodec = CodecParser.ParseAudioCodec(info.ReleaseTokens),
        };
    }

    /// <summary>
    /// Languages after the episode number; German anime releases often name it only in the leading tag instead of a sub
    /// group ("[GER] Title - 05 [1080p]"), so that tag counts when nothing else names a language.
    /// </summary>
    private static IReadOnlyList<Language> LanguagesOf(string releaseTokens, string? subGroup)
    {
        var languages = LanguageParser.ParseLanguages(releaseTokens);
        if (subGroup is { Length: > 0 and <= 12 } && languages.All(l => l == Language.Unknown))
        {
            var fromTag = LanguageParser.ParseLanguages(subGroup.Trim());
            if (fromTag.Any(l => l != Language.Unknown))
            {
                return fromTag;
            }
        }

        return languages;
    }

    internal enum EpisodeConfidence
    {
        /// <summary>Season/episode markers or an air date: certainly not a movie.</summary>
        Strong,

        /// <summary>
        /// Only absolute numbers, with anime markers (sub group, CRC hash) or an explicit episode marker ("E1206", "Episode 12").
        /// Loses against a movie whose year is that number.
        /// </summary>
        AbsoluteOnly,

        /// <summary>Numbers a movie name can produce as well. Loses against any movie parse.</summary>
        Weak,
    }

    private enum MatchOutcome
    {
        Parsed,
        NoResult,
        InvalidDate,
    }

    private sealed class EpisodeInfo
    {
        public string SeriesTitle { get; set; } = string.Empty;
        public List<int> Seasons { get; set; } = [];
        public bool SeasonCaptured { get; set; }

        /// <summary>Absolute numbers were matched, including decimal recap specials ("12.5") that end up in no list.</summary>
        public bool AbsoluteCaptured { get; set; }
        public IReadOnlyList<int> EpisodeNumbers { get; set; } = [];
        public IReadOnlyList<int> AbsoluteEpisodeNumbers { get; set; } = [];
        public string? AirDate { get; set; }
        public bool FullSeason { get; set; }
        public bool Special { get; set; }
        public string ReleaseTokens { get; set; } = string.Empty;
    }

    private static MatchOutcome ParseMatchCollection(MatchCollection matchCollection, string releaseTitle, out EpisodeInfo? info)
    {
        info = null;
        var first = matchCollection[0];

        var seriesName = first.Groups["title"].Value.Replace('.', ' ').Replace('_', ' ');
        seriesName = RequestInfoRegex().Replace(seriesName, string.Empty).Trim(' ');

        int.TryParse(first.Groups["airyear"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var airYear);

        var titleGroup = first.Groups["title"];
        var lastIndex = titleGroup.Success ? titleGroup.Index + titleGroup.Length : 0;

        var result = new EpisodeInfo();

        if (airYear < 1900)
        {
            foreach (Match matchGroup in matchCollection)
            {
                var episodeCaptures = matchGroup.Groups["episode"].Captures;
                var absoluteCaptures = matchGroup.Groups["absoluteepisode"].Captures;

                // An empty episode list is fine: that is a full season release.
                if (episodeCaptures.Count > 0)
                {
                    if (!TryParseNumber(episodeCaptures[0].Value, out var firstEpisode) ||
                        !TryParseNumber(episodeCaptures[^1].Value, out var lastEpisode) ||
                        firstEpisode > lastEpisode)
                    {
                        return MatchOutcome.NoResult;
                    }

                    result.EpisodeNumbers = Enumerable.Range(firstEpisode, lastEpisode - firstEpisode + 1).ToArray();
                    lastIndex = Math.Max(lastIndex, episodeCaptures[^1].Index + episodeCaptures[^1].Length);

                    if (matchGroup.Groups["special"].Success)
                    {
                        result.Special = true;
                    }
                }

                if (absoluteCaptures.Count > 0)
                {
                    if (!TryParseDecimal(absoluteCaptures[0].Value, out var firstAbsolute) ||
                        !TryParseDecimal(absoluteCaptures[^1].Value, out var lastAbsolute) ||
                        firstAbsolute > lastAbsolute)
                    {
                        return MatchOutcome.NoResult;
                    }

                    result.AbsoluteCaptured = true;

                    if (firstAbsolute % 1 != 0 || lastAbsolute % 1 != 0)
                    {
                        // Decimal numbers ("12.5") are recap specials; several of them are not allowed.
                        if (absoluteCaptures.Count != 1)
                        {
                            return MatchOutcome.NoResult;
                        }

                        result.Special = true;
                        lastIndex = Math.Max(lastIndex, absoluteCaptures[0].Index + absoluteCaptures[0].Length);
                    }
                    else
                    {
                        result.AbsoluteEpisodeNumbers = Enumerable.Range((int)firstAbsolute, (int)(lastAbsolute - firstAbsolute + 1)).ToArray();

                        if (matchGroup.Groups["special"].Success)
                        {
                            result.Special = true;
                        }

                        lastIndex = Math.Max(lastIndex, absoluteCaptures[^1].Index + absoluteCaptures[^1].Length);
                    }
                }

                if (episodeCaptures.Count == 0 && absoluteCaptures.Count == 0)
                {
                    // Partial season packs ("S01 Part 2") are neither full seasons nor specials; extras/subpacks still count as full season.
                    if (!string.IsNullOrWhiteSpace(first.Groups["seasonpart"].Value))
                    {
                        result.FullSeason = false;
                    }
                    else if (first.Groups["special"].Success)
                    {
                        result.Special = true;
                    }
                    else
                    {
                        result.FullSeason = true;
                    }
                }

                // "S1E1-10 of 10" is the whole season.
                if (episodeCaptures.Count == 2 && first.Groups["episodecount"].Success && episodeCaptures[^1].Value == first.Groups["episodecount"].Value)
                {
                    result.EpisodeNumbers = [];
                    result.FullSeason = true;
                }
            }

            var seasons = new List<int>();
            foreach (Capture seasonCapture in first.Groups["season"].Captures)
            {
                if (int.TryParse(seasonCapture.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSeason))
                {
                    seasons.Add(parsedSeason);
                    lastIndex = Math.Max(lastIndex, seasonCapture.Index + seasonCapture.Length);
                }
            }

            var distinctSeasons = seasons.Distinct().Order().ToList();

            if (distinctSeasons.Count is 1 or > 2)
            {
                result.Seasons = distinctSeasons;
                result.SeasonCaptured = true;
            }
            else if (distinctSeasons.Count == 2)
            {
                result.Seasons = Enumerable.Range(distinctSeasons[0], distinctSeasons[1] - distinctSeasons[0] + 1).ToList();
                result.SeasonCaptured = true;
            }
            else if (result.AbsoluteEpisodeNumbers.Count == 0 && result.EpisodeNumbers.Count > 0)
            {
                // No season and not absolute numbering: a mini-series, treated as season 1.
                result.Seasons = [1];
            }
        }
        else
        {
            // Daily show
            int airMonth;
            int airDay;

            if (first.Groups["ambiguousairmonth"].Success && first.Groups["ambiguousairday"].Success)
            {
                var ambiguousMonth = int.Parse(first.Groups["ambiguousairmonth"].Value, CultureInfo.InvariantCulture);
                var ambiguousDay = int.Parse(first.Groups["ambiguousairday"].Value, CultureInfo.InvariantCulture);

                if (ambiguousDay <= 12 && ambiguousMonth <= 12)
                {
                    return MatchOutcome.InvalidDate;
                }

                airMonth = ambiguousMonth;
                airDay = ambiguousDay;
            }
            else if (first.Groups["shortairmonth"].Success)
            {
                if (!ShortMonths.TryGetValue(first.Groups["shortairmonth"].Value, out airMonth))
                {
                    return MatchOutcome.InvalidDate;
                }

                airDay = int.Parse(first.Groups["airday"].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                airMonth = int.Parse(first.Groups["airmonth"].Value, CultureInfo.InvariantCulture);
                airDay = int.Parse(first.Groups["airday"].Value, CultureInfo.InvariantCulture);
            }

            // Swap day and month if the month is bigger than 12 (scene fail).
            if (airMonth > 12)
            {
                (airDay, airMonth) = (airMonth, airDay);
            }

            if (airMonth is < 1 or > 12 || airDay < 1 || airDay > DateTime.DaysInMonth(airYear, airMonth))
            {
                return MatchOutcome.InvalidDate;
            }

            var airDate = new DateTime(airYear, airMonth, airDay, 0, 0, 0, DateTimeKind.Unspecified);

            // Allow early releases, but reject dates too far in the future (most likely a parse error).
            if (airDate > DateTime.Today.AddDays(14))
            {
                return MatchOutcome.InvalidDate;
            }

            // Dates before 1970 are only plausible for the Plex DVR format, which has the title year.
            if (airDate < new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) && string.IsNullOrWhiteSpace(first.Groups["titleyear"].Value))
            {
                return MatchOutcome.InvalidDate;
            }

            foreach (var name in (ReadOnlySpan<string>)["airyear", "airmonth", "airday"])
            {
                var group = first.Groups[name];
                if (group.Success)
                {
                    lastIndex = Math.Max(lastIndex, group.Index + group.Length);
                }
            }

            result.AirDate = airDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        result.ReleaseTokens = lastIndex < releaseTitle.Length ? releaseTitle[lastIndex..] : releaseTitle;
        result.SeriesTitle = seriesName;
        info = result;
        return MatchOutcome.Parsed;
    }

    /// <summary>"Series Title 2010" → ("Series Title", 2010); "Series (2009)" → ("Series", 2009). Only a trailing year counts.</summary>
    internal static (string Title, int Year) SplitYear(string seriesTitle)
    {
        var match = ReleaseParserCommon.SafeMatch(YearInTitleRegex(), seriesTitle);
        if (!match.Success)
        {
            return (seriesTitle, 0);
        }

        return (match.Groups["title"].Value.TrimEnd(' ', '-', '_', '.'), int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture));
    }

    private static string? GetReleaseHash(Match match)
    {
        var hash = match.Groups["hash"];
        if (!hash.Success)
        {
            return null;
        }

        var value = hash.Value.Trim('[', ']', '(', ')');
        return value == "1280x720" || value.Length == 0 ? null : value;
    }

    private static bool TryParseNumber(string value, out int number)
    {
        var normalized = ConvertToNumerals(value.Normalize(NormalizationForm.FormKC));

        if (int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            return true;
        }

        number = Array.IndexOf(Numbers, value.ToLowerInvariant());
        return number != -1;
    }

    private static bool TryParseDecimal(string value, out decimal number)
    {
        var normalized = ConvertToNumerals(value.Normalize(NormalizationForm.FormKC));
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    }

    // Full-width and other Unicode digits → ASCII digits.
    private static string ConvertToNumerals(string input)
    {
        var builder = new StringBuilder(input.Length);

        foreach (var c in input)
        {
            if (char.IsDigit(c))
            {
                builder.Append((char)('0' + (int)char.GetNumericValue(c)));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"(?:(480|540|576|720|1080|1440|2160)[ip]|[xh][\W_]?26[456]|DD\W?5\W1|[<>?*]|848x480|1280x720|1920x1080|3840x2160|4096x2160|(?<![a-f0-9])(8|10)[ -]?(b(?![a-z0-9])|bit))\s*?", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex SimpleTitleRegex();

    [GeneratedRegex(@"(?<=[_.-])(?<airdate>(?<!\d)(?<airyear>[1-9]\d{1})(?<airmonth>[0-1][0-9])(?<airday>[0-3][0-9]))(?=[_.-])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex SixDigitAirDateRegex();

    // Unlike Sonarr (any 4 digits anywhere), only a trailing 18xx-20xx year is split off, so "Blade Runner 2049 2017" keeps "2049".
    [GeneratedRegex(@"^(?<title>.+?)[-_. ]+[\(\[]?(?<year>(?:18|19|20)\d{2})[\]\)]?$", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex YearInTitleRegex();

    [GeneratedRegex(@"^(?:\[.+?\])+", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex RequestInfoRegex();
}
