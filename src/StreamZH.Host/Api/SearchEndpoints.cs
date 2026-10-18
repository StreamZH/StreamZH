// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Indexers;
using StreamZH.Host.Auth;
using StreamZH.Library.Search;

namespace StreamZH.Host.Api;

/// <summary>The Search page: search indexers by free text without a library item and grab a result (like Prowlarr's Search).</summary>
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        // Searching and grabbing cost indexer API hits, so they need the same rights as changing things.
        var api = app.MapGroup("/api/v1/search").WithTags("Search").RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapGet("", async (string? query, string? type, string? categories, string? indexerIds, FreeSearchService search, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var searchType = ParseType(type);
            if (searchType is null)
            {
                errors["type"] = ["Use search, movie or tv."];
            }

            var categoryList = ParseIds(categories);
            if (categoryList is null)
            {
                errors["categories"] = ["Pass Newznab category numbers separated by commas, e.g. 2000,5040."];
            }

            var indexerList = ParseIds(indexerIds);
            if (indexerList is null)
            {
                errors["indexerIds"] = ["Pass indexer ids separated by commas."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var result = await search.SearchAsync(new FreeSearchRequest(query, searchType!.Value, categoryList!, indexerList!), ct);
            return Results.Ok(FreeSearchResource.From(result, DateTimeOffset.UtcNow));
        });

        api.MapPost("/grab", async (GrabRequest request, FreeSearchService search, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Guid) || request.IndexerId <= 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["guid"] = ["Pass the indexerId and guid of a search result."] });
            }

            try
            {
                var result = await search.GrabAsync(request.IndexerId, request.Guid, ct, request.Category);
                return Results.Ok(new GrabResource(result.Title, result.Indexer, Path.GetFileName(result.NzbPath), Path.GetDirectoryName(result.NzbPath) ?? string.Empty));
            }
            catch (IndexerException ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status502BadGateway, title: ex.Message, detail: ex.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = "indexer-error" });
            }
        });

        return app;
    }

    private static TextSearchType? ParseType(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "search" => TextSearchType.Search,
        "movie" or "movies" => TextSearchType.Movie,
        "tv" or "tvsearch" => TextSearchType.Tv,
        _ => null,
    };

    /// <summary>"1,2, 3" → [1, 2, 3]; empty → []; null when a part is not a positive number.</summary>
    private static List<int>? ParseIds(string? value)
    {
        var ids = new List<int>();
        foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return null;
            }

            ids.Add(id);
        }

        return ids;
    }
}

/// <summary>Free search result: releases newest first, how many indexers were asked and which failed.</summary>
public sealed record FreeSearchResource(int Indexers, IReadOnlyList<IndexerFailureResource> FailedIndexers, IReadOnlyList<FreeReleaseResource> Releases)
{
    public static FreeSearchResource From(FreeSearchResult result, DateTimeOffset now) => new(
        result.IndexerCount,
        [.. result.Failures.Select(f => new IndexerFailureResource(f.IndexerId, f.Indexer, f.Message))],
        [.. result.Releases.Select(r => FreeReleaseResource.From(r, now))]);
}

/// <summary>
/// One free search result. Quality, languages and custom formats (of the media type the name looks like) are read from the
/// name, for display only; without a library item there is no profile, so <see cref="CustomFormatScore"/> is 0.
/// </summary>
public sealed record FreeReleaseResource(
    string Guid,
    int IndexerId,
    string Indexer,
    string Title,
    long Size,
    double AgeHours,
    DateTimeOffset PublishDate,
    int? Grabs,
    IReadOnlyList<int> Categories,
    string? InfoUrl,
    string Quality,
    IReadOnlyList<string> Languages)
{
    public IReadOnlyList<CustomFormatRefResource> CustomFormats { get; init; } = [];

    public int CustomFormatScore { get; init; }

    public static FreeReleaseResource From(FreeSearchRelease r, DateTimeOffset now) => new(
        r.Release.Guid,
        r.Release.IndexerId,
        r.Release.Indexer,
        r.Release.Title,
        r.Release.Size,
        Math.Round(Math.Max(0, (now - r.Release.PublishDate).TotalHours), 1),
        r.Release.PublishDate,
        r.Release.Grabs,
        r.Release.Categories,
        r.Release.InfoUrl,
        r.Quality.Name,
        [.. r.Languages.Select(l => l.Name)])
    {
        CustomFormats = CustomFormatRefResource.From(r.CustomFormats),
    };
}
