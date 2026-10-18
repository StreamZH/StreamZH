// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Media;

/// <summary>Absolute episode numbers and scene numbering helpers.</summary>
public static class EpisodeNumbering
{
    /// <summary>
    /// Numbers the regular episodes 1, 2, 3 … over all seasons (season 1 first, in episode order); specials get none.
    /// TMDB has no absolute numbers, so this is what anime releases ("[Group] Title - 26") are matched against
    /// (TheXEM's scene absolute numbers win where they exist).
    /// </summary>
    public static void AssignAbsoluteNumbers(IEnumerable<Episode> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var number = 0;
        foreach (var episode in episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber))
        {
            episode.AbsoluteEpisodeNumber = episode.SeasonNumber >= 1 ? ++number : null;
        }
    }

    /// <summary>True when TheXEM gave scene numbers to at least one episode (Sonarr's UseSceneNumbering).</summary>
    public static bool HasSceneNumbering(IEnumerable<Episode> episodes) =>
        episodes.Any(e => e.SceneSeasonNumber is not null || e.SceneAbsoluteEpisodeNumber is not null);

    /// <summary>The scene season of an episode (its own season without a mapping).</summary>
    public static int SearchSeason(Episode episode) => episode.SceneSeasonNumber ?? episode.SeasonNumber;

    /// <summary>The scene episode number of an episode (its own number without a mapping).</summary>
    public static int SearchEpisode(Episode episode) =>
        episode.SceneSeasonNumber is not null && episode.SceneEpisodeNumber is { } scene ? scene : episode.EpisodeNumber;

    /// <summary>The absolute number releases use: scene absolute (TheXEM), else the counted absolute number.</summary>
    public static int? SearchAbsolute(Episode episode) => episode.SceneAbsoluteEpisodeNumber ?? episode.AbsoluteEpisodeNumber;
}
