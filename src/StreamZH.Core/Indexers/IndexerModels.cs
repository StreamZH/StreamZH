// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

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

/// <summary>A configured indexer. Only Newznab (Usenet) for now; Torznab follows in 1.0.</summary>
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

    public bool IsEnabledFor(IndexerUse use) => use switch
    {
        IndexerUse.Rss => EnableRss,
        IndexerUse.AutomaticSearch => EnableAutomaticSearch,
        _ => EnableInteractiveSearch,
    };
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

    public bool IsSeasonSearch => EpisodeNumbers.Count == 0 && AirDate is null;
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
}
