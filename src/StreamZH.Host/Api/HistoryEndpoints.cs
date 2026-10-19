// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Claims;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Host.Api;

public static class HistoryEndpoints
{
    public const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        // Filters: eventType (comma list of event names), mediaType (movie, series), search (words in the release title), indexer.
        app.MapGet("/api/v1/history", (int? page, int? pageSize, int? movieId, int? seriesId, string? eventType, string? mediaType, string? search, string? indexer, HistoryRepository history, ClaimsPrincipal user) =>
        {
            var errors = new Dictionary<string, string[]>();
            var eventTypes = new List<HistoryEventType>();
            foreach (var name in (eventType ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<HistoryEventType>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(name, out _))
                {
                    eventTypes.Add(parsed);
                }
                else
                {
                    errors["eventType"] = [$"Unknown event \"{name}\". Use {string.Join(", ", Enum.GetNames<HistoryEventType>())}."];
                }
            }

            var media = ParseMediaType(mediaType);
            if (media is null && !string.IsNullOrWhiteSpace(mediaType) && !IsAll(mediaType))
            {
                errors["mediaType"] = ["Use movie or series."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, MaxPageSize);
            var (records, total) = history.Page(p, size, movieId, seriesId, new HistoryFilter(eventTypes, media, search, indexer));
            var admin = user.IsInRole(nameof(UserRole.Owner)) || user.IsInRole(nameof(UserRole.Admin));
            return Results.Ok(new HistoryPage(p, size, total, [.. records.Select(h => HistoryResource.From(h, admin))]));
        }).RequireAuthorization().WithTags("History");

        // The indexers that occur in the history, for the filter.
        app.MapGet("/api/v1/history/indexers", (HistoryRepository history) => history.Indexers())
            .RequireAuthorization().WithTags("History");

        return app;
    }

    /// <summary>"movie"/"movies" or "series"/"tv" (case-insensitive); null for anything else.</summary>
    public static MediaType? ParseMediaType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" => MediaType.Movie,
        "series" or "tv" => MediaType.Series,
        _ => null,
    };

    private static bool IsAll(string value) => string.Equals(value.Trim(), "all", StringComparison.OrdinalIgnoreCase);
}

public sealed record HistoryPage(int Page, int PageSize, int TotalRecords, IReadOnlyList<HistoryResource> Records);

public sealed record HistoryResource(
    int Id,
    DateTime Date,
    HistoryEventType EventType,
    MediaType? MediaType,
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
    /// <param name="admin">Owner or Admin. Friends (0.12) never get the download link, not even without its keys.</param>
    public static HistoryResource From(HistoryItem h, bool admin) => new(
        h.Id, h.Date, h.EventType, h.MediaType, h.MovieId, h.SeriesId, h.EpisodeIds, h.SourceTitle,
        StreamZH.Core.Quality.Quality.FindById(h.QualityId).Name, h.QualityId,
        [.. h.LanguageIds.Select(Language.FindById).Where(l => l != Language.Unknown).Select(l => l.Name)],
        h.ReleaseGroup, h.Indexer, h.IndexerId, h.Size,
        admin ? h.Data : h.Data.Where(p => p.Key != HistoryRepository.DownloadUrlKey).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
}
