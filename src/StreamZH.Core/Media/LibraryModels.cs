// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Media;

/// <summary>A folder where StreamZH keeps one media type (e.g. /media/movies).</summary>
public sealed class RootFolder
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public MediaType MediaType { get; set; }
}

/// <summary>A media file on disk, relative to the item folder.</summary>
public sealed record MediaFile(string RelativePath, long Size, DateTime Modified);

public sealed class Movie
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string SortTitle { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Overview { get; set; }
    public int Runtime { get; set; }
    public string Status { get; set; } = string.Empty;
    public IReadOnlyList<string> Genres { get; set; } = [];
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public bool Monitored { get; set; } = true;
    public int QualityProfileId { get; set; }

    /// <summary>Original language from TMDB (Radarr language id, 0 = unknown).</summary>
    public int OriginalLanguageId { get; set; }
    public int RootFolderId { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public DateTime Added { get; set; }
    public DateTime? LastRefreshed { get; set; }
    public MediaFile? File { get; set; }

    /// <summary>TMDB release date (yyyy-MM-dd, usually the cinema release), for the minimum availability; null when unknown.</summary>
    public string? InCinemas { get; set; }

    /// <summary>Earliest digital release (yyyy-MM-dd) TMDB knows in any country; null when unknown.</summary>
    public string? DigitalRelease { get; set; }

    /// <summary>Earliest physical release (yyyy-MM-dd, DVD/Blu-ray) TMDB knows in any country; null when unknown.</summary>
    public string? PhysicalRelease { get; set; }
}

public sealed class Series
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public string? ImdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string SortTitle { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Overview { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Network { get; set; }
    public IReadOnlyList<string> Genres { get; set; } = [];
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public bool Monitored { get; set; } = true;
    public int QualityProfileId { get; set; }

    /// <summary>Original language from TMDB (Radarr language id, 0 = unknown).</summary>
    public int OriginalLanguageId { get; set; }
    public int RootFolderId { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public DateTime Added { get; set; }
    public DateTime? LastRefreshed { get; set; }

    /// <summary>Standard (S01E02), Daily (air date) or Anime (absolute numbers), like Sonarr.</summary>
    public SeriesType SeriesType { get; set; } = SeriesType.Standard;
}

/// <summary>How episodes of a series are named and searched (Sonarr's series type).</summary>
public enum SeriesType
{
    Standard = 0,
    Daily = 1,
    Anime = 2,
}

public sealed class Episode
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>ISO date (yyyy-MM-dd) of the first air date, if known.</summary>
    public string? AirDate { get; set; }

    public string? Overview { get; set; }
    public bool Monitored { get; set; } = true;
    public MediaFile? File { get; set; }

    /// <summary>Running number over all regular seasons (TMDB has none: counted on refresh, specials excluded).</summary>
    public int? AbsoluteEpisodeNumber { get; set; }

    /// <summary>Scene numbering from TheXEM when releases are numbered differently than TMDB; null without a mapping.</summary>
    public int? SceneSeasonNumber { get; set; }

    public int? SceneEpisodeNumber { get; set; }

    public int? SceneAbsoluteEpisodeNumber { get; set; }
}

/// <summary>Scene season, episode and absolute number of one episode (TheXEM).</summary>
public sealed record SceneNumber(int? SeasonNumber, int? EpisodeNumber, int? AbsoluteEpisodeNumber);

/// <summary>Episode counters per series, used for the library list.</summary>
public sealed record SeriesCounts(int SeriesId, int EpisodeCount, int AiredCount, int FileCount, long SizeOnDisk);

public static class SortTitles
{
    private static readonly string[] Articles = ["the ", "a ", "an ", "der ", "die ", "das "];

    /// <summary>Lower-case title without leading article, for alphabetical lists.</summary>
    public static string For(string title)
    {
        var lower = title.Trim().ToLowerInvariant();
        foreach (var article in Articles)
        {
            if (lower.StartsWith(article, StringComparison.Ordinal) && lower.Length > article.Length)
            {
                return lower[article.Length..];
            }
        }

        return lower;
    }
}