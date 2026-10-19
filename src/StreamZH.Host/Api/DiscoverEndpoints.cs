// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.Metadata;
using StreamZH.Library.Requests;
using StreamZH.Library.Search;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Host.Api;

/// <summary>
/// Discover (0.10), for every signed-in user incl. requesters: TMDB rows (trending, popular, upcoming, top rated) and
/// search with the library and request state of each title, and the details of a title with its seasons. Posters come
/// through StreamZH's image route (/api/v1/mediacover), never straight from TMDB.
/// </summary>
public static class DiscoverEndpoints
{
    public static IEndpointRouteBuilder MapDiscoverEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/discover").WithTags("Discover").RequireAuthorization(AuthSchemes.AnyUserPolicy);

        group.MapGet("/search", async (string? query, int? page, string? mediaType, DiscoverService discover, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = ["Enter a title to search for."] });
            }

            MediaType? type = null;
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                if (!RequestApi.TryType(mediaType, out var parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["Use movie or series."] });
                }

                type = parsed;
            }

            return Results.Ok(DiscoverPageResource.From(await discover.SearchAsync(query, page ?? 1, type, ct)));
        });

        // /discover/movies/trending, /discover/series/topRated …
        group.MapGet("/{type}/{chart}", async (string type, string chart, int? page, DiscoverService discover, CancellationToken ct) =>
        {
            if (!RequestApi.TryType(type, out var mediaType) || !TryChart(chart, out var parsed))
            {
                return Results.NotFound();
            }

            return Results.Ok(DiscoverPageResource.From(await discover.ChartAsync(mediaType, parsed, page ?? 1, ct)));
        });

        group.MapGet("/{type}/{tmdbId:int}", async (
            string type, int tmdbId, HttpContext http, DiscoverService discover, MetadataConfig metadata, AutomationHold hold,
            RequestRepository requests, UserRepository users, CancellationToken ct) =>
        {
            if (!RequestApi.TryType(type, out var mediaType) || tmdbId <= 0)
            {
                return Results.NotFound();
            }

            var details = await discover.DetailsAsync(mediaType, tmdbId, metadata.CertificationCountry, ct);
            var actor = RequestApi.Actor(http);
            var visible = requests.ForTitle(mediaType, tmdbId).Where(r => RequestService.CanSee(actor, r)).Take(20).ToList();
            var names = RequestUserNames.Of(users, visible.SelectMany(r => new[] { r.RequestedBy, r.DecidedBy }).OfType<int>());
            var item = DiscoverItemResource.From(details.Entry);
            var seriesState = details.Entry.State;
            return Results.Ok(new DiscoverDetailsResource(
                item.MediaType,
                item.TmdbId,
                item.Title,
                item.Year,
                item.Overview,
                item.PosterPath,
                item.PosterUrl,
                item.BackdropUrl,
                item.Rating,
                item.ReleaseDate,
                item.Status,
                details.Tagline,
                details.Genres,
                details.Runtime,
                details.Status,
                details.Certification,
                mediaType == MediaType.Series ? details.Studios.FirstOrDefault() : null,
                details.Studios,
                [.. details.Cast.Select(c => new DiscoverCastResource(c.Name, c.Character, RequestApi.Image(c.ProfilePath, "w185")))],
                [.. details.Seasons.Select(s => new DiscoverSeasonResource(
                    s.SeasonNumber,
                    s.Name,
                    s.EpisodeCount,
                    s.AirDate,
                    RequestApi.Image(s.PosterPath, "w342"),
                    TitleStateResource.From(new TitleState(seriesState.InLibrary, s.Available, s.PartiallyAvailable, seriesState.ItemId, s.Requested, s.RequestId))))],
                [.. visible.Select(r => RequestResource.From(r, names, actor))],
                hold.IsOn));
        });

        return app;
    }

    /// <summary>trending, popular, upcoming, topRated (top-rated and top_rated work too).</summary>
    public static bool TryChart(string? text, out TmdbChart chart)
    {
        switch (text?.Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal))
        {
            case "trending":
                chart = TmdbChart.Trending;
                return true;
            case "popular":
                chart = TmdbChart.Popular;
                return true;
            case "upcoming":
                chart = TmdbChart.Upcoming;
                return true;
            case "toprated":
                chart = TmdbChart.TopRated;
                return true;
            default:
                chart = default;
                return false;
        }
    }
}
