// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;

namespace StreamZH.Library.Discord;

/// <summary>Texts, colours and small formatting helpers for the bot's messages.</summary>
public static class DiscordText
{
    /// <summary>StreamZH's accent (salmon red).</summary>
    public const int Accent = 0xF0625A;
    public const int Green = 0x3BA55D;
    public const int Amber = 0xF0B232;
    public const int Red = 0xED4245;
    public const int Grey = 0x80848E;

    public const string PosterBase = "https://image.tmdb.org/t/p/w342";

    public static string? PosterUrl(string? posterPath) =>
        posterPath is { Length: > 1 } path && path[0] == '/' && !path.Contains("..", StringComparison.Ordinal) ? PosterBase + path : null;

    public static string TmdbUrl(MediaType type, int tmdbId) =>
        $"https://www.themoviedb.org/{(type == MediaType.Movie ? "movie" : "tv")}/{tmdbId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>"Title (2024)".</summary>
    public static string Name(string title, int? year) => year is { } y ? $"{title} ({y.ToString(CultureInfo.InvariantCulture)})" : title;

    /// <summary>Escapes Discord markdown so a title cannot format the message.</summary>
    public static string Md(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '#' or '[' or ']' or '<' or '@')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    public static string Cut(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    public static string Status(RequestStatus status) => status switch
    {
        RequestStatus.Pending => "Waiting for approval",
        RequestStatus.Approved => "Approved",
        RequestStatus.Declined => "Declined",
        RequestStatus.Processing => "Being downloaded",
        RequestStatus.PartiallyAvailable => "Partly available",
        RequestStatus.Available => "Available",
        RequestStatus.Failed => "Failed",
        _ => status.ToString(),
    };

    public static int StatusColor(RequestStatus status) => status switch
    {
        RequestStatus.Pending => Amber,
        RequestStatus.Approved or RequestStatus.Processing => Accent,
        RequestStatus.PartiallyAvailable or RequestStatus.Available => Green,
        RequestStatus.Declined or RequestStatus.Failed => Red,
        _ => Grey,
    };

    /// <summary>"All seasons", "Season 2", "Seasons 1, 2, 4"; empty for movies.</summary>
    public static string Seasons(MediaType type, IReadOnlyList<int>? seasons)
    {
        if (type != MediaType.Series)
        {
            return string.Empty;
        }

        if (seasons is null || seasons.Count == 0)
        {
            return "All seasons";
        }

        var sorted = seasons.Order().Select(s => s.ToString(CultureInfo.InvariantCulture));
        return seasons.Count == 1 ? $"Season {seasons[0].ToString(CultureInfo.InvariantCulture)}" : $"Seasons {string.Join(", ", sorted)}";
    }

    /// <summary>The state of a search result in a few words.</summary>
    public static string State(RequestTitleState state) =>
        state.Available ? "Available"
        : state.Requested is { } requested ? Status(requested)
        : state.InLibrary ? "In the library"
        : "Not requested yet";

    /// <summary>Discord timestamp markup: each reader sees it in their own time zone ("2 days ago").</summary>
    public static string Relative(DateTime utc) =>
        $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:R>";
}
