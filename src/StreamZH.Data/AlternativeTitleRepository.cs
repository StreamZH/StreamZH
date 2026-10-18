// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;
using StreamZH.Core.Media;

namespace StreamZH.Data;

/// <summary>Alternative titles of movies and series (table AlternativeTitles).</summary>
public sealed class AlternativeTitleRepository(Database database)
{
    private const string Columns = "Id, MediaType, ItemId, Title, CleanTitle, Source, LanguageId, SeasonNumber, SceneSeasonNumber";

    public IReadOnlyList<AlternativeTitle> For(MediaType mediaType, int itemId)
    {
        using var connection = database.Open();
        return
        [
            .. connection.Query<Row>(
                    $"SELECT {Columns} FROM AlternativeTitles WHERE MediaType = @type AND ItemId = @itemId ORDER BY Source, Title COLLATE NOCASE",
                    new { type = (int)mediaType, itemId })
                .Select(r => r.ToTitle()),
        ];
    }

    /// <summary>All titles of one media type (for matching release names to items).</summary>
    public IReadOnlyList<AlternativeTitle> All(MediaType mediaType)
    {
        using var connection = database.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM AlternativeTitles WHERE MediaType = @type", new { type = (int)mediaType }).Select(r => r.ToTitle())];
    }

    /// <summary>Items whose alternative titles have this clean title.</summary>
    public IReadOnlyList<AlternativeTitle> FindByCleanTitle(MediaType mediaType, string cleanTitle)
    {
        using var connection = database.Open();
        return
        [
            .. connection.Query<Row>(
                    $"SELECT {Columns} FROM AlternativeTitles WHERE MediaType = @type AND CleanTitle = @cleanTitle",
                    new { type = (int)mediaType, cleanTitle })
                .Select(r => r.ToTitle()),
        ];
    }

    /// <summary>Replaces the titles of one source for an item (a refresh); other sources (user titles) stay.</summary>
    public void ReplaceSource(MediaType mediaType, int itemId, string source, IEnumerable<AlternativeTitle> titles)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            "DELETE FROM AlternativeTitles WHERE MediaType = @type AND ItemId = @itemId AND Source = @source",
            new { type = (int)mediaType, itemId, source },
            transaction);
        foreach (var title in titles)
        {
            connection.Execute(
                """
                INSERT INTO AlternativeTitles (MediaType, ItemId, Title, CleanTitle, Source, LanguageId, SeasonNumber, SceneSeasonNumber)
                VALUES (@MediaType, @ItemId, @Title, @CleanTitle, @Source, @LanguageId, @SeasonNumber, @SceneSeasonNumber)
                """,
                Row.From(title with { MediaType = mediaType, ItemId = itemId, Source = source }),
                transaction);
        }

        transaction.Commit();
    }

    public int Add(AlternativeTitle title)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO AlternativeTitles (MediaType, ItemId, Title, CleanTitle, Source, LanguageId, SeasonNumber, SceneSeasonNumber)
            VALUES (@MediaType, @ItemId, @Title, @CleanTitle, @Source, @LanguageId, @SeasonNumber, @SceneSeasonNumber);
            SELECT last_insert_rowid();
            """,
            Row.From(title));
    }

    public AlternativeTitle? Find(int id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM AlternativeTitles WHERE Id = @id", new { id })?.ToTitle();
    }

    public void Delete(int id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM AlternativeTitles WHERE Id = @id", new { id });
    }

    /// <summary>When a movie or series is removed from the library.</summary>
    public void DeleteForItem(MediaType mediaType, int itemId)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM AlternativeTitles WHERE MediaType = @type AND ItemId = @itemId", new { type = (int)mediaType, itemId });
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public long MediaType { get; init; }
        public long ItemId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string CleanTitle { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public long? LanguageId { get; init; }
        public long? SeasonNumber { get; init; }
        public long? SceneSeasonNumber { get; init; }

        public static Row From(AlternativeTitle t) => new()
        {
            Id = t.Id,
            MediaType = (int)t.MediaType,
            ItemId = t.ItemId,
            Title = t.Title,
            CleanTitle = t.CleanTitle,
            Source = t.Source,
            LanguageId = t.LanguageId,
            SeasonNumber = t.SeasonNumber,
            SceneSeasonNumber = t.SceneSeasonNumber,
        };

        public AlternativeTitle ToTitle() => new()
        {
            Id = (int)Id,
            MediaType = (MediaType)MediaType,
            ItemId = (int)ItemId,
            Title = Title,
            CleanTitle = CleanTitle,
            Source = Source,
            LanguageId = (int?)LanguageId,
            SeasonNumber = (int?)SeasonNumber,
            SceneSeasonNumber = (int?)SceneSeasonNumber,
        };
    }
}
