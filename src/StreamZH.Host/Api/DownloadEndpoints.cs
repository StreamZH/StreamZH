// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using StreamZH.Core.Downloads;
using StreamZH.Host.Auth;
using StreamZH.Library.Downloads;

namespace StreamZH.Host.Api;

/// <summary>The built-in download queue: list, history, details, NZB upload and queue commands.</summary>
public static class DownloadEndpoints
{
    public const int MaxHistoryPageSize = 200;
    public const long MaxNzbBytes = 20L * 1024 * 1024;

    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/download").RequireAuthorization().WithTags("Downloads");

        api.MapGet("/", (DownloadEngine engine) =>
        {
            var status = engine.Status();
            return new DownloadQueueResource(
                [.. engine.Active().Select(i => DownloadItemResource.From(i))],
                status.SpeedBps,
                status.Paused,
                status.FreeSpaceBytes,
                status.SpeedLimitKBps,
                status.StatusMessage);
        });

        api.MapGet("/history", (int? page, int? pageSize, DownloadEngine engine) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, MaxHistoryPageSize);
            var (items, total) = engine.History(p, size);
            return new DownloadHistoryPage(p, size, total, [.. items.Select(i => DownloadItemResource.From(i))]);
        });

        // For the category pickers (Add NZB, Search page): name, whether it is a default one, and its kind.
        api.MapGet("/categories", (DownloadCategoryService categories) =>
            categories.All().Select(c => new DownloadCategoryChoice(c.Name, c.IsDefault, Camel(DownloadCategoryService.KindOf(c.Name).ToString()))).ToList());

        api.MapGet("/{id:int}", (int id, DownloadEngine engine) =>
            engine.Find(id) is { } item && engine.Files(id) is { } files
                ? Results.Ok(DownloadItemResource.From(item, files))
                : NotFound(id));

        api.MapPost("/", async (HttpRequest http, DownloadEngine engine, DownloadCategoryService categories, CancellationToken ct) =>
        {
            if (!http.HasFormContentType)
            {
                return Invalid("file", "Upload the .nzb file as multipart/form-data field \"file\".");
            }

            IFormCollection form;
            try
            {
                form = await http.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = MaxNzbBytes }, ct);
            }
            catch (InvalidDataException)
            {
                return Invalid("file", "The NZB file is larger than 20 MB.");
            }

            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return Invalid("file", "Choose an .nzb file.");
            }

            if (file.Length > MaxNzbBytes)
            {
                return Invalid("file", "The NZB file is larger than 20 MB.");
            }

            var category = DownloadCategory.Other;
            string? categoryName = null;
            if (form.TryGetValue("category", out var categoryValue) && !string.IsNullOrWhiteSpace(categoryValue))
            {
                if (ParseCategory(categoryValue.ToString()) is { } parsed)
                {
                    category = parsed;
                }
                else if (categories.Find(categoryValue.ToString()) is { } named)
                {
                    categoryName = named.Name;
                }
                else
                {
                    return Invalid("category", "Use movie, series, other or the name of a download category.");
                }
            }

            var rawTitle = form.TryGetValue("title", out var titleValue) && !string.IsNullOrWhiteSpace(titleValue)
                ? titleValue.ToString()
                : Path.GetFileNameWithoutExtension(file.FileName);
            var (title, password) = NzbPassword.Split(rawTitle.Trim());

            byte[] content;
            await using (var stream = file.OpenReadStream())
            {
                using var buffer = new MemoryStream((int)file.Length);
                await stream.CopyToAsync(buffer, ct);
                content = buffer.ToArray();
            }

            var item = await engine.AddAsync(new DownloadRequest(content, title, category, MediaLink.None, Password: password, CategoryName: categoryName), ct);
            return Results.Created($"/api/v1/download/{item.Id}", DownloadItemResource.From(item));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapPost("/{id:int}/pause", async (int id, DownloadEngine engine) => Result(id, await engine.PauseAsync(id), "Only queued or downloading items can be paused."))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapPost("/{id:int}/resume", async (int id, DownloadEngine engine) => Result(id, await engine.ResumeAsync(id), "This download is not in the queue any more."))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapPost("/{id:int}/retry", async (int id, DownloadEngine engine) => Result(id, await engine.RetryAsync(id), "Only failed downloads can be retried."))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapPut("/{id:int}", async (int id, UpdateDownloadRequest request, DownloadEngine engine, DownloadCategoryService categories) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (request.Priority is < DownloadEngine.MinPriority or > DownloadEngine.MaxPriority)
            {
                errors["priority"] = ["Use -1 (low), 0 (normal), 1 (high) or 2 (force)."];
            }

            var category = ParseCategory(request.Category);
            var categoryName = category is null && request.Category is not null ? categories.Find(request.Category)?.Name : null;
            if (request.Category is not null && category is null && categoryName is null)
            {
                errors["category"] = ["Use movie, series, other or the name of a download category."];
            }

            if (request.Position is < 0)
            {
                errors["position"] = ["Use 0 (top of the queue) or more."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await engine.UpdateAsync(id, request.Priority, category, request.Position, categoryName, request.Password);
            return item is null ? NotFound(id) : Results.Ok(DownloadItemResource.From(item));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapDelete("/{id:int}", async (int id, bool? deleteFiles, DownloadEngine engine) =>
            Result(id, await engine.DeleteAsync(id, deleteFiles ?? false), string.Empty))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapPost("/pause-all", async (DownloadEngine engine) =>
        {
            await engine.PauseAllAsync();
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapPost("/resume-all", async (DownloadEngine engine) =>
        {
            await engine.ResumeAllAsync();
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }

    public static string Camel(string value) => JsonNamingPolicy.CamelCase.ConvertName(value);

    public static DownloadCategory? ParseCategory(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" => DownloadCategory.Movie,
        "series" or "tv" => DownloadCategory.Series,
        "other" => DownloadCategory.Other,
        _ => null,
    };

    private static IResult Result(int id, DownloadActionResult result, string conflict) => result switch
    {
        DownloadActionResult.Ok => Results.NoContent(),
        DownloadActionResult.NotFound => NotFound(id),
        _ => Results.Problem(
            statusCode: StatusCodes.Status409Conflict, title: conflict, detail: conflict,
            extensions: new Dictionary<string, object?> { ["code"] = "conflict" }),
    };

    private static IResult NotFound(int id) => Results.Problem(
        statusCode: StatusCodes.Status404NotFound, title: $"Download {id} not found.", detail: $"Download {id} not found.",
        extensions: new Dictionary<string, object?> { ["code"] = "not-found" });

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>Whole queue: items in download order, total speed, pause state, free space in the incomplete folder.</summary>
public sealed record DownloadQueueResource(
    IReadOnlyList<DownloadItemResource> Items, long SpeedBps, bool Paused, long? FreeSpaceBytes, long SpeedLimitKBps, string? StatusMessage);

public sealed record DownloadHistoryPage(int Page, int PageSize, int TotalRecords, IReadOnlyList<DownloadItemResource> Records);

/// <summary>Kind is camelCase NzbFileKind: other, par2Index, par2Recovery, archive, nfo, sfv. Wanted = false: par2 volume held back.</summary>
public sealed record DownloadFileResource(string Name, long Size, long Downloaded, string Kind, int MissingArticles, bool Wanted);

/// <param name="Priority">-1 low, 0 normal, 1 high, 2 force. Null keeps it.</param>
/// <param name="Category">movie, series or other. Null keeps it.</param>
/// <param name="Position">New place in the queue (0 = top; the order applies within a priority). Null keeps it.</param>
/// <param name="Password">Archive password ("" removes it), e.g. for a download paused because it is password protected. Null keeps it.</param>
public sealed record UpdateDownloadRequest(int? Priority, string? Category, int? Position, string? Password = null);

/// <summary>A download category for pickers. <see cref="Kind"/>: movie, series or other.</summary>
public sealed record DownloadCategoryChoice(string Name, bool IsDefault, string Kind);

/// <summary>A download; status, category and file kinds are camelCase strings, mediaType like elsewhere ("Movie", "Series"). <see cref="Files"/> only in the detail response.</summary>
public sealed record DownloadItemResource(
    int Id,
    string Title,
    string Category,
    string Status,
    int Priority,
    long TotalBytes,
    long DownloadedBytes,
    double Progress,
    long SpeedBps,
    double? EtaSeconds,
    double? StepProgress,
    string? StatusMessage,
    int MissingArticles,
    string? Error,
    string? Indexer,
    string? MediaType,
    int? MovieId,
    int? SeriesId,
    IReadOnlyList<int> EpisodeIds,
    string? OutputPath,
    DateTimeOffset Added,
    DateTimeOffset? Completed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<DownloadFileResource>? Files,
    string CategoryName,
    bool Duplicate)
{
    public static DownloadItemResource From(DownloadItem item, IReadOnlyList<DownloadFileInfo>? files = null) => new(
        item.Id,
        item.Title,
        DownloadEndpoints.Camel(item.Category.ToString()),
        DownloadEndpoints.Camel(item.Status.ToString()),
        item.Priority,
        item.TotalBytes,
        item.DownloadedBytes,
        item.Progress,
        item.SpeedBps,
        item.Eta is { } eta ? Math.Round(eta.TotalSeconds) : null,
        item.StepProgress,
        item.StatusMessage,
        item.MissingArticles,
        item.Error,
        item.Indexer,
        item.Link.MediaType?.ToString(),
        item.Link.MovieId,
        item.Link.SeriesId,
        item.Link.EpisodeIds,
        item.OutputPath,
        item.Added,
        item.Completed,
        files?.Select(f => new DownloadFileResource(f.Name, f.Size, f.Downloaded, DownloadEndpoints.Camel(f.Kind.ToString()), f.MissingArticles, f.Wanted)).ToList(),
        item.CategoryName ?? DownloadCategoryService.DefaultName(item.Category),
        item.Duplicate);
}
