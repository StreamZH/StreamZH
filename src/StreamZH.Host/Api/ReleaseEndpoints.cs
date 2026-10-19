// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Mvc;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Downloads;
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

        // 0.11: releases waiting for the protocol delay (Settings → Indexers), like Radarr's "Pending" queue items.
        var pending = app.MapGroup("/api/v1/release/pending").WithTags("Release").RequireAuthorization();
        pending.MapGet("/", (PendingReleaseService store, IndexerOptions options) =>
        {
            var values = options.Values;
            return store.All().Select(p => PendingReleaseResource.From(p, values.UsenetDelay, values.TorrentDelay));
        });
        pending.MapDelete("/{id}", (string id, PendingReleaseService store) =>
            store.Remove(id) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

/// <param name="Category">Search page only: the download category (built-in engine); null = "other".</param>
public sealed record GrabRequest(int IndexerId, string? Guid, string? Category = null);

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

/// <summary>A custom format a release matches (id and name only).</summary>
public sealed record CustomFormatRefResource(int Id, string Name)
{
    public static IReadOnlyList<CustomFormatRefResource> From(IEnumerable<CustomFormat> formats) =>
        [.. formats.Select(f => new CustomFormatRefResource(f.Id, f.Name))];
}

/// <summary>
/// One search result. <see cref="Rank"/> is the default order (1 = best); series fields are empty for movies.
/// <see cref="PreferredScore"/> is the summed score of the release profiles' preferred terms, <see cref="CustomFormatScore"/>
/// the summed score of the matching <see cref="CustomFormats"/> in the item's quality profile.
/// </summary>
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
    IReadOnlyList<int> EpisodeIds,
    int PreferredScore)
{
    public IReadOnlyList<CustomFormatRefResource> CustomFormats { get; init; } = [];

    public int CustomFormatScore { get; init; }

    /// <summary>0.11: "usenet" or "torrent".</summary>
    public DownloadProtocol Protocol { get; init; } = DownloadProtocol.Usenet;

    /// <summary>Torrent only: seeders and peers (seeders + leechers) as the indexer reports them; null when unknown.</summary>
    public int? Seeders { get; init; }

    public int? Peers { get; init; }

    /// <summary>Radarr's indexer flag bits and their names ("freeleech", "halfleech", "doubleUpload", "internal", "scene", …).</summary>
    public int IndexerFlags { get; init; }

    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>Torrent only: 0 = freeleech, 1 = normal; null when the indexer does not say.</summary>
    public double? DownloadVolumeFactor { get; init; }

    public double? UploadVolumeFactor { get; init; }

    /// <summary>Torrent only: the tracker's minimum ratio and seed time (seconds), when the feed names them.</summary>
    public double? MinimumRatio { get; init; }

    public long? MinimumSeedTime { get; init; }

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
            d.EpisodeIds,
            d.Decision.PreferredScore)
        {
            CustomFormats = CustomFormatRefResource.From(d.Decision.CustomFormats),
            CustomFormatScore = d.Decision.CustomFormatScore,
            Protocol = d.Release.Protocol,
            Seeders = d.Release.Seeders,
            Peers = d.Release.Peers,
            IndexerFlags = d.Release.IndexerFlags,
            Flags = IndexerFlagBits.ToNames(d.Release.IndexerFlags),
            DownloadVolumeFactor = d.Release.DownloadVolumeFactor,
            UploadVolumeFactor = d.Release.UploadVolumeFactor,
            MinimumRatio = d.Release.MinimumRatio,
            MinimumSeedTime = d.Release.MinimumSeedTimeSeconds,
        };
    }
}

/// <summary>
/// A release waiting for its protocol delay. <see cref="ReleaseAfter"/> is when the delay has passed (publish date +
/// delay); the next RSS sync or "Process pending releases" run after that grabs the best waiting release.
/// </summary>
public sealed record PendingReleaseResource(
    string Id,
    StreamZH.Core.Media.MediaType MediaType,
    int? MovieId,
    int? SeriesId,
    IReadOnlyList<int> EpisodeIds,
    string Subject,
    string Title,
    int IndexerId,
    string Indexer,
    DownloadProtocol Protocol,
    long Size,
    int? Seeders,
    int? Peers,
    IReadOnlyList<string> Flags,
    DateTimeOffset PublishDate,
    DateTimeOffset Added,
    DateTimeOffset? ReleaseAfter,
    string Reason)
{
    public static PendingReleaseResource From(PendingRelease p, int usenetDelay, int torrentDelay)
    {
        var release = p.Release;
        var delay = release.Protocol == DownloadProtocol.Torrent ? torrentDelay : usenetDelay;
        return new(
            p.Id, p.MediaType, p.MovieId, p.SeriesId, p.EpisodeIds, p.Subject, release.Title, release.IndexerId, release.Indexer, release.Protocol,
            release.Size, release.Seeders, release.Peers, IndexerFlagBits.ToNames(release.IndexerFlags), release.PublishDate, p.Added,
            release.PublishDate == default ? null : release.PublishDate.AddMinutes(delay), p.Reason);
    }
}
