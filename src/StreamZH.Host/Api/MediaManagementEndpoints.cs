// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Downloads;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library;
using StreamZH.Library.Import;

namespace StreamZH.Host.Api;

/// <summary>
/// Settings → Media Management (naming, importing, file management, permissions, download handling) with a live naming
/// preview; the import of finished downloads (state, retry, manual import); the blocklist; renaming library files.
/// </summary>
public static class MediaManagementEndpoints
{
    public const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapMediaManagementEndpoints(this IEndpointRouteBuilder app)
    {
        // Settings
        var settings = app.MapGroup("/api/v1/settings/media-management").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);
        settings.MapGet("/", (MediaManagementSettings media) => new MediaManagementResource(media.Current));
        settings.MapPut("/", (MediaManagementValues request, MediaManagementSettings media, ILogger<MediaManagementSettings> logger) =>
        {
            var saved = media.Update(request);
            logger.LogInformation(
                "Media Management settings saved: rename movies {Movies}, rename episodes {Episodes}, import mode {Mode}, completed download handling {Completed}",
                saved.RenameMovies, saved.RenameEpisodes, saved.ImportMode, saved.CompletedDownloadHandling);
            return new MediaManagementResource(saved);
        });
        settings.MapPost("/preview", (MediaManagementValues request) => NamingPreview.For(request));

        // Import of finished downloads
        var import = app.MapGroup("/api/v1/import").WithTags("Import").RequireAuthorization();
        import.MapGet("/downloads", (string? ids, DownloadImportRepository imports) =>
        {
            var list = (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => int.TryParse(v, out var id) ? id : 0)
                .Where(id => id > 0)
                .Take(500);
            return imports.ForDownloads(list).Select(ImportStateResource.From);
        });
        import.MapGet("/pending", (DownloadImportRepository imports, IDownloadQueue queue) =>
            imports.WithState(ImportState.Queued, ImportState.Importing, ImportState.Waiting, ImportState.Failed)
                .Where(i => !(i.State == ImportState.Failed && i.Dismissed))
                .Select(i => (Import: i, Download: queue.Find(i.DownloadId)))
                .Where(x => x.Download is { Status: DownloadStatus.Completed })
                .OrderByDescending(x => x.Import.Date)
                .Select(x => new PendingImportResource(DownloadItemResource.From(x.Download!), ImportStateResource.From(x.Import))));
        import.MapPost("/downloads/{id:int}", (int id, IDownloadQueue queue, JobQueue jobs, DownloadImportService service, DownloadImportRepository imports) =>
        {
            // Import now, or again after a failure (an earlier manual choice is used again).
            var item = Completed(queue, id);
            if (item.Link.MediaType is null && imports.Find(id)?.Manual is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["This download belongs to no movie or series. Use Import… and choose one."] });
            }

            ImportJobs.Enqueue(jobs, service, item);
            return Results.Accepted(value: new { queued = true });
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
        import.MapGet("/downloads/{id:int}/files", (int id, int? seriesId, DownloadImportService service) => service.Candidates(id, seriesId))
            .RequireAuthorization(AuthSchemes.AdminPolicy);
        import.MapPost("/downloads/{id:int}/manual", (
            int id, ManualImportRequest request, IDownloadQueue queue, JobQueue jobs, DownloadImportService service, MovieRepository movies, SeriesRepository series) =>
        {
            var item = Completed(queue, id);
            var errors = new Dictionary<string, string[]>();
            if (request.MediaType == MediaType.Movie && (request.MovieId is not { } movieId || movies.Find(movieId) is null))
            {
                errors["movieId"] = ["Choose a movie from the library."];
            }

            if (request.MediaType == MediaType.Series)
            {
                if (request.SeriesId is not { } seriesId || series.Find(seriesId) is null)
                {
                    errors["seriesId"] = ["Choose a series from the library."];
                }
                else if (request.Files.Count == 0 || request.Files.All(f => f.EpisodeIds.Count == 0))
                {
                    errors["files"] = ["Choose the episodes of at least one file."];
                }
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            ImportJobs.Enqueue(jobs, service, item, request);
            return Results.Accepted(value: new { queued = true });
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
        import.MapPost("/downloads/{id:int}/dismiss", (int id, DownloadImportRepository imports) =>
        {
            imports.Dismiss(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // Blocklist
        var blocklist = app.MapGroup("/api/v1/blocklist").WithTags("Blocklist").RequireAuthorization();
        // Filters: search (words in the release title), mediaType (movie, series).
        blocklist.MapGet("/", (int? page, int? pageSize, string? search, string? mediaType, BlocklistRepository repo) =>
        {
            var media = HistoryEndpoints.ParseMediaType(mediaType);
            if (media is null && !string.IsNullOrWhiteSpace(mediaType) && !string.Equals(mediaType.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["Use movie or series."] });
            }

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, MaxPageSize);
            var (records, total) = repo.Page(p, size, search, media);
            return Results.Ok(new BlocklistPage(p, size, total, [.. records.Select(BlocklistResource.From)]));
        });
        blocklist.MapDelete("/{id:int}", (int id, BlocklistRepository repo) =>
            repo.Delete(id) ? Results.NoContent() : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not on the blocklist."))
            .RequireAuthorization(AuthSchemes.AdminPolicy);
        blocklist.MapDelete("/", (BlocklistRepository repo) => Results.Ok(new { removed = repo.Clear() }))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        // Rename library files
        var rename = app.MapGroup("/api/v1/rename").WithTags("Rename").RequireAuthorization(AuthSchemes.AdminPolicy);
        rename.MapGet("/movie/{id:int}", (int id, RenameService service) => service.PreviewMovie(id));
        rename.MapPost("/movie/{id:int}", async (int id, RenameService service, CancellationToken ct) =>
            new { renamed = await service.RenameMovieAsync(id, ct) });
        rename.MapGet("/series/{id:int}", (int id, int? season, RenameService service) => service.PreviewSeries(id, season));
        rename.MapPost("/series/{id:int}", async (int id, RenameSeriesRequest? request, RenameService service, CancellationToken ct) =>
            new { renamed = await service.RenameSeriesAsync(id, request?.Season, request?.Files, ct) });

        return app;
    }

    private static DownloadItem Completed(IDownloadQueue queue, int id)
    {
        var item = queue.Find(id) ?? throw new LibraryException($"Download {id} not found.", LibraryError.NotFound);
        return item.Status == DownloadStatus.Completed
            ? item
            : throw new LibraryException("Only completed downloads can be imported.", LibraryError.Conflict);
    }
}

/// <summary>The settings plus what the page needs to know about the system (permissions only apply outside Windows).</summary>
public sealed record MediaManagementResource : MediaManagementValues
{
    public MediaManagementResource(MediaManagementValues values)
        : base(values)
    {
    }

    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
}

/// <param name="Season">Only this season; null = all.</param>
/// <param name="Files">Only these files (existing relative paths from the preview); null = all.</param>
public sealed record RenameSeriesRequest(int? Season, IReadOnlyList<string>? Files);

public sealed record ImportStateResource(
    int DownloadId,
    string State,
    string? Message,
    DateTime Date,
    string Title,
    string? MediaType,
    int? MovieId,
    int? SeriesId,
    IReadOnlyList<int> EpisodeIds,
    IReadOnlyList<string> Files,
    bool IsUpgrade,
    bool Manual,
    bool Dismissed)
{
    public static ImportStateResource From(DownloadImport i) => new(
        i.DownloadId, JsonNamingPolicy.CamelCase.ConvertName(i.State.ToString()), i.Message, i.Date, i.Title, i.MediaType?.ToString(), i.MovieId, i.SeriesId,
        i.EpisodeIds, i.Files, i.IsUpgrade, i.Manual is not null, i.Dismissed);
}

public sealed record PendingImportResource(DownloadItemResource Download, ImportStateResource Import);

public sealed record BlocklistPage(int Page, int PageSize, int TotalRecords, IReadOnlyList<BlocklistResource> Records);

public sealed record BlocklistResource(
    int Id, DateTime Date, string? MediaType, int? MovieId, int? SeriesId, IReadOnlyList<int> EpisodeIds, string SourceTitle, string Quality,
    IReadOnlyList<string> Languages, string? Indexer, long Size, string? Message)
{
    public static BlocklistResource From(BlocklistItem b) => new(
        b.Id, b.Date, b.MediaType?.ToString(), b.MovieId, b.SeriesId, b.EpisodeIds, b.SourceTitle, Core.Quality.Quality.FindById(b.QualityId).Name,
        [.. b.LanguageIds.Select(Language.FindById).Where(l => l.Id > 0).Select(l => l.Name)], b.Indexer, b.Size, b.Message);
}

/// <summary>Example names for the settings page, built from sample items with the formats being edited.</summary>
public sealed record NamingPreview(
    string MovieFile,
    string MovieFolder,
    string StandardEpisode,
    string MultiEpisode,
    string DailyEpisode,
    string AnimeEpisode,
    string SeriesFolder,
    string? SeasonFolder,
    string? SpecialsFolder,
    IReadOnlyDictionary<string, string[]> Errors)
{
    private static readonly Movie SampleMovie = new()
    {
        Title = "The Movie Title: Part Two", OriginalTitle = "Le Titre du Film", Year = 2010, ImdbId = "tt0066921", TmdbId = 345691,
    };

    private static readonly Series SampleSeries = new()
    {
        Title = "The Series Title's!", OriginalTitle = "The Series Title's!", Year = 2010, ImdbId = "tt12345", TmdbId = 9999, TvdbId = 12345,
    };

    private static readonly Episode First = new() { SeasonNumber = 1, EpisodeNumber = 1, Title = "Episode Title (1)", AirDate = "2013-10-30" };
    private static readonly Episode Second = new() { SeasonNumber = 1, EpisodeNumber = 2, Title = "Episode Title (2)", AirDate = "2013-11-06" };
    private static readonly Episode Third = new() { SeasonNumber = 1, EpisodeNumber = 3, Title = "Episode Title (3)", AirDate = "2013-11-13" };

    private static readonly NamingFileInfo MovieFileInfo = new()
    {
        Quality = new QualityModel(Core.Quality.Quality.Bluray1080p, new Revision(2)),
        Languages = [Language.German, Language.English],
        ReleaseGroup = "RlsGrp",
        Edition = "IMAX",
        VideoCodec = "x264",
        AudioCodec = "DTS",
        Hdr = HdrFormat.None,
        SceneName = "The.Movie.Title.Part.Two.2010.IMAX.German.DL.1080p.BluRay.PROPER.x264-RlsGrp",
        OriginalFileName = "the.movie.title.part.two.2010.1080p-rlsgrp",
    };

    private static readonly NamingFileInfo EpisodeFileInfo = MovieFileInfo with
    {
        Quality = new QualityModel(Core.Quality.Quality.WebDl1080p),
        Edition = null,
        SceneName = "The.Series.Titles.S01E01.German.DL.1080p.WEB.h264-RlsGrp",
        OriginalFileName = "the.series.titles.s01e01.1080p-rlsgrp",
    };

    public static NamingPreview For(MediaManagementValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var errors = MediaManagementSettings.Validate(values)
            .Where(e => e.Key.EndsWith("Format", StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => e.Value);
        string Safe(string field, Func<string> build) => errors.ContainsKey(field) ? string.Empty : build();

        var builder = new FileNameBuilder(values);
        return new NamingPreview(
            Safe("standardMovieFormat", () => builder.MovieFileName(SampleMovie, MovieFileInfo) + ".mkv"),
            Safe("movieFolderFormat", () => builder.MovieFolderName(SampleMovie)),
            Safe("standardEpisodeFormat", () => builder.EpisodeFileName(SampleSeries, [First], EpisodeFileInfo) + ".mkv"),
            Safe("standardEpisodeFormat", () => builder.EpisodeFileName(SampleSeries, [First, Second, Third], EpisodeFileInfo) + ".mkv"),
            Safe("dailyEpisodeFormat", () => builder.EpisodeFileName(SampleSeries, [First], EpisodeFileInfo, EpisodeFormat.Daily) + ".mkv"),
            Safe("animeEpisodeFormat", () => builder.EpisodeFileName(SampleSeries, [First], EpisodeFileInfo, EpisodeFormat.Anime) + ".mkv"),
            Safe("seriesFolderFormat", () => builder.SeriesFolderName(SampleSeries)),
            errors.ContainsKey("seasonFolderFormat") ? null : builder.SeasonFolderName(SampleSeries, 1),
            errors.ContainsKey("specialsFolderFormat") ? null : builder.SeasonFolderName(SampleSeries, 0),
            errors);
    }
}
