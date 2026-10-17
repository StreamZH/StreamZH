// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Mvc;
using StreamZH.Core.Indexers;
using StreamZH.Core.Jobs;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library;
using StreamZH.Library.Search;

namespace StreamZH.Host.Api;

/// <summary>Interactive search, grabbing a release, and search commands (automatic search as background jobs).</summary>
public static class ReleaseEndpoints
{
    public const string MoviesSearch = "MoviesSearch";
    public const string MissingMoviesSearch = "MissingMoviesSearch";
    public const string SeasonSearch = "SeasonSearch";
    public const string EpisodeSearch = "EpisodeSearch";

    public static IEndpointRouteBuilder MapReleaseEndpoints(this IEndpointRouteBuilder app)
    {
        // Searching costs indexer API hits, so it needs the same rights as changing things.
        var api = app.MapGroup("/api/v1").WithTags("Release").RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapGet("/release", async (int? movieId, int? seriesId, int? seasonNumber, int? episodeId,
            ReleaseSearchService search, EpisodeLookup episodes, CancellationToken ct) =>
        {
            ReleaseSearchResult result;
            if (movieId is { } movie)
            {
                result = await search.SearchMovieAsync(movie, IndexerUse.InteractiveSearch, ct);
            }
            else if (episodeId is { } episode)
            {
                var (sid, season) = episodes.Locate(episode) ?? throw new LibraryException("Episode not found.", LibraryError.NotFound);
                result = await search.SearchEpisodesAsync(sid, season, [episode], IndexerUse.InteractiveSearch, ct);
            }
            else if (seriesId is { } series && seasonNumber is { } season)
            {
                result = await search.SearchEpisodesAsync(series, season, null, IndexerUse.InteractiveSearch, ct);
            }
            else
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieId"] = ["Pass movieId, seriesId with seasonNumber, or episodeId."],
                });
            }

            return Results.Ok(ReleaseSearchResource.From(result, DateTimeOffset.UtcNow));
        });

        api.MapGet("/release/indexers", ([FromServices] IIndexerProvider indexers) => new ActiveIndexersResource(
            indexers.Active(IndexerUse.AutomaticSearch).Count,
            indexers.Active(IndexerUse.InteractiveSearch).Count));

        api.MapPost("/release", async (GrabRequest request, GrabService grab, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Guid) || request.IndexerId <= 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["guid"] = ["Pass the indexerId and guid of a search result."] });
            }

            try
            {
                var result = await grab.GrabAsync(request.IndexerId, request.Guid, ct);
                return Results.Ok(new GrabResource(result.Title, result.Indexer, Path.GetFileName(result.NzbPath), Path.GetDirectoryName(result.NzbPath) ?? string.Empty));
            }
            catch (IndexerException ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status502BadGateway, title: ex.Message, detail: ex.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = "indexer-error" });
            }
        });

        api.MapPost("/command", (CommandRequest request, JobQueue jobs, MovieRepository movies, SeriesRepository seriesRepo, EpisodeLookup lookup) =>
        {
            var name = new[] { MoviesSearch, MissingMoviesSearch, SeasonSearch, EpisodeSearch }
                .FirstOrDefault(n => string.Equals(n, request.Name, StringComparison.OrdinalIgnoreCase));
            var queued = 0;

            switch (name)
            {
                case MoviesSearch:
                    var found = (request.MovieIds ?? []).Distinct().Select(movies.Find).OfType<StreamZH.Core.Media.Movie>().ToList();
                    if (found.Count == 0)
                    {
                        return Invalid("movieIds", "Pass the ids of one or more movies.");
                    }

                    var ids = found.Select(m => m.Id).ToList();
                    queued += jobs.Enqueue(new Job(
                        $"search-movies:{string.Join(',', ids)}",
                        found.Count == 1 ? $"Search {found[0].Title}" : $"Search {found.Count} movies",
                        (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchMoviesAsync(ids, ct))) ? 1 : 0;
                    break;

                case MissingMoviesSearch:
                    queued += jobs.Enqueue(new Job(
                        "search-missing-movies", "Search missing movies",
                        (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchMissingMoviesAsync(ct))) ? 1 : 0;
                    break;

                case SeasonSearch:
                    if (request.SeriesId is not { } sid || request.SeasonNumber is not { } season || seriesRepo.Find(sid) is not { } show)
                    {
                        return Invalid("seriesId", "Pass the seriesId and seasonNumber of a series in the library.");
                    }

                    queued += jobs.Enqueue(new Job(
                        $"search-season:{sid}:{season}",
                        $"Search {show.Title} season {season}",
                        (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchSeasonAsync(sid, season, ct))) ? 1 : 0;
                    break;

                case EpisodeSearch:
                    var bySeries = (request.EpisodeIds ?? []).Distinct()
                        .Select(id => (Id: id, Location: lookup.Locate(id)))
                        .Where(x => x.Location is not null)
                        .GroupBy(x => x.Location!.Value.SeriesId)
                        .ToList();
                    if (bySeries.Count == 0)
                    {
                        return Invalid("episodeIds", "Pass the ids of one or more episodes.");
                    }

                    foreach (var group in bySeries)
                    {
                        var seriesId = group.Key;
                        var episodeIds = group.Select(x => x.Id).Order().ToList();
                        var title = seriesRepo.Find(seriesId)?.Title ?? "series";
                        queued += jobs.Enqueue(new Job(
                            $"search-episodes:{seriesId}:{string.Join(',', episodeIds)}",
                            episodeIds.Count == 1 ? $"Search {title} episode" : $"Search {episodeIds.Count} episodes of {title}",
                            (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchEpisodesAsync(seriesId, episodeIds, ct))) ? 1 : 0;
                    }

                    break;

                default:
                    return Invalid("name", $"Use one of: {MoviesSearch}, {MissingMoviesSearch}, {SeasonSearch}, {EpisodeSearch}.");
            }

            return Results.Accepted(value: new CommandResponse(name, queued, jobs.Status));
        });

        return app;
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

public sealed record GrabRequest(int IndexerId, string? Guid);

public sealed record GrabResource(string Title, string Indexer, string FileName, string Folder);

public sealed record CommandRequest(string? Name, int[]? MovieIds, int? SeriesId, int? SeasonNumber, int[]? EpisodeIds);

public sealed record CommandResponse(string Name, int Queued, JobStatus Jobs);

public sealed record ActiveIndexersResource(int AutomaticSearch, int InteractiveSearch);

public sealed record IndexerFailureResource(int IndexerId, string Indexer, string Message);

/// <summary>Interactive search result: the rows in ranking order, plus how many indexers answered.</summary>
public sealed record ReleaseSearchResource(int Indexers, IReadOnlyList<IndexerFailureResource> FailedIndexers, IReadOnlyList<ReleaseResource> Releases)
{
    public static ReleaseSearchResource From(ReleaseSearchResult result, DateTimeOffset now) => new(
        result.IndexerCount,
        [.. result.Failures.Select(f => new IndexerFailureResource(f.IndexerId, f.Indexer, f.Message))],
        [.. result.Decisions.Select((d, index) => ReleaseResource.From(d, index + 1, now))]);
}

/// <summary>One search result. <see cref="Rank"/> is the default order (1 = best); series fields are empty for movies.</summary>
public sealed record ReleaseResource(
    string Guid,
    int IndexerId,
    string Indexer,
    string Title,
    long Size,
    int Age,
    double AgeHours,
    DateTimeOffset PublishDate,
    int? Grabs,
    string Quality,
    int QualityId,
    IReadOnlyList<string> Languages,
    string? ReleaseGroup,
    bool Accepted,
    IReadOnlyList<RejectionResource> Rejections,
    string? InfoUrl,
    int Rank,
    int? SeasonNumber,
    IReadOnlyList<int> EpisodeNumbers,
    bool FullSeason,
    IReadOnlyList<int> EpisodeIds)
{
    public static ReleaseResource From(ReleaseDecision d, int rank, DateTimeOffset now)
    {
        var parsed = d.Parsed;
        var age = now - d.Release.PublishDate;
        return new ReleaseResource(
            d.Release.Guid,
            d.Release.IndexerId,
            d.Release.Indexer,
            d.Release.Title,
            d.Release.Size,
            (int)Math.Max(0, Math.Floor(age.TotalDays)),
            Math.Round(Math.Max(0, age.TotalHours), 1),
            d.Release.PublishDate,
            d.Release.Grabs,
            parsed?.Quality.Quality.Name ?? StreamZH.Core.Quality.Quality.Unknown.Name,
            parsed?.Quality.Quality.Id ?? 0,
            [.. (parsed?.Languages ?? []).Where(l => l != Language.Unknown).Select(l => l.Name)],
            parsed?.ReleaseGroup,
            d.Accepted,
            [.. d.Rejections.Select(r => new RejectionResource(r.Code, r.Message))],
            d.Release.InfoUrl,
            rank,
            parsed?.SeasonNumber,
            parsed?.EpisodeNumbers ?? [],
            parsed?.FullSeason ?? false,
            d.EpisodeIds);
    }
}
