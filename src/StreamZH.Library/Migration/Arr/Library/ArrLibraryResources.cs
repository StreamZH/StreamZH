// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Library.Migration.Arr.Library;

// The parts of Radarr v5 and Sonarr v3/v4 API resources (/api/v3) the library sections read. Field names follow the
// apps' JSON (camelCase); enums arrive as camelCase strings. Everything is optional: older versions leave fields out.

/// <summary>{ "id": 4, "name": "German" }.</summary>
public sealed record ArrLanguage(int Id, string? Name);

/// <summary>{ "id": 3, "name": "WEBDL-1080p", "source": "webdl", "resolution": 1080, "modifier": "none" }.</summary>
public sealed record ArrQuality(int Id, string? Name, string? Source = null, int Resolution = 0, string? Modifier = null);

public sealed record ArrRevision(int Version = 1, int Real = 0, bool IsRepack = false);

/// <summary>A quality with its revision ("quality": {...}, "revision": {...}).</summary>
public sealed record ArrQualityModel(ArrQuality? Quality, ArrRevision? Revision);

/// <summary>Radarr's and Sonarr's media info summary of a file (MediaInfoResource).</summary>
public sealed record ArrMediaInfo
{
    public long AudioBitrate { get; init; }
    public decimal AudioChannels { get; init; }
    public string? AudioCodec { get; init; }

    /// <summary>ISO 639-2 codes joined with "/", e.g. "ger/eng".</summary>
    public string? AudioLanguages { get; init; }
    public int AudioStreamCount { get; init; }
    public int VideoBitDepth { get; init; }
    public long VideoBitrate { get; init; }
    public string? VideoCodec { get; init; }
    public decimal VideoFps { get; init; }
    public string? VideoDynamicRange { get; init; }
    public string? VideoDynamicRangeType { get; init; }

    /// <summary>"1920x1080".</summary>
    public string? Resolution { get; init; }

    /// <summary>"2:46:00" or "46:01".</summary>
    public string? RunTime { get; init; }
    public string? ScanType { get; init; }

    /// <summary>ISO 639-2 codes joined with "/".</summary>
    public string? Subtitles { get; init; }
}

/// <summary>The shared part of Radarr's movie file and Sonarr's episode file.</summary>
public abstract record ArrFileBase
{
    public int Id { get; init; }
    public string? RelativePath { get; init; }
    public string? Path { get; init; }
    public long Size { get; init; }
    public DateTime DateAdded { get; init; }
    public string? SceneName { get; init; }
    public string? ReleaseGroup { get; init; }
    public List<ArrLanguage>? Languages { get; init; }

    /// <summary>Sonarr v3: one language per file instead of a list.</summary>
    public ArrLanguage? Language { get; init; }
    public ArrQualityModel? Quality { get; init; }
    public ArrMediaInfo? MediaInfo { get; init; }

    public IReadOnlyList<ArrLanguage> AllLanguages => Languages is { Count: > 0 } list ? list : Language is { } single ? [single] : [];
}

public sealed record ArrMovieFile : ArrFileBase
{
    public int MovieId { get; init; }
    public string? Edition { get; init; }
}

public sealed record ArrEpisodeFile : ArrFileBase
{
    public int SeriesId { get; init; }
    public int SeasonNumber { get; init; }
}

public sealed record ArrMovie
{
    public int Id { get; init; }
    public string? Title { get; init; }
    public int Year { get; init; }
    public int TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public string? Path { get; init; }
    public string? RootFolderPath { get; init; }
    public int QualityProfileId { get; init; }
    public bool Monitored { get; init; }

    /// <summary>"tba", "announced", "inCinemas", "released", "deleted".</summary>
    public string? MinimumAvailability { get; init; }
    public bool HasFile { get; init; }
    public int MovieFileId { get; init; }
    public ArrMovieFile? MovieFile { get; init; }
    public List<int>? Tags { get; init; }
    public DateTime Added { get; init; }
}

public sealed record ArrSeason(int SeasonNumber, bool Monitored);

public sealed record ArrSeries
{
    public int Id { get; init; }
    public string? Title { get; init; }
    public int Year { get; init; }
    public int TvdbId { get; init; }

    /// <summary>Sonarr v4 only; 0 (or missing) on v3 and for shows TheTVDB has no TMDB link for.</summary>
    public int TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public string? Path { get; init; }
    public string? RootFolderPath { get; init; }
    public int QualityProfileId { get; init; }
    public bool Monitored { get; init; }

    /// <summary>"standard", "daily" or "anime".</summary>
    public string? SeriesType { get; init; }
    public bool SeasonFolder { get; init; }
    public List<ArrSeason>? Seasons { get; init; }
    public List<int>? Tags { get; init; }
    public DateTime Added { get; init; }
    public ArrSeriesStatistics? Statistics { get; init; }
}

public sealed record ArrSeriesStatistics(int EpisodeFileCount = 0, int EpisodeCount = 0, long SizeOnDisk = 0);

public sealed record ArrEpisode
{
    public int Id { get; init; }
    public int SeriesId { get; init; }
    public int EpisodeFileId { get; init; }
    public int SeasonNumber { get; init; }
    public int EpisodeNumber { get; init; }
    public string? Title { get; init; }

    /// <summary>yyyy-MM-dd.</summary>
    public string? AirDate { get; init; }
    public bool HasFile { get; init; }
    public bool Monitored { get; init; }
    public int? AbsoluteEpisodeNumber { get; init; }
}

public sealed record ArrRootFolder(int Id, string? Path);

public sealed record ArrQualityProfile(int Id, string? Name);

/// <summary>A history record of Radarr (movieId) or Sonarr (seriesId + episodeId).</summary>
public sealed record ArrHistoryRecord
{
    public int Id { get; init; }
    public int MovieId { get; init; }
    public int SeriesId { get; init; }
    public int EpisodeId { get; init; }
    public string? SourceTitle { get; init; }
    public List<ArrLanguage>? Languages { get; init; }
    public ArrLanguage? Language { get; init; }
    public ArrQualityModel? Quality { get; init; }
    public DateTime Date { get; init; }
    public string? DownloadId { get; init; }

    /// <summary>"grabbed", "downloadFolderImported", "downloadFailed", "movieFileDeleted", "episodeFileDeleted", …</summary>
    public string? EventType { get; init; }
    public Dictionary<string, string?>? Data { get; init; }

    public IReadOnlyList<ArrLanguage> AllLanguages => Languages is { Count: > 0 } list ? list : Language is { } single ? [single] : [];
}

/// <summary>A blocklisted release of Radarr (movieId) or Sonarr (seriesId + episodeIds).</summary>
public sealed record ArrBlocklistRecord
{
    public int Id { get; init; }
    public int MovieId { get; init; }
    public int SeriesId { get; init; }
    public List<int>? EpisodeIds { get; init; }
    public string? SourceTitle { get; init; }
    public List<ArrLanguage>? Languages { get; init; }
    public ArrLanguage? Language { get; init; }
    public ArrQualityModel? Quality { get; init; }
    public DateTime Date { get; init; }

    /// <summary>"usenet", "torrent" or "unknown".</summary>
    public string? Protocol { get; init; }
    public string? Indexer { get; init; }
    public string? Message { get; init; }

    public IReadOnlyList<ArrLanguage> AllLanguages => Languages is { Count: > 0 } list ? list : Language is { } single ? [single] : [];
}

/// <summary>A page of a paged list (history, blocklist).</summary>
public sealed record ArrPage<T>(int Page, int PageSize, int TotalRecords, List<T>? Records);
