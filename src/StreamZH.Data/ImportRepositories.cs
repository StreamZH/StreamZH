// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using StreamZH.Core.Media;

namespace StreamZH.Data;

/// <summary>Where the import of a finished download stands. Values are stored in the database; never renumber.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImportState>))]
public enum ImportState
{
    /// <summary>Waiting in the job queue.</summary>
    Queued = 1,

    /// <summary>Files are being moved into the library right now.</summary>
    Importing = 2,

    /// <summary>At least one file was imported.</summary>
    Imported = 3,

    /// <summary>Nothing was imported; <see cref="DownloadImport.Message"/> says why. The downloaded files are kept.</summary>
    Failed = 4,

    /// <summary>Held back for a while (e.g. the episode title is not known yet); tried again automatically.</summary>
    Waiting = 5,
}

/// <summary>The import of one finished download (completed download handling or a manual import).</summary>
/// <param name="Files">Full paths of the imported files in the library.</param>
/// <param name="Manual">JSON of a manual import (chosen movie/series and episodes per file); null for automatic imports.</param>
/// <param name="Dismissed">The status-window notice of a failed import was closed.</param>
public sealed record DownloadImport(
    int DownloadId,
    ImportState State,
    string? Message,
    DateTime Date,
    string Title,
    MediaType? MediaType,
    int? MovieId,
    int? SeriesId,
    IReadOnlyList<int> EpisodeIds,
    IReadOnlyList<string> Files,
    bool IsUpgrade = false,
    string? Manual = null,
    bool Dismissed = false);

public sealed class DownloadImportRepository(Database database)
{
    private const string Columns = "DownloadId, State, Message, Date, Title, MediaType, MovieId, SeriesId, EpisodeIds, Files, IsUpgrade, Manual, Dismissed";

    public DownloadImport? Find(int downloadId)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM DownloadImports WHERE DownloadId = @downloadId", new { downloadId })?.ToImport();
    }

    public IReadOnlyList<DownloadImport> ForDownloads(IEnumerable<int> downloadIds)
    {
        var ids = downloadIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM DownloadImports WHERE DownloadId IN @ids", new { ids }).Select(r => r.ToImport())];
    }

    public IReadOnlyList<DownloadImport> WithState(params ImportState[] states)
    {
        using var connection = database.Open();
        var values = states.Select(s => (int)s).ToList();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM DownloadImports WHERE State IN @values ORDER BY Date", new { values }).Select(r => r.ToImport())];
    }

    public void Save(DownloadImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO DownloadImports (DownloadId, State, Message, Date, Title, MediaType, MovieId, SeriesId, EpisodeIds, Files, IsUpgrade, Manual, Dismissed)
            VALUES (@DownloadId, @State, @Message, @Date, @Title, @MediaType, @MovieId, @SeriesId, @EpisodeIds, @Files, @IsUpgrade, @Manual, @Dismissed)
            ON CONFLICT(DownloadId) DO UPDATE SET State = excluded.State, Message = excluded.Message, Date = excluded.Date, Title = excluded.Title,
                MediaType = excluded.MediaType, MovieId = excluded.MovieId, SeriesId = excluded.SeriesId, EpisodeIds = excluded.EpisodeIds,
                Files = excluded.Files, IsUpgrade = excluded.IsUpgrade, Manual = excluded.Manual, Dismissed = excluded.Dismissed
            """,
            new
            {
                import.DownloadId,
                State = (int)import.State,
                import.Message,
                import.Date,
                import.Title,
                MediaType = import.MediaType is { } type ? (int)type : 0,
                import.MovieId,
                import.SeriesId,
                EpisodeIds = Ids.Join(import.EpisodeIds),
                Files = JsonSerializer.Serialize(import.Files),
                import.IsUpgrade,
                import.Manual,
                import.Dismissed,
            });
    }

    public void Dismiss(int downloadId)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE DownloadImports SET Dismissed = 1 WHERE DownloadId = @downloadId", new { downloadId });
    }

    public void Delete(int downloadId)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM DownloadImports WHERE DownloadId = @downloadId", new { downloadId });
    }

    private sealed class Row
    {
        public long DownloadId { get; init; }
        public long State { get; init; }
        public string? Message { get; init; }
        public DateTime Date { get; init; }
        public string Title { get; init; } = string.Empty;
        public long MediaType { get; init; }
        public long? MovieId { get; init; }
        public long? SeriesId { get; init; }
        public string? EpisodeIds { get; init; }
        public string Files { get; init; } = "[]";
        public bool IsUpgrade { get; init; }
        public string? Manual { get; init; }
        public bool Dismissed { get; init; }

        public DownloadImport ToImport() => new(
            (int)DownloadId,
            (ImportState)State,
            Message,
            DateTime.SpecifyKind(Date, DateTimeKind.Utc),
            Title,
            MediaType == 0 ? null : (MediaType)MediaType,
            MovieId is { } m ? (int)m : null,
            SeriesId is { } s ? (int)s : null,
            Ids.Split(EpisodeIds),
            JsonSerializer.Deserialize<List<string>>(Files) ?? [],
            IsUpgrade,
            Manual,
            Dismissed);
    }
}

/// <summary>A release that failed (download failed or was removed as bad); it is not grabbed again for the same item.</summary>
public sealed record BlocklistItem
{
    public int Id { get; init; }
    public DateTime Date { get; init; }
    public MediaType? MediaType { get; init; }
    public int? MovieId { get; init; }
    public int? SeriesId { get; init; }
    public IReadOnlyList<int> EpisodeIds { get; init; } = [];
    public required string SourceTitle { get; init; }
    public int QualityId { get; init; }
    public IReadOnlyList<int> LanguageIds { get; init; } = [];
    public string? Indexer { get; init; }
    public long Size { get; init; }
    public string? Message { get; init; }
}

public sealed class BlocklistRepository(Database database)
{
    private const string Columns = "Id, Date, MediaType, MovieId, SeriesId, EpisodeIds, SourceTitle, QualityId, Languages, Indexer, Size, Message";

    public int Add(BlocklistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO Blocklist (Date, MediaType, MovieId, SeriesId, EpisodeIds, SourceTitle, QualityId, Languages, Indexer, Size, Message)
            VALUES (@Date, @MediaType, @MovieId, @SeriesId, @EpisodeIds, @SourceTitle, @QualityId, @Languages, @Indexer, @Size, @Message);
            SELECT last_insert_rowid();
            """,
            new
            {
                item.Date,
                MediaType = item.MediaType is { } type ? (int)type : 0,
                item.MovieId,
                item.SeriesId,
                EpisodeIds = Ids.Join(item.EpisodeIds),
                item.SourceTitle,
                item.QualityId,
                Languages = Ids.Join(item.LanguageIds) ?? string.Empty,
                item.Indexer,
                item.Size,
                item.Message,
            });
    }

    /// <summary>Newest first. <paramref name="page"/> starts at 1.</summary>
    /// <param name="search">Words that must all appear in the release title (case-insensitive).</param>
    /// <param name="mediaType">Movie or Series; null for all.</param>
    public (IReadOnlyList<BlocklistItem> Records, int Total) Page(int page, int pageSize, string? search = null, MediaType? mediaType = null)
    {
        var conditions = new List<string> { "1 = 1" };
        var parameters = new DynamicParameters(new { take = pageSize, skip = (page - 1) * pageSize });
        if (mediaType is { } type)
        {
            conditions.Add("MediaType = @mediaType");
            parameters.Add("mediaType", (int)type);
        }

        TextSearch.Where("SourceTitle", search, conditions, parameters);

        var where = string.Join(" AND ", conditions);
        using var connection = database.Open();
        var total = connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM Blocklist WHERE {where}", parameters);
        var rows = connection.Query<Row>(
            $"SELECT {Columns} FROM Blocklist WHERE {where} ORDER BY Date DESC, Id DESC LIMIT @take OFFSET @skip", parameters);
        return ([.. rows.Select(r => r.ToItem())], total);
    }

    public BlocklistItem? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM Blocklist WHERE Id = @id", new { id })?.ToItem();
    }

    public bool Delete(int id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM Blocklist WHERE Id = @id", new { id }) > 0;
    }

    public int Clear()
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM Blocklist");
    }

    /// <summary>Blocklisted release titles of a movie (case-insensitive set).</summary>
    public IReadOnlySet<string> TitlesForMovie(int movieId)
    {
        using var connection = database.Open();
        return connection.Query<string>("SELECT SourceTitle FROM Blocklist WHERE MovieId = @movieId", new { movieId })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Blocklisted release titles of a series (any episode; a title names its episodes anyway).</summary>
    public IReadOnlySet<string> TitlesForSeries(int seriesId)
    {
        using var connection = database.Open();
        return connection.Query<string>("SELECT SourceTitle FROM Blocklist WHERE SeriesId = @seriesId", new { seriesId })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public DateTime Date { get; init; }
        public long MediaType { get; init; }
        public long? MovieId { get; init; }
        public long? SeriesId { get; init; }
        public string? EpisodeIds { get; init; }
        public string SourceTitle { get; init; } = string.Empty;
        public long QualityId { get; init; }
        public string Languages { get; init; } = string.Empty;
        public string? Indexer { get; init; }
        public long Size { get; init; }
        public string? Message { get; init; }

        public BlocklistItem ToItem() => new()
        {
            Id = (int)Id,
            Date = DateTime.SpecifyKind(Date, DateTimeKind.Utc),
            MediaType = MediaType == 0 ? null : (MediaType)MediaType,
            MovieId = MovieId is { } m ? (int)m : null,
            SeriesId = SeriesId is { } s ? (int)s : null,
            EpisodeIds = Ids.Split(EpisodeIds),
            SourceTitle = SourceTitle,
            QualityId = (int)QualityId,
            LanguageIds = Ids.Split(Languages),
            Indexer = Indexer,
            Size = Size,
            Message = Message,
        };
    }
}

/// <summary>
/// What StreamZH knows about a media file it imported: quality and revision, languages, release group and the release
/// it came from, and what ffprobe found in it (<see cref="MediaInfo"/>). Keyed by the full path, so a disk scan (which
/// only sees names and sizes) never loses it. Library files that were not imported get a row when they are analysed.
/// </summary>
/// <param name="MediaInfo">ffprobe's analysis; null when the file was not analysed yet.</param>
/// <param name="MediaInfoUpdated">When it was analysed (UTC).</param>
public sealed record MediaFileDetails(
    string Path,
    int QualityId,
    int RevisionVersion,
    int RevisionReal,
    bool IsRepack,
    IReadOnlyList<int> LanguageIds,
    string? ReleaseGroup,
    string? SceneName,
    string? Edition,
    string? VideoCodec,
    string? AudioCodec,
    DateTime DateAdded,
    Core.Media.MediaInfo? MediaInfo = null,
    DateTime? MediaInfoUpdated = null)
{
    /// <summary>
    /// The languages of the file: from the release name, else (no language in the name) the audio languages ffprobe found,
    /// so an existing German file counts as German (or German + original as dual language) for profiles and custom formats.
    /// </summary>
    public IReadOnlyList<Core.Quality.Language> Languages
    {
        get
        {
            var named = LanguageIds.Select(Core.Quality.Language.FindById).Where(l => l.Id > 0).ToList();
            return named.Count > 0 ? named : MediaInfo?.AudioLanguages ?? [];
        }
    }
}

public sealed class MediaFileDetailsRepository(Database database)
{
    private const string Columns =
        "Path, QualityId, RevisionVersion, RevisionReal, IsRepack, Languages, ReleaseGroup, SceneName, Edition, VideoCodec, AudioCodec, DateAdded, MediaInfo, MediaInfoUpdated";

    private static readonly System.Text.Json.JsonSerializerOptions MediaInfoJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>The stored key: the full path, lower-case where file names are not case-sensitive (Windows, macOS).</summary>
    public static string Key(string path)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        return OperatingSystem.IsLinux() ? full : full.ToLowerInvariant();
    }

    public MediaFileDetails? Find(string path)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM MediaFileDetails WHERE Path = @path", new { path = Key(path) })?.ToDetails();
    }

    public void Save(MediaFileDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO MediaFileDetails (Path, QualityId, RevisionVersion, RevisionReal, IsRepack, Languages, ReleaseGroup, SceneName, Edition, VideoCodec, AudioCodec, DateAdded, MediaInfo, MediaInfoUpdated)
            VALUES (@Path, @QualityId, @RevisionVersion, @RevisionReal, @IsRepack, @Languages, @ReleaseGroup, @SceneName, @Edition, @VideoCodec, @AudioCodec, @DateAdded, @MediaInfo, @MediaInfoUpdated)
            ON CONFLICT(Path) DO UPDATE SET QualityId = excluded.QualityId, RevisionVersion = excluded.RevisionVersion, RevisionReal = excluded.RevisionReal,
                IsRepack = excluded.IsRepack, Languages = excluded.Languages, ReleaseGroup = excluded.ReleaseGroup, SceneName = excluded.SceneName,
                Edition = excluded.Edition, VideoCodec = excluded.VideoCodec, AudioCodec = excluded.AudioCodec, DateAdded = excluded.DateAdded,
                MediaInfo = excluded.MediaInfo, MediaInfoUpdated = excluded.MediaInfoUpdated
            """,
            new
            {
                Path = Key(details.Path),
                details.QualityId,
                details.RevisionVersion,
                details.RevisionReal,
                details.IsRepack,
                Languages = Ids.Join(details.LanguageIds) ?? string.Empty,
                details.ReleaseGroup,
                details.SceneName,
                details.Edition,
                details.VideoCodec,
                details.AudioCodec,
                details.DateAdded,
                MediaInfo = Serialize(details.MediaInfo),
                details.MediaInfoUpdated,
            });
    }

    /// <summary>Stores a new analysis of the file; the rest of the row stays. False when the file has no row.</summary>
    public bool SetMediaInfo(string path, Core.Media.MediaInfo? mediaInfo, DateTime updated)
    {
        using var connection = database.Open();
        return connection.Execute(
            "UPDATE MediaFileDetails SET MediaInfo = @mediaInfo, MediaInfoUpdated = @updated WHERE Path = @path",
            new { path = Key(path), mediaInfo = Serialize(mediaInfo), updated }) > 0;
    }

    /// <summary>The rows of the files in a folder (and its subfolders), by stored key.</summary>
    public IReadOnlyDictionary<string, MediaFileDetails> InFolder(string folder)
    {
        var prefix = Key(folder) + System.IO.Path.DirectorySeparatorChar;
        using var connection = database.Open();
        return connection.Query<Row>(
                $"SELECT {Columns} FROM MediaFileDetails WHERE substr(Path, 1, @length) = @prefix",
                new { prefix, length = prefix.Length })
            .Select(r => r.ToDetails())
            .ToDictionary(d => d.Path, StringComparer.Ordinal);
    }

    private static string? Serialize(Core.Media.MediaInfo? mediaInfo) =>
        mediaInfo is null ? null : System.Text.Json.JsonSerializer.Serialize(mediaInfo, MediaInfoJson);

    /// <summary>The stored analysis; null when there is none or it cannot be read (it is analysed again then).</summary>
    public static Core.Media.MediaInfo? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Core.Media.MediaInfo>(json, MediaInfoJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The file was renamed or moved: the details move with it.</summary>
    public void Move(string from, string to)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM MediaFileDetails WHERE Path = @to AND Path <> @from", new { from = Key(from), to = Key(to) });
        connection.Execute("UPDATE MediaFileDetails SET Path = @to WHERE Path = @from", new { from = Key(from), to = Key(to) });
    }

    public void Delete(string path)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM MediaFileDetails WHERE Path = @path", new { path = Key(path) });
    }

    private sealed class Row
    {
        public string Path { get; init; } = string.Empty;
        public long QualityId { get; init; }
        public long RevisionVersion { get; init; }
        public long RevisionReal { get; init; }
        public bool IsRepack { get; init; }
        public string Languages { get; init; } = string.Empty;
        public string? ReleaseGroup { get; init; }
        public string? SceneName { get; init; }
        public string? Edition { get; init; }
        public string? VideoCodec { get; init; }
        public string? AudioCodec { get; init; }
        public DateTime DateAdded { get; init; }
        public string? MediaInfo { get; init; }
        public DateTime? MediaInfoUpdated { get; init; }

        public MediaFileDetails ToDetails() => new(
            Path, (int)QualityId, (int)RevisionVersion, (int)RevisionReal, IsRepack, Ids.Split(Languages), ReleaseGroup, SceneName, Edition,
            VideoCodec, AudioCodec, DateTime.SpecifyKind(DateAdded, DateTimeKind.Utc), Deserialize(MediaInfo),
            MediaInfoUpdated is { } updated ? DateTime.SpecifyKind(updated, DateTimeKind.Utc) : null);
    }
}

/// <summary>Sets the file of single episodes (an import touches a few episodes, not the whole series).</summary>
public sealed class EpisodeFileRepository(Database database)
{
    public void SetFile(IEnumerable<int> episodeIds, MediaFile? file)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var id in episodeIds)
        {
            connection.Execute(
                "UPDATE Episodes SET FileRelativePath = @RelativePath, FileSize = @Size, FileModified = @Modified WHERE Id = @id",
                new { id, file?.RelativePath, file?.Size, file?.Modified },
                transaction);
        }

        transaction.Commit();
    }

    /// <summary>Episode ids of the series whose file is <paramref name="relativePath"/>.</summary>
    public IReadOnlyList<int> EpisodesWithFile(int seriesId, string relativePath)
    {
        using var connection = database.Open();
        return [.. connection.Query<long>(
            "SELECT Id FROM Episodes WHERE SeriesId = @seriesId AND FileRelativePath = @relativePath", new { seriesId, relativePath }).Select(i => (int)i)];
    }
}

internal static class Ids
{
    public static string? Join(IReadOnlyList<int> ids) =>
        ids.Count == 0 ? null : string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    public static IReadOnlyList<int> Split(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture))];
}
