// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamZH.Library.Renamer;

/// <summary>What the rules know about the file or folder being renamed (the original, not the name being built).</summary>
/// <param name="Name">The original name with extension.</param>
/// <param name="Folder">Name of the folder it is in.</param>
/// <param name="Index">Position in the list (0 = first), for the counter.</param>
/// <param name="IndexInFolder">Position among the entries of the same folder.</param>
public sealed record RenameItem(string Name, bool IsFolder, string Folder, DateTime Modified, long Size, int Index, int IndexInFolder);

/// <summary>
/// The rules as pure functions: name in, name out. The media rule needs the library and TMDB and is applied by
/// <see cref="MediaNamer"/>; everything else is here.
/// </summary>
public static partial class RuleEngine
{
    public const int MaxRules = 50;

    /// <summary>Splits "Show.S01E01.mkv" into "Show.S01E01" and ".mkv". Folders and names without a dot have no extension.</summary>
    public static (string Stem, string Extension) Split(string name, bool isFolder)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (isFolder)
        {
            return (name, string.Empty);
        }

        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? (name, string.Empty) : (name[..dot], name[dot..]);
    }

    /// <summary>Applies one rule (not the media rule) to the current name.</summary>
    /// <exception cref="RuleException">The rule cannot be applied to this name.</exception>
    public static string Apply(RenameRule rule, string name, RenameItem item)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(item);
        return rule switch
        {
            ReplaceRule r => Scoped(name, item.IsFolder, r.Scope, part => Replace(part, r)),
            RemoveRule r => Scoped(name, item.IsFolder, r.Scope, part => Remove(part, r)),
            InsertRule r => Scoped(name, item.IsFolder, r.Scope, part => Insert(part, r, item)),
            CaseRule r => Scoped(name, item.IsFolder, r.Scope, part => TitleCaser.Apply(part, r.Mode, r.Language, r.KeepCapitals)),
            CounterRule r => Scoped(name, item.IsFolder, RuleScope.Name, part => Counter(part, r, item)),
            ExtensionRule r => Extension(name, item.IsFolder, r),
            TrimRule r => Scoped(name, item.IsFolder, r.Scope, part => Trim(part, r)),
            _ => name,
        };
    }

    private static string Scoped(string name, bool isFolder, RuleScope scope, Func<string, string> change)
    {
        if (scope == RuleScope.FullName || isFolder)
        {
            return change(name);
        }

        var (stem, extension) = Split(name, isFolder);
        return change(stem) + extension;
    }

    // ------------------------------------------------------------------ replace

    public static string Replace(string text, ReplaceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Find.Length == 0)
        {
            return text;
        }

        if (rule.UseRegex)
        {
            var regex = SafeRegex.Create(rule.Find, !rule.CaseSensitive);
            switch (rule.Occurrence)
            {
                case ReplaceOccurrence.First:
                    return SafeRegex.Replace(regex, text, rule.ReplaceWith, 1);
                case ReplaceOccurrence.Last:
                    var last = SafeRegex.LastMatch(regex, text);
                    return last is null ? text : text[..last.Index] + last.Result(rule.ReplaceWith) + text[(last.Index + last.Length)..];
                default:
                    return SafeRegex.Replace(regex, text, rule.ReplaceWith, -1);
            }
        }

        var comparison = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        switch (rule.Occurrence)
        {
            case ReplaceOccurrence.First:
                var first = text.IndexOf(rule.Find, comparison);
                return first < 0 ? text : text[..first] + rule.ReplaceWith + text[(first + rule.Find.Length)..];
            case ReplaceOccurrence.Last:
                var lastIndex = text.LastIndexOf(rule.Find, comparison);
                return lastIndex < 0 ? text : text[..lastIndex] + rule.ReplaceWith + text[(lastIndex + rule.Find.Length)..];
            default:
                return text.Replace(rule.Find, rule.ReplaceWith, comparison);
        }
    }

    // ------------------------------------------------------------------ remove

    public static string Remove(string text, RemoveRule rule)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rule);
        switch (rule.Mode)
        {
            case RemoveMode.Characters:
                {
                    var count = Math.Max(0, rule.Count);
                    var position = Math.Max(1, rule.Position);
                    var start = rule.FromEnd ? text.Length - position - count + 1 : position - 1;
                    var end = start + count;
                    start = Math.Clamp(start, 0, text.Length);
                    end = Math.Clamp(end, 0, text.Length);
                    return end <= start ? text : text[..start] + text[end..];
                }

            case RemoveMode.Between:
                return RemoveBetween(text, rule.StartMarker, rule.EndMarker, rule.IncludeMarkers);

            case RemoveMode.Brackets:
                {
                    var result = text;
                    string before;
                    do
                    {
                        before = result;
                        if (rule.Square)
                        {
                            result = SquareRegex().Replace(result, string.Empty);
                        }

                        if (rule.Round)
                        {
                            result = RoundRegex().Replace(result, string.Empty);
                        }

                        if (rule.Curly)
                        {
                            result = CurlyRegex().Replace(result, string.Empty);
                        }
                    }
                    while (result != before);

                    // What the brackets leave behind: "Show  - 01 " → "Show - 01".
                    return result == text ? text : SpacesRegex().Replace(result, " ").Trim(' ');
                }

            case RemoveMode.Separators:
                return rule.KeepDecimalDots ? SeparatorKeepDecimalsRegex().Replace(text, " ") : SeparatorRegex().Replace(text, " ");

            case RemoveMode.Spaces:
                return SpacesRegex().Replace(text, " ");

            default:
                return text;
        }
    }

    private static string RemoveBetween(string text, string start, string end, bool includeMarkers)
    {
        if (start.Length == 0 || end.Length == 0)
        {
            return text;
        }

        var builder = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf(start, index, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                break;
            }

            var close = text.IndexOf(end, open + start.Length, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                break;
            }

            if (includeMarkers)
            {
                builder.Append(text, index, open - index);
            }
            else
            {
                builder.Append(text, index, open + start.Length - index).Append(end);
            }

            index = close + end.Length;
        }

        builder.Append(text, index, text.Length - index);
        return builder.ToString();
    }

    // ------------------------------------------------------------------ insert

    /// <summary>The tokens of insert texts and folder formats.</summary>
    public static readonly IReadOnlyList<string> Tokens = ["{name}", "{ext}", "{folder}", "{date}", "{time}", "{year}", "{index}"];

    public static string Insert(string text, InsertRule rule, RenameItem item)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rule);
        var value = ExpandTokens(rule.Text, item);
        switch (rule.Where)
        {
            case InsertPosition.Suffix:
                return text + value;
            case InsertPosition.Position:
                var position = Math.Max(1, rule.Position);
                var index = rule.FromEnd ? text.Length - position + 1 : position - 1;
                index = Math.Clamp(index, 0, text.Length);
                return text[..index] + value + text[index..];
            default:
                return value + text;
        }
    }

    /// <summary>Replaces {name}, {ext}, {folder}, {date}, {time}, {year} and {index}; other braces stay as they are.</summary>
    public static string ExpandTokens(string text, RenameItem item)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(item);
        var (stem, extension) = Split(item.Name, item.IsFolder);
        return TokenRegex().Replace(text, m => m.Groups["token"].Value.ToLowerInvariant() switch
        {
            "name" => stem,
            "ext" => extension.TrimStart('.'),
            "folder" => item.Folder,
            "date" => item.Modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => item.Modified.ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            "year" => item.Modified.Year.ToString(CultureInfo.InvariantCulture),
            "index" => (item.Index + 1).ToString(CultureInfo.InvariantCulture),
            _ => m.Value,
        });
    }

    public static bool HasGenericTokens(string text) => TokenRegex().IsMatch(text);

    // ------------------------------------------------------------------ counter

    public static string Counter(string text, CounterRule rule, RenameItem item)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(item);
        var index = rule.PerFolder ? item.IndexInFolder : item.Index;
        var number = (long)rule.Start + ((long)rule.Step * index);
        var padding = Math.Clamp(rule.Padding, 0, 10);
        var digits = Math.Abs(number).ToString(CultureInfo.InvariantCulture).PadLeft(padding, '0');
        var counter = (number < 0 ? "-" : string.Empty) + digits;
        switch (rule.Where)
        {
            case CounterPosition.Replace:
                return counter;
            case CounterPosition.Suffix:
                return text + rule.Separator + counter;
            case CounterPosition.Position:
                var position = Math.Clamp(rule.Position - 1, 0, text.Length);
                return text[..position] + counter + text[position..];
            default:
                return counter + rule.Separator + text;
        }
    }

    // ------------------------------------------------------------------ extension

    public static string Extension(string name, bool isFolder, ExtensionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (isFolder)
        {
            return name;
        }

        var (stem, extension) = Split(name, isFolder);
        return rule.Mode switch
        {
            ExtensionMode.Lower => stem + extension.ToLowerInvariant(),
            ExtensionMode.Upper => stem + extension.ToUpperInvariant(),
            ExtensionMode.Remove => stem,
            ExtensionMode.Change => rule.NewExtension.Trim().TrimStart('.') is { Length: > 0 } changed ? $"{stem}.{changed}" : stem,
            _ => name,
        };
    }

    // ------------------------------------------------------------------ trim

    public static string Trim(string text, TrimRule rule)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rule);
        var characters = rule.Characters.Length == 0 ? [] : rule.Characters.ToCharArray();
        var result = characters.Length == 0
            ? text
            : rule.Where switch
            {
                TrimWhere.Start => text.TrimStart(characters),
                TrimWhere.End => text.TrimEnd(characters),
                _ => text.Trim(characters),
            };

        if (rule.MaxLength is { } max && max > 0 && result.Length > max)
        {
            result = result[..max];
            if (characters.Length > 0)
            {
                result = result.TrimEnd(characters);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ validation

    /// <summary>What is wrong with each rule (by index); empty when all can be used.</summary>
    public static IReadOnlyDictionary<int, string> Validate(IReadOnlyList<RenameRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var errors = new Dictionary<int, string>();
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i] is not { Enabled: true } rule)
            {
                continue;
            }

            if (Check(rule) is { } error)
            {
                errors[i] = error;
            }
        }

        return errors;
    }

    /// <summary>Null when the rule can be used.</summary>
    public static string? Check(RenameRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        switch (rule)
        {
            case ReplaceRule r:
                if (r.Find.Length == 0)
                {
                    return "Enter the text to find.";
                }

                if (r.UseRegex)
                {
                    return SafeRegex.TryCreate(r.Find, !r.CaseSensitive).Error ?? ReplacementError(r);
                }

                return r.Find.Length > SafeRegex.MaxPatternLength ? $"Use at most {SafeRegex.MaxPatternLength} characters." : null;

            case RemoveRule r:
                return r.Mode switch
                {
                    RemoveMode.Characters when r.Count < 1 || r.Count > 1000 => "Remove 1 to 1000 characters.",
                    RemoveMode.Characters when r.Position < 1 || r.Position > 1000 => "The position is 1 to 1000.",
                    RemoveMode.Between when r.StartMarker.Length == 0 || r.EndMarker.Length == 0 => "Enter both markers, e.g. ( and ).",
                    RemoveMode.Brackets when !r.Square && !r.Round && !r.Curly => "Choose at least one kind of bracket.",
                    _ => null,
                };

            case InsertRule r:
                if (r.Text.Length == 0)
                {
                    return "Enter the text to insert.";
                }

                return r.Where == InsertPosition.Position && (r.Position < 1 || r.Position > 1000) ? "The position is 1 to 1000." : InvalidText(r.Text);

            case CaseRule r:
                return TitleCaser.SmallWords.ContainsKey(r.Language) ? null : "Unknown language for the small words.";

            case CounterRule r:
                if (r.Step == 0)
                {
                    return "The step cannot be 0.";
                }

                if (r.Padding is < 0 or > 10)
                {
                    return "Use 0 to 10 digits.";
                }

                if (r.Start is < -1_000_000 or > 1_000_000)
                {
                    return "Start between -1000000 and 1000000.";
                }

                return r.Where == CounterPosition.Position && (r.Position < 1 || r.Position > 1000) ? "The position is 1 to 1000." : InvalidText(r.Separator);

            case ExtensionRule r:
                if (r.Mode != ExtensionMode.Change)
                {
                    return null;
                }

                var extension = r.NewExtension.Trim().TrimStart('.');
                return extension.Length == 0 ? "Enter the new extension, e.g. mkv." : InvalidText(extension);

            case TrimRule r:
                if (r.Characters.Length == 0 && r.MaxLength is null)
                {
                    return "Enter the characters to trim.";
                }

                return r.MaxLength is < 1 or > 255 ? "The maximum length is 1 to 255." : null;

            case MediaRule r:
                return MediaNamer.Check(r);

            default:
                return "Unknown rule.";
        }
    }

    /// <summary>"$3" when the pattern has two groups: the replacement would keep "$3" literally, which nobody wants.</summary>
    private static string? ReplacementError(ReplaceRule rule)
    {
        var regex = SafeRegex.TryCreate(rule.Find, !rule.CaseSensitive).Regex!;
        var numbers = regex.GetGroupNumbers().ToHashSet();
        var names = regex.GetGroupNames().ToHashSet(StringComparer.Ordinal);
        foreach (Match reference in GroupReferenceRegex().Matches(rule.ReplaceWith))
        {
            var group = reference.Groups["group"].Value;
            if (int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                if (!numbers.Contains(number))
                {
                    return $"The replacement uses ${group}, but the pattern has {numbers.Count - 1} group(s).";
                }
            }
            else if (!names.Contains(group))
            {
                return $"The replacement uses ${{{group}}}, but the pattern has no group of that name.";
            }
        }

        return null;
    }

    private static string? InvalidText(string text) =>
        text.IndexOfAny(['/', '\\']) >= 0 ? "Folders cannot be created here; remove / and \\." : null;

    [GeneratedRegex(@"\[[^\[\]]*\]")]
    private static partial Regex SquareRegex();

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex RoundRegex();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex CurlyRegex();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"[._]")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"_|(?<!\d)\.|\.(?!\d)")]
    private static partial Regex SeparatorKeepDecimalsRegex();

    [GeneratedRegex(@"\{(?<token>name|ext|folder|date|time|year|index)\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?<!\$)\$(?:(?<group>\d+)|\{(?<group>[A-Za-z_][A-Za-z0-9_]*|\d+)\})")]
    private static partial Regex GroupReferenceRegex();
}
