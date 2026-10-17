// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StreamZH.Core.Quality;

namespace StreamZH.Parsing.Releases;

/// <summary>Port of Radarr's Parser.ParseMovieTitle (without the folder-name mode and hardcoded-subs detection).</summary>
internal static partial class MovieTitleParser
{
    private const string EditionPattern =
        @"\(?\b(?<edition>(((Recut.|Extended.|Ultimate.)?(Director.?s|Collector.?s|Theatrical|Ultimate|Extended|Despecialized|(Special|Rouge|Final|Assembly|Imperial|Diamond|Signature|Hunter|Rekall)(?=(.(Cut|Edition|Version)))|\d{2,3}(th)?.Anniversary)(?:.(Cut|Edition|Version))?(.(Extended|Uncensored|Remastered|Unrated|Uncut|Open.?Matte|IMAX|Fan.?Edit))?|((Uncensored|Remastered|Unrated|Uncut|Open?.Matte|IMAX|Fan.?Edit|Restored|((2|3|4)in1))))))\b\)?";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] MovieTitleRegexes =
    [
        AnimeYearInParenthesesRegex(),
        AnimeYearRegex(),
        AnimeVersionedHashRegex(),
        AnimeDoubleBracketsHashRegex(),
        AnimeBracketsHashRegex(),
        GermanTrackerRegex(),
        EditionBeforeYearRegex(),
        NormalMovieRegex(),
        PassThePopcornRegex(),
        BracketYearRegex(),
        LastResortRegex(),
    ];

    public static ParsedRelease? Parse(string title)
    {
        var originalTitle = title;

        if (!ReleaseParserCommon.IsParsable(title, sonarrRules: false))
        {
            return null;
        }

        title = ReleaseParserCommon.UnreverseTitle(title, includeEpisodeMarker: false);

        var releaseTitle = ReleaseParserCommon.RemoveFileExtension(title).Trim('-', '_');
        releaseTitle = releaseTitle.Replace('【', '[').Replace('】', ']');

        var simpleTitle = ReleaseParserCommon.SafeReplace(SimpleTitleRegex(), releaseTitle, string.Empty);
        simpleTitle = ReleaseParserCommon.StripWebsites(simpleTitle);
        simpleTitle = ReleaseParserCommon.StripTrailingQualityBracket(simpleTitle);

        foreach (var regex in MovieTitleRegexes)
        {
            var matches = ReleaseParserCommon.SafeMatches(regex, simpleTitle);
            if (matches is null)
            {
                continue;
            }

            var match = matches[0];
            var titleGroup = match.Groups["title"];
            if (!titleGroup.Success || titleGroup.Value == "(")
            {
                continue;
            }

            var movieName = BuildMovieName(titleGroup.Value);
            int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year);

            var edition = match.Groups["edition"].Success ? match.Groups["edition"].Value.Replace('.', ' ') : null;

            // Radarr replaces the title with a placeholder before looking for the group and languages,
            // so words in the title ("The Good German") are not mistaken for release info.
            var simpleReleaseTitle = SimpleReleaseTitleRegex().Replace(releaseTitle, string.Empty);
            var placeholder = titleGroup.Value.Contains('.') ? "A.Movie" : "A Movie";
            if (titleGroup.Index + titleGroup.Length <= simpleReleaseTitle.Length)
            {
                simpleReleaseTitle = simpleReleaseTitle.Remove(titleGroup.Index, titleGroup.Length).Insert(titleGroup.Index, placeholder);
            }

            var subGroup = match.Groups["subgroup"];
            var releaseGroup = subGroup.Success && !string.IsNullOrWhiteSpace(subGroup.Value)
                ? subGroup.Value
                : ReleaseGroupParser.ParseReleaseGroup(simpleReleaseTitle);

            var languages = LanguageParser.ParseLanguages(
                string.IsNullOrWhiteSpace(releaseGroup) ? simpleReleaseTitle : simpleReleaseTitle.Replace(releaseGroup, "RlsGrp", StringComparison.Ordinal));

            if (string.IsNullOrWhiteSpace(edition))
            {
                edition = ParseEdition(simpleReleaseTitle);
            }

            var tokens = ReleaseTokens(releaseTitle, titleGroup.Value);

            return new ParsedRelease
            {
                ReleaseTitle = originalTitle,
                Title = movieName,
                CleanTitle = TitleNormalizer.Clean(movieName),
                Year = year,
                Edition = string.IsNullOrWhiteSpace(edition) ? null : edition,
                ReleaseHash = GetReleaseHash(match),
                ImdbId = ReleaseIds.ParseImdbId(simpleReleaseTitle),
                TmdbId = ReleaseIds.ParseTmdbId(simpleReleaseTitle),
                ReleaseGroup = releaseGroup,
                Languages = languages,
                Quality = QualityParser.ParseQuality(title),
                Hdr = QualityParser.ParseHdr(title),
                VideoCodec = CodecParser.ParseVideoCodec(tokens),
                AudioCodec = CodecParser.ParseAudioCodec(tokens),
            };
        }

        return null;
    }

    /// <summary>Radarr ParseEdition: the first edition keyword after the title, dots turned into spaces.</summary>
    internal static string? ParseEdition(string title)
    {
        var match = ReleaseParserCommon.SafeMatch(ReportEditionRegex(), title);
        var edition = match.Success ? match.Groups["edition"].Value : null;
        return string.IsNullOrWhiteSpace(edition) ? null : edition.Replace('.', ' ');
    }

    /// <summary>The part of the release name after the title, where codecs and other release info live.</summary>
    internal static string ReleaseTokens(string releaseTitle, string rawTitle)
    {
        var index = string.IsNullOrEmpty(rawTitle) ? -1 : releaseTitle.IndexOf(rawTitle, StringComparison.Ordinal);
        return index < 0 ? releaseTitle : releaseTitle[(index + rawTitle.Length)..];
    }

    // Radarr ParseMovieMatchCollection: dots become spaces, except in acronyms ("R.I.P.D.", "L.A.", "Dr.").
    private static string BuildMovieName(string rawTitle)
    {
        var movieName = rawTitle.Replace('_', ' ');
        movieName = NormalizeAlternativeTitleRegex().Replace(movieName, " AKA ");
        movieName = RequestInfoRegex().Replace(movieName, string.Empty).Trim(' ');

        var parts = movieName.Split('.');
        var builder = new StringBuilder(movieName.Length + 4);
        var previousAcronym = false;

        for (var n = 0; n < parts.Length; n++)
        {
            var part = parts[n];
            var nextPart = n + 1 < parts.Length ? parts[n + 1] : string.Empty;

            if (part.Length == 1 && !part.Equals("a", StringComparison.OrdinalIgnoreCase) && !int.TryParse(part, out _) &&
                (previousAcronym || n < parts.Length - 1) &&
                (previousAcronym || nextPart.Length != 1 || !int.TryParse(nextPart, out _)))
            {
                builder.Append(part).Append('.');
                previousAcronym = true;
            }
            else if (part.Equals("a", StringComparison.OrdinalIgnoreCase) && (previousAcronym || nextPart.Length == 1))
            {
                builder.Append(part).Append('.');
                previousAcronym = true;
            }
            else if (part.Equals("dr", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(part).Append('.');
                previousAcronym = true;
            }
            else
            {
                if (previousAcronym)
                {
                    builder.Append(' ');
                    previousAcronym = false;
                }

                builder.Append(part).Append(' ');
            }
        }

        return builder.ToString().Trim(' ');
    }

    private static string? GetReleaseHash(Match match)
    {
        var hash = match.Groups["hash"];
        if (!hash.Success)
        {
            return null;
        }

        var value = hash.Value.Trim('[', ']');
        return value == "1280x720" || value.Length == 0 ? null : value;
    }

    // Anime [Subgroup] with year explicitly in parentheses, e.g. [Sub] Title 1993 (1993) [VHS].
    // Must come before the generic anime+year regex to handle titles that contain a year as part of the name.
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)(?<title>(?![(\[]).+?)\s*\((?<year>(1(8|9)|20)\d{2})\).*?(?<hash>\[\w{8}\])?(?:$|\.)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex AnimeYearInParenthesesRegex();

    // Anime [Subgroup] and Year
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)(?<title>(?![(\[]).+?)?(?:(?:[-_\W](?<![)\[!]))*(?<year>(1(8|9)|20)\d{2}(?!p|i|x|\d+|\]|\W\d+)))+.*?(?<hash>\[\w{8}\])?(?:$|\.)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex AnimeYearRegex();

    // Anime [Subgroup] no year, versioned title, hash
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)(?<title>(?![(\[]).+?)((v)(?:\d{1,2})(?:([-_. ])))(\[.*)?(?:[\[(][^])])?.*?(?<hash>\[\w{8}\])(?:$|\.)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex AnimeVersionedHashRegex();

    // Anime [Subgroup] no year, info in double sets of brackets, hash
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)(?<title>(?![(\[]).+?)(\[.*).*?(?<hash>\[\w{8}\])(?:$|\.)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex AnimeDoubleBracketsHashRegex();

    // Anime [Subgroup] no year, info in parentheses or brackets, hash
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)(?<title>(?![(\[]).+)(?:[\[(][^])]).*?(?<hash>\[\w{8}\])(?:$|\.)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex AnimeBracketsHashRegex();

    // Some German or French tracker formats (missing year, ...). Only applies to German and TrueFrench releases.
    [GeneratedRegex(@"^(?<title>(?![(\[]).+?)((\W|_))(" + EditionPattern + @".{1,3})?(?:(?<!(19|20)\d{2}.*?)(?<!(?:Good|The)[_ .-])(German|TrueFrench))(.+?)(?=((19|20)\d{2}|$))(?<year>(19|20)\d{2}(?!p|i|\d+|\]|\W\d+))?(\W+|_|$)(?!\\)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex GermanTrackerRegex();

    // Special, Despecialized, etc. Edition Movies, e.g: Mission.Impossible.3.Special.Edition.2011
    [GeneratedRegex(@"^(?<title>(?![(\[]).+?)?(?:(?:[-_\W](?<![)\[!]))*" + EditionPattern + @".{1,3}(?<year>(1(8|9)|20)\d{2}(?!p|i|\d+|\]|\W\d+)))+(\W+|_|$)(?!\\)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex EditionBeforeYearRegex();

    // Normal movie format, e.g: Mission.Impossible.3.2011
    [GeneratedRegex(@"^(?<title>(?![(\[]).+?)?(?:(?:[-_\W](?<![)\[!]))*(?<year>(1(8|9)|20)\d{2}(?!p|i|(1(8|9)|20)\d{2}|\]|\W(1(8|9)|20)\d{2})))+(\W+|_|$)(?!\\)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex NormalMovieRegex();

    // PassThePopcorn torrent names: Star.Wars[PassThePopcorn]
    [GeneratedRegex(@"^(?<title>.+?)?(?:(?:[-_\W](?<![()\[!]))*(?<year>(\[\w *\])))+(\W+|_|$)(?!\\)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex PassThePopcornRegex();

    // That did not work? Maybe some tool uses [] for years.
    [GeneratedRegex(@"^(?<title>(?![(\[]).+?)?(?:(?:[-_\W](?<![)!]))*(?<year>(1(8|9)|20)\d{2}(?!p|i|\d+|\W\d+)))+(\W+|_|$)(?!\\)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex BracketYearRegex();

    // As a last resort for movies that have ( or [ in their title.
    [GeneratedRegex(@"^(?<title>.+?)?(?:(?:[-_\W](?<![)\[!]))*(?<year>(1(8|9)|20)\d{2}(?!p|i|\d+|\]|\W\d+)))+(\W+|_|$)(?!\\)", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex LastResortRegex();

    [GeneratedRegex(@"^.+?" + EditionPattern, Options, RegexTimeouts.Milliseconds)]
    private static partial Regex ReportEditionRegex();

    [GeneratedRegex(@"(?:(480|540|576|720|1080|2160)[ip]|[xh][\W_]?26[45]|DD\W?5\W1|[<>?*]|848x480|1280x720|1920x1080|3840x2160|4096x2160|(8|10)b(it)?|10-bit)\s*?(?![a-b0-9])", Options, RegexTimeouts.Milliseconds)]
    private static partial Regex SimpleTitleRegex();

    [GeneratedRegex(@"\s*(?:[<>?*|])", Options)]
    private static partial Regex SimpleReleaseTitleRegex();

    [GeneratedRegex(@"[ ]+(?:A\.K\.A\.)[ ]+", Options)]
    private static partial Regex NormalizeAlternativeTitleRegex();

    [GeneratedRegex(@"^(?:\[.+?\])+", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex RequestInfoRegex();
}
