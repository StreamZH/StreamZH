// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json.Serialization;

namespace StreamZH.Core.Indexers;

/// <summary>Which job asks for releases; each indexer can be switched on or off per use.</summary>
public enum IndexerUse
{
    /// <summary>Periodic fetch of the newest releases (0.7).</summary>
    Rss,

    /// <summary>Search started by StreamZH for missing items or upgrades.</summary>
    AutomaticSearch,

    /// <summary>Search started by the user in the UI (manual search).</summary>
    InteractiveSearch,
}

/// <summary>A configured indexer: Newznab (Usenet) or, since 0.11, Torznab (torrents; same API, torrent attributes in the feed).</summary>
/// <param name="BaseUrl">e.g. "https://indexer.example" or a Prowlarr proxy "http://prowlarr:9696/5".</param>
/// <param name="ApiPath">Usually "/api".</param>
/// <param name="Categories">Newznab categories to search for movies (2000 range).</param>
/// <param name="TvCategories">Newznab categories to search for series (5000 range).</param>
/// <param name="Priority">1 (highest) to 50, default 25, like Radarr; used as a tie-breaker when ranking.</param>
public sealed record IndexerDefinition(
    int Id,
    string Name,
    string Implementation,
    string BaseUrl,
    string ApiPath,
    string ApiKey,
    IReadOnlyList<int> Categories,
    IReadOnlyList<int> TvCategories,
    int Priority,
    bool EnableRss,
    bool EnableAutomaticSearch,
    bool EnableInteractiveSearch)
{
    public const string Newznab = "Newznab";

    /// <summary>0.11: Torznab (torrent) indexers; everything else is Usenet.</summary>
    public const string Torznab = "Torznab";

    public Downloads.DownloadProtocol Protocol =>
        string.Equals(Implementation, Torznab, StringComparison.OrdinalIgnoreCase) ? Downloads.DownloadProtocol.Torrent : Downloads.DownloadProtocol.Usenet;

    /// <summary>Torrent only: releases with fewer seeders are rejected (Radarr's "Minimum Seeders", default 1).</summary>
    public int MinimumSeeders { get; init; } = 1;

    /// <summary>Torrent only: seed until this ratio (null = the download client's default).</summary>
    public double? SeedRatio { get; init; }

    /// <summary>Torrent only: seed at least this many minutes (null = the download client's default).</summary>
    public int? SeedTimeMinutes { get; init; }

    public int? SeasonPackSeedTimeMinutes { get; init; }

    /// <summary>Master switch (Prowlarr's "Enable"): a disabled indexer is never used.</summary>
    public bool Enable { get; init; } = true;

    /// <summary>Key of the catalog entry the indexer was created from; null for older indexers (treated as "generic-newznab").</summary>
    public string? DefinitionKey { get; init; }

    /// <summary>Extra parameters appended to every API request, e.g. "&amp;attrs=poster" (leading '&amp;' optional).</summary>
    public string AdditionalParameters { get; init; } = string.Empty;

    /// <summary>End of the VIP membership as "yyyy-MM-dd", or null.</summary>
    public string? VipExpiration { get; init; }

    /// <summary>Maximum API queries (search and RSS) per <see cref="LimitsUnit"/>; null or 0 = unlimited.</summary>
    public int? QueryLimit { get; init; }

    /// <summary>Maximum NZB downloads per <see cref="LimitsUnit"/>; null or 0 = unlimited.</summary>
    public int? GrabLimit { get; init; }

    public IndexerLimitsUnit LimitsUnit { get; init; } = IndexerLimitsUnit.Day;

    /// <summary>The VIP expiration as a date; null when not set or not readable.</summary>
    public DateOnly? VipExpirationDate =>
        DateOnly.TryParseExact(VipExpiration, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>The rolling window of the query and grab limits.</summary>
    public TimeSpan LimitsWindow => LimitsUnit == IndexerLimitsUnit.Hour ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24);

    public bool IsEnabledFor(IndexerUse use) => Enable && use switch
    {
        IndexerUse.Rss => EnableRss,
        IndexerUse.AutomaticSearch => EnableAutomaticSearch,
        _ => EnableInteractiveSearch,
    };
}

/// <summary>Window for query and grab limits: rolling 24 hours or rolling hour (like Prowlarr).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndexerLimitsUnit>))]
public enum IndexerLimitsUnit
{
    [JsonStringEnumMemberName("day")]
    Day,

    [JsonStringEnumMemberName("hour")]
    Hour,
}

/// <summary>One category from the indexer's capabilities, with its sub-categories.</summary>
public sealed record IndexerCategory(int Id, string Name, IReadOnlyList<IndexerCategory> SubCategories);

/// <summary>What an indexer supports (t=caps). Parameter names are the Newznab ones ("q", "imdbid", "tmdbid", "tvdbid", "season", "ep").</summary>
public sealed record IndexerCapabilities(
    int DefaultPageSize,
    int MaxPageSize,
    bool SearchAvailable,
    IReadOnlyList<string> MovieSearchParams,
    IReadOnlyList<string> TvSearchParams,
    IReadOnlyList<IndexerCategory> Categories)
{
    public bool MovieSearchAvailable => MovieSearchParams.Count > 0;
    public bool TvSearchAvailable => TvSearchParams.Count > 0;
}

/// <summary>One result from an indexer, before parsing. Ids are 0/null when the indexer does not send them.</summary>
public sealed record ReleaseInfo
{
    public required string Guid { get; init; }
    public required string Title { get; init; }
    public required string DownloadUrl { get; init; }
    public string? InfoUrl { get; init; }
    public long Size { get; init; }
    public DateTimeOffset PublishDate { get; init; }
    public int IndexerId { get; init; }
    public string Indexer { get; init; } = string.Empty;
    public int IndexerPriority { get; init; } = 25;
    public IReadOnlyList<int> Categories { get; init; } = [];
    public int? Grabs { get; init; }
    public int TmdbId { get; init; }
    public int TvdbId { get; init; }
    public string? ImdbId { get; init; }

    /// <summary>0.11: Usenet (NZB) or Torrent (Torznab).</summary>
    public Downloads.DownloadProtocol Protocol { get; init; } = Downloads.DownloadProtocol.Usenet;

    /// <summary>Torrent only: seeders/peers as the indexer reports them (null when unknown).</summary>
    public int? Seeders { get; init; }

    public int? Peers { get; init; }

    /// <summary>Torrent only: BitTorrent info hash (hex, lower case) when the indexer gives it.</summary>
    public string? InfoHash { get; init; }

    /// <summary>Torrent only: magnet link; <see cref="DownloadUrl"/> may point to a .torrent file or be the magnet itself.</summary>
    public string? MagnetUrl { get; init; }

    /// <summary>Torrent only: 0 = freeleech, 1 = normal (Torznab downloadvolumefactor).</summary>
    public double? DownloadVolumeFactor { get; init; }

    public double? UploadVolumeFactor { get; init; }

    /// <summary>Torrent only: the tracker's minimum ratio / seed time (Torznab minimumratio, minimumseedtime in seconds).</summary>
    public double? MinimumRatio { get; init; }

    public long? MinimumSeedTimeSeconds { get; init; }

    /// <summary>Radarr's IndexerFlags bits (see <see cref="IndexerFlagBits"/>: G_Freeleech 1, G_Halfleech 2, G_DoubleUpload 4, PTP_Golden 8, PTP_Approved 16, G_Internal 32, G_Scene 128, G_Freeleech75 256, G_Freeleech25 512, Nuked 2048).</summary>
    public int IndexerFlags { get; init; }

    /// <summary>Age in days at <paramref name="now"/>.</summary>
    public double AgeDays(DateTimeOffset now) => Math.Max(0, (now - PublishDate).TotalDays);
}

/// <summary>What to search for. The client turns it into Newznab parameters based on the capabilities.</summary>
public abstract record SearchCriteria
{
    /// <summary>Titles to try with q= when no id search is possible (title, original title).</summary>
    public required IReadOnlyList<string> Titles { get; init; }
    public int Year { get; init; }
}

public sealed record MovieSearchCriteria : SearchCriteria
{
    public int TmdbId { get; init; }
    public string? ImdbId { get; init; }
}

/// <summary>Single episode, several episodes of one season, a season pack (<see cref="EpisodeNumbers"/> empty) or a daily episode (<see cref="AirDate"/>).</summary>
public sealed record EpisodeSearchCriteria : SearchCriteria
{
    public int TvdbId { get; init; }
    public int TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public int SeasonNumber { get; init; }
    public IReadOnlyList<int> EpisodeNumbers { get; init; } = [];

    /// <summary>yyyy-MM-dd for daily shows.</summary>
    public string? AirDate { get; init; }

    /// <summary>Anime: the absolute numbers of the episodes (scene numbering), searched as "Title 12" too (Sonarr).</summary>
    public IReadOnlyList<int> AbsoluteEpisodeNumbers { get; init; } = [];

    public bool IsSeasonSearch => EpisodeNumbers.Count == 0 && AirDate is null && AbsoluteEpisodeNumbers.Count == 0;
}

/// <summary>Which Newznab function a free text search uses.</summary>
public enum TextSearchType
{
    /// <summary>t=search: everything the indexer has.</summary>
    Search,

    /// <summary>t=movie with q (falls back to t=search when the indexer has no movie text search).</summary>
    Movie,

    /// <summary>t=tvsearch with q (falls back to t=search when the indexer has no TV text search).</summary>
    Tv,
}

/// <summary>
/// Free text search without a library item (the Search page, like Prowlarr's). An empty <see cref="Query"/> lists the newest
/// releases. Empty <see cref="Categories"/> means the indexer's movie categories (Movie), TV categories (Tv) or no filter (Search).
/// </summary>
public sealed record TextSearchCriteria : SearchCriteria
{
    public string Query { get; init; } = string.Empty;
    public TextSearchType Type { get; init; }
    public IReadOnlyList<int> Categories { get; init; } = [];
}

/// <summary>An indexer answered with an error. <see cref="RetryAfter"/> is set for request limits.</summary>
public sealed class IndexerException : Exception
{
    public IndexerException(string message, int? code = null, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        RetryAfter = retryAfter;
    }

    /// <summary>Newznab error code (100 wrong API key, 500 request limit, …), null for HTTP/network errors.</summary>
    public int? Code { get; }

    public TimeSpan? RetryAfter { get; }

    /// <summary>Wrong API key or account problem: retrying will not help until the settings change.</summary>
    public bool IsAuthError => Code is >= 100 and < 200;

    /// <summary>
    /// StreamZH's own query or grab limit for the indexer is reached and no request was sent.
    /// Not a failure of the indexer, so it does not count towards the back-off.
    /// </summary>
    public bool IsLimitReached { get; init; }
}
