// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Host.Api;

public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        // Movies
        var movies = api.MapGroup("/movies").WithTags("Movies");
        movies.MapGet("/", (MovieRepository repo) => repo.All().Select(MovieResource.From));
        movies.MapGet("/{id:int}", (int id, MovieRepository repo) =>
            repo.Find(id) is { } m ? Results.Ok(MovieResource.From(m)) : Results.NotFound());
        movies.MapPost("/", async (AddRequest request, LibraryService library, CancellationToken ct) =>
        {
            var movie = await library.AddMovieAsync(request.TmdbId, request.RootFolderId, request.Monitored, cancellationToken: ct);
            return Results.Created($"/api/v1/movies/{movie.Id}", MovieResource.From(movie));
        });
        movies.MapPut("/{id:int}/monitor", async (int id, MonitorRequest request, LibraryService library) =>
        {
            await library.SetMovieMonitoredAsync(id, request.Monitored);
            return Results.NoContent();
        });
        movies.MapPost("/{id:int}/refresh", (int id, MovieRepository repo, JobQueue jobs) =>
            repo.Find(id) is { } m ? Queued(jobs, RefreshMovie(m.Id, m.Title)) : Results.NotFound());
        movies.MapDelete("/{id:int}", async (int id, LibraryService library) =>
        {
            await library.DeleteMovieAsync(id);
            return Results.NoContent();
        });

        // Series
        var series = api.MapGroup("/series").WithTags("Series");
        series.MapGet("/", (SeriesRepository repo) =>
        {
            var counts = repo.Counts();
            return repo.All().Select(s => SeriesResource.From(s, counts.GetValueOrDefault(s.Id)));
        });
        series.MapGet("/{id:int}", (int id, SeriesRepository repo) =>
        {
            if (repo.Find(id) is not { } s)
            {
                return Results.NotFound();
            }

            var episodes = repo.Episodes(id);
            var seasons = episodes.GroupBy(e => e.SeasonNumber).OrderByDescending(g => g.Key)
                .Select(g => new SeasonResource(g.Key, g.Count(), g.Count(e => e.File is not null), g.All(e => e.Monitored)))
                .ToList();
            return Results.Ok(new SeriesDetailResource(
                SeriesResource.From(s, repo.Counts().GetValueOrDefault(id)), seasons, [.. episodes.Select(EpisodeResource.From)]));
        });
        series.MapPost("/", async (AddRequest request, LibraryService library, JobQueue jobs, CancellationToken ct) =>
        {
            var item = await library.AddSeriesAsync(request.TmdbId, request.RootFolderId, request.Monitored, cancellationToken: ct);
            jobs.Enqueue(RefreshSeries(item.Id, item.Title));
            return Results.Created($"/api/v1/series/{item.Id}", SeriesResource.From(item, null));
        });
        series.MapPut("/{id:int}/monitor", async (int id, MonitorRequest request, LibraryService library) =>
        {
            await library.SetSeriesMonitoredAsync(id, request.Monitored);
            return Results.NoContent();
        });
        series.MapPut("/{id:int}/seasons/{season:int}/monitor", async (int id, int season, MonitorRequest request, LibraryService library) =>
        {
            await library.SetSeasonMonitoredAsync(id, season, request.Monitored);
            return Results.NoContent();
        });
        series.MapPost("/{id:int}/refresh", (int id, SeriesRepository repo, JobQueue jobs) =>
            repo.Find(id) is { } s ? Queued(jobs, RefreshSeries(s.Id, s.Title)) : Results.NotFound());
        series.MapDelete("/{id:int}", async (int id, LibraryService library) =>
        {
            await library.DeleteSeriesAsync(id);
            return Results.NoContent();
        });

        // TMDB search
        var lookup = api.MapGroup("/lookup").WithTags("Lookup");
        lookup.MapGet("/movies", async (string term, int? year, ITmdbClient tmdb, MovieRepository repo, CancellationToken ct) =>
        {
            var inLibrary = repo.TmdbIdToId();
            var results = await tmdb.SearchMoviesAsync(term.Trim(), year, ct);
            return results.Select(r => ToLookup(r, inLibrary));
        });
        lookup.MapGet("/series", async (string term, int? year, ITmdbClient tmdb, SeriesRepository repo, CancellationToken ct) =>
        {
            var inLibrary = repo.TmdbIdToId();
            var results = await tmdb.SearchSeriesAsync(term.Trim(), year, ct);
            return results.Select(r => ToLookup(r, inLibrary));
        });

        // Root folders (configuration)
        var roots = api.MapGroup("/rootfolders").WithTags("Root folders");
        roots.MapGet("/", (RootFolderService service) => service.All());
        roots.MapPost("/", (AddRootFolderRequest request, RootFolderService service) =>
        {
            var created = service.Add(request.Path, request.MediaType);
            return Results.Created($"/api/v1/rootfolders/{created.Id}", created);
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
        roots.MapDelete("/{id:int}", (int id, RootFolderService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // Import existing folders
        var import = api.MapGroup("/library-import").WithTags("Library import").RequireAuthorization(AuthSchemes.AdminPolicy);
        import.MapGet("/{rootFolderId:int}", (int rootFolderId, LibraryImportService service) => service.Candidates(rootFolderId));
        import.MapPost("/", (LibraryImportRequest request, LibraryImportService service, RootFolderRepository rootFolders, JobQueue jobs) =>
        {
            var root = rootFolders.Find(request.RootFolderId) ?? throw new LibraryException("Root folder not found.", LibraryError.NotFound);
            var prepared = service.Prepare(request.RootFolderId, request.Items);

            foreach (var (item, folder) in prepared)
            {
                jobs.Enqueue(root.MediaType == MediaType.Movie
                    ? new Job($"import:{folder}", $"Import {item.FolderName}", async (sp, ct) =>
                    {
                        var library = sp.GetRequiredService<LibraryService>();
                        await library.AddMovieAsync(item.TmdbId, root.Id, request.Monitored, folder, ct);
                    })
                    : new Job($"import:{folder}", $"Import {item.FolderName}", async (sp, ct) =>
                    {
                        var library = sp.GetRequiredService<LibraryService>();
                        var added = await library.AddSeriesAsync(item.TmdbId, root.Id, request.Monitored, folder, ct);
                        await library.RefreshSeriesAsync(added.Id, ct);
                    }));
            }

            return Results.Accepted(value: new { queued = prepared.Count });
        });

        // Library-wide
        var library = api.MapGroup("/library").WithTags("Library");
        library.MapGet("/stats", (MovieRepository movieRepo, SeriesRepository seriesRepo) =>
        {
            var allMovies = movieRepo.All();
            var counts = seriesRepo.Counts().Values.ToList();
            return new LibraryStats(
                allMovies.Count,
                allMovies.Count(m => m.File is not null),
                seriesRepo.All().Count,
                counts.Sum(c => c.EpisodeCount),
                counts.Sum(c => c.FileCount),
                allMovies.Sum(m => m.File?.Size ?? 0) + counts.Sum(c => c.SizeOnDisk));
        });
        library.MapPost("/refresh-all", (JobQueue jobs, MovieRepository movieRepo, SeriesRepository seriesRepo) =>
        {
            var queued = EnqueueRefreshAll(jobs, movieRepo, seriesRepo);
            return Results.Accepted(value: new { queued });
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        library.MapPost("/rescan", (JobQueue jobs) =>
            Queued(jobs, new Job("rescan", "Rescan library folders", (sp, ct) => sp.GetRequiredService<LibraryService>().RescanAllAsync(ct))))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapGet("/system/jobs", (JobQueue jobs) => jobs.Status).WithTags("System");

        return app;
    }

    /// <summary>Queues a metadata refresh for every movie and series, e.g. after the language changed.</summary>
    public static int EnqueueRefreshAll(JobQueue jobs, MovieRepository movies, SeriesRepository series)
    {
        var count = 0;
        foreach (var m in movies.All())
        {
            count += jobs.Enqueue(RefreshMovie(m.Id, m.Title)) ? 1 : 0;
        }

        foreach (var s in series.All())
        {
            count += jobs.Enqueue(RefreshSeries(s.Id, s.Title)) ? 1 : 0;
        }

        return count;
    }

    private static Job RefreshMovie(int id, string title) =>
        new($"refresh-movie:{id}", $"Refresh {title}", (sp, ct) => sp.GetRequiredService<LibraryService>().RefreshMovieAsync(id, ct));

    private static Job RefreshSeries(int id, string title) =>
        new($"refresh-series:{id}", $"Refresh {title}", (sp, ct) => sp.GetRequiredService<LibraryService>().RefreshSeriesAsync(id, ct));

    private static IResult Queued(JobQueue jobs, Job job)
    {
        jobs.Enqueue(job);
        return Results.Accepted(value: jobs.Status);
    }

    private static LookupResource ToLookup(TmdbSearchResult r, IReadOnlyDictionary<int, int> inLibrary) =>
        new(r.TmdbId, r.Title, r.OriginalTitle, r.Year, r.Overview, r.PosterPath, inLibrary.TryGetValue(r.TmdbId, out var id) ? id : null);
}