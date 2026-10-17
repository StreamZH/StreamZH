// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Parsing.Releases;

/// <summary>Episode release name patterns, ported 1:1 from Sonarr's Parser.ReportTitleRegex. Order matters: first match wins.</summary>
internal static partial class EpisodeTitleRegexes
{
    /// <summary>All patterns in evaluation order, with how much a match can be trusted against a movie parse.</summary>
    internal static readonly (Regex Regex, EpisodePatternKind Kind)[] All =
    [
        (Pattern000(), EpisodePatternKind.Normal),
        (Pattern001(), EpisodePatternKind.Normal),
        (Pattern002(), EpisodePatternKind.Normal),
        (Pattern003(), EpisodePatternKind.Normal),
        (Pattern004(), EpisodePatternKind.Normal),
        (Pattern005(), EpisodePatternKind.Normal),
        (Pattern006(), EpisodePatternKind.Normal),
        (Pattern007(), EpisodePatternKind.Normal),
        (Pattern008(), EpisodePatternKind.Normal),
        (Pattern009(), EpisodePatternKind.Normal),
        (Pattern010(), EpisodePatternKind.Normal),
        (Pattern011(), EpisodePatternKind.Normal),
        (Pattern012(), EpisodePatternKind.Normal),
        (Pattern013(), EpisodePatternKind.Normal),
        (Pattern014(), EpisodePatternKind.Normal),
        (Pattern015(), EpisodePatternKind.Normal),
        (Pattern016(), EpisodePatternKind.Normal),
        (Pattern017(), EpisodePatternKind.Normal),
        (Pattern018(), EpisodePatternKind.Normal),
        (Pattern019(), EpisodePatternKind.Normal),
        (Pattern020(), EpisodePatternKind.Normal),
        (Pattern021(), EpisodePatternKind.Normal),
        (Pattern022(), EpisodePatternKind.Normal),
        (Pattern023(), EpisodePatternKind.Normal),
        (Pattern024(), EpisodePatternKind.Normal),
        (Pattern025(), EpisodePatternKind.Normal),
        (Pattern026(), EpisodePatternKind.Normal),
        (Pattern027(), EpisodePatternKind.Normal),
        (Pattern028(), EpisodePatternKind.Normal),
        (Pattern029(), EpisodePatternKind.Normal),
        (Pattern030(), EpisodePatternKind.Normal),
        (Pattern031(), EpisodePatternKind.Normal),
        (Pattern032(), EpisodePatternKind.Normal),
        (Pattern033(), EpisodePatternKind.Normal),
        (Pattern034(), EpisodePatternKind.Normal),
        (Pattern035(), EpisodePatternKind.Normal),
        (Pattern036(), EpisodePatternKind.Normal),
        (Pattern037(), EpisodePatternKind.Normal),
        (Pattern038(), EpisodePatternKind.Normal),
        (Pattern039(), EpisodePatternKind.Normal),
        (Pattern040(), EpisodePatternKind.Normal),
        (Pattern041(), EpisodePatternKind.Normal),
        (Pattern042(), EpisodePatternKind.Normal),
        (Pattern043(), EpisodePatternKind.Normal),
        (Pattern044(), EpisodePatternKind.Normal),
        (Pattern045(), EpisodePatternKind.Normal),
        (Pattern046(), EpisodePatternKind.Ambiguous),
        (Pattern047(), EpisodePatternKind.Normal),
        (Pattern048(), EpisodePatternKind.Normal),
        (Pattern049(), EpisodePatternKind.Normal),
        (Pattern050(), EpisodePatternKind.Normal),
        (Pattern051(), EpisodePatternKind.Normal),
        (Pattern052(), EpisodePatternKind.Ambiguous),
        (Pattern053(), EpisodePatternKind.Normal),
        (Pattern054(), EpisodePatternKind.Normal),
        (Pattern055(), EpisodePatternKind.Normal),
        (Pattern056(), EpisodePatternKind.Normal),
        (Pattern057(), EpisodePatternKind.Normal),
        (Pattern058(), EpisodePatternKind.Ambiguous),
        (Pattern059(), EpisodePatternKind.Normal),
        (Pattern060(), EpisodePatternKind.Normal),
        (Pattern061(), EpisodePatternKind.Normal),
        (Pattern062(), EpisodePatternKind.Normal),
        (Pattern063(), EpisodePatternKind.Normal),
        (Pattern064(), EpisodePatternKind.Normal),
        (Pattern065(), EpisodePatternKind.Normal),
        (Pattern066(), EpisodePatternKind.Normal),
        (Pattern067(), EpisodePatternKind.Normal),
        (Pattern068(), EpisodePatternKind.Normal),
        (Pattern069(), EpisodePatternKind.Normal),
        (Pattern070(), EpisodePatternKind.Normal),
        (Pattern071(), EpisodePatternKind.Normal),
        (Pattern072(), EpisodePatternKind.Normal),
        (Pattern073(), EpisodePatternKind.Normal),
        (Pattern074(), EpisodePatternKind.Normal),
        (Pattern075(), EpisodePatternKind.Normal),
        (Pattern076(), EpisodePatternKind.SeasonOnly),
        (Pattern077(), EpisodePatternKind.SeasonOnly),
        (Pattern078(), EpisodePatternKind.Normal),
        (Pattern079(), EpisodePatternKind.Normal),
        (Pattern080(), EpisodePatternKind.Ambiguous),
        (Pattern081(), EpisodePatternKind.Normal),
        (Pattern082(), EpisodePatternKind.Normal),
        (Pattern083(), EpisodePatternKind.ExplicitAbsolute),
        (Pattern084(), EpisodePatternKind.Normal),
        (Pattern085(), EpisodePatternKind.Normal),
        (Pattern086(), EpisodePatternKind.Normal),
        (Pattern087(), EpisodePatternKind.ExplicitAbsolute),
        (Pattern088(), EpisodePatternKind.Normal),
        (Pattern089(), EpisodePatternKind.Ambiguous),
        (Pattern090(), EpisodePatternKind.Normal),
        (Pattern091(), EpisodePatternKind.Normal),
        (Pattern092(), EpisodePatternKind.Normal),
        (Pattern093(), EpisodePatternKind.Ambiguous),
        (Pattern094(), EpisodePatternKind.Ambiguous),
        (Pattern095(), EpisodePatternKind.Ambiguous),
        (Pattern096(), EpisodePatternKind.ExplicitAbsolute),
        (Pattern097(), EpisodePatternKind.ExplicitAbsolute),
        (Pattern098(), EpisodePatternKind.ExplicitAbsolute),
        (Pattern099(), EpisodePatternKind.Normal),
        (Pattern100(), EpisodePatternKind.ExplicitAbsolute),
        (Pattern101(), EpisodePatternKind.Normal),
        (Pattern102(), EpisodePatternKind.Normal),
        (Pattern103(), EpisodePatternKind.Normal),
        (Pattern104(), EpisodePatternKind.Normal),
        (Pattern105(), EpisodePatternKind.Ambiguous),
        (Pattern106(), EpisodePatternKind.Normal),
        (Pattern107(), EpisodePatternKind.Normal),
        (Pattern108(), EpisodePatternKind.Normal),
    ];

    // Daily episode with year in series title and air time after date (Plex DVR format)
    [GeneratedRegex(@"^^(?<title>.+?\((?<titleyear>\d{4})\))[-_. ]+(?<airyear>19[4-9]\d|20\d\d)(?<sep>[-_]?)(?<airmonth>0\d|1[0-2])\k<sep>(?<airday>[0-2]\d|3[01])[-_. ]\d{2}[-_. ]\d{2}[-_. ]\d{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern000();

    // Daily episodes without title (2018-10-12, 20181012) (Strict pattern to avoid false matches)
    [GeneratedRegex(@"^(?<airyear>19[6-9]\d|20\d\d)(?<sep>[-_]?)(?<airmonth>0\d|1[0-2])\k<sep>(?<airday>[0-2]\d|3[01])(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern001();

    // Multi-Part episodes without a title (S01E05.S01E06)
    [GeneratedRegex(@"^(?:\W*S(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?!\d+))(?:e{1,2}(?<episode>\d{1,3}(?!\d+)))+){2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern002();

    // Multi-Part episodes without a title (1x05.1x06)
    [GeneratedRegex(@"^(?:\W*(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?!\d+))(?:x{1,2}(?<episode>\d{1,3}(?!\d+)))+){2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern003();

    // Episodes without a title, Multi (S01E04E05, 1x04x05, etc)
    [GeneratedRegex(@"^(?:S?(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?!\d+))(?:(?:[-_]|[ex]){1,2}(?<episode>\d{2,3}(?!\d+))){2,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern004();

    // Split episodes (S01E05a, S01E05b, etc)
    [GeneratedRegex(@"^(?<title>.+?)(?:S?(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?!\d+))(?:(?:[-_ ]?[ex])(?<episode>\d{2,3}(?!\d+))(?<splitepisode>[a-d])(?:[ _.])))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern005();

    // Episodes without a title, Single (S01E05, 1x05)
    [GeneratedRegex(@"^(?:S?(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?!\d+))(?:(?:[-_ ]?[ex])(?<episode>\d{2,3}(?!\d+))))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern006();

    // Anime - [SubGroup] Title Absolute (Season+Episode)
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\](?:_|-|\s|\.)?)(?<title>.+?)[-_. ]+(?<absoluteepisode>(?<!\d+)\d{2,3}(\.\d{1,2})?(?!\d+))(?:[-_. ])+\((?:S(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:(?:[ex]|\W[ex]){1,2}(?<episode>\d{2}(?!\d+))))(?:v\d+)?(?:\)(?!\d+)).*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern007();

    // Anime - [SubGroup] Title Absolute - Season+Episode
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\](?:_|-|\s|\.)?)(?<title>.+?)[-_. ]+(?<absoluteepisode>(?<!\d+)\d{2,3}(\.\d{1,2})?(?!\d+))(?:[-_. ](?<![()\[!]))+(?:S(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:(?:[ex]|\W[ex]){1,2}(?<episode>\d{2}(?!\d+))))(?:v\d+)?(?:[_. ](?!\d+)).*?(?<hash>[(\[][0-9A-F]{8}[)\]])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern008();

    // Anime - [SubGroup] Title Season+Episode
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\](?:_|-|\s|\.)?)(?<title>.+?)(?:[-_\W](?<![()\[!]))+(?:S?(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:(?:[ex]|\W[ex]){1,2}(?<episode>\d{2}(?!\d+)))+)(?:v\d+)?(?:[_. ](?!\d+)).*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern009();

    // Anime - [SubGroup] Title Episode Absolute Episode Number ([SubGroup] Series Title Episode 01)
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)(?<title>.+?)[-_. ]+?(?:Episode)(?:[-_. ]+(?<absoluteepisode>(?<!\d+)\d{2,3}(\.\d{1,2})?(?!\d+)))+.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern010();

    // Anime - [SubGroup] Title Absolute Episode Number + Season+Episode
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\](?:_|-|\s|\.)?)(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+(?<absoluteepisode>\d{2,3}(\.\d{1,2})?))+(?:_|-|\s|\.)+(?:S?(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:(?:\-|[ex]|\W[ex]){1,2}(?<episode>\d{2}(?!\d+)))+).*?(?<hash>[(\[][0-9A-F]{8}[)\]])?(?:$|\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern011();

    // Anime - [SubGroup] Title Multi-episode + optional Absolute Episode Number
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\](?:_|-|\s|\.)?)(?<title>.+?)(?:[-_\W](?<![()\[!]))+(?:S?(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:(?:\-|[ex]|\W[ex]){1,2}(?<episode>\d{2}(?!\d+))){2,})(?:(?:_|-|\s|\.)+(?<absoluteepisode>(?<!\d+)\d{2,3}(\.\d{1,2})?(?!\d+|\-[a-z])))*.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern012();

    // Anime - [SubGroup] Title Single episode + Absolute Episode Number
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\](?:_|-|\s|\.)?)(?<title>.+?)(?:[-_\W](?<![()\[!]))+(?:S?(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:(?:\-|[ex]|\W[ex]){1,2}(?<episode>\d{2}(?!\d+))))(?:(?:_|-|\s|\.)+(?<absoluteepisode>(?<!\d+)\d{2,3}(\.\d{1,2})?(?!\d+|\-[a-z])))+.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern013();

    // Anime - [SubGroup] Title with trailing number Absolute Episode Number - Batch separated with tilde
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>.+?[^-]+?)(?:(?<![-_. ]|\b[0]\d+) - )[-_. ]?(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+))\s?~\s?(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+))(?:[-_. ]+(?<special>special|ova|ovd))?.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern014();

    // Anime - [SubGroup] Title with season number in brackets Absolute Episode Number
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>[^-]+?)[_. ]+?\(Season[_. ](?<season>\d+)\)[-_. ]+?(?:[-_. ]?(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+)))+(?:[-_. ]+(?<special>special|ova|ovd))?.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern015();

    // Anime - [SubGroup] Title with trailing 3-digit number and sub title - Absolute Episode Number
    [GeneratedRegex(@"^\[(?<subgroup>[^\]]+?)\][-_. ]?(?<title>[^]]+?)(?:[-_. ]{3}?(?<absoluteepisode>\d{2}(\.\d{1,2})?(?!-?\d+|-[a-z]+)))+(?:[-_. ]+(?<special>special|ova|ovd))?.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern016();

    // Anime - [SubGroup] Multi-season pack
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>.+?)[-_. ]+S(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))(?:(?:-S?|[-_. ]{1,3}S)(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))){1,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern017();

    // Anime - [SubGroup] Partial season pack
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>.+?)(?:\W+S(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))\W+(?:(?:(?:Part|Vol)\W?|(?<!\d+\W+)e|p)(?<seasonpart>\d{1,2}(?!\d+)))+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern018();

    // Anime - [SubGroup] Title with trailing number Absolute Episode Number
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>[^-]+?)(?:(?<![-_. ]|\b[0]\d+) - )(?:[-_. ]?(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+)))+(?:[-_. ]+(?<special>special|ova|ovd))?.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern019();

    // Anime - [SubGroup] Title with trailing number S## (Full season)
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>.+?)[-_. ]+(?:S(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?![ex]?\d+))).+?(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern020();

    // Anime - [SubGroup] Title with trailing number Absolute Episode Number
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>[^-]+?)(?:(?<![-_. ]|\b[0]\d+)[_ ]+)(?:[-_. ]?(?<absoluteepisode>\d{3}(\.\d{1,2})?(?!\d+|-[a-z]+)))+(?:[-_. ]+(?<special>special|ova|ovd))?.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern021();

    // Anime - [SubGroup] Title - Absolute Episode Number
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>.+?)(?:(?<!\b[0]\d+))(?:[. ]-[. ](?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+|[-])))+(?:[-_. ]+(?<special>special|ova|ovd))?.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern022();

    // Anime - [SubGroup] Title Absolute Episode Number - Absolute Episode Number (batches without full separator between title and absolute episode numbers)
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>.+?)(?:(?<!\b[0]\d+))(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+|[-]))[. ]-[. ](?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+|[-]))(?:[-_. ]+(?<special>special|ova|ovd))?.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern023();

    // Anime - [SubGroup] Title (Season only)
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ])?(?<title>.+?)[-_. ]+?[\[(](?:S|Season|Saison|Series|Stagione|Sezon)[-_. ]?(?<season>\d{1,2}(?![-_. ]?\d+))(?:[-_. )\]]|$)+(?<extras>EXTRAS|SUBPACK)?(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern024();

    // Anime - [SubGroup] Title Absolute Episode Number
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>.+?)[-_. ]+\(?(?:[-_. ]?#?(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+|-[a-z]+)))+\)?(?:[-_. ]+(?<special>special|ova|ovd))?.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern025();

    // Multi-episode Repeated (S01E05 - S01E06)
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+S(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?!\d+))(?:(?:e|[-_. ]e){1,2}(?<episode>\d{1,3}(?!\d+)))+){2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern026();

    // Multi-episode Repeated (1x05 - 1x06)
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?!\d+))(?:x{1,2}(?<episode>\d{1,3}(?!\d+)))+){2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern027();

    // Single episodes with a title (S01E05, 1x05, etc) followed by ".5 [SP]"
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+S?(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))(?:[ex]|\W[ex]|_){1,2}(?<episode>\d{2,3}(?!\d+|(?:[ex]|\W[ex]|_|-){1,2}\d+))(?<special>\.5[ .]\[SP\]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern028();

    // Single episodes with a title (S01E05, 1x05, etc) and trailing info in slashes
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+S?(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))(?:[ex]|\W[ex]|_){1,2}(?<episode>\d{2,3}(?!\d+|(?:[ex]|\W[ex]|_|-){1,2}\d+))).+?(?:\[.+?\])(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern029();

    // Anime - Title Season EpisodeNumber + Absolute Episode Number [SubGroup]
    [GeneratedRegex(@"^(?<title>.+?)(?:[-_\W](?<![()\[!]))+(?:S?(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:(?:[ex]|\W[ex]|-){1,2}(?<episode>(?<!\d+)\d{2}(?!\d+)))+)[-_. (]+?(?:[-_. ]?(?<absoluteepisode>(?<!\d+)\d{3}(\.\d{1,2})?(?!\d+|[pi])))+.+?\[(?<subgroup>.+?)\](?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern030();

    // Multi-Episode with a title (S01E05E06, S01E05-06, S01E05 E06, etc) and trailing info in slashes
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+S?(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))(?:[ex]|\W[ex]|_){1,2}(?<episode>\d{2,3}(?!\d+))(?:(?:\-|[ex]|\W[ex]|_){1,2}(?<episode>\d{2,3}(?!\d+)))+).+?(?:\[.+?\])(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern031();

    // Anime - Title Absolute Episode Number [SubGroup] [Hash]? (Series Title Episode 99-100 [RlsGroup] [ABCD1234])
    [GeneratedRegex(@"^(?<title>.+?)[-_. ]Episode(?:[-_. ]+(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+)))+(?:.+?)\[(?<subgroup>.+?)\].*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern032();

    // Anime - Title Absolute Episode Number [SubGroup] [Hash]
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:_|-|\s|\.)+(?<absoluteepisode>\d{3}(\.\d{1,2})(?!\d+)))+(?:.+?)\[(?<subgroup>.+?)\].*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern033();

    // Anime - Title Absolute Episode Number (Year) [SubGroup]
    [GeneratedRegex(@"^(?<title>.+?)[-_. ]+(?<absoluteepisode>(?<!\d+)\d{2}(?!\d+))[-_. ](\(\d{4}\))[-_. ]\[(?<subgroup>.+?)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern034();

    // Anime - Title with trailing number, Absolute Episode Number and hash
    [GeneratedRegex(@"^(?<title>[^-]+?)(?:(?<![-_. ]|\b[0]\d+) - )(?:[-_. ]?(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+)))+(?:[-_. ]+(?<special>special|ova|ovd))?.*?(?<hash>[(\[][0-9A-F]{8}[)\]])(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern035();

    // Anime - Title Absolute Episode Number [Hash]
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:_|-|\s|\.)+(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+)))+(?:[-_. ]+(?<special>special|ova|ovd))?[-_. ]+.*?(?<hash>[(\[][0-9A-F]{8}[)\]])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern036();

    // Episodes with airdate AND season/episode number, capture season/episode only
    [GeneratedRegex(@"^(?<title>.+?)?\W*(?<airdate>\d{4}\W+[0-1][0-9]\W+[0-3][0-9])(?!\W+[0-3][0-9])[-_. ](?:s?(?<season>(?<!\d+)(?:\d{1,2})(?!\d+)))(?:[ex](?<episode>(?<!\d+)(?:\d{1,3})(?!\d+)))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern037();

    // Episodes with airdate AND season/episode number
    [GeneratedRegex(@"^(?<title>.+?)?\W*(?<airyear>\d{4})\W+(?<airmonth>[0-1][0-9])\W+(?<airday>[0-3][0-9])(?!\W+[0-3][0-9]).+?(?:s?(?<season>(?<!\d+)(?:\d{1,2})(?!\d+)))(?:[ex](?<episode>(?<!\d+)(?:\d{1,3})(?!\d+)))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern038();

    // Episodes with absolute episode number AND airdate (TJET Wrestling)
    [GeneratedRegex(@"^(?<title>.+?)?[-_. ](?:e\d{2,3}(?!\d+))[-_. ](?<airyear>\d{4})\W+(?<airmonth>[0-1][0-9])\W+(?<airday>[0-3][0-9])(?!\W+[0-3][0-9]).+?(?:\[[a-z]+\])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern039();

    // Single or multi episode releases with multiple titles, then season and episode numbers after the last title. (Title1 / Title2 / ... / S1E1-2 of 6)
    [GeneratedRegex(@"^((?<title>.*?)[ ._]\/[ ._])+\(?S(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:\W|_)?E?[ ._]?(?<episode>(?<!\d+)\d{1,2}(?!\d+))(?:-(?<episode>(?<!\d+)\d{1,2}(?!\d+)))?(?:[ ._]of[ ._](?<episodecount>\d{1,2}))?\)?[ ._][\(\[]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern040();

    // Multi-episode with title (S01E99-100, S01E05-06)
    [GeneratedRegex(@"^(?<title>.+?)(?:[-_\W](?<![()\[!]))+S(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))E(?<episode>\d{2,3}(?!\d+))(?:-(?<episode>\d{2,3}(?!\d+)))+(?:[-_. ]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern041();

    // Multi-episode with title (S01E05-06, S01E05-6)
    [GeneratedRegex(@"^(?<title>.+?)(?:[-_\W](?<![()\[!]))+S(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))E(?<episode>\d{1,2}(?!\d+))(?:-(?<episode>\d{1,2}(?!\d+)))+(?:[-_. ]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern042();

    // Episodes with a Chinese title and English title, Single episodes (S01E05, 1x05, etc) & Multi-episode (S01E05E06, S01E05-06, S01E05 E06, etc)
    [GeneratedRegex(@"^(?<title>[\u4E00-\u9FCC]+)[_. ](?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+S?(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))(?:[ex]|\W[ex]){1,2}(?<episode>\d{2,3}(?!\d+))(?:(?:\-|[ex]|\W[ex]|_){1,2}(?<episode>\d{2,3}(?!\d+)))*)(?:[-_. ]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern043();

    // Episodes with a title, Single episodes (S01E05, 1x05, etc) & Multi-episode (S01E05E06, S01E05-06, S01E05 E06, etc)
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+S?(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))(?:[ex]|\W[ex]){1,2}(?<episode>\d{2,3}(?!\d+))(?:(?:\-|[ex]|\W[ex]|_){1,2}(?<episode>\d{2,3}(?!\d+)))*)(?:[-_. ]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern044();

    // Episodes with a title, 4 digit season number, Single episodes (S2016E05, etc) & Multi-episode (S2016E05E06, S2016E05-06, S2016E05 E06, etc)
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+S(?<season>(?<!\d+)(?:\d{4})(?!\d+))(?:e|\We|_){1,2}(?<episode>\d{2,4}(?!\d+))(?:(?:\-|e|\We|_){1,2}(?<episode>\d{2,3}(?!\d+)))*)\W?(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern045();

    // Episodes with a title, 4 digit season number, Single episodes (2016x05, etc) & Multi-episode (2016x05x06, 2016x05-06, 2016x05 x06, etc)
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+(?<season>(?<!\d+)(?:\d{4})(?!\d+))(?:x|\Wx){1,2}(?<episode>\d{2,4}(?!\d+))(?:(?:\-|x|\Wx|_){1,2}(?<episode>\d{2,3}(?!\d+)))*)\W?(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern046();

    // Episodes with a title, Single episodes (s01.05)
    [GeneratedRegex(@"^(?<title>.+?)(?:[-_\W](?<![()\[!]))+S(?<season>(?<!\d+)(?:\d{2})(?!\d+))(?:\.)(?<episode>\d{2,3}(?!\d+))(?:[-_. ]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern047();

    // Multi-season pack
    [GeneratedRegex(@"^(?<title>.+?)(Complete Series)?[-_. ]+(?:S|(?:Season|Saison|Series|Stagione)[_. ])(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))(?:(?:[-_. ]{1}|[-_. ]{3})(?:S|(?:Season|Saison|Series|Stagione)[_. ])?(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))){1,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern048();

    // Partial season pack
    [GeneratedRegex(@"^(?<title>.+?)(?:\W+S(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))\W+(?:(?:(?:Part|Vol)\W?|(?<!\d+\W+)e|p)(?<seasonpart>\d{1,2}(?!\d+)))+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern049();

    // Anime - 4 digit absolute episode number in batch (1017-1088) or 1017-1088
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)(?<title>.+?)[-_. ]+?\(?(?<absoluteepisode>\d{4}(\.\d{1,2})?(?!\d+))[-](?<absoluteepisode>\d{4}(\.\d{1,2})?(?!\d+))\)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern050();

    // Anime - 4 digit absolute episode number
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)(?<title>.+?)[-_. ]+?(?<absoluteepisode>\d{4}(\.\d{1,2})?(?!\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern051();

    // Mini-Series, treated as season 1, multi episodes are labelled as E1-E2
    [GeneratedRegex(@"^(?<title>.+?)(?:[-._ ][e])(?<episode>\d{2,3}(?!\d+))(?:(?:\-?[e])(?<episode>\d{2,3}(?!\d+)))+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern052();

    // 5 digit episode number with a title
    [GeneratedRegex(@"^(?:(?<title>.+?)(?:_|-|\s|\.)+)(?:S?(?<season>(?<!\d+)\d{1,2}(?!\d+)))(?:(?:\-|[ex]|\W[ex]|_){1,2}(?<episode>(?<!\d+)\d{5}(?!\d+)))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern053();

    // 5 digit multi-episode with a title
    [GeneratedRegex(@"^(?:(?<title>.+?)(?:_|-|\s|\.)+)(?:S?(?<season>(?<!\d+)\d{1,2}(?!\d+)))(?:(?:[-_. ]{1,3}ep){1,2}(?<episode>(?<!\d+)\d{5}(?!\d+)))+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern054();

    // Episodes with airdate and part (2018.04.28.Part.2)
    [GeneratedRegex(@"^(?<title>.+?)?\W*(?<airyear>\d{4})[-_. ]+(?<airmonth>[0-1][0-9])[-_. ]+(?<airday>[0-3][0-9])(?![-_. ]+[0-3][0-9])[-_. ]+Part[-_. ]?(?<part>[1-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern055();

    // Episodes with airdate (2018.04.28)
    [GeneratedRegex(@"^(?<title>.+?)?\W*(?<airyear>\d{4})[-_. ]+(?<airmonth>[0-1][0-9])[-_. ]+(?<airday>[0-3][0-9])(?![-_. ]+[0-3][0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern056();

    // Anime - Title 4-digit Absolute Episode Number [SubGroup]
    [GeneratedRegex(@"^(?<title>.+?)[-_. ]+(?<absoluteepisode>(?<!\d+)\d{4}(?!\d+))[-_. ]\[(?<subgroup>.+?)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern057();

    // Mini-Series with year in title, treated as season 1, episodes are labelled as Part01, Part 01, Part.1
    [GeneratedRegex(@"^(?<title>.+?\d{4})(?:\W+(?:(?:Part\W?|e)(?<episode>\d{1,2}(?!\d+)))+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern058();

    // 3-digit season number
    // Single episode: Title.S001E01
    // Multi episode: Title.S001E01-E02
    [GeneratedRegex(@"^(?<title>.+?)[-_. ]+S(?<season>(?<!\d+)\d{3}(?!\d+))E(?<episode>(?<!\d+)\d{2}(?!\d+))(?:-[ex](?<episode>(?<!\d+)\d{2}(?!\d+)))?([_. ]|$)(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern059();

    // Mini-Series, treated as season 1, episodes are labelled as XofY
    [GeneratedRegex(@"^(?<title>.+?)(?:\W+(?:(?<episode>(?<!\d+)\d{1,2}(?!\d+))of\d+)+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern060();

    // Supports Season 01 Episode 03
    [GeneratedRegex(@"(?:.*(?:\""|^))(?<title>.*?)(?:[-_\W](?<![()\[]))+(?:\W?Season\W?)(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:\W|_)+(?:Episode\W)(?:[-_. ]?(?<episode>(?<!\d+)\d{1,2}(?!\d+)))+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern061();

    // Multi-episode with episodes in square brackets (Series Title [S01E11E12] or Series Title [S01E11-12])
    [GeneratedRegex(@"(?:.*(?:^))(?<title>.*?)[-._ ]+\[S(?<season>(?<!\d+)\d{2}(?!\d+))(?:[E-]{1,2}(?<episode>(?<!\d+)\d{2}(?!\d+)))+\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern062();

    // Multi-episode with episodes in brackets (Series Title (S01E11E12) or Series Title (S01E11-12) or Series Title (S01E1-3 of 12))
    [GeneratedRegex(@"(?:.*(?:^))(?<title>.*?)[-._ ]+\(S(?<season>(?<!\d+)\d{2}(?!\d+))(?:[E-]{1,2}(?<episode>(?<!\d+)\d{1,2}(?!\d+)))+([._ ]+of[._ ]+[\dx]+)?\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern063();

    // Multi-episode release with no space between series title and season (S01E11E12)
    [GeneratedRegex(@"(?:.*(?:^))(?<title>.*?)S(?<season>(?<!\d+)\d{2}(?!\d+))(?:E(?<episode>(?<!\d+)\d{2}(?!\d+)))+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern064();

    // Multi-episode with single episode numbers (S6.E1-E2, S6.E1E2, S6E1E2, etc)
    [GeneratedRegex(@"^(?<title>.+?)[-_. ]S(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?!\d+))(?:[-_. ]?[ex]?(?<episode>(?<!\d+)\d{1,2}(?!\d+)))+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern065();

    // Single or multi episode releases with multiple titles, each followed by season and episode numbers in brackets
    [GeneratedRegex(@"^(?<title>.*?)[ ._]\(S(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:\W|_)?E?[ ._]?(?<episode>(?<!\d+)\d{1,2}(?!\d+))(?:-(?<episode>(?<!\d+)\d{1,2}(?!\d+)))?\)(?:[ ._]\/[ ._])(?<title>.*?)[ ._]\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern066();

    // Multi-episode with episodes in brackets (Series Title (S01E11E12) or Series Title (S1E11-12) or Series Title (S1E1-3 of 12))
    [GeneratedRegex(@"(?:.*(?:^))(?<title>.*?)[-._ ]+\(S(?<season>(?<!\d+)\d{1}(?!\d+))(?:[E-]{1,2}(?<episode>(?<!\d+)\d{1,2}(?!\d+)))+([._ ]+of[._ ]+[\dx]+)?\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern067();

    // Single episode season or episode S1E1 or S1-E1 or S1.Ep1 or S01.Ep.01
    [GeneratedRegex(@"(?:.*(?:\""|^))(?<title>.*?)(?:\W?|_)S(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:\W|_)?Ep?[ ._]?(?<episode>(?<!\d+)\d{1,2}(?!\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern068();

    // 3 digit season S010E05
    [GeneratedRegex(@"(?:.*(?:\""|^))(?<title>.*?)(?:\W?|_)S(?<season>(?<!\d+)\d{3}(?!\d+))(?:\W|_)?E(?<episode>(?<!\d+)\d{1,2}(?!\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern069();

    // Separated season and episode numbers S01 - E01
    [GeneratedRegex(@"^(?<title>.+?)(?:_|-|\s|\.)+S(?<season>\d{2}(?!\d+))(\W-\W)E(?<episode>(?<!\d+)\d{2}(?!\d+))(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern070();

    // Season and episode numbers in square brackets (single and multi-episode)
    // Series Title - [02x01] - Episode 1
    // Series Title - [02x01x02] - Episode 1
    [GeneratedRegex(@"^(?<title>.+?)?(?:[-_\W](?<![()\[!]))+\[(?:s)?(?<season>(?<!\d+)\d{1,2})(?:(?:[ex])(?<episode>\d{2}))(?:(?:[-ex]){1,2}(?<episode>\d{2}))*\].+?(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern071();

    // Anime - Title with season number - Absolute Episode Number (Title S01 - EP14)
    [GeneratedRegex(@"^(?<title>.+?S\d{1,2})[-_. ]{3,}(?:EP)?(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+|[-]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern072();

    // Anime - French titles with single episode numbers, with or without leading sub group ([RlsGroup] Title - Episode 1)
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)?(?<title>.+?)[-_. ]+?(?:Episode[-_. ]+?)(?<absoluteepisode>\d{1}(\.\d{1,2})?(?!\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern073();

    // Anime - Absolute episode number in square brackets
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)(?<title>.+?)[-_. ]+?\[(?<absoluteepisode>\d{2,3}(\.\d{1,2})?(?!\d+))\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern074();

    // Japanese variety shows with leading date
    [GeneratedRegex(@"^(?<airyear>\d{2})(?<airmonth>[0-1][0-9])(?<airday>[0-3][0-9])(?![-_. ]+[0-3][0-9])[-_. ](?<title>.+?)[-_. ](?:Season[-_. ]?(?<season>\d{1,2})[-_. ])?(?:ep|#)(?<episode>\d{2,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern075();

    // Season only releases followed by year
    [GeneratedRegex(@"^(?<title>.+?)[-_. ]+?(?:S|Season|Saison|Series|Stagione)[-_. ]?(?<season>\d{1,2}(?=[-_. ]\d{4}[-_. ]+))(?<extras>EXTRAS|SUBPACK)?(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern076();

    // Season only releases
    [GeneratedRegex(@"^(?<title>.+?)[-_. ]+?(?:S|Season|Saison|Series|Stagione)[-_. ]?(?<season>\d{1,2}(?![-_. ]?\d+))(?:[-_. ]|$)+(?<extras>EXTRAS|SUBPACK)?(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern077();

    // 4 digit season only releases
    [GeneratedRegex(@"^(?<title>.+?)[-_. ]+?(?:S|Season|Saison|Series|Stagione)[-_. ]?(?<season>\d{4}(?![-_. ]?\d+))(\W+|_|$)(?<extras>EXTRAS|SUBPACK)?(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern078();

    // Spanish tracker releases
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_. ]+?Temporada.+?|\[.+?\])\[Cap)(?:[-_. ]+(?<season>(?<!\d+)\d{1,2})(?<episode>(?<!e|x)(?:[1-9][0-9]|[0][1-9])))+(?:\])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern079();

    // Supports 103/113 naming
    [GeneratedRegex(@"^(?<title>.+?)?(?:(?:[_.-](?<![()\[!]))+(?<season>(?<!\d+)[1-9])(?<episode>[1-9][0-9]|[0][1-9])(?![a-z]|\d+))+(?:[_.]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern080();

    // 4 digit episode number
    // Episodes without a title, Single (S01E05, 1x05) AND Multi (S01E04E05, 1x04x05, etc)
    [GeneratedRegex(@"^(?:S?(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:(?:\-|[ex]|\W[ex]|_){1,2}(?<episode>\d{4}(?!\d+|i|p)))+)(\W+|_|$)(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern081();

    // 4 digit episode number
    // Episodes with a title, Single episodes (S01E05, 1x05, etc) & Multi-episode (S01E05E06, S01E05-06, S01E05 E06, etc)
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_\W](?<![()\[!]|\d{1,2}-))+S?(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:(?:\-|[ex]|\W[ex]|_){1,2}(?<episode>\d{4}(?!\d+|i|p)))+)\W?(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern082();

    // Turkish tracker releases (01 BLM, 3. Blm, 04.Bolum, etc)
    [GeneratedRegex(@"^(?<title>.+?)[_. ](?<absoluteepisode>\d{1,4})(?:[_. ]+)(?:BLM|B[oö]l[uü]m)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern083();

    // Episodes with airdate (04.28.2018)
    [GeneratedRegex(@"^(?<title>.+?)?\W*(?<ambiguousairmonth>[0-1][0-9])[-_. ]+(?<ambiguousairday>[0-3][0-9])[-_. ]+(?<airyear>\d{4})(?!\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern084();

    // Episodes with airdate (28.04.2018)
    [GeneratedRegex(@"^(?<title>.+?)?\W*(?<ambiguousairday>[0-3][0-9])[-_. ]+(?<ambiguousairmonth>[0-1][0-9])[-_. ]+(?<airyear>\d{4})(?!\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern085();

    // Episodes with airdate (20180428)
    [GeneratedRegex(@"^(?<title>.+?)?\W*(?<!\d+)(?<airyear>\d{4})(?<airmonth>[0-1][0-9])(?<airday>[0-3][0-9])(?!\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern086();

    // Anime - Title Absolute Episode Number (E195 or E1206)
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)?(?<title>.+?)(?:(?:_|-|\s|\.)+(?:e|ep)(?<absoluteepisode>(\d{3}|\d{4})(\.\d{1,2})?))+[-_. ].*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern087();

    // Daily episodes that use short month format instead of number
    [GeneratedRegex(@"^(?<title>.+?)[-_. ]+(?<airday>[1-2]\d|3[01]|[1-9])(?:th|st|rd)[-_. ](?<shortairmonth>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[-_. ](?<airyear>19[4-9]\d|20\d\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern088();

    // Supports 1103/1113 naming
    [GeneratedRegex(@"^(?<title>.+?)?(?:(?:[-_. ](?<![()\[!]))*(?<!\d{1,2}-)(?<season>(?<!\d+|\(|\[|e|x)\d{2})(?<episode>(?<!e|x)(?:[1-9][0-9]|[0][1-9])(?!p|i|\d+|\)|\]|\W\d+|\W(?:e|ep|x)\d+)))+([-_. ]+|$)(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern089();

    // Dutch/Flemish release titles
    [GeneratedRegex(@"^(?<title>.+?)[-_. ](?:Se\.(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?!\d+))(?:(?:[-_ ]?afl\.)(?<episode>\d{1,3}(?!\d+))(?:(?:[-]|[-_ ]en[-_ ])(?<episode>\d{1,3}(?!\d+)))*))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern090();

    // Episodes with single digit episode number (S01E1, S01E5E6, etc)
    [GeneratedRegex(@"^(?<title>.*?)(?:(?:[-_\W](?<![()\[!]))+S?(?<season>(?<!\d+)\d{1,2}(?!\d+))(?:(?:\-|[ex]){1,2}(?<episode>\d{1}))+)+(\W+|_|$)(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern091();

    // iTunes Season 1\05 Title (Quality).ext
    [GeneratedRegex(@"^(?:Season(?:_|-|\s|\.)(?<season>(?<!\d+)\d{1,2}(?!\d+)))(?:_|-|\s|\.)(?<episode>(?<!\d+)\d{1,2}(?!\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern092();

    // iTunes 1-05 Title (Quality).ext
    [GeneratedRegex(@"^(?:(?<season>(?<!\d+)(?:\d{1,2})(?!\d+))(?:-(?<episode>\d{2,3}(?!\d+))))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern093();

    // Mini-Series, treated as season 1, episodes are labelled as Part01, Part 01, Part.1
    [GeneratedRegex(@"^(?<title>.+?)(?:\W+(?:(?:(?<!\()Part\W?|(?<!\d+\W+)e)(?<episode>\d{1,2}(?!\d+|\))))+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern094();

    // Mini-Series, treated as season 1, episodes are labelled as Part One/Two/Three/...Nine, Part.One, Part_One
    [GeneratedRegex(@"^(?<title>.+?)(?:\W+(?:Part[-._ ](?<episode>One|Two|Three|Four|Five|Six|Seven|Eight|Nine)(?>[-._ ])))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern095();

    // Anime Range - Title Absolute Episode Number (ep01-12)
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)?(?<title>.+?)(?:_|\s|\.)+(?:e|ep)(?<absoluteepisode>\d{2,3}(\.\d{1,2})?)-(?<absoluteepisode>(?<!\d+)\d{1,2}(\.\d{1,2})?(?!\d+|-)).*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern096();

    // Anime - Title Absolute Episode Number (e66)
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)?(?<title>.+?)(?:(?:_|-|\s|\.)+(?:e|ep)(?<absoluteepisode>\d{2,4}(\.\d{1,2})?))+[-_. ].*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern097();

    // Anime - Title Episode Absolute Episode Number (Series Title Episode 01)
    [GeneratedRegex(@"^(?<title>.+?)[-_. ](?:Episode)(?:[-_. ]+(?<absoluteepisode>(?<!\d+)\d{2,3}(\.\d{1,2})?(?!\d+)))+.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern098();

    // Anime Range - Title Absolute Episode Number (1 or 2 digit absolute episode numbers in a range, 1-10)
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)?(?<title>.+?)[_. ]+(?<absoluteepisode>(?<!\d+)\d{1,2}(\.\d{1,2})?(?!\d+))-(?<absoluteepisode>(?<!\d+)\d{1,2}(\.\d{1,2})?(?!\d+|-)).*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern099();

    // Anime - Title Episode/Episodio Absolute Episode Number
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)?(?<title>.+?)[-_. ]+(?:Episode|Episodio)(?:[-_. ]+(?<absoluteepisode>(?<!\d+)\d{2,4}(\.\d{1,2})?(?!\d+|[ip])))+.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern100();

    // Anime - Title [Absolute Episode Number] from AniLibriaTV
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)?(?<title>.+?)(?:[-_. ]\[)(?:(?:-?)(?<absoluteepisode>(?<!\d+)\d{2,3}(\.\d{1,2})?(?!\d+|[ip])))+(?:\][-_. ]).*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern101();

    // Simple season releases in the format 'Series (year) - Temporada season'
    [GeneratedRegex(@"^(?<title>.+?)[_. ]-[_. ](?:Temporada)[-_. ]?(?<season>\d{1,2}(?![_. ]?\d+))(?:[_. ]|$)+(?<extras>EXTRAS|SUBPACK)?(?!\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern102();

    // Anime - Title Absolute Episode Number
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)?(?<title>.+?)(?:[-_. ]+(?<absoluteepisode>(?<!\d+)\d{2,4}(\.\d{1,2})?(?!\d+|[ip])))+.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern103();

    // Anime - Title {Absolute Episode Number}
    [GeneratedRegex(@"^(?:\[(?<subgroup>.+?)\][-_. ]?)?(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+(?<absoluteepisode>(?<!\d+)\d{2,3}(\.\d{1,2})?(?!\d+|[ip])))+.*?(?<hash>[(\[][0-9A-F]{8}[)\]])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern104();

    // Extant, terrible multi-episode naming (extant.10708.hdtv-lol.mp4)
    [GeneratedRegex(@"^(?<title>.+?)[-_. ](?<season>[0]?\d?)(?:(?<episode>\d{2}){2}(?!\d+))[-_. ]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern105();

    // Episodes without a title, Single episode numbers (S1E1, 1x1)
    [GeneratedRegex(@"^(?:S?(?<season>(?<!\d+)(?:\d{1,2}|\d{4})(?!\d+))(?:(?:[-_ ]?[ex])(?<episode>\d{1}(?!\d+))))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern106();

    // Anime OVA special
    [GeneratedRegex(@"^\[(?<subgroup>.+?)\][-_. ]?(?<title>.+?)(?:[-_. ]+(?<special>special|ova|ovd|ncop|nced)).*?(?<hash>[(\[][0-9A-F]{8}[)\]])?(?:$|\.mkv)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern107();

    // Episodes with a title, 3 digit season number, Single episodes (S001E05, 1x05, etc) & Multi-episode (S001E05E06, S001E05-06, S001E05 E06, etc)
    [GeneratedRegex(@"^(?<title>.+?)(?:(?:[-_\W](?<![()\[!]))+S?(?<season>(?<!\d+)(?:\d{3})(?!\d+))(?:[ex]|\W[ex]){1,2}(?<episode>\d{2,3}(?!\d+))(?:(?:\-|[ex]|\W[ex]|_){1,2}(?<episode>\d{2,3}(?!\d+)))*)(?:[-_. ]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex Pattern108();
}

internal enum EpisodePatternKind
{
    /// <summary>Season/episode markers that a movie name does not produce.</summary>
    Normal,

    /// <summary>Numbers a movie name can produce too: 103/1103 naming, mini-series parts, "2016x05" style seasons.</summary>
    Ambiguous,

    /// <summary>Season-only packs ("Title.S02"), checked for a lone "S" that is really part of an acronym ("V.H.S.2.2013").</summary>
    SeasonOnly,

    /// <summary>Absolute numbers behind an explicit marker ("E1206", "ep12", "Episode 12", "60. Bölüm") even without an anime sub group.</summary>
    ExplicitAbsolute,
}
