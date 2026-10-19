// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using StreamZH.Core.Media;

namespace StreamZH.Data;

/// <summary>Result of the last syncs of a list; times are UTC.</summary>
/// <param name="Failures">Syncs in a row that failed (0 after a successful one).</param>
/// <param name="LastFound">Items the list returned at its last successful sync.</param>
/// <param name="LastAdded">Items added to the library at the last successful sync.</param>
/// <param name="LastAddedTitles">"Title (Year)" of the items added last (at most a few).</param>
public sealed record ImportListStatus(
    DateTime? LastSync,
    DateTime? LastSuccess,
    string? LastError,
    int Failures,
    int? LastFound,
    int? LastAdded,
    IReadOnlyList<string> LastAddedTitles)
{
    public static ImportListStatus None { get; } = new(null, null, null, 0, null, null, []);

    public bool Failing => Failures > 0;
}

/// <summary>One import list (Settings → Import lists).</summary>
/// <param name="Implementation">Catalog key, e.g. "tmdbList", "traktList" or "plexWatchlist".</param>
/// <param name="EnableAuto">Adds new items on its own; off: the list only shows what it would add (preview).</param>
/// <param name="Monitor">Movies: movieOnly, movieAndCollection, none. Series: all, future, missing, firstSeason, latestSeason, none.</param>
/// <param name="QualityProfileId">Null = the default quality profile.</param>
/// <param name="MinimumAvailability">Movies: announced, inCinemas or released (when a movie is added).</param>
/// <param name="Settings">Type-specific settings as stored (JSON values; secrets in plain text).</param>
public sealed record ImportListDefinition(
    int Id,
    string Name,
    string Implementation,
    MediaType MediaType,
    bool Enabled,
    bool EnableAuto,
    bool SearchOnAdd,
    string Monitor,
    int RootFolderId,
    int? QualityProfileId,
    string MinimumAvailability,
    IReadOnlyDictionary<string, JsonElement> Settings)
{
    public ImportListStatus Status { get; init; } = ImportListStatus.None;
}

/// <summary>A movie or series no import list may add (Radarr's "List exclusions").</summary>
public sealed record ImportListExclusion(int Id, MediaType MediaType, int TmdbId, string Title, int Year, DateTime Added);

/// <summary>A library item that an import list added.</summary>
public sealed record ImportListOrigin(MediaType MediaType, int TmdbId, int ListId, string ListName, DateTime Added);

public sealed class ImportListRepository(Database database)
{
    private const string Columns = """
        Id, Name, Implementation, MediaType, Enabled, EnableAuto, SearchOnAdd, Monitor, RootFolderId, QualityProfileId,
        MinimumAvailability, Settings, LastSync, LastSuccess, LastError, Failures, LastFound, LastAdded, LastAddedTitles
        """;

    public static string MediaKey(MediaType type) => type == MediaType.Series ? "series" : "movie";

    public static MediaType ParseMedia(string? key) => key == "series" ? MediaType.Series : MediaType.Movie;

    // ---- Lists ----

    public IReadOnlyList<ImportListDefinition> All()
    {
        using var connection = database.Open();
        return [.. connection.Query<ListRow>($"SELECT {Columns} FROM ImportLists ORDER BY Name COLLATE NOCASE").Select(r => r.ToDefinition())];
    }

    public ImportListDefinition? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<ListRow>($"SELECT {Columns} FROM ImportLists WHERE Id = @id", new { id })?.ToDefinition();
    }

    public ImportListDefinition? FindByName(string name)
    {
        using var connection = database.Open();
        return connection.QueryFirstOrDefault<ListRow>($"SELECT {Columns} FROM ImportLists WHERE Name = @name COLLATE NOCASE", new { name })?.ToDefinition();
    }

    public int Add(ImportListDefinition definition, DateTime added)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO ImportLists (Name, Implementation, MediaType, Enabled, EnableAuto, SearchOnAdd, Monitor, RootFolderId,
                QualityProfileId, MinimumAvailability, Settings, Added)
            VALUES (@Name, @Implementation, @MediaType, @Enabled, @EnableAuto, @SearchOnAdd, @Monitor, @RootFolderId,
                @QualityProfileId, @MinimumAvailability, @Settings, @Added);
            SELECT last_insert_rowid();
            """,
            Parameters(definition, added));
    }

    /// <summary>Saves the definition; a changed type or settings clear the error (they may have fixed it).</summary>
    public void Update(ImportListDefinition definition, bool clearStatus)
    {
        using var connection = database.Open();
        connection.Execute(
            $"""
            UPDATE ImportLists SET Name = @Name, Implementation = @Implementation, MediaType = @MediaType, Enabled = @Enabled,
                EnableAuto = @EnableAuto, SearchOnAdd = @SearchOnAdd, Monitor = @Monitor, RootFolderId = @RootFolderId,
                QualityProfileId = @QualityProfileId, MinimumAvailability = @MinimumAvailability, Settings = @Settings
                {(clearStatus ? ", LastError = NULL, Failures = 0" : string.Empty)}
            WHERE Id = @Id
            """,
            Parameters(definition, default));
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM ImportLists WHERE Id = @id", new { id });
    }

    public void SetSuccess(int id, DateTime at, int found, int added, IReadOnlyList<string> addedTitles)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE ImportLists SET LastSync = @at, LastSuccess = @at, LastError = NULL, Failures = 0, LastFound = @found,
                LastAdded = @added, LastAddedTitles = CASE WHEN @added > 0 THEN @titles ELSE LastAddedTitles END
            WHERE Id = @id
            """,
            new { id, at, found, added, titles = JsonSerializer.Serialize(addedTitles) });
    }

    public void SetFailure(int id, DateTime at, string error)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE ImportLists SET LastSync = @at, LastError = @error, Failures = Failures + 1 WHERE Id = @id",
            new { id, at, error = Core.Security.SecretUrls.Redact(error) });
    }

    // ---- Exclusions ----

    public IReadOnlyList<ImportListExclusion> Exclusions()
    {
        using var connection = database.Open();
        return [.. connection.Query<ExclusionRow>("SELECT Id, MediaType, TmdbId, Title, Year, Added FROM ImportListExclusions ORDER BY Title COLLATE NOCASE, Year")
            .Select(r => r.ToExclusion())];
    }

    public ImportListExclusion? FindExclusion(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<ExclusionRow>("SELECT Id, MediaType, TmdbId, Title, Year, Added FROM ImportListExclusions WHERE Id = @id", new { id })
            ?.ToExclusion();
    }

    public IReadOnlySet<int> ExcludedTmdbIds(MediaType type)
    {
        using var connection = database.Open();
        return connection.Query<int>("SELECT TmdbId FROM ImportListExclusions WHERE MediaType = @media", new { media = MediaKey(type) }).ToHashSet();
    }

    /// <summary>Adds the exclusion; an existing one for the same item is kept (its id is returned).</summary>
    public int AddExclusion(MediaType type, int tmdbId, string title, int year, DateTime added)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO ImportListExclusions (MediaType, TmdbId, Title, Year, Added) VALUES (@media, @tmdbId, @title, @year, @added)
            ON CONFLICT (MediaType, TmdbId) DO NOTHING
            """,
            new { media = MediaKey(type), tmdbId, title, year, added });
        return connection.ExecuteScalar<int>(
            "SELECT Id FROM ImportListExclusions WHERE MediaType = @media AND TmdbId = @tmdbId", new { media = MediaKey(type), tmdbId });
    }

    public void DeleteExclusion(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM ImportListExclusions WHERE Id = @id", new { id });
    }

    // ---- Origins ----

    public IReadOnlyList<ImportListOrigin> Origins()
    {
        using var connection = database.Open();
        return [.. connection.Query<OriginRow>("SELECT MediaType, TmdbId, ListId, ListName, Added FROM ImportListOrigins").Select(r => r.ToOrigin())];
    }

    public ImportListOrigin? FindOrigin(MediaType type, int tmdbId)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<OriginRow>(
            "SELECT MediaType, TmdbId, ListId, ListName, Added FROM ImportListOrigins WHERE MediaType = @media AND TmdbId = @tmdbId",
            new { media = MediaKey(type), tmdbId })?.ToOrigin();
    }

    public void SetOrigin(MediaType type, int tmdbId, int listId, string listName, DateTime added)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO ImportListOrigins (MediaType, TmdbId, ListId, ListName, Added) VALUES (@media, @tmdbId, @listId, @listName, @added)
            ON CONFLICT (MediaType, TmdbId) DO UPDATE SET ListId = excluded.ListId, ListName = excluded.ListName, Added = excluded.Added
            """,
            new { media = MediaKey(type), tmdbId, listId, listName, added });
    }

    public void DeleteOrigin(MediaType type, int tmdbId)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM ImportListOrigins WHERE MediaType = @media AND TmdbId = @tmdbId", new { media = MediaKey(type), tmdbId });
    }

    private static object Parameters(ImportListDefinition d, DateTime added) => new
    {
        d.Id,
        d.Name,
        d.Implementation,
        MediaType = MediaKey(d.MediaType),
        d.Enabled,
        d.EnableAuto,
        d.SearchOnAdd,
        d.Monitor,
        d.RootFolderId,
        d.QualityProfileId,
        d.MinimumAvailability,
        Settings = JsonSerializer.Serialize(d.Settings),
        Added = added,
    };

    private static IReadOnlyDictionary<string, JsonElement> DeserializeSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonElement>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    private static IReadOnlyList<string> DeserializeTitles(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private sealed class ListRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Implementation { get; init; } = string.Empty;
        public string MediaType { get; init; } = "movie";
        public bool Enabled { get; init; }
        public bool EnableAuto { get; init; }
        public bool SearchOnAdd { get; init; }
        public string Monitor { get; init; } = string.Empty;
        public long RootFolderId { get; init; }
        public long? QualityProfileId { get; init; }
        public string MinimumAvailability { get; init; } = "announced";
        public string Settings { get; init; } = "{}";
        public DateTime? LastSync { get; init; }
        public DateTime? LastSuccess { get; init; }
        public string? LastError { get; init; }
        public long Failures { get; init; }
        public long? LastFound { get; init; }
        public long? LastAdded { get; init; }
        public string LastAddedTitles { get; init; } = "[]";

        public ImportListDefinition ToDefinition() => new(
            (int)Id,
            Name,
            Implementation,
            ParseMedia(MediaType),
            Enabled,
            EnableAuto,
            SearchOnAdd,
            Monitor,
            (int)RootFolderId,
            (int?)QualityProfileId,
            MinimumAvailability,
            DeserializeSettings(Settings))
        {
            Status = new ImportListStatus(
                Utc(LastSync), Utc(LastSuccess), LastError, (int)Failures, (int?)LastFound, (int?)LastAdded, DeserializeTitles(LastAddedTitles)),
        };
    }

    private sealed class ExclusionRow
    {
        public long Id { get; init; }
        public string MediaType { get; init; } = "movie";
        public long TmdbId { get; init; }
        public string Title { get; init; } = string.Empty;
        public long Year { get; init; }
        public DateTime Added { get; init; }

        public ImportListExclusion ToExclusion() =>
            new((int)Id, ParseMedia(MediaType), (int)TmdbId, Title, (int)Year, DateTime.SpecifyKind(Added, DateTimeKind.Utc));
    }

    private sealed class OriginRow
    {
        public string MediaType { get; init; } = "movie";
        public long TmdbId { get; init; }
        public long ListId { get; init; }
        public string ListName { get; init; } = string.Empty;
        public DateTime Added { get; init; }

        public ImportListOrigin ToOrigin() =>
            new(ParseMedia(MediaType), (int)TmdbId, (int)ListId, ListName, DateTime.SpecifyKind(Added, DateTimeKind.Utc));
    }
}
