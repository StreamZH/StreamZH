// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;

namespace StreamZH.Data;

/// <summary>A movie of the library as Discover needs it.</summary>
public sealed record DiscoverMovieState(int Id, int TmdbId, bool HasFile);

/// <summary>A series of the library: aired episodes (seasons above 0) and how many of them have files.</summary>
public sealed record DiscoverSeriesState(int Id, int TmdbId, int Aired, int AiredWithFiles);

/// <summary>An open request of a title (pending, approved, processing, partially available).</summary>
public sealed record DiscoverRequestState(int Id, int TmdbId, RequestStatus Status, IReadOnlyList<int>? Seasons);

/// <summary>
/// Library and request state of a page of TMDB results in three queries (0.10), so a Discover page with twenty titles
/// does not read the library twenty times.
/// </summary>
public sealed class DiscoverLookup(Database database)
{
    public IReadOnlyDictionary<int, DiscoverMovieState> Movies(IReadOnlyCollection<int> tmdbIds)
    {
        if (tmdbIds.Count == 0)
        {
            return new Dictionary<int, DiscoverMovieState>();
        }

        using var connection = database.Open();
        return connection.Query<(long Id, long TmdbId, long HasFile)>(
                "SELECT Id, TmdbId, CASE WHEN FileRelativePath IS NULL THEN 0 ELSE 1 END FROM Movies WHERE TmdbId IN @ids",
                new { ids = tmdbIds.ToArray() })
            .ToDictionary(r => (int)r.TmdbId, r => new DiscoverMovieState((int)r.Id, (int)r.TmdbId, r.HasFile == 1));
    }

    /// <param name="today">yyyy-MM-dd; episodes up to this air date count as aired.</param>
    public IReadOnlyDictionary<int, DiscoverSeriesState> Series(IReadOnlyCollection<int> tmdbIds, string today)
    {
        if (tmdbIds.Count == 0)
        {
            return new Dictionary<int, DiscoverSeriesState>();
        }

        using var connection = database.Open();
        return connection.Query<(long Id, long TmdbId, long Aired, long Files)>(
                """
                SELECT s.Id, s.TmdbId,
                       IFNULL(SUM(CASE WHEN e.SeasonNumber > 0 AND e.AirDate IS NOT NULL AND e.AirDate <= @today THEN 1 ELSE 0 END), 0),
                       IFNULL(SUM(CASE WHEN e.SeasonNumber > 0 AND e.AirDate IS NOT NULL AND e.AirDate <= @today AND e.FileRelativePath IS NOT NULL THEN 1 ELSE 0 END), 0)
                FROM Series s LEFT JOIN Episodes e ON e.SeriesId = s.Id
                WHERE s.TmdbId IN @ids
                GROUP BY s.Id, s.TmdbId
                """,
                new { ids = tmdbIds.ToArray(), today })
            .ToDictionary(r => (int)r.TmdbId, r => new DiscoverSeriesState((int)r.Id, (int)r.TmdbId, (int)r.Aired, (int)r.Files));
    }

    /// <summary>Open requests of the titles, oldest first.</summary>
    public IReadOnlyList<DiscoverRequestState> OpenRequests(MediaType mediaType, IReadOnlyCollection<int> tmdbIds)
    {
        if (tmdbIds.Count == 0)
        {
            return [];
        }

        using var connection = database.Open();
        return
        [
            .. connection.Query<(long Id, long TmdbId, long Status, string? Seasons)>(
                    "SELECT Id, TmdbId, Status, Seasons FROM Requests WHERE MediaType = @type AND TmdbId IN @ids AND Status IN (1, 2, 4, 5) ORDER BY Id",
                    new { type = (int)mediaType, ids = tmdbIds.ToArray() })
                .Select(r => new DiscoverRequestState(
                    (int)r.Id,
                    (int)r.TmdbId,
                    (RequestStatus)r.Status,
                    r.Seasons is null ? null : System.Text.Json.JsonSerializer.Deserialize<int[]>(r.Seasons))),
        ];
    }
}
