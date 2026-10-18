// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Indexers;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Search;

public sealed class FreeSearchServiceTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    private FreeSearchService Service(TimeSpan? timeout = null) =>
        new(_bed.Indexers, _bed.Client, _bed.Cache, _bed.Grab(), NullLogger<FreeSearchService>.Instance)
        {
            IndexerTimeout = timeout ?? TimeSpan.FromSeconds(10),
        };

    private static FreeSearchRequest Request(string query, TextSearchType type = TextSearchType.Search, int[]? categories = null, int[]? indexerIds = null) =>
        new(query, type, categories ?? [], indexerIds ?? []);

    [Fact]
    public async Task Searches_every_interactive_indexer_newest_first_with_quality_and_languages()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.AddIndexer(2, "Beta");
        _bed.Indexers.Indexers.Add(FakeIndexerProvider.Definition(3, "NoInteractive") with { EnableInteractiveSearch = false });
        _bed.Client.Returns(1, _bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", ageDays: 5));
        _bed.Client.Returns(2,
            _bed.Release("Dune.Part.Two.2024.2160p.WEB.H265-GRP", indexerId: 2, ageDays: 1) with { Categories = [2045] },
            _bed.Release("some random words", indexerId: 2, ageDays: 3));

        var result = await Service().SearchAsync(Request("  dune  ", TextSearchType.Search, [2000, 2000, 5000]));

        Assert.Equal(2, result.IndexerCount);
        Assert.Empty(result.Failures);
        Assert.Equal(
            ["Dune.Part.Two.2024.2160p.WEB.H265-GRP", "some random words", "Dune.2021.German.DL.1080p.BluRay.x264-GRP"],
            result.Releases.Select(r => r.Release.Title));
        Assert.Equal(Quality.WebDl2160p, result.Releases[0].Quality);
        Assert.Equal(Quality.Unknown, result.Releases[1].Quality);
        Assert.Contains(Language.German, result.Releases[2].Languages);
        Assert.DoesNotContain(Language.Unknown, result.Releases[1].Languages);

        var searches = _bed.Client.Searches.ToList();
        Assert.Equal([1, 2], searches.Select(s => s.IndexerId).Order());
        var criteria = Assert.IsType<TextSearchCriteria>(searches[0].Criteria);
        Assert.Equal(("dune", TextSearchType.Search), (criteria.Query, criteria.Type));
        Assert.Equal([2000, 5000], criteria.Categories);
    }

    [Fact]
    public async Task Indexer_ids_narrow_the_search_and_failures_are_reported()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.AddIndexer(2, "Beta");
        _bed.AddIndexer(3, "Gamma");
        _bed.Client.Handlers[2] = (_, _) => throw new IndexerException("Request limit reached", 500);
        _bed.Client.Handlers[3] = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return [];
        };

        var result = await Service(TimeSpan.FromMilliseconds(200)).SearchAsync(Request("dune", indexerIds: [2, 3]));

        Assert.Equal(2, result.IndexerCount);
        Assert.Equal([2, 3], _bed.Client.Searches.Select(s => s.IndexerId).Order());
        Assert.Equal(["Beta", "Gamma"], result.Failures.Select(f => f.Indexer).Order());
        Assert.Equal("Request limit reached", result.Failures.Single(f => f.IndexerId == 2).Message);
        Assert.Equal([2, 3], _bed.Indexers.Failures.Select(f => f.IndexerId).Order());
    }

    [Fact]
    public async Task No_active_indexer_returns_nothing()
    {
        _bed.AddIndexer(1, "Alpha");

        var result = await Service().SearchAsync(Request("dune", indexerIds: [9]));

        Assert.Equal(0, result.IndexerCount);
        Assert.Empty(result.Releases);
        Assert.Empty(_bed.Client.Searches);
    }

    [Fact]
    public async Task Rejects_overlong_queries()
    {
        _bed.AddIndexer(1, "Alpha");

        var error = await Assert.ThrowsAsync<LibraryException>(() => Service().SearchAsync(Request(new string('x', FreeSearchService.MaxQueryLength + 1))));

        Assert.Equal(LibraryError.Invalid, error.Error);
    }

    [Fact]
    public async Task Grab_writes_the_nzb_and_records_history_without_media_item()
    {
        const string Title = "Some.Show.S01E02.1080p.WEB.H264-GRP";
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1, _bed.Release(Title, 2) with { Categories = [5040] });
        var service = Service();
        var found = await service.SearchAsync(Request("some show"));
        var release = found.Releases[0].Release;

        var grabbed = await service.GrabAsync(release.IndexerId, release.Guid);

        Assert.Equal(Path.Combine(_bed.DataDirectory, "downloads", "nzb", $"{Title}.nzb"), grabbed.NzbPath);
        Assert.True(File.Exists(grabbed.NzbPath));
        var (records, total) = _bed.History.Page(1, 10);
        Assert.Equal(1, total);
        var entry = records[0];
        Assert.Equal(HistoryEventType.Grabbed, entry.EventType);
        Assert.Null(entry.MediaType);
        Assert.Null(entry.MovieId);
        Assert.Null(entry.SeriesId);
        Assert.Empty(entry.EpisodeIds);
        Assert.Equal((Title, "Alpha", Quality.WebDl1080p.Id), (entry.SourceTitle, entry.Indexer, entry.QualityId));

        _bed.UseBuiltInClient();
        await service.GrabAsync(release.IndexerId, release.Guid);
        var queued = Assert.Single(_bed.Engine.Active());
        Assert.Equal(StreamZH.Core.Downloads.DownloadCategory.Other, queued.Category);
        Assert.Null(queued.Link.MediaType);

        var grabbedEvent = _bed.Grabbed.First();
        Assert.Null(grabbedEvent.MediaType);

        await Assert.ThrowsAsync<LibraryException>(() => service.GrabAsync(1, "unknown"));
    }

    [Fact]
    public async Task Results_show_the_custom_formats_of_the_media_type_the_name_looks_like()
    {
        var formats = new CustomFormatRepository(_bed.Database);
        formats.Add(CustomFormats.CustomFormatTestData.Movie("x265 movie", 0, CustomFormats.CustomFormatTestData.Title("x265")));
        formats.Add(CustomFormats.CustomFormatTestData.Format(
            Core.Media.MediaType.Series, "x265 series", 0, CustomFormats.CustomFormatTestData.Title("x265")));
        _bed.Indexers.Indexers.Add(FakeIndexerProvider.Definition(1, "Alpha"));
        _bed.Client.Returns(1,
            _bed.Release("Dune.2021.1080p.WEB.x265-GRP", ageDays: 1),
            _bed.Release("Show.S01E01.1080p.WEB.x265-GRP", ageDays: 2),
            _bed.Release("Dune.2021.1080p.WEB.x264-GRP", ageDays: 3));

        var service = new FreeSearchService(_bed.Indexers, _bed.Client, _bed.Cache, _bed.Grab(), NullLogger<FreeSearchService>.Instance, formats);
        var result = await service.SearchAsync(Request("x"));

        Assert.Equal(
            [["x265 movie"], ["x265 series"], []],
            result.Releases.Select(r => r.CustomFormats.Select(f => f.Name).ToArray()));
    }

    public void Dispose() => _bed.Dispose();
}
