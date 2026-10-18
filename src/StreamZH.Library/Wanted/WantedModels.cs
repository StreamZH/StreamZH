// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace StreamZH.Library.Wanted;

/// <summary>Which half of a Wanted list: movies or episodes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WantedItemType>))]
public enum WantedItemType
{
    [JsonStringEnumMemberName("movies")]
    Movies,

    [JsonStringEnumMemberName("episodes")]
    Episodes,
}

/// <summary>Columns a Wanted list can be sorted by.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WantedSortKey>))]
public enum WantedSortKey
{
    /// <summary>Movie title, or series title then season and episode.</summary>
    [JsonStringEnumMemberName("title")]
    Title,

    /// <summary>Release date (movies) or air date (episodes).</summary>
    [JsonStringEnumMemberName("date")]
    Date,

    [JsonStringEnumMemberName("lastSearched")]
    LastSearched,

    [JsonStringEnumMemberName("status")]
    Status,

    [JsonStringEnumMemberName("profile")]
    Profile,

    /// <summary>Current quality (Cutoff unmet), by its profile rank.</summary>
    [JsonStringEnumMemberName("quality")]
    Quality,
}

/// <summary>
/// One page of a Wanted list. <see cref="MonitoredOnly"/> hides unmonitored movies, series and episodes (default, like
/// Radarr/Sonarr); <see cref="AvailableOnly"/> (Missing) hides movies before their minimum availability and episodes
/// that have not aired.
/// </summary>
public sealed record WantedQuery(
    WantedItemType Type,
    int Page = 1,
    int PageSize = WantedQuery.DefaultPageSize,
    WantedSortKey Sort = WantedSortKey.Date,
    bool Descending = true,
    bool MonitoredOnly = true,
    bool AvailableOnly = true)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 250;
}

/// <summary>
/// One row of Wanted → Missing or Cutoff unmet. Movies fill the movie fields, episodes the series and episode fields.
/// Dates are yyyy-MM-dd (TMDB dates, no time of day).
/// </summary>
/// <param name="Status">"missing", "notAvailable" (movie before its minimum availability), "unaired" (episode) or "cutoffUnmet".</param>
/// <param name="Queue">"downloading", "waitingForImport" or "grabbed" (blackhole folder, last 12 hours); null when nothing is on its way.</param>
/// <param name="Reasons">Cutoff unmet: "quality" (below the cutoff), "language" (not the profile's language), "dualLanguage" (not dual language).</param>
public sealed record WantedRecord(
    string Type,
    int Id,
    int? MovieId,
    int? SeriesId,
    int? EpisodeId,
    string Title,
    int Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    string? Date,
    string? InCinemas,
    string? DigitalRelease,
    string? PhysicalRelease,
    string? AvailableFrom,
    bool Monitored,
    string Status,
    string? Queue,
    int QualityProfileId,
    string Profile,
    string? Quality,
    string Cutoff,
    string? WantedLanguage,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Reasons,
    DateTime? LastSearched,
    string? PosterPath)
{
    /// <summary>Sort helpers, not sent.</summary>
    [JsonIgnore]
    internal string SortTitle { get; init; } = string.Empty;

    [JsonIgnore]
    internal int QualityRank { get; init; } = -1;
}

/// <summary>Movies and episodes that match the same filters, for the tab counters.</summary>
public sealed record WantedCounts(int Movies, int Episodes);

public sealed record WantedPage(
    int Page,
    int PageSize,
    int TotalRecords,
    WantedSortKey SortKey,
    string SortDirection,
    WantedCounts Counts,
    IReadOnlyList<WantedRecord> Records);

/// <summary>Counters for the Home page (default filters: monitored and available).</summary>
public sealed record WantedSummary(int MissingMovies, int MissingEpisodes, int CutoffUnmetMovies, int CutoffUnmetEpisodes);

public static class WantedStatus
{
    public const string Missing = "missing";
    public const string NotAvailable = "notAvailable";
    public const string Unaired = "unaired";
    public const string CutoffUnmet = "cutoffUnmet";
}
