// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;
using StreamZH.Core.Media;

namespace StreamZH.Data;

public sealed class RootFolderRepository(Database database)
{
    public IReadOnlyList<RootFolder> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<RootFolder>("SELECT Id, Path, MediaType FROM RootFolders ORDER BY MediaType, Path")];
    }

    public RootFolder? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<RootFolder>("SELECT Id, Path, MediaType FROM RootFolders WHERE Id = @id", new { id });
    }

    public int Add(string path, MediaType mediaType)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO RootFolders (Path, MediaType) VALUES (@path, @mediaType); SELECT last_insert_rowid();",
            new { path, mediaType = (int)mediaType });
    }

    public bool InUse(int id)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            "SELECT (SELECT COUNT(*) FROM Movies WHERE RootFolderId = @id) + (SELECT COUNT(*) FROM Series WHERE RootFolderId = @id)",
            new { id }) > 0;
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM RootFolders WHERE Id = @id", new { id });
    }
}

public sealed class MovieRepository(Database database)
{
    private const string Columns = """
        Id, TmdbId, ImdbId, Title, OriginalTitle, SortTitle, Year, Overview, Runtime, Status, Genres, PosterPath, BackdropPath,
        Monitored, QualityProfileId, OriginalLanguageId, RootFolderId, FolderPath, Added, LastRefreshed, FileRelativePath, FileSize, FileModified, InCinemas, DigitalRelease, PhysicalRelease
        """;

    public IReadOnlyList<Movie> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<MovieRow>($"SELECT {Columns} FROM Movies ORDER BY SortTitle").Select(r => r.ToMovie())];
    }

    public Movie? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<MovieRow>($"SELECT {Columns} FROM Movies WHERE Id = @id", new { id })?.ToMovie();
    }

    public Movie? FindByTmdbId(int tmdbId)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<MovieRow>($"SELECT {Columns} FROM Movies WHERE TmdbId = @tmdbId", new { tmdbId })?.ToMovie();
    }

    public IReadOnlyDictionary<int, int> TmdbIdToId()
    {
        using var connection = database.Open();
        return connection.Query<(long TmdbId, long Id)>("SELECT TmdbId, Id FROM Movies").ToDictionary(r => (int)r.TmdbId, r => (int)r.Id);
    }

    public IReadOnlySet<string> FolderPaths()
    {
        using var connection = database.Open();
        return connection.Query<string>("SELECT FolderPath FROM Movies").ToHashSet(PathComparer.Instance);
    }

    public int Add(Movie movie)
    {
        using var connection = database.Open();
        movie.Id = connection.ExecuteScalar<int>(
            """
            INSERT INTO Movies (TmdbId, ImdbId, Title, OriginalTitle, SortTitle, Year, Overview, Runtime, Status, Genres, PosterPath, BackdropPath,
                Monitored, QualityProfileId, OriginalLanguageId, RootFolderId, FolderPath, Added, LastRefreshed, FileRelativePath, FileSize, FileModified, InCinemas, DigitalRelease, PhysicalRelease)
            VALUES (@TmdbId, @ImdbId, @Title, @OriginalTitle, @SortTitle, @Year, @Overview, @Runtime, @Status, @Genres, @PosterPath, @BackdropPath,
                @Monitored, @QualityProfileId, @OriginalLanguageId, @RootFolderId, @FolderPath, @Added, @LastRefreshed, @FileRelativePath, @FileSize, @FileModified, @InCinemas, @DigitalRelease, @PhysicalRelease);
            SELECT last_insert_rowid();
            """,
            MovieRow.From(movie));
        return movie.Id;
    }

    public void Update(Movie movie)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Movies SET ImdbId = @ImdbId, Title = @Title, OriginalTitle = @OriginalTitle, SortTitle = @SortTitle, Year = @Year,
                Overview = @Overview, Runtime = @Runtime, Status = @Status, Genres = @Genres, PosterPath = @PosterPath, BackdropPath = @BackdropPath,
                Monitored = @Monitored, QualityProfileId = @QualityProfileId, OriginalLanguageId = @OriginalLanguageId, FolderPath = @FolderPath, LastRefreshed = @LastRefreshed,
                FileRelativePath = @FileRelativePath, FileSize = @FileSize, FileModified = @FileModified, InCinemas = @InCinemas,
                DigitalRelease = @DigitalRelease, PhysicalRelease = @PhysicalRelease
            WHERE Id = @Id
            """,
            MovieRow.From(movie));
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM Movies WHERE Id = @id", new { id });
    }

    private sealed class MovieRow
    {
        public long Id { get; init; }
        public long TmdbId { get; init; }
        public string? ImdbId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string OriginalTitle { get; init; } = string.Empty;
        public string SortTitle { get; init; } = string.Empty;
        public long Year { get; init; }
        public string? Overview { get; init; }
        public long Runtime { get; init; }
        public string Status { get; init; } = string.Empty;
        public string Genres { get; init; } = string.Empty;
        public string? PosterPath { get; init; }
        public string? BackdropPath { get; init; }
        public bool Monitored { get; init; }
        public long QualityProfileId { get; init; }
        public long OriginalLanguageId { get; init; }
        public long RootFolderId { get; init; }
        public string FolderPath { get; init; } = string.Empty;
        public DateTime Added { get; init; }
        public DateTime? LastRefreshed { get; init; }
        public string? FileRelativePath { get; init; }
        public long? FileSize { get; init; }
        public DateTime? FileModified { get; init; }
        public string? InCinemas { get; init; }
        public string? DigitalRelease { get; init; }
        public string? PhysicalRelease { get; init; }

        public static MovieRow From(Movie m) => new()
        {
            Id = m.Id,
            TmdbId = m.TmdbId,
            ImdbId = m.ImdbId,
            Title = m.Title,
            OriginalTitle = m.OriginalTitle,
            SortTitle = m.SortTitle,
            Year = m.Year,
            Overview = m.Overview,
            Runtime = m.Runtime,
            Status = m.Status,
            Genres = GenreList.Join(m.Genres),
            PosterPath = m.PosterPath,
            BackdropPath = m.BackdropPath,
            Monitored = m.Monitored,
            QualityProfileId = m.QualityProfileId,
            OriginalLanguageId = m.OriginalLanguageId,
            RootFolderId = m.RootFolderId,
            FolderPath = m.FolderPath,
            Added = m.Added,
            LastRefreshed = m.LastRefreshed,
            FileRelativePath = m.File?.RelativePath,
            FileSize = m.File?.Size,
            FileModified = m.File?.Modified,
            InCinemas = m.InCinemas,
            DigitalRelease = m.DigitalRelease,
            PhysicalRelease = m.PhysicalRelease,
        };

        public Movie ToMovie() => new()
        {
            Id = (int)Id,
            TmdbId = (int)TmdbId,
            ImdbId = ImdbId,
            Title = Title,
            OriginalTitle = OriginalTitle,
            SortTitle = SortTitle,
            Year = (int)Year,
            Overview = Overview,
            Runtime = (int)Runtime,
            Status = Status,
            Genres = GenreList.Split(Genres),
            PosterPath = PosterPath,
            BackdropPath = BackdropPath,
            Monitored = Monitored,
            QualityProfileId = (int)QualityProfileId,
            OriginalLanguageId = (int)OriginalLanguageId,
            RootFolderId = (int)RootFolderId,
            FolderPath = FolderPath,
            Added = Utc(Added),
            LastRefreshed = LastRefreshed is { } r ? Utc(r) : null,
            File = FileRelativePath is null ? null : new MediaFile(FileRelativePath, FileSize ?? 0, Utc(FileModified ?? DateTime.MinValue)),
            InCinemas = InCinemas,
            DigitalRelease = DigitalRelease,
            PhysicalRelease = PhysicalRelease,
        };
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

public sealed class SeriesRepository(Database database)
{
    private const string Columns = """
        Id, TmdbId, TvdbId, ImdbId, Title, OriginalTitle, SortTitle, Year, Overview, Status, Network, Genres, PosterPath, BackdropPath,
        Monitored, QualityProfileId, OriginalLanguageId, RootFolderId, FolderPath, Added, LastRefreshed
        """;

    private const string EpisodeColumns = """
        Id, SeriesId, SeasonNumber, EpisodeNumber, Title, AirDate, Overview, Monitored, FileRelativePath, FileSize, FileModified
        """;

    public IReadOnlyList<Series> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<SeriesRow>($"SELECT {Columns} FROM Series ORDER BY SortTitle").Select(r => r.ToSeries())];
    }

    public Series? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<SeriesRow>($"SELECT {Columns} FROM Series WHERE Id = @id", new { id })?.ToSeries();
    }

    public IReadOnlyDictionary<int, int> TmdbIdToId()
    {
        using var connection = database.Open();
        return connection.Query<(long TmdbId, long Id)>("SELECT TmdbId, Id FROM Series").ToDictionary(r => (int)r.TmdbId, r => (int)r.Id);
    }

    public IReadOnlySet<string> FolderPaths()
    {
        using var connection = database.Open();
        return connection.Query<string>("SELECT FolderPath FROM Series").ToHashSet(PathComparer.Instance);
    }

    public int Add(Series series)
    {
        using var connection = database.Open();
        series.Id = connection.ExecuteScalar<int>(
            """
            INSERT INTO Series (TmdbId, TvdbId, ImdbId, Title, OriginalTitle, SortTitle, Year, Overview, Status, Network, Genres, PosterPath, BackdropPath,
                Monitored, QualityProfileId, OriginalLanguageId, RootFolderId, FolderPath, Added, LastRefreshed)
            VALUES (@TmdbId, @TvdbId, @ImdbId, @Title, @OriginalTitle, @SortTitle, @Year, @Overview, @Status, @Network, @Genres, @PosterPath, @BackdropPath,
                @Monitored, @QualityProfileId, @OriginalLanguageId, @RootFolderId, @FolderPath, @Added, @LastRefreshed);
            SELECT last_insert_rowid();
            """,
            SeriesRow.From(series));
        return series.Id;
    }

    public void Update(Series series)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Series SET TvdbId = @TvdbId, ImdbId = @ImdbId, Title = @Title, OriginalTitle = @OriginalTitle, SortTitle = @SortTitle, Year = @Year,
                Overview = @Overview, Status = @Status, Network = @Network, Genres = @Genres, PosterPath = @PosterPath, BackdropPath = @BackdropPath,
                Monitored = @Monitored, QualityProfileId = @QualityProfileId, OriginalLanguageId = @OriginalLanguageId, FolderPath = @FolderPath, LastRefreshed = @LastRefreshed
            WHERE Id = @Id
            """,
            SeriesRow.From(series));
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM Episodes WHERE SeriesId = @id; DELETE FROM Series WHERE Id = @id", new { id });
    }

    public IReadOnlyList<Episode> Episodes(int seriesId)
    {
        using var connection = database.Open();
        return [.. connection.Query<EpisodeRow>(
            $"SELECT {EpisodeColumns} FROM Episodes WHERE SeriesId = @seriesId ORDER BY SeasonNumber, EpisodeNumber",
            new { seriesId }).Select(r => r.ToEpisode())];
    }

    /// <summary>Episode counters for every series. "Aired" counts episodes with an air date up to today.</summary>
    public IReadOnlyDictionary<int, SeriesCounts> Counts()
    {
        using var connection = database.Open();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return connection.Query<(long SeriesId, long Total, long Aired, long Files, long Size)>(
            """
            SELECT SeriesId,
                   SUM(CASE WHEN SeasonNumber > 0 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN SeasonNumber > 0 AND AirDate IS NOT NULL AND AirDate <= @today THEN 1 ELSE 0 END),
                   SUM(CASE WHEN SeasonNumber > 0 AND FileRelativePath IS NOT NULL THEN 1 ELSE 0 END),
                   IFNULL(SUM(FileSize), 0)
            FROM Episodes GROUP BY SeriesId
            """,
            new { today })
            .ToDictionary(r => (int)r.SeriesId, r => new SeriesCounts((int)r.SeriesId, (int)r.Total, (int)r.Aired, (int)r.Files, r.Size));
    }

    /// <summary>Replaces the episode list with fresh metadata, keeping monitored flags and file info of existing episodes.</summary>
    public void UpsertEpisodes(int seriesId, IEnumerable<Episode> fresh, bool monitorNew)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var existing = connection.Query<EpisodeRow>(
            $"SELECT {EpisodeColumns} FROM Episodes WHERE SeriesId = @seriesId", new { seriesId }, transaction)
            .ToDictionary(e => ((int)e.SeasonNumber, (int)e.EpisodeNumber));

        var seen = new HashSet<(int, int)>();
        foreach (var episode in fresh)
        {
            var key = (episode.SeasonNumber, episode.EpisodeNumber);
            if (!seen.Add(key))
            {
                continue;
            }

            if (existing.TryGetValue(key, out var row))
            {
                connection.Execute(
                    "UPDATE Episodes SET Title = @Title, AirDate = @AirDate, Overview = @Overview WHERE Id = @Id",
                    new { Id = row.Id, episode.Title, episode.AirDate, episode.Overview },
                    transaction);
            }
            else
            {
                connection.Execute(
                    """
                    INSERT INTO Episodes (SeriesId, SeasonNumber, EpisodeNumber, Title, AirDate, Overview, Monitored)
                    VALUES (@seriesId, @SeasonNumber, @EpisodeNumber, @Title, @AirDate, @Overview, @monitored)
                    """,
                    new { seriesId, episode.SeasonNumber, episode.EpisodeNumber, episode.Title, episode.AirDate, episode.Overview, monitored = monitorNew && episode.SeasonNumber > 0 },
                    transaction);
            }
        }

        // Episodes TMDB no longer lists are removed unless a file is attached.
        foreach (var (key, row) in existing)
        {
            if (!seen.Contains(key) && row.FileRelativePath is null)
            {
                connection.Execute("DELETE FROM Episodes WHERE Id = @Id", new { row.Id }, transaction);
            }
        }

        transaction.Commit();
    }

    /// <summary>Sets file info for all episodes of a series at once (null clears).</summary>
    public void SetEpisodeFiles(int seriesId, IReadOnlyDictionary<(int Season, int Episode), MediaFile> files)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        connection.Execute(
            "UPDATE Episodes SET FileRelativePath = NULL, FileSize = NULL, FileModified = NULL WHERE SeriesId = @seriesId",
            new { seriesId }, transaction);

        foreach (var ((season, episode), file) in files)
        {
            connection.Execute(
                """
                UPDATE Episodes SET FileRelativePath = @RelativePath, FileSize = @Size, FileModified = @Modified
                WHERE SeriesId = @seriesId AND SeasonNumber = @season AND EpisodeNumber = @episode
                """,
                new { seriesId, season, episode, file.RelativePath, file.Size, file.Modified },
                transaction);
        }

        transaction.Commit();
    }

    public void SetSeasonMonitored(int seriesId, int seasonNumber, bool monitored)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE Episodes SET Monitored = @monitored WHERE SeriesId = @seriesId AND SeasonNumber = @seasonNumber",
            new { seriesId, seasonNumber, monitored });
    }

    public void SetEpisodeMonitored(int episodeId, bool monitored)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Episodes SET Monitored = @monitored WHERE Id = @episodeId", new { episodeId, monitored });
    }

    private sealed class SeriesRow
    {
        public long Id { get; init; }
        public long TmdbId { get; init; }
        public long? TvdbId { get; init; }
        public string? ImdbId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string OriginalTitle { get; init; } = string.Empty;
        public string SortTitle { get; init; } = string.Empty;
        public long Year { get; init; }
        public string? Overview { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? Network { get; init; }
        public string Genres { get; init; } = string.Empty;
        public string? PosterPath { get; init; }
        public string? BackdropPath { get; init; }
        public bool Monitored { get; init; }
        public long QualityProfileId { get; init; }
        public long OriginalLanguageId { get; init; }
        public long RootFolderId { get; init; }
        public string FolderPath { get; init; } = string.Empty;
        public DateTime Added { get; init; }
        public DateTime? LastRefreshed { get; init; }

        public static SeriesRow From(Series s) => new()
        {
            Id = s.Id,
            TmdbId = s.TmdbId,
            TvdbId = s.TvdbId,
            ImdbId = s.ImdbId,
            Title = s.Title,
            OriginalTitle = s.OriginalTitle,
            SortTitle = s.SortTitle,
            Year = s.Year,
            Overview = s.Overview,
            Status = s.Status,
            Network = s.Network,
            Genres = GenreList.Join(s.Genres),
            PosterPath = s.PosterPath,
            BackdropPath = s.BackdropPath,
            Monitored = s.Monitored,
            QualityProfileId = s.QualityProfileId,
            OriginalLanguageId = s.OriginalLanguageId,
            RootFolderId = s.RootFolderId,
            FolderPath = s.FolderPath,
            Added = s.Added,
            LastRefreshed = s.LastRefreshed,
        };

        public Series ToSeries() => new()
        {
            Id = (int)Id,
            TmdbId = (int)TmdbId,
            TvdbId = TvdbId is { } t ? (int)t : null,
            ImdbId = ImdbId,
            Title = Title,
            OriginalTitle = OriginalTitle,
            SortTitle = SortTitle,
            Year = (int)Year,
            Overview = Overview,
            Status = Status,
            Network = Network,
            Genres = GenreList.Split(Genres),
            PosterPath = PosterPath,
            BackdropPath = BackdropPath,
            Monitored = Monitored,
            QualityProfileId = (int)QualityProfileId,
            OriginalLanguageId = (int)OriginalLanguageId,
            RootFolderId = (int)RootFolderId,
            FolderPath = FolderPath,
            Added = DateTime.SpecifyKind(Added, DateTimeKind.Utc),
            LastRefreshed = LastRefreshed is { } r ? DateTime.SpecifyKind(r, DateTimeKind.Utc) : null,
        };
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

internal static class GenreList
{
    public static string Join(IReadOnlyList<string> genres) => string.Join('|', genres);

    public static IReadOnlyList<string> Split(string value) =>
        value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Path comparison: case-insensitive on Windows and macOS, case-sensitive on Linux.</summary>
public sealed class PathComparer : IEqualityComparer<string>
{
    public static readonly PathComparer Instance = new();

    private static readonly StringComparer Inner =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public bool Equals(string? x, string? y) => Inner.Equals(Normalize(x), Normalize(y));

    public int GetHashCode(string obj) => Inner.GetHashCode(Normalize(obj)!);

    private static string? Normalize(string? path) => path?.TrimEnd('/', '\\');
}