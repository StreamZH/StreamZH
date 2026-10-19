// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using StreamZH.Core.Media;

namespace StreamZH.Data;

/// <summary>What happened to a release. Values are stored in the database; never renumber.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HistoryEventType>))]
public enum HistoryEventType
{
    /// <summary>The NZB was fetched from the indexer and handed to the download step.</summary>
    Grabbed = 1,

    /// <summary>The built-in engine finished a download (repaired and unpacked if needed).</summary>
    DownloadCompleted = 2,

    /// <summary>The built-in engine gave up on a download (missing articles, repair or unpack failed).</summary>
    DownloadFailed = 3,

    /// <summary>Files of a finished download were moved into the library (completed download handling or manual import).</summary>
    Imported = 4,

    /// <summary>An import replaced an existing file with a better one (the old file went to the recycle bin or was deleted).</summary>
    Upgraded = 5,

    /// <summary>A finished download could not be imported; the files stay in the download folder.</summary>
    ImportFailed = 6,

    /// <summary>A file in the library was renamed to the naming scheme.</summary>
    Renamed = 7,

    /// <summary>A library file was deleted (taken over from Radarr's or Sonarr's history, 0.9).</summary>
    FileDeleted = 8,
}

/// <summary>One history entry. <see cref="Data"/> holds event-specific details (download URL, NZB path, …).</summary>
public sealed record HistoryItem
{
    public int Id { get; init; }
    public DateTime Date { get; init; }
    public HistoryEventType EventType { get; init; }
    /// <summary>Null for a grab from the Search page that belongs to no library item (stored as 0).</summary>
    public MediaType? MediaType { get; init; }
    public int? MovieId { get; init; }
    public int? SeriesId { get; init; }
    public IReadOnlyList<int> EpisodeIds { get; init; } = [];
    public required string SourceTitle { get; init; }
    public int QualityId { get; init; }
    public IReadOnlyList<int> LanguageIds { get; init; } = [];
    public string? ReleaseGroup { get; init; }
    public string? Indexer { get; init; }
    public int? IndexerId { get; init; }
    public long Size { get; init; }
    public IReadOnlyDictionary<string, string?> Data { get; init; } = new Dictionary<string, string?>();
}

/// <summary>Filters of the history list; null or empty fields do not filter.</summary>
/// <param name="EventTypes">Any of these events.</param>
/// <param name="MediaType">Movie or Series.</param>
/// <param name="Search">Words that must all appear in the release title (case-insensitive).</param>
/// <param name="Indexer">The indexer name (case-insensitive).</param>
public sealed record HistoryFilter(
    IReadOnlyList<HistoryEventType>? EventTypes = null,
    MediaType? MediaType = null,
    string? Search = null,
    string? Indexer = null);

public sealed class HistoryRepository(Database database)
{
    private const string Columns = """
        Id, Date, EventType, MediaType, MovieId, SeriesId, EpisodeIds, SourceTitle, QualityId, Languages, ReleaseGroup, Indexer, IndexerId, Size, Data
        """;

    public int Add(HistoryItem item)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO History (Date, EventType, MediaType, MovieId, SeriesId, EpisodeIds, SourceTitle, QualityId, Languages, ReleaseGroup, Indexer, IndexerId, Size, Data)
            VALUES (@Date, @EventType, @MediaType, @MovieId, @SeriesId, @EpisodeIds, @SourceTitle, @QualityId, @Languages, @ReleaseGroup, @Indexer, @IndexerId, @Size, @Data);
            SELECT last_insert_rowid();
            """,
            new
            {
                item.Date,
                EventType = (int)item.EventType,
                MediaType = item.MediaType is { } mediaType ? (int)mediaType : 0,
                item.MovieId,
                item.SeriesId,
                EpisodeIds = item.EpisodeIds.Count == 0 ? null : JoinIds(item.EpisodeIds),
                item.SourceTitle,
                item.QualityId,
                Languages = JoinIds(item.LanguageIds),
                item.ReleaseGroup,
                item.Indexer,
                item.IndexerId,
                item.Size,
                Data = JsonSerializer.Serialize(item.Data),
            });
    }

    /// <summary>Newest first. <paramref name="page"/> starts at 1.</summary>
    public (IReadOnlyList<HistoryItem> Records, int Total) Page(int page, int pageSize, int? movieId = null, int? seriesId = null, HistoryFilter? filter = null)
    {
        var conditions = new List<string> { "(@movieId IS NULL OR MovieId = @movieId)", "(@seriesId IS NULL OR SeriesId = @seriesId)" };
        var parameters = new DynamicParameters(new { movieId, seriesId, take = pageSize, skip = (page - 1) * pageSize });

        if (filter?.EventTypes is { Count: > 0 } eventTypes)
        {
            conditions.Add("EventType IN @eventTypes");
            parameters.Add("eventTypes", eventTypes.Select(e => (int)e).Distinct().ToList());
        }

        if (filter?.MediaType is { } mediaType)
        {
            conditions.Add("MediaType = @mediaType");
            parameters.Add("mediaType", (int)mediaType);
        }

        if (!string.IsNullOrWhiteSpace(filter?.Indexer))
        {
            conditions.Add("Indexer = @indexer COLLATE NOCASE");
            parameters.Add("indexer", filter.Indexer.Trim());
        }

        TextSearch.Where("SourceTitle", filter?.Search, conditions, parameters);

        var where = string.Join(" AND ", conditions);
        using var connection = database.Open();
        var total = connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM History WHERE {where}", parameters);
        var rows = connection.Query<HistoryRow>(
            $"SELECT {Columns} FROM History WHERE {where} ORDER BY Date DESC, Id DESC LIMIT @take OFFSET @skip", parameters);
        return ([.. rows.Select(r => r.ToItem())], total);
    }

    /// <summary>The indexer names that occur in the history (for the filter), sorted.</summary>
    public IReadOnlyList<string> Indexers()
    {
        using var connection = database.Open();
        return [.. connection.Query<string>(
            "SELECT Indexer FROM History WHERE Indexer IS NOT NULL AND TRIM(Indexer) <> '' GROUP BY Indexer COLLATE NOCASE ORDER BY Indexer COLLATE NOCASE")];
    }

    /// <summary>True when the movie has an event of this type at or after <paramref name="since"/>.</summary>
    public bool MovieHasEventSince(int movieId, HistoryEventType eventType, DateTime since)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM History WHERE MovieId = @movieId AND EventType = @eventType AND Date >= @since",
            new { movieId, eventType = (int)eventType, since }) > 0;
    }

    /// <summary>Episode ids of the series with an event of this type at or after <paramref name="since"/>.</summary>
    public IReadOnlySet<int> EpisodesWithEventSince(int seriesId, HistoryEventType eventType, DateTime since)
    {
        using var connection = database.Open();
        return connection.Query<string?>(
                "SELECT EpisodeIds FROM History WHERE SeriesId = @seriesId AND EventType = @eventType AND Date >= @since",
                new { seriesId, eventType = (int)eventType, since })
            .SelectMany(SplitIds)
            .ToHashSet();
    }

    private static string JoinIds(IEnumerable<int> ids) => string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    private static IReadOnlyList<int> SplitIds(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture))];

    private sealed class HistoryRow
    {
        public long Id { get; init; }
        public DateTime Date { get; init; }
        public long EventType { get; init; }
        public long MediaType { get; init; }
        public long? MovieId { get; init; }
        public long? SeriesId { get; init; }
        public string? EpisodeIds { get; init; }
        public string SourceTitle { get; init; } = string.Empty;
        public long QualityId { get; init; }
        public string Languages { get; init; } = string.Empty;
        public string? ReleaseGroup { get; init; }
        public string? Indexer { get; init; }
        public long? IndexerId { get; init; }
        public long Size { get; init; }
        public string Data { get; init; } = "{}";

        public HistoryItem ToItem() => new()
        {
            Id = (int)Id,
            Date = DateTime.SpecifyKind(Date, DateTimeKind.Utc),
            EventType = (HistoryEventType)EventType,
            MediaType = MediaType == 0 ? null : (MediaType)MediaType,
            MovieId = MovieId is { } m ? (int)m : null,
            SeriesId = SeriesId is { } s ? (int)s : null,
            EpisodeIds = SplitIds(EpisodeIds),
            SourceTitle = SourceTitle,
            QualityId = (int)QualityId,
            LanguageIds = SplitIds(Languages),
            ReleaseGroup = ReleaseGroup,
            Indexer = Indexer,
            IndexerId = IndexerId is { } i ? (int)i : null,
            Size = Size,
            Data = JsonSerializer.Deserialize<Dictionary<string, string?>>(Data) ?? [],
        };
    }
}

/// <summary>Finds the series and season of an episode by its id (for "search this episode").</summary>
public sealed class EpisodeLookup(Database database)
{
    public (int SeriesId, int SeasonNumber)? Locate(int episodeId)
    {
        using var connection = database.Open();
        var rows = connection.Query<(long SeriesId, long SeasonNumber)>(
            "SELECT SeriesId, SeasonNumber FROM Episodes WHERE Id = @episodeId", new { episodeId }).ToList();
        return rows.Count == 0 ? null : ((int)rows[0].SeriesId, (int)rows[0].SeasonNumber);
    }
}
