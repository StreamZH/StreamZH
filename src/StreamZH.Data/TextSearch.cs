// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;

namespace StreamZH.Data;

/// <summary>
/// The search box of list pages: every word must appear somewhere in the column (case-insensitive LIKE, so
/// "dune part two" finds "Dune.Part.Two.2024.2160p"). % and _ are matched literally.
/// </summary>
public static class TextSearch
{
    /// <summary>More words are ignored (keeps the SQL small).</summary>
    public const int MaxTerms = 8;

    /// <summary>Longer input is cut (a release title is far shorter).</summary>
    public const int MaxLength = 200;

    /// <summary>The words of a search (empty when there is nothing to search for).</summary>
    public static IReadOnlyList<string> Terms(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return [];
        }

        var text = search.Trim();
        if (text.Length > MaxLength)
        {
            text = text[..MaxLength];
        }

        return [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTerms)];
    }

    /// <summary>A LIKE pattern that finds <paramref name="term"/> anywhere, with \ as the escape character.</summary>
    public static string ContainsPattern(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return "%" + term.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
    }

    /// <summary>Adds one condition per word to <paramref name="conditions"/> and its parameter to <paramref name="parameters"/>.</summary>
    public static void Where(string column, string? search, List<string> conditions, DynamicParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(parameters);
        var terms = Terms(search);
        for (var i = 0; i < terms.Count; i++)
        {
            conditions.Add($@"{column} LIKE @search{i} ESCAPE '\'");
            parameters.Add($"search{i}", ContainsPattern(terms[i]));
        }
    }
}
