// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Dapper;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>
/// The few writes of the library sections the repositories do one row at a time: episode monitoring and files of a whole
/// series, history and blocklist rows in batches (one transaction each), and the added date of a taken-over item.
/// </summary>
public sealed class LibraryMigrationStore(Database database)
{
    /// <summary>History rows written by a migration carry this data key ("radarr:1234"), so a second run skips them.</summary>
    public const string MigratedFromKey = "migratedFrom";

    public void SetMovieAdded(int movieId, DateTime added)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Movies SET Added = @added WHERE Id = @movieId", new { movieId, added = Utc(added) });
    }

    public void SetSeriesAdded(int seriesId, DateTime added)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Series SET Added = @added WHERE Id = @seriesId", new { seriesId, added = Utc(added) });
    }

    /// <summary>Monitored flags and files of many episodes in one transaction. A null file in <paramref name="files"/> is not written.</summary>
    public void UpdateEpisodes(IReadOnlyDictionary<int, bool> monitored, IReadOnlyDictionary<int, MediaFile> files)
    {
        if (monitored.Count == 0 && files.Count == 0)
        {
            return;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (id, value) in monitored)
        {
            connection.Execute("UPDATE Episodes SET Monitored = @value WHERE Id = @id", new { id, value }, transaction);
        }

        foreach (var (id, file) in files)
        {
            connection.Execute(
                "UPDATE Episodes SET FileRelativePath = @RelativePath, FileSize = @Size, FileModified = @Modified WHERE Id = @id",
                new { id, file.RelativePath, file.Size, file.Modified },
                transaction);
        }

        transaction.Commit();
    }

    /// <summary>The <see cref="MigratedFromKey"/> markers of history rows already taken over ("radarr:1234").</summary>
    public IReadOnlySet<string> MigratedHistory()
    {
        using var connection = database.Open();
        var markers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var data in connection.Query<string>("SELECT Data FROM History WHERE Data LIKE '%\"migratedFrom\"%'"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Dictionary<string, string?>>(data)?.GetValueOrDefault(MigratedFromKey) is { Length: > 0 } marker)
                {
                    markers.Add(marker);
                }
            }
            catch (JsonException)
            {
                // A row this code did not write; ignore it.
            }
        }

        return markers;
    }

    /// <summary>Adds history rows in one transaction.</summary>
    public void AddHistory(IReadOnlyList<HistoryItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var item in items)
        {
            connection.Execute(
                """
                INSERT INTO History (Date, EventType, MediaType, MovieId, SeriesId, EpisodeIds, SourceTitle, QualityId, Languages, ReleaseGroup, Indexer, IndexerId, Size, Data)
                VALUES (@Date, @EventType, @MediaType, @MovieId, @SeriesId, @EpisodeIds, @SourceTitle, @QualityId, @Languages, @ReleaseGroup, @Indexer, NULL, @Size, @Data)
                """,
                new
                {
                    Date = Utc(item.Date),
                    EventType = (int)item.EventType,
                    MediaType = item.MediaType is { } type ? (int)type : 0,
                    item.MovieId,
                    item.SeriesId,
                    EpisodeIds = item.EpisodeIds.Count == 0 ? null : Join(item.EpisodeIds),
                    item.SourceTitle,
                    item.QualityId,
                    Languages = Join(item.LanguageIds),
                    item.ReleaseGroup,
                    item.Indexer,
                    item.Size,
                    Data = JsonSerializer.Serialize(item.Data),
                },
                transaction);
        }

        transaction.Commit();
    }

    /// <summary>Keys (title|movie|series|date) of the blocklist rows, to skip releases taken over before.</summary>
    public IReadOnlySet<string> BlocklistKeys()
    {
        using var connection = database.Open();
        return connection.Query<(string SourceTitle, long? MovieId, long? SeriesId, DateTime Date)>("SELECT SourceTitle, MovieId, SeriesId, Date FROM Blocklist")
            .Select(r => BlocklistKey(r.SourceTitle, (int?)r.MovieId, (int?)r.SeriesId, r.Date))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static string BlocklistKey(string sourceTitle, int? movieId, int? seriesId, DateTime date) =>
        string.Join(
            '|',
            sourceTitle.Trim(),
            movieId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            seriesId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Utc(date).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));

    /// <summary>Adds blocklist rows in one transaction.</summary>
    public void AddBlocklist(IReadOnlyList<BlocklistItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var item in items)
        {
            connection.Execute(
                """
                INSERT INTO Blocklist (Date, MediaType, MovieId, SeriesId, EpisodeIds, SourceTitle, QualityId, Languages, Indexer, Size, Message)
                VALUES (@Date, @MediaType, @MovieId, @SeriesId, @EpisodeIds, @SourceTitle, @QualityId, @Languages, @Indexer, @Size, @Message)
                """,
                new
                {
                    Date = Utc(item.Date),
                    MediaType = item.MediaType is { } type ? (int)type : 0,
                    item.MovieId,
                    item.SeriesId,
                    EpisodeIds = item.EpisodeIds.Count == 0 ? null : Join(item.EpisodeIds),
                    item.SourceTitle,
                    item.QualityId,
                    Languages = Join(item.LanguageIds),
                    item.Indexer,
                    item.Size,
                    item.Message,
                },
                transaction);
        }

        transaction.Commit();
    }

    private static string Join(IEnumerable<int> ids) => string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
