// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Tests.Data;

/// <summary>Filters and paging of the Activity lists: download history, history events and blocklist.</summary>
public sealed class ActivityFilterTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");
    private readonly Database _database;
    private readonly DownloadRepository _downloads;
    private readonly DownloadImportRepository _imports;
    private readonly HistoryRepository _history;
    private readonly BlocklistRepository _blocklist;

    public ActivityFilterTests()
    {
        _database = new Database(_file);
        _database.Migrate();
        _downloads = new DownloadRepository(_database);
        _imports = new DownloadImportRepository(_database);
        _history = new HistoryRepository(_database);
        _blocklist = new BlocklistRepository(_database);
    }

    [Fact]
    public void Download_history_without_filter_lists_finished_downloads_newest_first()
    {
        Seed();
        _downloads.Add(Download("Still.Downloading.2024", DownloadStatus.Downloading, DownloadCategory.Movie, 0), []);

        var (records, total) = _downloads.History(1, 50);

        Assert.Equal(7, total);
        Assert.Equal(["Dune.Part.Two.2024.2160p", "Arrival.2016.1080p", "Severance.S02E01.1080p", "Severance.S02E02.1080p", "Some.Linux.ISO", "Old.Movie.1999", "Anime.Show.S01E01"],
            records.Select(r => r.Title));
        Assert.Equal(7, _downloads.History(1, 50, new DownloadHistoryFilter()).Total);
    }

    [Fact]
    public void Download_history_filters_by_status()
    {
        Seed();

        Assert.Equal(["Severance.S02E02.1080p", "Old.Movie.1999"], Titles(new DownloadHistoryFilter(Status: DownloadStatus.Failed)));
        Assert.Equal(5, _downloads.History(1, 50, new DownloadHistoryFilter(Status: DownloadStatus.Completed)).Total);
    }

    [Fact]
    public void Download_history_filters_by_import_state_and_counts_completed_downloads_without_an_import_as_not_imported()
    {
        Seed();

        Assert.Equal(["Dune.Part.Two.2024.2160p"], Titles(new DownloadHistoryFilter(Import: DownloadImportFilter.Imported)));
        Assert.Equal(["Severance.S02E01.1080p"], Titles(new DownloadHistoryFilter(Import: DownloadImportFilter.ImportFailed)));

        // Arrival: import waiting; Some.Linux.ISO and Anime.Show: no import row. Failed downloads are never "not imported".
        Assert.Equal(["Arrival.2016.1080p", "Some.Linux.ISO", "Anime.Show.S01E01"], Titles(new DownloadHistoryFilter(Import: DownloadImportFilter.NotImported)));
    }

    [Fact]
    public void Download_history_filters_by_kind_and_category_name()
    {
        Seed();

        Assert.Equal(["Dune.Part.Two.2024.2160p", "Arrival.2016.1080p", "Old.Movie.1999"], Titles(new DownloadHistoryFilter(Kind: DownloadCategory.Movie)));
        Assert.Equal(["Severance.S02E01.1080p", "Severance.S02E02.1080p", "Anime.Show.S01E01"], Titles(new DownloadHistoryFilter(Kind: DownloadCategory.Series)));
        Assert.Equal(["Anime.Show.S01E01"], Titles(new DownloadHistoryFilter(CategoryName: "ANIME")));
        Assert.Empty(Titles(new DownloadHistoryFilter(CategoryName: "nothing")));
    }

    [Fact]
    public void Download_history_search_needs_every_word_and_ignores_case()
    {
        Seed();

        Assert.Equal(["Dune.Part.Two.2024.2160p"], Titles(new DownloadHistoryFilter(Search: "dune TWO")));
        Assert.Equal(["Severance.S02E01.1080p", "Severance.S02E02.1080p"], Titles(new DownloadHistoryFilter(Search: "  severance  ")));
        Assert.Empty(Titles(new DownloadHistoryFilter(Search: "dune arrival")));
        Assert.Equal(7, _downloads.History(1, 50, new DownloadHistoryFilter(Search: "   ")).Total);
    }

    [Fact]
    public void Download_history_search_matches_percent_underscore_and_backslash_literally()
    {
        _downloads.Add(Download("100% Pure", DownloadStatus.Completed, DownloadCategory.Other, 1), []);
        _downloads.Add(Download("1000 Pure", DownloadStatus.Completed, DownloadCategory.Other, 2), []);
        _downloads.Add(Download("my_file", DownloadStatus.Completed, DownloadCategory.Other, 3), []);
        _downloads.Add(Download("myXfile", DownloadStatus.Completed, DownloadCategory.Other, 4), []);
        _downloads.Add(Download(@"back\slash", DownloadStatus.Completed, DownloadCategory.Other, 5), []);
        _downloads.Add(Download("backXslash", DownloadStatus.Completed, DownloadCategory.Other, 6), []);

        Assert.Equal(["100% Pure"], Titles(new DownloadHistoryFilter(Search: "100%")));
        Assert.Equal(["my_file"], Titles(new DownloadHistoryFilter(Search: "y_f")));
        Assert.Equal([@"back\slash"], Titles(new DownloadHistoryFilter(Search: @"k\s")));
    }

    [Fact]
    public void Download_history_filters_by_date_and_combines_filters()
    {
        Seed();

        Assert.Equal(["Dune.Part.Two.2024.2160p", "Arrival.2016.1080p", "Severance.S02E01.1080p"], Titles(new DownloadHistoryFilter(Since: Now.AddHours(-3.5))));
        Assert.Equal(
            ["Arrival.2016.1080p"],
            Titles(new DownloadHistoryFilter(Status: DownloadStatus.Completed, Import: DownloadImportFilter.NotImported, Kind: DownloadCategory.Movie, Search: "1080p")));
        Assert.Empty(Titles(new DownloadHistoryFilter(Status: DownloadStatus.Failed, Import: DownloadImportFilter.Imported)));
    }

    [Fact]
    public void Download_history_pages_the_filtered_list()
    {
        for (var i = 0; i < 120; i++)
        {
            _downloads.Add(Download($"Release.{i:000}", i % 3 == 0 ? DownloadStatus.Failed : DownloadStatus.Completed, DownloadCategory.Other, i), []);
        }

        var filter = new DownloadHistoryFilter(Status: DownloadStatus.Failed);
        var (first, total) = _downloads.History(1, 25, filter);
        var (last, _) = _downloads.History(2, 25, filter);
        Assert.Equal(40, total);
        Assert.Equal(25, first.Count);
        Assert.Equal(15, last.Count);
        Assert.Equal("Release.000", first[0].Title);
        Assert.Empty(_downloads.History(3, 25, filter).Records);
        Assert.Equal(120, _downloads.History(1, 200).Records.Count);
    }

    [Fact]
    public void History_events_filter_by_event_media_type_indexer_and_search()
    {
        AddEvent("Dune.Part.Two.2024.2160p", HistoryEventType.Grabbed, MediaType.Movie, "NZBgeek", 1);
        AddEvent("Dune.Part.Two.2024.2160p", HistoryEventType.DownloadCompleted, MediaType.Movie, "NZBgeek", 2);
        AddEvent("Dune.Part.Two.2024.2160p", HistoryEventType.Imported, MediaType.Movie, null, 3);
        AddEvent("Severance.S02E01", HistoryEventType.Grabbed, MediaType.Series, "DrunkenSlug", 4);
        AddEvent("Severance.S02E01", HistoryEventType.ImportFailed, MediaType.Series, "drunkenslug", 5);
        AddEvent("Search.Page.Grab", HistoryEventType.Grabbed, null, "nzbgeek", 6);

        Assert.Equal(6, _history.Page(1, 50).Total);
        Assert.Equal(3, _history.Page(1, 50, filter: new HistoryFilter(EventTypes: [HistoryEventType.Grabbed])).Total);
        Assert.Equal(2, _history.Page(1, 50, filter: new HistoryFilter(EventTypes: [HistoryEventType.Imported, HistoryEventType.ImportFailed])).Total);
        Assert.Equal(3, _history.Page(1, 50, filter: new HistoryFilter(MediaType: MediaType.Movie)).Total);
        Assert.Equal(2, _history.Page(1, 50, filter: new HistoryFilter(MediaType: MediaType.Series)).Total);
        Assert.Equal(3, _history.Page(1, 50, filter: new HistoryFilter(Indexer: "NZBGEEK")).Total);
        Assert.Equal(3, _history.Page(1, 50, filter: new HistoryFilter(Search: "part two")).Total);

        var combined = _history.Page(1, 50, filter: new HistoryFilter([HistoryEventType.Grabbed], MediaType.Series, "severance", "DrunkenSlug"));
        Assert.Equal("Severance.S02E01", Assert.Single(combined.Records).SourceTitle);
        Assert.Equal(1, _history.Page(1, 50, movieId: 1, filter: new HistoryFilter(EventTypes: [HistoryEventType.Imported])).Total);

        Assert.Equal(["DrunkenSlug", "NZBgeek"], _history.Indexers(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Blocklist_filters_by_search_and_media_type()
    {
        _blocklist.Add(new BlocklistItem { Date = Now.AddHours(-1), SourceTitle = "Dune.2021.1080p-BAD", MediaType = MediaType.Movie, MovieId = 1 });
        _blocklist.Add(new BlocklistItem { Date = Now.AddHours(-2), SourceTitle = "Severance.S01E01-BAD", MediaType = MediaType.Series, SeriesId = 1 });
        _blocklist.Add(new BlocklistItem { Date = Now.AddHours(-3), SourceTitle = "Arrival_2016-BAD", MediaType = MediaType.Movie, MovieId = 2 });

        Assert.Equal(3, _blocklist.Page(1, 50).Total);
        Assert.Equal(["Dune.2021.1080p-BAD", "Arrival_2016-BAD"], _blocklist.Page(1, 50, mediaType: MediaType.Movie).Records.Select(r => r.SourceTitle));
        Assert.Equal(["Severance.S01E01-BAD"], _blocklist.Page(1, 50, "bad s01").Records.Select(r => r.SourceTitle));
        Assert.Equal(["Arrival_2016-BAD"], _blocklist.Page(1, 50, "l_2").Records.Select(r => r.SourceTitle));
        Assert.Empty(_blocklist.Page(1, 50, "severance", MediaType.Movie).Records);
    }

    [Fact]
    public void Search_terms_are_trimmed_deduplicated_limited_and_escaped()
    {
        Assert.Empty(TextSearch.Terms(null));
        Assert.Empty(TextSearch.Terms("  \t "));
        Assert.Equal(["dune", "two"], TextSearch.Terms("  dune  DUNE two "));
        Assert.Equal(TextSearch.MaxTerms, TextSearch.Terms("a b c d e f g h i j k").Count);
        Assert.Equal(@"%50\%\_off\\%", TextSearch.ContainsPattern(@"50%_off\"));
    }

    private void Seed()
    {
        // Newest first: Dune (imported), Arrival (import waiting), Severance E01 (import failed), Severance E02 (download failed),
        // Linux ISO (other, no import), Old movie (download failed), Anime (custom category, no import).
        var dune = _downloads.Add(Download("Dune.Part.Two.2024.2160p", DownloadStatus.Completed, DownloadCategory.Movie, 1), []);
        var arrival = _downloads.Add(Download("Arrival.2016.1080p", DownloadStatus.Completed, DownloadCategory.Movie, 2), []);
        var severance = _downloads.Add(Download("Severance.S02E01.1080p", DownloadStatus.Completed, DownloadCategory.Series, 3), []);
        _downloads.Add(Download("Severance.S02E02.1080p", DownloadStatus.Failed, DownloadCategory.Series, 4), []);
        _downloads.Add(Download("Some.Linux.ISO", DownloadStatus.Completed, DownloadCategory.Other, 5), []);
        _downloads.Add(Download("Old.Movie.1999", DownloadStatus.Failed, DownloadCategory.Movie, 6), []);
        _downloads.Add(Download("Anime.Show.S01E01", DownloadStatus.Completed, DownloadCategory.Series, 7) with { CategoryName = "anime" }, []);

        _imports.Save(Import(dune.Id, ImportState.Imported));
        _imports.Save(Import(arrival.Id, ImportState.Waiting));
        _imports.Save(Import(severance.Id, ImportState.Failed));
    }

    private IReadOnlyList<string> Titles(DownloadHistoryFilter filter) => [.. _downloads.History(1, 200, filter).Records.Select(r => r.Title)];

    private static DownloadRecord Download(string title, DownloadStatus status, DownloadCategory category, int hoursAgo) => new()
    {
        Title = title,
        Status = status,
        Category = category,
        NzbPath = "/tmp/x.nzb",
        WorkFolder = "/tmp/x",
        Added = Now.AddHours(-hoursAgo - 1),
        Completed = status is DownloadStatus.Completed or DownloadStatus.Failed ? Now.AddHours(-hoursAgo) : null,
    };

    private static DownloadImport Import(int downloadId, ImportState state) =>
        new(downloadId, state, null, Now, "x", MediaType.Movie, 1, null, [], []);

    private void AddEvent(string title, HistoryEventType eventType, MediaType? mediaType, string? indexer, int hoursAgo) => _history.Add(new HistoryItem
    {
        Date = Now.AddHours(-hoursAgo),
        EventType = eventType,
        MediaType = mediaType,
        MovieId = mediaType == MediaType.Movie ? 1 : null,
        SeriesId = mediaType == MediaType.Series ? 1 : null,
        SourceTitle = title,
        Indexer = indexer,
    });

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(file);
        }
    }
}
