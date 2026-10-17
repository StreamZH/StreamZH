// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;

namespace StreamZH.Host.Api;

public static class HistoryEndpoints
{
    public const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/history", (int? page, int? pageSize, int? movieId, int? seriesId, HistoryRepository history) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, MaxPageSize);
            var (records, total) = history.Page(p, size, movieId, seriesId);
            return new HistoryPage(p, size, total, [.. records.Select(HistoryResource.From)]);
        }).RequireAuthorization().WithTags("History");

        return app;
    }
}

public sealed record HistoryPage(int Page, int PageSize, int TotalRecords, IReadOnlyList<HistoryResource> Records);

public sealed record HistoryResource(
    int Id,
    DateTime Date,
    HistoryEventType EventType,
    MediaType MediaType,
    int? MovieId,
    int? SeriesId,
    IReadOnlyList<int> EpisodeIds,
    string SourceTitle,
    string Quality,
    int QualityId,
    IReadOnlyList<string> Languages,
    string? ReleaseGroup,
    string? Indexer,
    int? IndexerId,
    long Size,
    IReadOnlyDictionary<string, string?> Data)
{
    public static HistoryResource From(HistoryItem h) => new(
        h.Id, h.Date, h.EventType, h.MediaType, h.MovieId, h.SeriesId, h.EpisodeIds, h.SourceTitle,
        StreamZH.Core.Quality.Quality.FindById(h.QualityId).Name, h.QualityId,
        [.. h.LanguageIds.Select(Language.FindById).Where(l => l != Language.Unknown).Select(l => l.Name)],
        h.ReleaseGroup, h.Indexer, h.IndexerId, h.Size, h.Data);
}
