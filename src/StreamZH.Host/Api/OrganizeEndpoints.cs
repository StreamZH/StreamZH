// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Mvc;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Host.Auth;
using StreamZH.Library.Import;
using StreamZH.Library.Organize;

namespace StreamZH.Host.Api;

/// <summary>
/// The library mass editor (<c>/api/v1/movie/editor</c>, <c>/api/v1/series/editor</c>, like Radarr's and Sonarr's) and
/// Organize (<c>/api/v1/organize/preview|apply|undo|status</c>). Admins only: they change and move files.
/// </summary>
public static class OrganizeEndpoints
{
    public const int MaxItems = 10_000;

    public static IEndpointRouteBuilder MapOrganizeEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization(AuthSchemes.AdminPolicy);

        var movies = api.MapGroup("/movie/editor").WithTags("Movies");
        movies.MapPut("/", (MovieEditorRequest request, MassEditService service, JobQueue jobs, LibraryRunTracker tracker) =>
            EditAsync(MediaType.Movie, request.MovieIds, request.Changes, service, jobs, tracker));
        movies.MapDelete("/", ([FromBody] MovieDeleteRequest request, MassEditService service) =>
            DeleteAsync(MediaType.Movie, request.MovieIds, request.DeleteFiles, service));

        var series = api.MapGroup("/series/editor").WithTags("Series");
        series.MapPut("/", (SeriesEditorRequest request, MassEditService service, JobQueue jobs, LibraryRunTracker tracker) =>
            EditAsync(MediaType.Series, request.SeriesIds, request.Changes, service, jobs, tracker));
        series.MapDelete("/", ([FromBody] SeriesDeleteRequest request, MassEditService service) =>
            DeleteAsync(MediaType.Series, request.SeriesIds, request.DeleteFiles, service));

        var organize = api.MapGroup("/organize").WithTags("Organize");
        organize.MapPost("/preview", (OrganizeRequest request, OrganizeService service) =>
            Invalid(request.MediaType, request.Ids) ?? Results.Ok(service.Preview(request.MediaType, request.Ids!)));
        organize.MapPost("/apply", (OrganizeRequest request, JobQueue jobs, LibraryRunTracker tracker) =>
        {
            if (Invalid(request.MediaType, request.Ids) is { } invalid)
            {
                return invalid;
            }

            return OrganizeJobs.Organize(jobs, tracker, request.MediaType, [.. request.Ids!.Distinct()])
                ? Results.Accepted(value: new { queued = true })
                : Busy();
        });
        organize.MapPost("/undo", (UndoRequest? request, OrganizeService service, JobQueue jobs, LibraryRunTracker tracker) =>
        {
            var run = service.Undoable();
            if (run is null || (request?.RunId is { } id && id != run.Id))
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This organize can no longer be undone.");
            }

            return OrganizeJobs.Undo(jobs, tracker, run) ? Results.Accepted(value: new { queued = true }) : Busy();
        });
        organize.MapGet("/status", (OrganizeService service, LibraryRunTracker tracker, MediaManagementSettings settings) =>
        {
            var run = service.Undoable();
            return new OrganizeStatusResource(
                tracker.Current,
                tracker.Last,
                run is null ? null : new UndoResource(run.Id, run.MediaType, run.Renamed, run.Finished!.Value, run.Finished.Value + OrganizeService.UndoWindow),
                settings.Current.RecycleBin.Length > 0 ? settings.Current.RecycleBin : null);
        });

        return app;
    }

    private static async Task<IResult> EditAsync(
        MediaType mediaType, IReadOnlyList<int>? ids, MassEditChanges changes, MassEditService service, JobQueue jobs, LibraryRunTracker tracker)
    {
        if (Invalid(mediaType, ids) is { } invalid)
        {
            return invalid;
        }

        if (changes.RootFolderId is not null && changes.MoveFiles && tracker.Current is not null)
        {
            return Busy();
        }

        if (changes.RootFolderId is { } root)
        {
            service.RootFolder(root, mediaType);
        }

        var result = await service.EditAsync(mediaType, ids!, changes);
        var moving = result.ToMove.Count > 0 && OrganizeJobs.Move(jobs, tracker, mediaType, result.ToMove, changes.RootFolderId!.Value);
        if (result.ToMove.Count > 0 && !moving)
        {
            return Busy();
        }

        return Results.Ok(new MassEditResource(result.Updated, moving ? result.ToMove.Count : 0));
    }

    private static async Task<IResult> DeleteAsync(MediaType mediaType, IReadOnlyList<int>? ids, bool deleteFiles, MassEditService service)
    {
        if (Invalid(mediaType, ids) is { } invalid)
        {
            return invalid;
        }

        return Results.Ok(await service.DeleteAsync(mediaType, ids!, deleteFiles));
    }

    private static IResult? Invalid(MediaType mediaType, IReadOnlyList<int>? ids)
    {
        if (mediaType is not (MediaType.Movie or MediaType.Series))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["Choose movies or series."] });
        }

        if (ids is null || ids.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ids"] = ["Select at least one item."] });
        }

        return ids.Count > MaxItems
            ? Results.ValidationProblem(new Dictionary<string, string[]> { ["ids"] = [$"Select at most {MaxItems} items at once."] })
            : null;
    }

    private static IResult Busy() =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Another organize or move is queued or running. Wait until it is done.");
}

/// <summary>Radarr's mass editor body: the movies and the fields to change (null = keep).</summary>
public sealed record MovieEditorRequest(IReadOnlyList<int>? MovieIds, bool? Monitored, int? QualityProfileId, int? RootFolderId, bool? MoveFiles)
{
    public MassEditChanges Changes => new(Monitored, QualityProfileId, RootFolderId, MoveFiles ?? false);
}

public sealed record SeriesEditorRequest(IReadOnlyList<int>? SeriesIds, bool? Monitored, int? QualityProfileId, int? RootFolderId, bool? MoveFiles)
{
    public MassEditChanges Changes => new(Monitored, QualityProfileId, RootFolderId, MoveFiles ?? false);
}

/// <param name="DeleteFiles">Also delete the folders (into the recycle bin of Media Management when one is set).</param>
public sealed record MovieDeleteRequest(IReadOnlyList<int>? MovieIds, bool DeleteFiles);

public sealed record SeriesDeleteRequest(IReadOnlyList<int>? SeriesIds, bool DeleteFiles);

public sealed record MassEditResource(int Updated, int Moving);

public sealed record OrganizeRequest(MediaType MediaType, IReadOnlyList<int>? Ids);

public sealed record UndoRequest(int? RunId);

/// <param name="Until">"Undo last organize" is offered until then.</param>
public sealed record UndoResource(int RunId, MediaType MediaType, int Items, DateTime Finished, DateTime Until);

/// <param name="Running">The queued or running organize, undo or move.</param>
/// <param name="Last">The result of the last one since StreamZH started.</param>
/// <param name="Undo">The last organize, while it can be undone.</param>
/// <param name="RecycleBin">Where deleted files go; null: they are deleted for good.</param>
public sealed record OrganizeStatusResource(LibraryRunProgress? Running, LibraryRunResult? Last, UndoResource? Undo, string? RecycleBin);
