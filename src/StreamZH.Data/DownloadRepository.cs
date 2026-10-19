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

    /// <summary>0.11: Usenet rows belong to the built-in engine; torrent rows to a download client (<see cref="ClientId"/>).</summary>
    public DownloadProtocol Protocol { get; init; } = DownloadProtocol.Usenet;

    /// <summary>Torrent: the DownloadClients row.</summary>
    public int? ClientId { get; init; }

    /// <summary>Torrent: the info hash (hex, lower case).</summary>
    public string? ExternalId { get; init; }

    /// <summary>Torrent: done and still in the download client (seeding, or stopped but not removed yet).</summary>
    public bool Seeding { get; init; }

    public double? Ratio { get; init; }

    public long UploadedBytes { get; init; }

    public int? Seeders { get; init; }

    public int? Peers { get; init; }

    /// <summary>Torrent: seed goals from the indexer (null = the client's own limits).</summary>
    public double? SeedRatio { get; init; }

    public int? SeedTimeMinutes { get; init; }
}

/// <summary>Which finished downloads by their import (see <see cref="DownloadRepository.History"/>).</summary>
public enum DownloadImportFilter
{
    /// <summary>Completed, and at least one file went into the library.</summary>
    Imported,

    /// <summary>Completed, but not (yet) imported: no import, or one that is queued, running or waiting.</summary>
    NotImported,

    /// <summary>Completed, and the import failed.</summary>
    ImportFailed,
}

/// <summary>Filters of the download history; null fields do not filter.</summary>
/// <param name="Status">Completed or Failed (other values are ignored).</param>
/// <param name="Kind">Movie, series or other (the kind of the category).</param>
/// <param name="CategoryName">A download category by name (case-insensitive).</param>
/// <param name="Search">Words that must all appear in the title (case-insensitive).</param>
/// <param name="Since">Finished (or added, when there is no finish time) at or after this time.</param>
public sealed record DownloadHistoryFilter(
    DownloadStatus? Status = null,
    DownloadImportFilter? Import = null,
    DownloadCategory? Kind = null,
    string? CategoryName = null,
    string? Search = null,
    DateTime? Since = null);

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
        CategoryName, NzbHash, Duplicate, Protocol, ClientId, ExternalId, Seeding, Ratio, UploadedBytes, Seeders, Peers,
        SeedRatio, SeedTimeMinutes
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
                CategoryName, NzbHash, Duplicate, Protocol, ClientId, ExternalId, Seeding, Ratio, UploadedBytes, Seeders, Peers,
                SeedRatio, SeedTimeMinutes)
            VALUES (@Title, @Category, @Status, @Priority, @TotalBytes, @DownloadedBytes, @MissingArticles, @Error, @StatusMessage, @Indexer,
                    @MediaType, @MovieId, @SeriesId, @EpisodeIds, @Password, @NzbPath, @WorkFolder, @OutputPath, @Added, @Completed, @SortOrder,
                    @CategoryName, @NzbHash, @Duplicate, @Protocol, @ClientId, @ExternalId, @Seeding, @Ratio, @UploadedBytes, @Seeders, @Peers,
                    @SeedRatio, @SeedTimeMinutes);
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

    /// <summary>Usenet downloads (the built-in engine's) that are not completed or failed, in queue order. Torrent rows are left out.</summary>
    public IReadOnlyList<DownloadRecord> Unfinished()
    {
        using var connection = database.Open();
        return [.. connection.Query<DownloadRow>(
                $"SELECT {Columns} FROM Downloads WHERE Status NOT IN (@completed, @failed) AND Protocol = @usenet ORDER BY Priority DESC, SortOrder, Id",
                new { completed = (int)DownloadStatus.Completed, failed = (int)DownloadStatus.Failed, usenet = (int)DownloadProtocol.Usenet })
            .Select(r => r.ToRecord())];
    }

    /// <summary>0.11: torrent downloads the tracker follows: unfinished ones, and finished ones still in their client (<see cref="DownloadRecord.Seeding"/>).</summary>
    public IReadOnlyList<DownloadRecord> TrackedTorrents()
    {
        using var connection = database.Open();
        return [.. connection.Query<DownloadRow>(
                $"SELECT {Columns} FROM Downloads WHERE Protocol = @torrent AND (Status NOT IN (@completed, @failed) OR Seeding = 1) ORDER BY Priority DESC, SortOrder, Id",
                new { completed = (int)DownloadStatus.Completed, failed = (int)DownloadStatus.Failed, torrent = (int)DownloadProtocol.Torrent })
            .Select(r => r.ToRecord())];
    }

    /// <summary>The newest torrent row of a client with this info hash.</summary>
    public DownloadRecord? FindTorrent(int clientId, string hash)
    {
        using var connection = database.Open();
        return connection.QueryFirstOrDefault<DownloadRow>(
            $"SELECT {Columns} FROM Downloads WHERE Protocol = @torrent AND ClientId = @clientId AND ExternalId = @hash COLLATE NOCASE ORDER BY Id DESC",
            new { torrent = (int)DownloadProtocol.Torrent, clientId, hash })?.ToRecord();
    }

    /// <summary>Saves what the torrent tracker learns from the client (state, sizes, seeding, ratio, peers, output path).</summary>
    public void UpdateTorrent(DownloadRecord download)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Downloads SET Title = @Title, Status = @Status, TotalBytes = @TotalBytes, DownloadedBytes = @DownloadedBytes, Error = @Error,
                StatusMessage = @StatusMessage, OutputPath = @OutputPath, Completed = @Completed, Seeding = @Seeding, Ratio = @Ratio,
                UploadedBytes = @UploadedBytes, Seeders = @Seeders, Peers = @Peers
            WHERE Id = @Id AND Protocol = @Protocol
            """,
            Parameters(download));
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
    public (IReadOnlyList<DownloadRecord> Records, int Total) History(int page, int pageSize, DownloadHistoryFilter? filter = null)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters(new
        {
            completed = (int)DownloadStatus.Completed,
            failed = (int)DownloadStatus.Failed,
            imported = (int)ImportState.Imported,
            importFailed = (int)ImportState.Failed,
            take = pageSize,
            skip = (page - 1) * pageSize,
        });

        conditions.Add(filter?.Status switch
        {
            DownloadStatus.Completed => "Status = @completed",
            DownloadStatus.Failed => "Status = @failed",
            _ => "Status IN (@completed, @failed)",
        });

        // Only completed downloads are imported. One without an import row (never tried, or not linked) is "not imported";
        // queued, running and waiting imports count as not imported yet.
        switch (filter?.Import)
        {
            case DownloadImportFilter.Imported:
                conditions.Add("Status = @completed AND Id IN (SELECT DownloadId FROM DownloadImports WHERE State = @imported)");
                break;
            case DownloadImportFilter.ImportFailed:
                conditions.Add("Status = @completed AND Id IN (SELECT DownloadId FROM DownloadImports WHERE State = @importFailed)");
                break;
            case DownloadImportFilter.NotImported:
                conditions.Add("Status = @completed AND Id NOT IN (SELECT DownloadId FROM DownloadImports WHERE State IN (@imported, @importFailed))");
                break;
        }

        if (filter?.Kind is { } kind)
        {
            conditions.Add("Category = @kind");
            parameters.Add("kind", (int)kind);
        }

        if (!string.IsNullOrWhiteSpace(filter?.CategoryName))
        {
            conditions.Add("CategoryName = @categoryName COLLATE NOCASE");
            parameters.Add("categoryName", filter.CategoryName.Trim());
        }

        if (filter?.Since is { } since)
        {
            conditions.Add("IFNULL(Completed, Added) >= @since");
            parameters.Add("since", since.ToUniversalTime());
        }

        TextSearch.Where("Title", filter?.Search, conditions, parameters);

        var where = string.Join(" AND ", conditions.Select(c => $"({c})"));
        using var connection = database.Open();
        var total = connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM Downloads WHERE {where}", parameters);
        var rows = connection.Query<DownloadRow>(
            $"SELECT {Columns} FROM Downloads WHERE {where} ORDER BY IFNULL(Completed, Added) DESC, Id DESC LIMIT @take OFFSET @skip", parameters);
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
        Protocol = (int)d.Protocol,
        d.ClientId,
        d.ExternalId,
        d.Seeding,
        d.Ratio,
        d.UploadedBytes,
        d.Seeders,
        d.Peers,
        d.SeedRatio,
        d.SeedTimeMinutes,
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
        public long Protocol { get; init; } = 1;
        public long? ClientId { get; init; }
        public string? ExternalId { get; init; }
        public bool Seeding { get; init; }
        public double? Ratio { get; init; }
        public long UploadedBytes { get; init; }
        public long? Seeders { get; init; }
        public long? Peers { get; init; }
        public double? SeedRatio { get; init; }
        public long? SeedTimeMinutes { get; init; }

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
            Protocol = Protocol == (long)DownloadProtocol.Torrent ? DownloadProtocol.Torrent : DownloadProtocol.Usenet,
            ClientId = ClientId is { } client ? (int)client : null,
            ExternalId = ExternalId,
            Seeding = Seeding,
            Ratio = Ratio,
            UploadedBytes = UploadedBytes,
            Seeders = Seeders is { } seeders ? (int)seeders : null,
            Peers = Peers is { } peers ? (int)peers : null,
            SeedRatio = SeedRatio,
            SeedTimeMinutes = SeedTimeMinutes is { } minutes ? (int)minutes : null,
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
