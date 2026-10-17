// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamZH.Library.Renamer;

/// <summary>A rule cannot be applied to one name (e.g. its regular expression took too long on it).</summary>
public sealed class RuleException(string message) : Exception(message);

/// <summary>
/// Regular expressions typed by the user: compiled with a match timeout (so a pattern with catastrophic backtracking
/// cannot hang the server) and with readable error messages.
/// </summary>
public static partial class SafeRegex
{
    public const int MaxPatternLength = 1000;

    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The regex, or the reason it cannot be used.</summary>
    public static (Regex? Regex, string? Error) TryCreate(string pattern, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return (null, "Enter a regular expression.");
        }

        if (pattern.Length > MaxPatternLength)
        {
            return (null, $"Use at most {MaxPatternLength} characters.");
        }

        try
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            return (new Regex(pattern, options, Timeout), null);
        }
        catch (RegexParseException ex)
        {
            return (null, Describe(ex));
        }
        catch (ArgumentException ex)
        {
            return (null, "Invalid regular expression: " + ex.Message);
        }
    }

    public static Regex Create(string pattern, bool ignoreCase) =>
        TryCreate(pattern, ignoreCase) is { Regex: { } regex } ? regex : throw new RuleException(TryCreate(pattern, ignoreCase).Error!);

    /// <summary>"Invalid pattern '(a' at offset 2. Not enough )'s." → "Missing ) (at character 2)."</summary>
    public static string Describe(RegexParseException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var what = ex.Error switch
        {
            RegexParseError.InsufficientClosingParentheses => "A ( is not closed",
            RegexParseError.InsufficientOpeningParentheses => "There is a ) without (",
            RegexParseError.UnterminatedBracket => "A [ is not closed",
            RegexParseError.QuantifierAfterNothing => "*, + or ? has nothing in front of it",
            RegexParseError.NestedQuantifiersNotParenthesized => "Two quantifiers in a row (like a**)",
            RegexParseError.UnescapedEndingBackslash => "The pattern ends with a single \\",
            RegexParseError.ReversedCharacterRange => "A range like [z-a] is reversed",
            RegexParseError.ReversedQuantifierRange => "A range like {5,2} is reversed",
            RegexParseError.UndefinedNumberedReference or RegexParseError.UndefinedNamedReference => "A back reference points to a group that does not exist",
            RegexParseError.InvalidGroupingConstruct => "A group (?…) is written wrong",
            RegexParseError.UnrecognizedEscape => "Unknown escape (use \\\\ for a backslash)",
            _ => "The regular expression is invalid",
        };
        return $"{what} (at character {ex.Offset.ToString(CultureInfo.InvariantCulture)}).";
    }

    /// <summary>Replaces with a timeout turned into a readable rule error.</summary>
    public static string Replace(Regex regex, string input, string replacement, int count)
    {
        ArgumentNullException.ThrowIfNull(regex);
        try
        {
            return count < 0 ? regex.Replace(input, replacement) : regex.Replace(input, replacement, count);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new RuleException("The regular expression took too long on this name. Simplify it.");
        }
    }

    public static Match? LastMatch(Regex regex, string input)
    {
        ArgumentNullException.ThrowIfNull(regex);
        try
        {
            Match? last = null;
            for (var m = regex.Match(input); m.Success; m = m.NextMatch())
            {
                last = m;
            }

            return last;
        }
        catch (RegexMatchTimeoutException)
        {
            throw new RuleException("The regular expression took too long on this name. Simplify it.");
        }
    }

    /// <summary>"/pattern/" or "/pattern/i" → the pattern and whether it ignores case; null for a glob.</summary>
    public static (string Pattern, bool IgnoreCase)? SlashPattern(string text)
    {
        var match = SlashRegex().Match(text.Trim());
        return match.Success ? (match.Groups["pattern"].Value, match.Groups["flags"].Value.Contains('i', StringComparison.Ordinal)) : null;
    }

    /// <summary>A glob ("*.mkv", "Show?S01*", "[ab]*") as regex: whole name, case-insensitive.</summary>
    public static string GlobToRegex(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        var builder = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    builder.Append(".*");
                    break;
                case '?':
                    builder.Append('.');
                    break;
                case '[' when glob.IndexOf(']', i + 1) is var close and > 0:
                    var set = glob[(i + 1)..close];
                    builder.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]) : Regex.Escape(set)).Append(']');
                    i = close;
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.Append('$').ToString();
    }

    [GeneratedRegex(@"^/(?<pattern>.+)/(?<flags>[a-z]*)$", RegexOptions.Singleline)]
    private static partial Regex SlashRegex();
}

/// <summary>An include or exclude pattern of the filter: globs separated by ; or one /regex/.</summary>
public sealed class NamePattern
{
    private readonly Regex[] _patterns;

    private NamePattern(Regex[] patterns) => _patterns = patterns;

    public bool IsEmpty => _patterns.Length == 0;

    /// <summary>The pattern, or the reason it cannot be used.</summary>
    public static (NamePattern? Pattern, string? Error) TryParse(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return (new NamePattern([]), null);
        }

        if (SafeRegex.SlashPattern(value) is { } slash)
        {
            var (regex, error) = SafeRegex.TryCreate(slash.Pattern, slash.IgnoreCase);
            return regex is null ? (null, error) : (new NamePattern([regex]), null);
        }

        var globs = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => g.Contains('*', StringComparison.Ordinal) || g.Contains('?', StringComparison.Ordinal) || g.Contains('[', StringComparison.Ordinal) ? g : $"*{g}*")
            .Select(g => new Regex(SafeRegex.GlobToRegex(g), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, SafeRegex.Timeout))
            .ToArray();
        return (new NamePattern(globs), null);
    }

    public bool Matches(string name)
    {
        try
        {
            return _patterns.Any(p => p.IsMatch(name));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>Sorts names like Explorer and Finder: "Episode 2" before "Episode 10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    j++;
                }

                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var byLength = a.Length.CompareTo(b.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                var byDigits = string.CompareOrdinal(a, b);
                if (byDigits != 0)
                {
                    return byDigits;
                }

                continue;
            }

            var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (c != 0)
            {
                return c;
            }

            i++;
            j++;
        }

        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
