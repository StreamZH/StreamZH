// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Jobs;
using StreamZH.Host.Auth;
using StreamZH.Library.Wanted;

namespace StreamZH.Host.Api;

/// <summary>Wanted → Missing and Cutoff unmet (Radarr/Sonarr): paged lists, search buttons and "Unmonitor selected".</summary>
public static class WantedEndpoints
{
    public static IEndpointRouteBuilder MapWantedEndpoints(this IEndpointRouteBuilder app)
    {
        var wanted = app.MapGroup("/api/v1/wanted").WithTags("Wanted").RequireAuthorization();

        wanted.MapGet("/missing", (HttpRequest request, WantedService service) =>
            Parse(request, out var query) is { } invalid ? invalid : Results.Ok(service.Missing(query)));

        wanted.MapGet("/cutoff", (HttpRequest request, WantedService service) =>
            Parse(request, out var query) is { } invalid ? invalid : Results.Ok(service.Cutoff(query)));

        wanted.MapGet("/summary", (WantedService service) => service.Summary());

        // Searching costs indexer API hits, so it needs the same rights as the other search commands.
        wanted.MapPost("/search", (WantedSearchRequest request, WantedSearch search, JobQueue jobs) =>
        {
            var kind = request.Kind?.Trim().ToLowerInvariant();
            if (kind is not ("missing" or "cutoff"))
            {
                return Invalid("kind", "Use missing or cutoff.");
            }

            WantedItemType? type = null;
            if (!string.IsNullOrEmpty(request.Type))
            {
                if (ParseType(request.Type) is not { } parsed)
                {
                    return Invalid("type", "Use movies or episodes.");
                }

                type = parsed;
            }

            int queued;
            if (request.All)
            {
                queued = kind == "missing" ? search.AllMissing(type) : search.AllCutoffUnmet(type);
            }
            else
            {
                var movieIds = request.MovieIds ?? [];
                var episodeIds = request.EpisodeIds ?? [];
                if (movieIds.Length == 0 && episodeIds.Length == 0)
                {
                    return Invalid("movieIds", "Select movies or episodes, or search all.");
                }

                queued = search.Selected(movieIds, episodeIds);
            }

            return Results.Accepted(value: new WantedSearchResponse(queued, jobs.Status));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        wanted.MapPost("/monitor", async (WantedMonitorRequest request, WantedService service, CancellationToken ct) =>
        {
            var movieIds = request.MovieIds ?? [];
            var episodeIds = request.EpisodeIds ?? [];
            if (movieIds.Length == 0 && episodeIds.Length == 0)
            {
                return Invalid("movieIds", "Select movies or episodes.");
            }

            var changed = await service.SetMonitoredAsync(movieIds, episodeIds, request.Monitored, ct);
            return Results.Ok(new WantedMonitorResponse(changed));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }

    /// <summary>
    /// Query string: type (movies, episodes), page, pageSize (1–250), sortKey (title, date, lastSearched, status, profile,
    /// quality), sortDirection (ascending, descending), monitored (true = monitored only), available (true = available only).
    /// </summary>
    private static IResult? Parse(HttpRequest request, out WantedQuery query)
    {
        query = new WantedQuery(WantedItemType.Movies);
        var errors = new Dictionary<string, string[]>();
        var q = request.Query;

        var type = WantedItemType.Movies;
        if (q.TryGetValue("type", out var typeValue) && !string.IsNullOrEmpty(typeValue))
        {
            if (ParseType(typeValue!) is { } parsed)
            {
                type = parsed;
            }
            else
            {
                errors["type"] = ["Use movies or episodes."];
            }
        }

        var page = Int(q["page"], 1, 1, int.MaxValue, "page", errors);
        var pageSize = Int(q["pageSize"], WantedQuery.DefaultPageSize, 1, WantedQuery.MaxPageSize, "pageSize", errors);

        var sort = WantedSortKey.Date;
        if (q["sortKey"].ToString() is { Length: > 0 } sortValue)
        {
            var key = Enum.GetValues<WantedSortKey>().FirstOrDefault(k => string.Equals(k.ToString(), sortValue, StringComparison.OrdinalIgnoreCase), (WantedSortKey)(-1));
            if ((int)key < 0)
            {
                errors["sortKey"] = ["Use title, date, lastSearched, status, profile or quality."];
            }
            else
            {
                sort = key;
            }
        }

        var descending = true;
        if (q["sortDirection"].ToString() is { Length: > 0 } direction)
        {
            switch (direction.ToLowerInvariant())
            {
                case "descending" or "desc":
                    descending = true;
                    break;
                case "ascending" or "asc":
                    descending = false;
                    break;
                default:
                    errors["sortDirection"] = ["Use ascending or descending."];
                    break;
            }
        }

        var monitored = Bool(q["monitored"], true, "monitored", errors);
        var available = Bool(q["available"], true, "available", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        query = new WantedQuery(type, page, pageSize, sort, descending, monitored, available);
        return null;
    }

    private static WantedItemType? ParseType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "movies" or "movie" => WantedItemType.Movies,
        "episodes" or "episode" or "series" => WantedItemType.Episodes,
        _ => null,
    };

    private static int Int(string? value, int fallback, int min, int max, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= min && number <= max)
        {
            return number;
        }

        errors[field] = [max == int.MaxValue ? $"Use a whole number from {min}." : $"Use a whole number from {min} to {max}."];
        return fallback;
    }

    private static bool Bool(string? value, bool fallback, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        errors[field] = ["Use true or false."];
        return fallback;
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

/// <param name="Kind">"missing" or "cutoff".</param>
/// <param name="Type">"movies", "episodes" or empty for both (with <see cref="All"/>).</param>
/// <param name="All">Search everything on the list (default filters) instead of the selected ids.</param>
public sealed record WantedSearchRequest(string? Kind, string? Type = null, int[]? MovieIds = null, int[]? EpisodeIds = null, bool All = false);

public sealed record WantedSearchResponse(int Queued, JobStatus Jobs);

public sealed record WantedMonitorRequest(int[]? MovieIds, int[]? EpisodeIds, bool Monitored = false);

public sealed record WantedMonitorResponse(int Changed);
