// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) Sonarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StreamZH.Core.Decisions;

/// <summary>A preferred term and its score: matching releases rank higher (positive) or lower (negative).</summary>
public sealed record PreferredTerm(string Term, int Score);

/// <summary>
/// Sonarr v3 release profile: terms a release must contain (at least one), must not contain (none), and preferred
/// terms with a score. Applies to all indexers and quality profiles, or only the selected ones (empty = all).
/// A term is plain text (case-insensitive) or a regular expression written as /pattern/ or /pattern/i.
/// </summary>
public sealed record ReleaseProfile
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>"Must contain": the release needs at least one of these terms.</summary>
    public IReadOnlyList<string> Required { get; init; } = [];

    /// <summary>"Must not contain": the release is rejected when it contains any of these terms.</summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];

    public IReadOnlyList<PreferredTerm> Preferred { get; init; } = [];

    /// <summary>Indexers this profile applies to; empty = all.</summary>
    public IReadOnlyList<int> IndexerIds { get; init; } = [];

    /// <summary>Quality profiles this profile applies to; empty = all.</summary>
    public IReadOnlyList<int> QualityProfileIds { get; init; } = [];

    /// <summary>
    /// Enabled and meant for this indexer and quality profile. An indexer id of 0 (no indexer, e.g. the release test
    /// page or a file on disk) only matches profiles for all indexers.
    /// </summary>
    public bool AppliesTo(int indexerId, int qualityProfileId) =>
        Enabled
        && (IndexerIds.Count == 0 || IndexerIds.Contains(indexerId))
        && (QualityProfileIds.Count == 0 || QualityProfileIds.Contains(qualityProfileId));
}

/// <summary>
/// Term matching like Sonarr's TermMatcherService: plain terms match case-insensitively anywhere in the name,
/// "/pattern/flags" is a regular expression (flag i = ignore case). Regexes run with a timeout; a timeout is "no match".
/// </summary>
public static class ReleaseTerms
{
    public const int MaxTermLength = 200;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<string, Regex?> Cache = new(StringComparer.Ordinal);

    /// <summary>True for "/pattern/" and "/pattern/flags".</summary>
    public static bool IsRegex(string term) =>
        term.Length >= 2 && term[0] == '/' && term.LastIndexOf('/') > 0;

    /// <summary>Null when the term can be used, otherwise a short reason (empty, too long, invalid regex).</summary>
    public static string? Validate(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return "Empty terms are not allowed.";
        }

        if (term.Length > MaxTermLength)
        {
            return $"Terms can have at most {MaxTermLength} characters.";
        }

        if (!IsRegex(term))
        {
            return null;
        }

        var (pattern, flags) = Split(term);
        if (pattern.Length == 0)
        {
            return $"'{term}' is an empty regular expression.";
        }

        if (flags.Any(f => f != 'i'))
        {
            return $"'{term}': only the flag i (ignore case) is supported.";
        }

        try
        {
            _ = new Regex(pattern, Options(flags), Timeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"'{term}' is not a valid regular expression: {ex.Message}";
        }
    }

    public static bool Matches(string term, string title)
    {
        ArgumentNullException.ThrowIfNull(term);
        ArgumentNullException.ThrowIfNull(title);
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        if (!IsRegex(term))
        {
            return title.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (Cache.GetOrAdd(term, Compile) is not { } regex)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(title);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Regex? Compile(string term)
    {
        var (pattern, flags) = Split(term);
        try
        {
            return pattern.Length == 0 ? null : new Regex(pattern, Options(flags), Timeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static (string Pattern, string Flags) Split(string term)
    {
        var end = term.LastIndexOf('/');
        return (term[1..end], term[(end + 1)..]);
    }

    private static RegexOptions Options(string flags) =>
        RegexOptions.CultureInvariant | (flags.Contains('i', StringComparison.Ordinal) ? RegexOptions.IgnoreCase : RegexOptions.None);

    /// <summary>"+10", "-5", "0".</summary>
    public static string FormatScore(int score) =>
        score > 0 ? string.Create(CultureInfo.InvariantCulture, $"+{score}") : score.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What the release profiles say about one release name.</summary>
/// <param name="Profile">The profile.</param>
/// <param name="MissingRequired">The profile has "must contain" terms and none of them is in the name.</param>
/// <param name="RequiredMatched">"Must contain" terms found in the name.</param>
/// <param name="IgnoredMatched">"Must not contain" terms found in the name.</param>
/// <param name="PreferredMatched">Preferred terms found in the name, with their scores.</param>
public sealed record ReleaseProfileMatch(
    ReleaseProfile Profile,
    bool MissingRequired,
    IReadOnlyList<string> RequiredMatched,
    IReadOnlyList<string> IgnoredMatched,
    IReadOnlyList<PreferredTerm> PreferredMatched)
{
    public bool Rejects => MissingRequired || IgnoredMatched.Count > 0;

    public int Score => PreferredMatched.Sum(p => p.Score);
}

/// <summary>Release profiles applied to a release name (rejection, preferred score, explanation for the release test).</summary>
public static class ReleaseProfileRules
{
    /// <summary>Every applicable profile with what matched; profiles where nothing matched and nothing is missing are left out.</summary>
    public static IReadOnlyList<ReleaseProfileMatch> Explain(ReleaseRules rules, int indexerId, int qualityProfileId, string title)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(title);
        var matches = new List<ReleaseProfileMatch>();
        foreach (var profile in rules.ReleaseProfiles.Where(p => p.AppliesTo(indexerId, qualityProfileId)))
        {
            var required = profile.Required.Where(t => ReleaseTerms.Matches(t, title)).ToList();
            var ignored = profile.Ignored.Where(t => ReleaseTerms.Matches(t, title)).ToList();
            var preferred = profile.Preferred.Where(p => ReleaseTerms.Matches(p.Term, title)).ToList();
            var missing = profile.Required.Count > 0 && required.Count == 0;

            if (missing || required.Count > 0 || ignored.Count > 0 || preferred.Count > 0)
            {
                matches.Add(new ReleaseProfileMatch(profile, missing, required, ignored, preferred));
            }
        }

        return matches;
    }

    /// <summary>Sum of the scores of all preferred terms in the name (Sonarr adds them up over all profiles).</summary>
    public static int Score(ReleaseRules rules, int indexerId, int qualityProfileId, string title)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.ReleaseProfiles.Count == 0 || string.IsNullOrEmpty(title))
        {
            return 0;
        }

        return rules.ReleaseProfiles
            .Where(p => p.Preferred.Count > 0 && p.AppliesTo(indexerId, qualityProfileId))
            .SelectMany(p => p.Preferred)
            .Where(p => ReleaseTerms.Matches(p.Term, title))
            .Sum(p => p.Score);
    }

    /// <summary>True when an applicable profile has preferred terms (only then a score can make an upgrade).</summary>
    public static bool HasPreferred(ReleaseRules rules, int indexerId, int qualityProfileId) =>
        rules.ReleaseProfiles.Any(p => p.Preferred.Count > 0 && p.AppliesTo(indexerId, qualityProfileId));
}
