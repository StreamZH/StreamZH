// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;
using System.Text.RegularExpressions;

namespace StreamZH.Library.Renamer;

/// <summary>
/// Title Case and Sentence case for file names. Small words ("of", "the"; German "und", "der" …) stay lower case unless
/// they start or end the title or follow " - " or ":". Words with digits are left alone except episode numbers
/// (s01e02 → S01E02, 1X02 → 1x02); Roman numerals (II, IV) and words already in capitals (FBI) keep their capitals.
/// Letters are changed with invariant rules, so umlauts and accents work (ärger → Ärger).
/// </summary>
public static partial class TitleCaser
{
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> SmallWords = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = Words("a an and as at but by en for from if in into nor of off on onto or per so than the to up upon via vs with yet"),
        ["de"] = Words("der die das des dem den ein eine einer eines einem einen und oder aber von vom zu zum zur im am an auf aus bei mit nach für über unter vor in ins als wie um"),
        ["fr"] = Words("le la les l un une des du de d qu j m n s t et ou à au aux en par pour sur dans avec sans sous chez"),
        ["es"] = Words("el la los las un una unos unas y e o u de del a al en con por para sin sobre"),
        ["it"] = Words("il lo la l i gli le un uno una un e o di d del della dell dei delle a al da in con su per tra fra"),
        ["nl"] = Words("de het een en of van in op met voor aan bij uit te tot om"),
        ["pt"] = Words("o a os as um uma e ou de do da dos das em no na nos nas por para com"),
        ["none"] = Words(string.Empty),
    };

    public static string Apply(string text, CaseMode mode, string language = "en", bool keepCapitals = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        return mode switch
        {
            CaseMode.Lower => text.ToLowerInvariant(),
            CaseMode.Upper => text.ToUpperInvariant(),
            CaseMode.Sentence => Sentence(text, keepCapitals),
            _ => Title(text, language, keepCapitals),
        };
    }

    public static string Title(string text, string language, bool keepCapitals)
    {
        ArgumentNullException.ThrowIfNull(text);
        var small = SmallWords.GetValueOrDefault(language ?? "en") ?? SmallWords["en"];
        var allCaps = IsAllCaps(text);
        var words = WordRegex().Matches(text);
        var builder = new StringBuilder(text);
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var value = word.Value;
            var lower = value.ToLowerInvariant();
            string result;
            if (Special(value) is { } special)
            {
                result = special;
            }
            else if (keepCapitals && !allCaps && value.Length > 1 && IsAllCaps(value))
            {
                result = value;
            }
            else if (i > 0 && i < words.Count - 1 && small.Contains(lower) && !StartsPart(text, word.Index))
            {
                result = lower;
            }
            else if (Elision(lower, small) is { } apostrophe)
            {
                // French and Italian elision: "d'amélie" → "d'Amélie", "l'amour" at the start → "L'Amour".
                var prefix = lower[..apostrophe];
                result = (i == 0 || StartsPart(text, word.Index) ? Capitalize(prefix) : prefix) + lower[apostrophe] + Capitalize(lower[(apostrophe + 1)..]);
            }
            else
            {
                result = Capitalize(lower);
            }

            builder.Remove(word.Index, word.Length).Insert(word.Index, result);
        }

        return builder.ToString();
    }

    public static string Sentence(string text, bool keepCapitals)
    {
        ArgumentNullException.ThrowIfNull(text);
        var allCaps = IsAllCaps(text);
        var words = WordRegex().Matches(text);
        var builder = new StringBuilder(text);
        var first = true;
        foreach (Match word in words)
        {
            var value = word.Value;
            string result;
            if (Special(value) is { } special)
            {
                result = special;
            }
            else if (keepCapitals && !allCaps && value.Length > 1 && IsAllCaps(value))
            {
                result = value;
            }
            else
            {
                result = first ? Capitalize(value.ToLowerInvariant()) : value.ToLowerInvariant();
            }

            if (value.Any(char.IsLetter))
            {
                first = false;
            }

            builder.Remove(word.Index, word.Length).Insert(word.Index, result);
        }

        return builder.ToString();
    }

    /// <summary>Episode numbers, Roman numerals and words with digits; null for normal words.</summary>
    private static string? Special(string word)
    {
        if (EpisodeRegex().IsMatch(word))
        {
            return word.ToUpperInvariant();
        }

        if (CrossEpisodeRegex().IsMatch(word))
        {
            return word.ToLowerInvariant();
        }

        if (word.Any(char.IsDigit))
        {
            return word;
        }

        return RomanRegex().IsMatch(word) ? word.ToUpperInvariant() : null;
    }

    /// <summary>Index of the apostrophe after an elided small word ("d'", "l'", "dell'"), or null.</summary>
    private static int? Elision(string word, IReadOnlySet<string> small)
    {
        var index = word.IndexOfAny(['\'', '’']);
        return index is > 0 and <= 4 && index < word.Length - 1 && small.Contains(word[..index]) ? index : null;
    }

    /// <summary>After " - ", ": " or an opening bracket a new part starts, so a small word is capitalized there.</summary>
    private static bool StartsPart(string text, int index)
    {
        var before = text[..index].TrimEnd(' ', '.', '_');
        return before.Length == 0 || before[^1] is '-' or ':' or '(' or '[' or '{' or '!' or '?';
    }

    private static string Capitalize(string word)
    {
        if (word.Length == 0)
        {
            return word;
        }

        // "o'brien" → "O'brien", "l'amour" → "L'amour": only the first letter.
        var index = 0;
        while (index < word.Length && !char.IsLetter(word[index]))
        {
            index++;
        }

        return index >= word.Length ? word : word[..index] + char.ToUpperInvariant(word[index]) + word[(index + 1)..];
    }

    private static bool IsAllCaps(string text) => text.Any(char.IsLetter) && text.Where(char.IsLetter).All(char.IsUpper);

    private static HashSet<string> Words(string list) =>
        new(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    // Letters, digits and apostrophes inside a word; dots, underscores, dashes and brackets separate words.
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^s\d{1,4}(?:e\d{1,4})+$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"^\d{1,2}x\d{2,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex CrossEpisodeRegex();

    // Only I, V and X numerals of two or more letters (II … XXXIX); "mix" or "dim" stay words.
    [GeneratedRegex(@"^(?=[ivx]{2,}$)x{0,3}(?:ix|iv|v?i{0,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex RomanRegex();
}
