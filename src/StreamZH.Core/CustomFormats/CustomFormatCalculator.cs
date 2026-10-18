// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;

namespace StreamZH.Core.CustomFormats;

/// <summary>Whether one condition of a format applies to the input (negate already applied).</summary>
public sealed record SpecificationResult(CustomFormatSpecification Specification, bool Matched);

/// <summary>One format checked against one input, with the result of every condition (for the test page).</summary>
public sealed record CustomFormatEvaluation(CustomFormat Format, bool Matched, IReadOnlyList<SpecificationResult> Specifications);

/// <summary>The formats that match a release or file (ordered by name) and their summed score in a quality profile.</summary>
public sealed record CustomFormatResult(IReadOnlyList<CustomFormat> Formats, int Score)
{
    public static CustomFormatResult None { get; } = new([], 0);
}

/// <summary>
/// Custom format matching ported from Radarr's and Sonarr's CustomFormatCalculationService: the conditions of a format are
/// grouped by type; the format matches when, in every group, no required condition fails and at least one condition
/// matches. Negate inverts a single condition. Regular expressions are case-insensitive like Radarr's, compiled once and
/// cached, and stop after <see cref="RegexTimeout"/> (a runaway pattern counts as "no match").
/// </summary>
public static class CustomFormatCalculator
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    private const long Gigabyte = 1024L * 1024 * 1024;
    private const int MaxCachedRegexes = 20_000;

    private static readonly ConcurrentDictionary<string, Regex?> Regexes = new(StringComparer.Ordinal);

    /// <summary>The formats of the input's media type that match, ordered by name (like Radarr).</summary>
    public static IReadOnlyList<CustomFormat> Matching(IEnumerable<CustomFormat> formats, CustomFormatInput input)
    {
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(input);
        return
        [
            .. formats
                .Where(f => f.MediaType == input.MediaType && Evaluate(f, input).Matched)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Id),
        ];
    }

    /// <summary>Matching formats and their score in <paramref name="profile"/> (0 without a profile).</summary>
    public static CustomFormatResult Calculate(QualityProfile? profile, IEnumerable<CustomFormat> formats, CustomFormatInput input)
    {
        var matched = Matching(formats, input);
        return new CustomFormatResult(matched, profile is null ? 0 : Score(profile, matched));
    }

    /// <summary>Sum of the profile's scores for the given formats (Radarr's CalculateCustomFormatScore).</summary>
    public static int Score(QualityProfile profile, IEnumerable<CustomFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(formats);
        var ids = formats.Select(f => f.Id).ToHashSet();
        if (ids.Count == 0)
        {
            return 0;
        }

        var sum = profile.FormatItems.Where(i => ids.Contains(i.CustomFormatId)).Sum(i => (long)i.Score);
        return (int)Math.Clamp(sum, int.MinValue, int.MaxValue);
    }

    /// <summary>The profile's score for one format (0 when the profile does not score it).</summary>
    public static int ScoreOf(QualityProfile profile, int customFormatId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.FormatItems.Where(i => i.CustomFormatId == customFormatId).Sum(i => i.Score);
    }

    /// <summary>Checks one format, keeping the result of every condition.</summary>
    public static CustomFormatEvaluation Evaluate(CustomFormat format, CustomFormatInput input)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(input);

        var results = format.Specifications.Select(s => new SpecificationResult(s, IsSatisfied(s, input))).ToList();

        // Radarr's SpecificationMatchesGroup.DidMatch, per condition type.
        var matched = results
            .GroupBy(r => r.Specification.Implementation, StringComparer.OrdinalIgnoreCase)
            .All(g => !(g.Any(r => r.Specification.Required && !r.Matched) || g.All(r => !r.Matched)));

        return new CustomFormatEvaluation(format, matched, results);
    }

    /// <summary>Whether one condition applies, negate included. Unknown condition types never apply.</summary>
    public static bool IsSatisfied(CustomFormatSpecification specification, CustomFormatInput input)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(input);

        var match = Match(specification, input);
        if (match is null)
        {
            return false;
        }

        return specification.Negate ? !match.Value : match.Value;
    }

    /// <summary>True when the pattern is a valid regular expression (for validation).</summary>
    public static bool IsValidRegex(string? pattern) => !string.IsNullOrEmpty(pattern) && RegexFor(pattern) is not null;

    private static bool? Match(CustomFormatSpecification s, CustomFormatInput input)
    {
        var quality = input.Quality.Quality;
        switch (s.Implementation)
        {
            case CustomFormatImplementations.ReleaseTitle:
                return MatchRegex(s, input.ReleaseTitle) || MatchRegex(s, input.FileName);
            case CustomFormatImplementations.ReleaseGroup:
                return MatchRegex(s, input.ReleaseGroup);
            case CustomFormatImplementations.Edition:
                return MatchRegex(s, input.Edition);
            case CustomFormatImplementations.Language:
                return MatchLanguage(s, input);
            case CustomFormatImplementations.Source:
                return CustomFormatFields.Int(s, CustomFormatFields.Value) == CustomFormatValues.Source(input.MediaType, quality);
            case CustomFormatImplementations.Resolution:
                return CustomFormatFields.Int(s, CustomFormatFields.Value) == CustomFormatValues.Resolution(input.MediaType, quality);
            case CustomFormatImplementations.QualityModifier:
                return CustomFormatFields.Int(s, CustomFormatFields.Value) == CustomFormatValues.Modifier(quality);
            case CustomFormatImplementations.ReleaseType:
                return CustomFormatFields.Int(s, CustomFormatFields.Value) == (int)input.ReleaseType;
            case CustomFormatImplementations.IndexerFlag:
                return CustomFormatFields.Int(s, CustomFormatFields.Value) is { } flag && (input.IndexerFlags & flag) == flag;
            case CustomFormatImplementations.Size:
            {
                var min = CustomFormatFields.Double(s, CustomFormatFields.Min) ?? 0;
                var max = CustomFormatFields.Double(s, CustomFormatFields.Max) ?? 0;
                return input.SizeBytes > min * Gigabyte && input.SizeBytes <= max * Gigabyte;
            }

            case CustomFormatImplementations.Year:
            {
                var min = CustomFormatFields.Int(s, CustomFormatFields.Min) ?? 0;
                var max = CustomFormatFields.Int(s, CustomFormatFields.Max) ?? 0;
                return input.Year > 0 && input.Year >= min && input.Year <= max;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Radarr's LanguageSpecification: -2 means the original language of the movie or series; "except language" matches
    /// when any other language is present. When the original language is not known, -2 means English, the same fallback
    /// the decision engine uses for untagged and "DL" releases (<see cref="Decisions.LanguageRules.Normalize"/>).
    /// </summary>
    private static bool MatchLanguage(CustomFormatSpecification s, CustomFormatInput input)
    {
        var value = CustomFormatFields.Int(s, CustomFormatFields.Value) ?? 0;
        var original = input.OriginalLanguage.Id > 0 ? input.OriginalLanguage : Language.English;
        var compared = value == Language.Original.Id
            ? original.Id
            : CustomFormatValues.LanguageId(input.MediaType, value) ?? int.MinValue;

        return CustomFormatFields.Bool(s, CustomFormatFields.ExceptLanguage)
            ? input.Languages.Any(l => l.Id != compared)
            : input.Languages.Any(l => l.Id == compared);
    }

    private static bool MatchRegex(CustomFormatSpecification s, string? text)
    {
        if (text is null || CustomFormatFields.String(s, CustomFormatFields.Value) is not { Length: > 0 } pattern || RegexFor(pattern) is not { } regex)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Regex? RegexFor(string pattern)
    {
        if (Regexes.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        if (Regexes.Count > MaxCachedRegexes)
        {
            Regexes.Clear();
        }

        Regex? regex;
        try
        {
            // Not RegexOptions.Compiled: a few thousand TRaSH patterns would cost seconds to compile; the interpreter is fast enough.
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            regex = null;
        }

        return Regexes.GetOrAdd(pattern, regex);
    }
}
