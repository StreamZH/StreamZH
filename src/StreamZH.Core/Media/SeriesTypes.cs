// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Media;

/// <summary>API names of <see cref="SeriesType"/> ("standard", "daily", "anime", like Sonarr) and the guess for new series.</summary>
public static class SeriesTypes
{
    /// <summary>TMDB TV genre ids.</summary>
    public const int AnimationGenre = 16;
    public const int NewsGenre = 10763;
    public const int TalkGenre = 10767;

    public static string ToApi(SeriesType type) => type switch
    {
        SeriesType.Daily => "daily",
        SeriesType.Anime => "anime",
        _ => "standard",
    };

    /// <summary>"standard", "daily" or "anime" (any case); false for anything else.</summary>
    public static bool TryParse(string? value, out SeriesType type)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "standard":
                type = SeriesType.Standard;
                return true;
            case "daily":
                type = SeriesType.Daily;
                return true;
            case "anime":
                type = SeriesType.Anime;
                return true;
            default:
                type = SeriesType.Standard;
                return false;
        }
    }

    /// <summary>
    /// The series type a new series most likely has: Anime for Japanese animation (TMDB genre Animation and origin country
    /// Japan, or original language Japanese when TMDB names no country), Daily for talk shows and news, else Standard.
    /// Genre names are a fallback for callers that only know the (English or German) names.
    /// </summary>
    public static SeriesType Suggest(
        IEnumerable<int>? genreIds, IEnumerable<string>? genreNames, IEnumerable<string>? originCountries, string? originalLanguage)
    {
        var ids = (genreIds ?? []).ToHashSet();
        var names = (genreNames ?? []).Select(n => n.Trim().ToLowerInvariant()).ToHashSet();
        var countries = (originCountries ?? []).Select(c => c.Trim().ToUpperInvariant()).ToList();

        var animation = ids.Contains(AnimationGenre) || names.Contains("animation") || names.Contains("animación");
        var japanese = countries.Count > 0
            ? countries.Contains("JP")
            : string.Equals(originalLanguage, "ja", StringComparison.OrdinalIgnoreCase);
        if (animation && japanese)
        {
            return SeriesType.Anime;
        }

        var talkOrNews = ids.Contains(TalkGenre) || ids.Contains(NewsGenre)
            || names.Contains("talk") || names.Contains("news") || names.Contains("nachrichten") || names.Contains("talkshow");
        return talkOrNews ? SeriesType.Daily : SeriesType.Standard;
    }
}
