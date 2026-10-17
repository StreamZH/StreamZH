// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only
// Portions based on Prowlarr (https://github.com/Prowlarr/Prowlarr), Copyright (c) Prowlarr contributors, GPL-3.0.

using System.Globalization;
using System.Text.Json.Serialization;
using Dapper;
using StreamZH.Core.Indexers;

namespace StreamZH.Data;

/// <summary>Kind of API call made to an indexer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndexerHistoryEventType>))]
public enum IndexerHistoryEventType
{
    /// <summary>A search (t=search/movie/tvsearch with a query or ids).</summary>
    [JsonStringEnumMemberName("query")]
    Query,

    /// <summary>Newest releases without a query.</summary>
    [JsonStringEnumMemberName("rss")]
    Rss,

    /// <summary>NZB download.</summary>
    [JsonStringEnumMemberName("grab")]
    Grab,

    /// <summary>t=caps (test).</summary>
    [JsonStringEnumMemberName("caps")]
    Caps,
}

/// <summary>One API call to an indexer. <see cref="Indexer"/> is filled when reading (the indexer's current name).</summary>
public sealed record IndexerHistoryEntry
{
    public int Id { get; init; }
    public DateTime Date { get; init; }
    public int IndexerId { get; init; }
    public string Indexer { get; init; } = string.Empty;
    public IndexerHistoryEventType EventType { get; init; }
    public bool Successful { get; init; }
    public int ElapsedMs { get; init; }
    public IndexerCallSource Source { get; init; }
    public string? Query { get; init; }
    public int? Results { get; init; }
    public string? Title { get; init; }
    public string? Error { get; init; }
}

/// <summary>Calls in the current limit windows: last 24 hours and last hour.</summary>
public sealed record IndexerUsageCounts(int QueriesDay, int QueriesHour, int GrabsDay, int GrabsHour)
{
    public static readonly IndexerUsageCounts None = new(0, 0, 0, 0);

    public int Queries(IndexerLimitsUnit unit) => unit == IndexerLimitsUnit.Hour ? QueriesHour : QueriesDay;

    public int Grabs(IndexerLimitsUnit unit) => unit == IndexerLimitsUnit.Hour ? GrabsHour : GrabsDay;
}

/// <summary>Totals per indexer for the stats page; indexers without calls have zeros.</summary>
public sealed record IndexerStatsRow(int IndexerId, string Indexer, int Queries, int RssQueries, int Grabs, int FailedQueries, int FailedGrabs, int AverageResponseMs);

/// <summary>Queries (search and RSS) and grabs of one UTC day ("yyyy-MM-dd").</summary>
public sealed record IndexerDailyRow(string Date, int Queries, int Grabs);

public sealed class IndexerHistoryRepository(Database database)
{
    /// <summary>How long calls are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private const string QueryTypes = "('query', 'rss')";

    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly Lock _pruneLock = new();
    private DateTime _nextPrune = DateTime.MinValue;

    public static string ToStorage(IndexerHistoryEventType type) => type.ToString().ToLowerInvariant();

    public static string ToStorage(IndexerCallSource source) => source.ToString().ToLowerInvariant();

    /// <summary>
    /// Adds a call. Rows for indexers that do not exist (an unsaved indexer being tested, or one deleted meanwhile) are dropped.
    /// </summary>
    public void Add(IndexerHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO IndexerHistory (Date, IndexerId, EventType, Successful, ElapsedMs, Source, Query, Results, Title, Error)
            SELECT @Date, @IndexerId, @EventType, @Successful, @ElapsedMs, @Source, @Query, @Results, @Title, @Error
            WHERE EXISTS (SELECT 1 FROM Indexers WHERE Id = @IndexerId)
            """,
            new
            {
                entry.Date,
                entry.IndexerId,
                EventType = ToStorage(entry.EventType),
                entry.Successful,
                entry.ElapsedMs,
                Source = ToStorage(entry.Source),
                entry.Query,
                entry.Results,
                entry.Title,
                entry.Error,
            });
    }

    /// <summary>Queries (search and RSS) of one indexer since <paramref name="since"/>, failed ones included (like Prowlarr).</summary>
    public int CountQueriesSince(int indexerId, DateTime since) =>
        Count($"EventType IN {QueryTypes}", indexerId, since);

    /// <summary>Grabs of one indexer since <paramref name="since"/>, failed ones included.</summary>
    public int CountGrabsSince(int indexerId, DateTime since) =>
        Count("EventType = 'grab'", indexerId, since);

    /// <summary>The oldest query or grab in the window: the limit frees up again when it drops out.</summary>
    public DateTime? OldestSince(int indexerId, DateTime since, bool grabs)
    {
        using var connection = database.Open();
        var value = connection.ExecuteScalar<string?>(
            $"SELECT MIN(Date) FROM IndexerHistory WHERE IndexerId = @indexerId AND Date >= @since AND {(grabs ? "EventType = 'grab'" : $"EventType IN {QueryTypes}")}",
            new { indexerId, since });
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
    }

    /// <summary>Calls per indexer in the last 24 hours and the last hour (before <paramref name="now"/>).</summary>
    public IReadOnlyDictionary<int, IndexerUsageCounts> Usage(DateTime now)
    {
        var day = now.AddHours(-24);
        var hour = now.AddHours(-1);
        using var connection = database.Open();
        return connection.Query<UsageRow>(
            $"""
            SELECT IndexerId,
                SUM(CASE WHEN EventType IN {QueryTypes} THEN 1 ELSE 0 END) AS QueriesDay,
                SUM(CASE WHEN EventType IN {QueryTypes} AND Date >= @hour THEN 1 ELSE 0 END) AS QueriesHour,
                SUM(CASE WHEN EventType = 'grab' THEN 1 ELSE 0 END) AS GrabsDay,
                SUM(CASE WHEN EventType = 'grab' AND Date >= @hour THEN 1 ELSE 0 END) AS GrabsHour
            FROM IndexerHistory
            WHERE Date >= @day
            GROUP BY IndexerId
            """,
            new { day, hour })
            .ToDictionary(r => (int)r.IndexerId, r => new IndexerUsageCounts((int)r.QueriesDay, (int)r.QueriesHour, (int)r.GrabsDay, (int)r.GrabsHour));
    }

    /// <summary>Newest first. <paramref name="eventType"/> null = all types.</summary>
    public (IReadOnlyList<IndexerHistoryEntry> Records, int Total) Page(int page, int pageSize, int? indexerId, IndexerHistoryEventType? eventType)
    {
        const string Filter = "(@indexerId IS NULL OR h.IndexerId = @indexerId) AND (@eventType IS NULL OR h.EventType = @eventType)";
        using var connection = database.Open();
        var parameters = new
        {
            indexerId,
            eventType = eventType is { } t ? ToStorage(t) : null,
            take = pageSize,
            skip = (page - 1) * pageSize,
        };

        var total = connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM IndexerHistory h WHERE {Filter}", parameters);
        var rows = connection.Query<HistoryRow>(
            $"""
            SELECT h.Id, h.Date, h.IndexerId, i.Name AS Indexer, h.EventType, h.Successful, h.ElapsedMs, h.Source, h.Query, h.Results, h.Title, h.Error
            FROM IndexerHistory h JOIN Indexers i ON i.Id = h.IndexerId
            WHERE {Filter}
            ORDER BY h.Date DESC, h.Id DESC
            LIMIT @take OFFSET @skip
            """,
            parameters);
        return ([.. rows.Select(r => r.ToEntry())], total);
    }

    /// <summary>Totals per indexer since <paramref name="from"/>, aggregated in SQL; every indexer is listed.</summary>
    public IReadOnlyList<IndexerStatsRow> Stats(DateTime from)
    {
        using var connection = database.Open();
        return [.. connection.Query<StatsRow>(
            $"""
            SELECT i.Id AS IndexerId, i.Name AS Indexer,
                COALESCE(SUM(CASE WHEN h.EventType = 'query' THEN 1 ELSE 0 END), 0) AS Queries,
                COALESCE(SUM(CASE WHEN h.EventType = 'rss' THEN 1 ELSE 0 END), 0) AS RssQueries,
                COALESCE(SUM(CASE WHEN h.EventType = 'grab' THEN 1 ELSE 0 END), 0) AS Grabs,
                COALESCE(SUM(CASE WHEN h.EventType IN {QueryTypes} AND h.Successful = 0 THEN 1 ELSE 0 END), 0) AS FailedQueries,
                COALESCE(SUM(CASE WHEN h.EventType = 'grab' AND h.Successful = 0 THEN 1 ELSE 0 END), 0) AS FailedGrabs,
                COALESCE(AVG(CASE WHEN h.EventType IN {QueryTypes} AND h.Successful = 1 THEN h.ElapsedMs END), 0.0) AS AverageResponseMs
            FROM Indexers i
            LEFT JOIN IndexerHistory h ON h.IndexerId = i.Id AND h.Date >= @from
            GROUP BY i.Id, i.Name
            ORDER BY i.Name COLLATE NOCASE
            """,
            new { from })
            .Select(r => new IndexerStatsRow(
                (int)r.IndexerId, r.Indexer, (int)r.Queries, (int)r.RssQueries, (int)r.Grabs, (int)r.FailedQueries, (int)r.FailedGrabs,
                (int)Math.Round(r.AverageResponseMs, MidpointRounding.AwayFromZero)))];
    }

    /// <summary>Queries (search and RSS) and grabs per UTC day since <paramref name="from"/>; only days with calls.</summary>
    public IReadOnlyList<IndexerDailyRow> Daily(DateTime from)
    {
        using var connection = database.Open();
        return [.. connection.Query<DailyRow>(
            $"""
            SELECT strftime('%Y-%m-%d', Date) AS Day,
                SUM(CASE WHEN EventType IN {QueryTypes} THEN 1 ELSE 0 END) AS Queries,
                SUM(CASE WHEN EventType = 'grab' THEN 1 ELSE 0 END) AS Grabs
            FROM IndexerHistory
            WHERE Date >= @from
            GROUP BY Day
            ORDER BY Day
            """,
            new { from })
            .Select(r => new IndexerDailyRow(r.Day, (int)r.Queries, (int)r.Grabs))];
    }

    /// <summary>
    /// Removes calls older than <see cref="Retention"/>, at most once per hour per repository instance (the repository is a singleton).
    /// Returns how many rows were removed.
    /// </summary>
    public int PruneIfDue(DateTime now)
    {
        lock (_pruneLock)
        {
            if (now < _nextPrune)
            {
                return 0;
            }

            _nextPrune = now + PruneInterval;
        }

        return DeleteOlderThan(now - Retention);
    }

    /// <summary>Removes calls older than <paramref name="cutoff"/>; returns how many.</summary>
    public int DeleteOlderThan(DateTime cutoff)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM IndexerHistory WHERE Date < @cutoff", new { cutoff });
    }

    private int Count(string condition, int indexerId, DateTime since)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM IndexerHistory WHERE IndexerId = @indexerId AND Date >= @since AND {condition}",
            new { indexerId, since });
    }

    private sealed class UsageRow
    {
        public long IndexerId { get; init; }
        public long QueriesDay { get; init; }
        public long QueriesHour { get; init; }
        public long GrabsDay { get; init; }
        public long GrabsHour { get; init; }
    }

    private sealed class StatsRow
    {
        public long IndexerId { get; init; }
        public string Indexer { get; init; } = string.Empty;
        public long Queries { get; init; }
        public long RssQueries { get; init; }
        public long Grabs { get; init; }
        public long FailedQueries { get; init; }
        public long FailedGrabs { get; init; }
        public double AverageResponseMs { get; init; }
    }

    private sealed class DailyRow
    {
        public string Day { get; init; } = string.Empty;
        public long Queries { get; init; }
        public long Grabs { get; init; }
    }

    private sealed class HistoryRow
    {
        public long Id { get; init; }
        public DateTime Date { get; init; }
        public long IndexerId { get; init; }
        public string Indexer { get; init; } = string.Empty;
        public string EventType { get; init; } = string.Empty;
        public bool Successful { get; init; }
        public long ElapsedMs { get; init; }
        public string Source { get; init; } = string.Empty;
        public string? Query { get; init; }
        public long? Results { get; init; }
        public string? Title { get; init; }
        public string? Error { get; init; }

        public IndexerHistoryEntry ToEntry() => new()
        {
            Id = (int)Id,
            Date = DateTime.SpecifyKind(Date, DateTimeKind.Utc),
            IndexerId = (int)IndexerId,
            Indexer = Indexer,
            EventType = Enum.TryParse<IndexerHistoryEventType>(EventType, ignoreCase: true, out var type) ? type : IndexerHistoryEventType.Query,
            Successful = Successful,
            ElapsedMs = (int)ElapsedMs,
            Source = Enum.TryParse<IndexerCallSource>(Source, ignoreCase: true, out var source) ? source : IndexerCallSource.Interactive,
            Query = Query,
            Results = Results is { } r ? (int)r : null,
            Title = Title,
            Error = Error,
        };
    }
}
