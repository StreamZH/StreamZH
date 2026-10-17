// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamZH.Parsing.Releases;

/// <summary>
/// Builds the "clean title" used to match release names against library titles:
/// lower-case, no punctuation or whitespace, no "a/an/the/and/or/of" inside the title, German umlauts
/// spelled out (ä → ae) and diacritics removed. Port of Radarr's CleanMovieTitle combined with Sonarr's
/// CleanSeriesTitle ("3%" → "3percent", "Ríkið" → "rikid").
/// </summary>
public static partial class TitleNormalizer
{
    private static readonly Dictionary<char, string> SpecialLetters = new()
    {
        ['ä'] = "ae",
        ['ö'] = "oe",
        ['ü'] = "ue",
        ['ß'] = "ss",
        ['æ'] = "ae",
        ['œ'] = "oe",
        ['ø'] = "o",
        ['đ'] = "d",
        ['ð'] = "d",
        ['þ'] = "th",
        ['ł'] = "l",
        ['ı'] = "i",
    };

    /// <summary>"The Lord of the Rings: The Return of the King" → "thelordringsreturnking".</summary>
    public static string Clean(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // A title that is only a number ("1917", "24") is kept as it is.
        if (long.TryParse(title, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return title;
        }

        try
        {
            title = PercentRegex().Replace(title, "percent");
            title = NormalizeRegex().Replace(title, string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            title = NonWordRegex().Replace(title, string.Empty);
        }

        return RemoveDiacritics(ReplaceSpecialLetters(title.ToLowerInvariant()));
    }

    /// <summary>Removes accents: "Amélie" → "Amelie". Letters without a decomposition (ß, ø, ð …) are left alone.</summary>
    internal static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string ReplaceSpecialLetters(string text)
    {
        StringBuilder? builder = null;

        for (var i = 0; i < text.Length; i++)
        {
            if (SpecialLetters.TryGetValue(text[i], out var replacement))
            {
                builder ??= new StringBuilder(text, 0, i, text.Length + 8);
                builder.Append(replacement);
            }
            else
            {
                builder?.Append(text[i]);
            }
        }

        return builder?.ToString() ?? text;
    }

    // Radarr: drops the joining words (but not at the start or end of the title, and not "a" inside acronyms like "A.N.K.L.E"),
    // then every non-word character.
    [GeneratedRegex(@"((?:\b|_)(?<!^|[^a-zA-Z0-9_']\w[^a-zA-Z0-9_'])([aà](?!$|[^a-zA-Z0-9_']\w[^a-zA-Z0-9_'])|an|the|and|or|of)(?!$)(?:\b|_))|\W|_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeouts.Milliseconds)]
    private static partial Regex NormalizeRegex();

    // Sonarr: "3%" → "3percent" so "3%" and "3 Percent" match.
    [GeneratedRegex(@"(?<=\b\d+)%", RegexOptions.None, RegexTimeouts.Milliseconds)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"\W|_", RegexOptions.None)]
    private static partial Regex NonWordRegex();
}
