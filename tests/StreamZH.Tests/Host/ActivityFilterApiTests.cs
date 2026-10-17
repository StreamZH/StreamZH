// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Api;

namespace StreamZH.Tests.Host;

/// <summary>Filter and page-size parameters of the Activity endpoints (download history, history events, blocklist).</summary>
public sealed class ActivityFilterApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());

    [Fact]
    public async Task Download_history_takes_status_import_category_search_and_since()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var downloads = _factory.Services.GetRequiredService<DownloadRepository>();
        var imports = _factory.Services.GetRequiredService<DownloadImportRepository>();
        var now = DateTime.UtcNow;
        var dune = downloads.Add(Download("Dune.Part.Two.2024.2160p", DownloadStatus.Completed, DownloadCategory.Movie, now.AddHours(-1)), []);
        downloads.Add(Download("Arrival.2016.1080p", DownloadStatus.Completed, DownloadCategory.Movie, now.AddDays(-10)), []);
        downloads.Add(Download("Severance.S02E01", DownloadStatus.Failed, DownloadCategory.Series, now.AddHours(-2)), []);
        downloads.Add(Download("Anime.S01E01", DownloadStatus.Completed, DownloadCategory.Series, now.AddHours(-3)) with { CategoryName = "anime" }, []);
        imports.Save(new DownloadImport(dune.Id, ImportState.Imported, null, now, "Dune", MediaType.Movie, 1, null, [], []));

        Assert.Equal(4, await TotalAsync(client, "/api/v1/download/history"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/download/history?status=failed"));
        Assert.Equal(3, await TotalAsync(client, "/api/v1/download/history?status=Completed"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/download/history?import=imported"));
        Assert.Equal(2, await TotalAsync(client, "/api/v1/download/history?import=notImported"));
        Assert.Equal(0, await TotalAsync(client, "/api/v1/download/history?import=importFailed"));
        Assert.Equal(2, await TotalAsync(client, "/api/v1/download/history?category=movies"));
        Assert.Equal(2, await TotalAsync(client, "/api/v1/download/history?category=series"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/download/history?category=anime"));
        Assert.Equal(4, await TotalAsync(client, "/api/v1/download/history?category=all&status=all&import=all"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/download/history?search=part%20TWO"));
        Assert.Equal(3, await TotalAsync(client, $"/api/v1/download/history?since={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/download/history?status=completed&import=notImported&category=movie&search=arrival"));

        var invalid = await client.GetAsync("/api/v1/download/history?status=downloading&import=maybe");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!;
        Assert.NotNull(errors["status"]);
        Assert.NotNull(errors["import"]);
    }

    [Fact]
    public async Task History_takes_event_types_media_type_search_and_indexer()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var history = _factory.Services.GetRequiredService<HistoryRepository>();
        var now = DateTime.UtcNow;
        history.Add(Event("Dune.Part.Two.2024", HistoryEventType.Grabbed, MediaType.Movie, "NZBgeek", now.AddHours(-3)));
        history.Add(Event("Dune.Part.Two.2024", HistoryEventType.Imported, MediaType.Movie, null, now.AddHours(-2)));
        history.Add(Event("Severance.S02E01", HistoryEventType.DownloadFailed, MediaType.Series, "DrunkenSlug", now.AddHours(-1)));

        Assert.Equal(3, await TotalAsync(client, "/api/v1/history"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/history?eventType=grabbed"));
        Assert.Equal(2, await TotalAsync(client, "/api/v1/history?eventType=Imported,DownloadFailed"));
        Assert.Equal(2, await TotalAsync(client, "/api/v1/history?mediaType=movie"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/history?mediaType=Series"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/history?indexer=nzbgeek"));
        Assert.Equal(2, await TotalAsync(client, "/api/v1/history?search=dune"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/history?search=dune&eventType=Imported&mediaType=movie"));

        var indexers = (await client.GetFromJsonAsync<string[]>("/api/v1/history/indexers"))!;
        Assert.Equal(["DrunkenSlug", "NZBgeek"], indexers);

        var invalid = await client.GetAsync("/api/v1/history?eventType=Grabbed,Exploded&mediaType=book");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!;
        Assert.NotNull(errors["eventType"]);
        Assert.NotNull(errors["mediaType"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/history?eventType=3")).StatusCode);
    }

    [Fact]
    public async Task Blocklist_takes_search_and_media_type()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var blocklist = _factory.Services.GetRequiredService<BlocklistRepository>();
        blocklist.Add(new BlocklistItem { Date = DateTime.UtcNow, SourceTitle = "Dune.2021-BAD", MediaType = MediaType.Movie, MovieId = 1 });
        blocklist.Add(new BlocklistItem { Date = DateTime.UtcNow, SourceTitle = "Severance.S01E01-BAD", MediaType = MediaType.Series, SeriesId = 1 });

        Assert.Equal(2, await TotalAsync(client, "/api/v1/blocklist"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/blocklist?mediaType=series"));
        Assert.Equal(1, await TotalAsync(client, "/api/v1/blocklist?search=dune"));
        Assert.Equal(0, await TotalAsync(client, "/api/v1/blocklist?search=dune&mediaType=series"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/blocklist?mediaType=music")).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/download/history")]
    [InlineData("/api/v1/history")]
    [InlineData("/api/v1/blocklist")]
    public async Task Page_size_goes_up_to_200(string path)
    {
        var client = await _factory.CreateOwnerClientAsync();

        Assert.Equal(200, (await client.GetFromJsonAsync<JsonObject>($"{path}?pageSize=200"))!["pageSize"]!.GetValue<int>());
        Assert.Equal(200, (await client.GetFromJsonAsync<JsonObject>($"{path}?pageSize=5000"))!["pageSize"]!.GetValue<int>());
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonObject>($"{path}?pageSize=0"))!["pageSize"]!.GetValue<int>());
        Assert.Equal(DownloadEndpoints.MaxHistoryPageSize, HistoryEndpoints.MaxPageSize);
    }

    private static async Task<int> TotalAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["totalRecords"]!.GetValue<int>();
    }

    private static DownloadRecord Download(string title, DownloadStatus status, DownloadCategory category, DateTime completed) => new()
    {
        Title = title,
        Status = status,
        Category = category,
        NzbPath = "/tmp/x.nzb",
        WorkFolder = "/tmp/x",
        Added = completed.AddMinutes(-30),
        Completed = completed,
    };

    private static HistoryItem Event(string title, HistoryEventType eventType, MediaType mediaType, string? indexer, DateTime date) => new()
    {
        Date = date,
        EventType = eventType,
        MediaType = mediaType,
        MovieId = mediaType == MediaType.Movie ? 1 : null,
        SeriesId = mediaType == MediaType.Series ? 1 : null,
        SourceTitle = title,
        Indexer = indexer,
    };

    public void Dispose() => _factory.Dispose();
}
