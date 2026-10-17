// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Dapper;
using StreamZH.Core.Downloads;
using StreamZH.Core.Media;

namespace StreamZH.Data;

/// <summary>One download of the built-in queue as stored in the database. Dates are UTC.</summary>
public sealed record DownloadRecord
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public DownloadCategory Category { get; init; }
    public DownloadStatus Status { get; init; }
    public int Priority { get; init; }
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public int MissingArticles { get; init; }
    public string? Error { get; init; }
    public string? StatusMessage { get; init; }
    public string? Indexer { get; init; }
    public MediaType? MediaType { get; init; }
    public int? MovieId { get; init; }
    public int? SeriesId { get; init; }
    public IReadOnlyList<int> EpisodeIds { get; init; } = [];
    public string? Password { get; init; }
    public required string NzbPath { get; init; }
    public required string WorkFolder { get; init; }
    public string? OutputPath { get; init; }
    public DateTime Added { get; init; }
    public DateTime? Completed { get; init; }
    public int SortOrder { get; init; }

    /// <summary>Download category by name; null = the default one of <see cref="Category"/>.</summary>
    public string? CategoryName { get; init; }

    /// <summary>Fingerprint of the NZB (its article ids) for duplicate detection.</summary>
    public string? NzbHash { get; init; }

    /// <summary>Tagged by duplicate detection.</summary>
    public bool Duplicate { get; init; }
}

/// <summary>What duplicate detection and the history cleanup need of a download.</summary>
public sealed record DownloadSummary(int Id, string Title, string? NzbHash, DownloadStatus Status, DateTime Added, DateTime? Completed);

/// <summary>
/// One file of a download's NZB. <see cref="Done"/> and <see cref="Missing"/> hold one bit per segment (NZB order);
/// <see cref="Kind"/> is the numeric NzbFileKind. <see cref="Wanted"/> is false for par2 recovery volumes held back.
/// </summary>
public sealed record DownloadFileRecord
{
    public int Id { get; init; }
    public int DownloadId { get; init; }
    public int FileIndex { get; init; }
    public required string FileName { get; init; }
    public int Kind { get; init; }
    public long TotalBytes { get; init; }
    public int Segments { get; init; }
    public bool Wanted { get; init; } = true;
    public long DownloadedBytes { get; init; }
    public int MissingArticles { get; init; }
    public byte[] Done { get; init; } = [];
    public byte[] Missing { get; init; } = [];
}

public sealed class DownloadRepository(Database database)
{
    /// <summary>The database this repository uses (for helpers that need another repository on the same database).</summary>
    public Database Database => database;

    private const string Columns = """
        Id, Title, Category, Status, Priority, TotalBytes, DownloadedBytes, MissingArticles, Error, StatusMessage, Indexer,
        MediaType, MovieId, SeriesId, EpisodeIds, Password, NzbPath, WorkFolder, OutputPath, Added, Completed, SortOrder,
        CategoryName, NzbHash, Duplicate
        """;

    private const string FileColumns = """
        Id, DownloadId, FileIndex, FileName, Kind, TotalBytes, Segments, Wanted, DownloadedBytes, MissingArticles, Done, Missing
        """;

    /// <summary>Stores a new download at the end of the queue and returns it with its id and sort order.</summary>
    public DownloadRecord Add(DownloadRecord download, IReadOnlyList<DownloadFileRecord> files)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var sortOrder = connection.ExecuteScalar<int>("SELECT IFNULL(MAX(SortOrder), 0) + 1 FROM Downloads", transaction: transaction);
        var stored = download with { SortOrder = sortOrder };
        var id = connection.ExecuteScalar<int>(
            """
            INSERT INTO Downloads (Title, Category, Status, Priority, TotalBytes, DownloadedBytes, MissingArticles, Error, StatusMessage, Indexer,
                MediaType, MovieId, SeriesId, EpisodeIds, Password, NzbPath, WorkFolder, OutputPath, Added, Completed, SortOrder,
                CategoryName, NzbHash, Duplicate)
            VALUES (@Title, @Category, @Status, @Priority, @TotalBytes, @DownloadedBytes, @MissingArticles, @Error, @StatusMessage, @Indexer,
                    @MediaType, @MovieId, @SeriesId, @EpisodeIds, @Password, @NzbPath, @WorkFolder, @OutputPath, @Added, @Completed, @SortOrder,
                    @CategoryName, @NzbHash, @Duplicate);
            SELECT last_insert_rowid();
            """,
            Parameters(stored),
            transaction);

        foreach (var file in files)
        {
            connection.Execute(
                """
                INSERT INTO DownloadFiles (DownloadId, FileIndex, FileName, Kind, TotalBytes, Segments, Wanted, DownloadedBytes, MissingArticles, Done, Missing)
                VALUES (@DownloadId, @FileIndex, @FileName, @Kind, @TotalBytes, @Segments, @Wanted, @DownloadedBytes, @MissingArticles, @Done, @Missing)
                """,
                file with { DownloadId = id },
                transaction);
        }

        transaction.Commit();
        return stored with { Id = id };
    }

    /// <summary>Some code paths need the id before the work folder or NZB path are final; this sets them afterwards.</summary>
    public void SetPaths(int id, string nzbPath, string workFolder)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Downloads SET NzbPath = @nzbPath, WorkFolder = @workFolder WHERE Id = @id", new { id, nzbPath, workFolder });
    }

    public void Update(DownloadRecord download)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Downloads SET Title = @Title, Category = @Category, Status = @Status, Priority = @Priority, TotalBytes = @TotalBytes,
                DownloadedBytes = @DownloadedBytes, MissingArticles = @MissingArticles, Error = @Error, StatusMessage = @StatusMessage,
                Password = @Password, NzbPath = @NzbPath, WorkFolder = @WorkFolder, OutputPath = @OutputPath, Completed = @Completed, SortOrder = @SortOrder,
                CategoryName = @CategoryName, Duplicate = @Duplicate
            WHERE Id = @Id
            """,
            Parameters(download));
    }

    /// <summary>Saves progress of several files at once (bitmaps, counters, wanted flag).</summary>
    public void UpdateFiles(IEnumerable<DownloadFileRecord> files)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var file in files)
        {
            connection.Execute(
                """
                UPDATE DownloadFiles SET Wanted = @Wanted, DownloadedBytes = @DownloadedBytes, MissingArticles = @MissingArticles, Done = @Done, Missing = @Missing
                WHERE Id = @Id
                """,
                file,
                transaction);
        }

        transaction.Commit();
    }

    public DownloadRecord? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<DownloadRow>($"SELECT {Columns} FROM Downloads WHERE Id = @id", new { id })?.ToRecord();
    }

    /// <summary>Everything that is not completed or failed, in queue order.</summary>
    public IReadOnlyList<DownloadRecord> Unfinished()
    {
        using var connection = database.Open();
        return [.. connection.Query<DownloadRow>(
                $"SELECT {Columns} FROM Downloads WHERE Status NOT IN (@completed, @failed) ORDER BY Priority DESC, SortOrder, Id",
                new { completed = (int)DownloadStatus.Completed, failed = (int)DownloadStatus.Failed })
            .Select(r => r.ToRecord())];
    }

    /// <summary>Completed downloads that belong to a movie or series (candidates for the import).</summary>
    public IReadOnlyList<DownloadRecord> CompletedWithLink()
    {
        using var connection = database.Open();
        return [.. connection.Query<DownloadRow>(
                $"SELECT {Columns} FROM Downloads WHERE Status = @completed AND (MovieId IS NOT NULL OR SeriesId IS NOT NULL) ORDER BY Id",
                new { completed = (int)DownloadStatus.Completed })
            .Select(r => r.ToRecord())];
    }

    /// <summary>Completed and failed downloads, newest first. <paramref name="page"/> starts at 1.</summary>
    public (IReadOnlyList<DownloadRecord> Records, int Total) History(int page, int pageSize)
    {
        const string Filter = "Status IN (@completed, @failed)";
        using var connection = database.Open();
        var parameters = new
        {
            completed = (int)DownloadStatus.Completed,
            failed = (int)DownloadStatus.Failed,
            take = pageSize,
            skip = (page - 1) * pageSize,
        };
        var total = connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM Downloads WHERE {Filter}", parameters);
        var rows = connection.Query<DownloadRow>(
            $"SELECT {Columns} FROM Downloads WHERE {Filter} ORDER BY IFNULL(Completed, Added) DESC, Id DESC LIMIT @take OFFSET @skip", parameters);
        return ([.. rows.Select(r => r.ToRecord())], total);
    }

    /// <summary>Every download (queue and history), newest first, without the details.</summary>
    public IReadOnlyList<DownloadSummary> Summaries()
    {
        using var connection = database.Open();
        return [.. connection.Query<(long Id, string Title, string? NzbHash, long Status, DateTime Added, DateTime? Completed)>(
                "SELECT Id, Title, NzbHash, Status, Added, Completed FROM Downloads ORDER BY IFNULL(Completed, Added) DESC, Id DESC")
            .Select(r => new DownloadSummary(
                (int)r.Id,
                r.Title,
                r.NzbHash,
                (DownloadStatus)r.Status,
                DateTime.SpecifyKind(r.Added, DateTimeKind.Utc),
                r.Completed is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null))];
    }

    public IReadOnlyList<DownloadFileRecord> Files(int downloadId)
    {
        using var connection = database.Open();
        return [.. connection.Query<FileRow>($"SELECT {FileColumns} FROM DownloadFiles WHERE DownloadId = @downloadId ORDER BY FileIndex", new { downloadId })
            .Select(r => r.ToRecord())];
    }

    /// <summary>Removes the download and its file rows.</summary>
    public bool Delete(int id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM Downloads WHERE Id = @id", new { id }) > 0;
    }

    public void SetSortOrders(IEnumerable<(int Id, int SortOrder)> orders)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (id, sortOrder) in orders)
        {
            connection.Execute("UPDATE Downloads SET SortOrder = @sortOrder WHERE Id = @id", new { id, sortOrder }, transaction);
        }

        transaction.Commit();
    }

    private static object Parameters(DownloadRecord d) => new
    {
        d.Id,
        d.Title,
        Category = (int)d.Category,
        Status = (int)d.Status,
        d.Priority,
        d.TotalBytes,
        d.DownloadedBytes,
        d.MissingArticles,
        d.Error,
        d.StatusMessage,
        d.Indexer,
        MediaType = d.MediaType is { } mediaType ? (int?)mediaType : null,
        d.MovieId,
        d.SeriesId,
        EpisodeIds = d.EpisodeIds.Count == 0 ? null : string.Join(',', d.EpisodeIds.Select(i => i.ToString(CultureInfo.InvariantCulture))),
        d.Password,
        d.NzbPath,
        d.WorkFolder,
        d.OutputPath,
        d.Added,
        d.Completed,
        d.SortOrder,
        d.CategoryName,
        d.NzbHash,
        d.Duplicate,
    };

    private sealed class DownloadRow
    {
        public long Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public long Category { get; init; }
        public long Status { get; init; }
        public long Priority { get; init; }
        public long TotalBytes { get; init; }
        public long DownloadedBytes { get; init; }
        public long MissingArticles { get; init; }
        public string? Error { get; init; }
        public string? StatusMessage { get; init; }
        public string? Indexer { get; init; }
        public long? MediaType { get; init; }
        public long? MovieId { get; init; }
        public long? SeriesId { get; init; }
        public string? EpisodeIds { get; init; }
        public string? Password { get; init; }
        public string NzbPath { get; init; } = string.Empty;
        public string WorkFolder { get; init; } = string.Empty;
        public string? OutputPath { get; init; }
        public DateTime Added { get; init; }
        public DateTime? Completed { get; init; }
        public long SortOrder { get; init; }
        public string? CategoryName { get; init; }
        public string? NzbHash { get; init; }
        public bool Duplicate { get; init; }

        public DownloadRecord ToRecord() => new()
        {
            Id = (int)Id,
            Title = Title,
            Category = (DownloadCategory)Category,
            Status = (DownloadStatus)Status,
            Priority = (int)Priority,
            TotalBytes = TotalBytes,
            DownloadedBytes = DownloadedBytes,
            MissingArticles = (int)MissingArticles,
            Error = Error,
            StatusMessage = StatusMessage,
            Indexer = Indexer,
            MediaType = MediaType is { } m ? (MediaType)m : null,
            MovieId = MovieId is { } movie ? (int)movie : null,
            SeriesId = SeriesId is { } series ? (int)series : null,
            EpisodeIds = string.IsNullOrEmpty(EpisodeIds)
                ? []
                : [.. EpisodeIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture))],
            Password = Password,
            NzbPath = NzbPath,
            WorkFolder = WorkFolder,
            OutputPath = OutputPath,
            Added = DateTime.SpecifyKind(Added, DateTimeKind.Utc),
            Completed = Completed is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null,
            SortOrder = (int)SortOrder,
            CategoryName = CategoryName,
            NzbHash = NzbHash,
            Duplicate = Duplicate,
        };
    }

    private sealed class FileRow
    {
        public long Id { get; init; }
        public long DownloadId { get; init; }
        public long FileIndex { get; init; }
        public string FileName { get; init; } = string.Empty;
        public long Kind { get; init; }
        public long TotalBytes { get; init; }
        public long Segments { get; init; }
        public bool Wanted { get; init; }
        public long DownloadedBytes { get; init; }
        public long MissingArticles { get; init; }
        public byte[] Done { get; init; } = [];
        public byte[] Missing { get; init; } = [];

        public DownloadFileRecord ToRecord() => new()
        {
            Id = (int)Id,
            DownloadId = (int)DownloadId,
            FileIndex = (int)FileIndex,
            FileName = FileName,
            Kind = (int)Kind,
            TotalBytes = TotalBytes,
            Segments = (int)Segments,
            Wanted = Wanted,
            DownloadedBytes = DownloadedBytes,
            MissingArticles = (int)MissingArticles,
            Done = Done,
            Missing = Missing,
        };
    }
}
