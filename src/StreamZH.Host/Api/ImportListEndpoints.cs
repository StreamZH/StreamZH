// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Jobs;
using StreamZH.Library.ImportLists;
using StreamZH.Library.Notifications;

namespace StreamZH.Host.Api;

/// <summary>
/// Settings → Import lists: the lists (catalog, add/edit/delete, test, preview, sync now, add one item), the options
/// (<c>/settings/importlists</c>) and the exclusions. Secrets come back as "********", which keeps the stored value.
/// </summary>
public static class ImportListEndpoints
{
    /// <summary>The import list services and the scheduled sync (one line in Program.cs).</summary>
    public static IServiceCollection AddImportLists(this IServiceCollection services) => services.AddImportListServices();

    public static IEndpointRouteBuilder MapImportListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/importlist").RequireAuthorization(AuthSchemes.AdminPolicy).WithTags("Import lists");

        group.MapGet("/", (ImportListService service) => service.All().Select(ImportListResource.From));

        group.MapGet("/schema", () => new ImportListSchema(
            ImportListCatalog.All, ImportListMonitor.MovieOptions, ImportListMonitor.SeriesOptions, ImportListAvailability.Options));

        group.MapGet("/{id:int}", (int id, ImportListService service) =>
            service.Find(id) is { } list ? Results.Ok(ImportListResource.From(list)) : Results.NotFound());

        group.MapPost("/", (ImportListRequest request, ImportListService service) =>
        {
            var created = service.Create(request.ToInput());
            return Results.Created($"/api/v1/importlist/{created.Id}", ImportListResource.From(created));
        });

        group.MapPut("/{id:int}", (int id, ImportListRequest request, ImportListService service) =>
            ImportListResource.From(service.Update(id, request.ToInput())));

        group.MapDelete("/{id:int}", (int id, ImportListService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        });

        // Nothing is saved. With the id of a saved list, masked secrets use the stored ones.
        group.MapPost("/test", async (ImportListRequest request, ImportListService service, ImportListSyncService sync, CancellationToken ct) =>
        {
            var draft = service.Draft(request.ToInput(), request.Id > 0 ? request.Id : null);
            try
            {
                var resolved = await sync.FetchAsync(draft, ct);
                return Results.Ok(new ImportListTestResult(TestMessage(draft, resolved)));
            }
            catch (ImportListException ex)
            {
                return Failed(draft.Implementation, ex);
            }
        });

        group.MapPost("/preview", async (ImportListRequest request, ImportListService service, ImportListSyncService sync, CancellationToken ct) =>
        {
            var draft = service.Draft(request.ToInput(), request.Id > 0 ? request.Id : null);
            try
            {
                return Results.Ok(await sync.PreviewAsync(draft, ct));
            }
            catch (ImportListException ex)
            {
                return Failed(draft.Implementation, ex);
            }
        });

        // Adds one item of a saved list now, with the list's options ("Add" in the preview).
        group.MapPost("/{id:int}/add", async (int id, ImportListAddRequest request, ImportListService service, ImportListSyncService sync, CancellationToken ct) =>
        {
            if (service.Find(id) is not { } list)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(new ImportListAddResult(await sync.AddAsync(list, request.TmdbId, ct)));
            }
            catch (ImportListException ex)
            {
                return Failed(list.Implementation, ex);
            }
        });

        group.MapPost("/{id:int}/sync", (int id, ImportListService service, JobQueue jobs) =>
        {
            if (service.Find(id) is not { } list)
            {
                return Results.NotFound();
            }

            var queued = jobs.Enqueue(new Job(
                $"import-list-sync:{id.ToString(CultureInfo.InvariantCulture)}",
                $"Sync import list {list.Name}",
                (sp, ct) => sp.GetRequiredService<ImportListSyncService>().SyncAsync(id, ct)));
            return Results.Ok(new ImportListQueued(queued));
        });

        // All enabled lists plus "Clean library", as the scheduled task (System → Tasks shows the run).
        group.MapPost("/sync", (ScheduledTaskRunner runner) => Results.Ok(new ImportListQueued(runner.RunNow(ImportListSyncTask.TaskKey))));

        group.MapGet("/origin", (MediaType mediaType, int tmdbId, ImportListService service) =>
            service.Origin(mediaType, tmdbId) is { } origin
                ? new ImportListOriginResource(origin.ListId, origin.ListName)
                : new ImportListOriginResource(null, null));

        var exclusions = group.MapGroup("/exclusion");
        exclusions.MapGet("/", (ImportListService service) => service.Exclusions());
        exclusions.MapPost("/", (ImportListExclusionRequest request, ImportListService service) =>
        {
            var exclusion = service.AddExclusion(request.MediaType, request.TmdbId, request.Title, request.Year);
            return Results.Created($"/api/v1/importlist/exclusion/{exclusion.Id}", exclusion);
        });
        exclusions.MapDelete("/{id:int}", (int id, ImportListService service) =>
        {
            service.DeleteExclusion(id);
            return Results.NoContent();
        });

        var settings = app.MapGroup("/api/v1/settings/importlists").RequireAuthorization(AuthSchemes.AdminPolicy).WithTags("Settings");
        settings.MapGet("/", (ImportListOptions options) => options.Values);
        settings.MapPut("/", (ImportListOptionsRequest request, ImportListOptions options, ILogger<ImportListOptions> logger) =>
        {
            // The level comes as text, so an unknown one is a field error instead of a broken request.
            var level = Enum.GetValues<CleanLibraryLevel>()
                .Where(l => string.Equals(JsonSerializer.Serialize(l).Trim('"'), request.CleanLibraryLevel, StringComparison.OrdinalIgnoreCase))
                .Cast<CleanLibraryLevel?>()
                .FirstOrDefault();
            var values = new ImportListOptionValues(request.SyncInterval, level ?? (CleanLibraryLevel)(-1));
            var errors = ImportListOptions.Validate(values);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            options.Save(values);
            logger.LogInformation(
                "Import list settings saved: sync every {Hours} h, clean library {Level}", values.SyncInterval, values.CleanLibraryLevel);
            return Results.Ok(options.Values);
        });

        return app;
    }

    private static string TestMessage(ImportListDefinition list, ImportListResolved resolved)
    {
        var what = list.MediaType == MediaType.Series ? "series" : resolved.Items.Count == 1 ? "movie" : "movies";
        var name = resolved.Name is { Length: > 0 } n ? $" in “{n}”" : string.Empty;
        var unmatched = resolved.Unmatched.Count > 0 ? $" ({resolved.Unmatched.Count} not found on TMDB)" : string.Empty;
        return $"Found {resolved.Items.Count} {what}{name}{unmatched}.";
    }

    private static IResult Failed(string implementation, ImportListException ex)
    {
        var type = ImportListCatalog.Find(implementation)?.Name ?? implementation;
        var text = ex.Message.StartsWith(type, StringComparison.OrdinalIgnoreCase) || ex.Message.Contains(':', StringComparison.Ordinal)
            ? ex.Message
            : $"{type}: {ex.Message}";
        return Results.Problem(
            title: text,
            detail: text,
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "import-list-failed" });
    }
}

/// <summary>The catalog with the choices of the common fields.</summary>
public sealed record ImportListSchema(
    IReadOnlyList<ImportListType> Types,
    IReadOnlyList<ConnectFieldOption> MovieMonitorOptions,
    IReadOnlyList<ConnectFieldOption> SeriesMonitorOptions,
    IReadOnlyList<ConnectFieldOption> AvailabilityOptions);

public sealed record ImportListStatusResource(
    bool Failing,
    DateTime? LastSync,
    DateTime? LastSuccess,
    string? LastError,
    int Failures,
    int? LastFound,
    int? LastAdded,
    IReadOnlyList<string> LastAddedTitles);

/// <summary>A list as the API returns it; <see cref="Settings"/> holds every field of the type, secrets masked.</summary>
public sealed record ImportListResource(
    int Id,
    string Name,
    string Implementation,
    string ImplementationName,
    MediaType MediaType,
    bool Enabled,
    bool EnableAuto,
    bool SearchOnAdd,
    string Monitor,
    int RootFolderId,
    int? QualityProfileId,
    string MinimumAvailability,
    IReadOnlyDictionary<string, object?> Settings,
    ImportListStatusResource Status)
{
    public static ImportListResource From(ImportListDefinition l)
    {
        ArgumentNullException.ThrowIfNull(l);
        var type = ImportListCatalog.Find(l.Implementation);
        var s = l.Status;
        return new ImportListResource(
            l.Id,
            l.Name,
            l.Implementation,
            type?.Name ?? l.Implementation,
            l.MediaType,
            l.Enabled,
            l.EnableAuto,
            l.SearchOnAdd,
            l.Monitor,
            l.RootFolderId,
            l.QualityProfileId,
            l.MinimumAvailability,
            type is null ? new Dictionary<string, object?>() : ConnectionSettings.ToResource(type.AsConnectType(), l.Settings),
            new ImportListStatusResource(s.Failing, s.LastSync, s.LastSuccess, s.LastError, s.Failures, s.LastFound, s.LastAdded, s.LastAddedTitles));
    }
}

/// <summary>Body of create, update, test and preview. Settings not sent keep their stored value; "********" keeps a stored secret.</summary>
public sealed record ImportListRequest(
    int Id,
    string? Name,
    string? Implementation,
    MediaType? MediaType = null,
    bool Enabled = true,
    bool EnableAuto = true,
    bool SearchOnAdd = true,
    string? Monitor = null,
    int RootFolderId = 0,
    int? QualityProfileId = null,
    string? MinimumAvailability = null,
    Dictionary<string, JsonElement>? Settings = null)
{
    public ImportListInput ToInput() => new(
        Name, Implementation, MediaType, Enabled, EnableAuto, SearchOnAdd, Monitor, RootFolderId, QualityProfileId, MinimumAvailability, Settings);
}

/// <param name="SyncInterval">Hours between two syncs of all lists.</param>
/// <param name="CleanLibraryLevel">disabled, logOnly, keepAndUnmonitor or removeAndKeepFiles.</param>
public sealed record ImportListOptionsRequest(int SyncInterval, string? CleanLibraryLevel);

public sealed record ImportListTestResult(string Message);

public sealed record ImportListQueued(bool Queued);

public sealed record ImportListAddRequest(int TmdbId);

public sealed record ImportListAddResult(IReadOnlyList<string> Added);

public sealed record ImportListOriginResource(int? ListId, string? ListName);

public sealed record ImportListExclusionRequest(MediaType MediaType, int TmdbId, string? Title, int Year = 0);
