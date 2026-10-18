// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Dapper;
using StreamZH.Core.Media;

namespace StreamZH.Data;

/// <summary>When an automatic search last looked for a movie or an episode (Radarr/Sonarr "last searched"), for Wanted.</summary>
public sealed class LastSearchRepository(Database database)
{
    private const int MovieType = 1;
    private const int EpisodeType = 2;

    public void MarkMovies(IEnumerable<int> movieIds, DateTime time) => Mark(MovieType, movieIds, time);

    public void MarkEpisodes(IEnumerable<int> episodeIds, DateTime time) => Mark(EpisodeType, episodeIds, time);

    /// <summary>Movie id → last automatic search (UTC).</summary>
    public IReadOnlyDictionary<int, DateTime> Movies() => Read(MovieType);

    /// <summary>Episode id → last automatic search (UTC).</summary>
    public IReadOnlyDictionary<int, DateTime> Episodes() => Read(EpisodeType);

    private void Mark(int type, IEnumerable<int> ids, DateTime time)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0)
        {
            return;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var utc = time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime();
        foreach (var id in list)
        {
            connection.Execute(
                """
                INSERT INTO LastSearches (MediaType, ItemId, Time) VALUES (@type, @id, @utc)
                ON CONFLICT(MediaType, ItemId) DO UPDATE SET Time = excluded.Time
                """,
                new { type, id, utc },
                transaction);
        }

        transaction.Commit();
    }

    private Dictionary<int, DateTime> Read(int type)
    {
        using var connection = database.Open();
        return connection.Query<(long ItemId, DateTime Time)>("SELECT ItemId, Time FROM LastSearches WHERE MediaType = @type", new { type })
            .ToDictionary(r => (int)r.ItemId, r => DateTime.SpecifyKind(r.Time, DateTimeKind.Utc));
    }
}

/// <summary>A grab from the history: what it was for.</summary>
public sealed record GrabbedItem(int? MovieId, int? SeriesId, IReadOnlyList<int> EpisodeIds);

/// <summary>Bulk reads for Wanted and Calendar, so a list of thousands of episodes stays a handful of queries.</summary>
public sealed class WantedQueries(Database database)
{
    /// <summary>Every episode of every series.</summary>
    public IReadOnlyList<Episode> AllEpisodes()
    {
        using var connection = database.Open();
        return [.. connection.Query<EpisodeRow>(
            """
            SELECT Id, SeriesId, SeasonNumber, EpisodeNumber, Title, AirDate, Overview, Monitored, FileRelativePath, FileSize, FileModified
            FROM Episodes ORDER BY SeriesId, SeasonNumber, EpisodeNumber
            """).Select(r => r.ToEpisode())];
    }

    /// <summary>Episodes with an air date in [<paramref name="from"/>, <paramref name="to"/>] (yyyy-MM-dd, inclusive).</summary>
    public IReadOnlyList<Episode> EpisodesAiring(string from, string to)
    {
        using var connection = database.Open();
        return [.. connection.Query<EpisodeRow>(
            """
            SELECT Id, SeriesId, SeasonNumber, EpisodeNumber, Title, AirDate, Overview, Monitored, FileRelativePath, FileSize, FileModified
            FROM Episodes WHERE AirDate IS NOT NULL AND AirDate >= @from AND AirDate <= @to ORDER BY AirDate, SeriesId, SeasonNumber, EpisodeNumber
            """,
            new { from, to }).Select(r => r.ToEpisode())];
    }

    /// <summary>Grabs at or after <paramref name="since"/> (the blackhole folder's "on its way").</summary>
    public IReadOnlyList<GrabbedItem> GrabsSince(DateTime since)
    {
        using var connection = database.Open();
        return [.. connection.Query<(long? MovieId, long? SeriesId, string? EpisodeIds)>(
                "SELECT MovieId, SeriesId, EpisodeIds FROM History WHERE EventType = @grabbed AND Date >= @since",
                new { grabbed = (int)HistoryEventType.Grabbed, since })
            .Select(r => new GrabbedItem(
                r.MovieId is { } m ? (int)m : null,
                r.SeriesId is { } s ? (int)s : null,
                string.IsNullOrEmpty(r.EpisodeIds)
                    ? []
                    : [.. r.EpisodeIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture))]))];
    }

    private sealed class EpisodeRow
    {
        public long Id { get; init; }
        public long SeriesId { get; init; }
        public long SeasonNumber { get; init; }
        public long EpisodeNumber { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? AirDate { get; init; }
        public string? Overview { get; init; }
        public bool Monitored { get; init; }
        public string? FileRelativePath { get; init; }
        public long? FileSize { get; init; }
        public DateTime? FileModified { get; init; }

        public Episode ToEpisode() => new()
        {
            Id = (int)Id,
            SeriesId = (int)SeriesId,
            SeasonNumber = (int)SeasonNumber,
            EpisodeNumber = (int)EpisodeNumber,
            Title = Title,
            AirDate = AirDate,
            Overview = Overview,
            Monitored = Monitored,
            File = FileRelativePath is null
                ? null
                : new MediaFile(FileRelativePath, FileSize ?? 0, DateTime.SpecifyKind(FileModified ?? DateTime.MinValue, DateTimeKind.Utc)),
        };
    }
}
