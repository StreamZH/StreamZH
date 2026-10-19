// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.Downloads.Torrent;

namespace StreamZH.Host.Api;

/// <summary>
/// 0.11 Settings → Download clients (Radarr's /api/v3/downloadclient): list, schema per implementation, add, edit, delete,
/// test, and the remote path mappings. Secrets (passwords) are never returned: they show as "********", and sending that
/// back keeps the stored value. Reads for Owner, Admin and Friend; changes and tests for Owner and Admin.
/// </summary>
public static class DownloadClientEndpoints
{
    public static IEndpointRouteBuilder MapDownloadClientEndpoints(this IEndpointRouteBuilder app)
    {
        var clients = app.MapGroup("/api/v1/downloadclient").RequireAuthorization().WithTags("Download clients");

        clients.MapGet("/", (DownloadClientService service, TorrentTracker tracker) =>
            service.All().Select(c => DownloadClientResource.From(c, tracker.Health(c.Id))).ToList());

        clients.MapGet("/schema", (DownloadClientService service) =>
            service.Schema().Select(DownloadClientResource.Template).ToList());

        clients.MapGet("/{id:int}", (int id, DownloadClientService service, TorrentTracker tracker) =>
            service.Find(id) is { } client ? Results.Ok(DownloadClientResource.From(client, tracker.Health(id))) : NotFound(id));

        clients.MapPost("/", (DownloadClientRequest request, DownloadClientService service, TorrentTracker tracker) =>
        {
            var created = service.Create(request.ToDefinition(0));
            return Results.Created($"/api/v1/downloadclient/{created.Id}", DownloadClientResource.From(created, tracker.Health(created.Id)));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        clients.MapPut("/{id:int}", (int id, DownloadClientRequest request, DownloadClientService service, TorrentTracker tracker) =>
            DownloadClientResource.From(service.Update(id, request.ToDefinition(id)), tracker.Health(id)))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        clients.MapDelete("/{id:int}", (int id, DownloadClientService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // Nothing is saved. With the id of a saved client, masked secrets use its stored ones.
        clients.MapPost("/test", async (DownloadClientRequest request, DownloadClientService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var info = await service.TestAsync(request.ToDefinition(request.Id ?? 0), request.Id is > 0 ? request.Id : null, cancellationToken);
                return Results.Ok(new DownloadClientTestResult(info.Name, info.Version, info.Categories));
            }
            catch (DownloadClientException ex)
            {
                var extensions = new Dictionary<string, object?> { ["code"] = "download-client-test-failed" };
                if (ex.Field is { } field)
                {
                    extensions["field"] = field;
                    extensions["errors"] = new Dictionary<string, string[]> { [field] = [ex.Message] };
                }

                return Results.Problem(title: ex.Message, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, extensions: extensions);
            }
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        var mappings = app.MapGroup("/api/v1/remotepathmapping").RequireAuthorization().WithTags("Download clients");
        mappings.MapGet("/", (RemotePathMappingService service) => service.All());
        mappings.MapGet("/{id:int}", (int id, RemotePathMappingService service) =>
            service.Find(id) is { } mapping ? Results.Ok(mapping) : Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: $"Remote path mapping {id} not found.", extensions: new Dictionary<string, object?> { ["code"] = "not-found" }));
        mappings.MapPost("/", (RemotePathMappingRequest request, RemotePathMappingService service) =>
        {
            var created = service.Create(request.Host, request.RemotePath, request.LocalPath);
            return Results.Created($"/api/v1/remotepathmapping/{created.Id}", created);
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
        mappings.MapPut("/{id:int}", (int id, RemotePathMappingRequest request, RemotePathMappingService service) =>
            service.Update(id, request.Host, request.RemotePath, request.LocalPath))
            .RequireAuthorization(AuthSchemes.AdminPolicy);
        mappings.MapDelete("/{id:int}", (int id, RemotePathMappingService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }

    private static IResult NotFound(int id) => Results.Problem(
        statusCode: StatusCodes.Status404NotFound, title: $"Download client {id} not found.", detail: $"Download client {id} not found.",
        extensions: new Dictionary<string, object?> { ["code"] = "not-found" });
}

/// <summary>One setting of a client, with what the UI needs to show it (Radarr's field shape). Secrets are masked.</summary>
public sealed record DownloadClientFieldResource(
    int Order,
    string Name,
    string Label,
    string Type,
    JsonElement Value,
    string? HelpText,
    bool Advanced,
    bool Required,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Privacy);

/// <summary>Whether StreamZH can reach the client (null until it was polled or tested by grabbing).</summary>
public sealed record DownloadClientStatusResource(bool Ok, string? Error, string? Field, DateTimeOffset? Since, DateTimeOffset? LastContact);

/// <summary>A download client. <see cref="Protocol"/> is "torrent"; <see cref="Fields"/> in the order of the schema.</summary>
public sealed record DownloadClientResource(
    int Id,
    string Name,
    string Implementation,
    string ImplementationName,
    DownloadProtocol Protocol,
    bool Enable,
    int Priority,
    bool RemoveCompleted,
    IReadOnlyList<DownloadClientFieldResource> Fields,
    DownloadClientStatusResource? Status,
    string? Description,
    string? InfoLink)
{
    public static DownloadClientResource From(DownloadClientDefinition client, TorrentClientHealth? health = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var masked = DownloadClientService.Masked(client);
        var implementation = DownloadClientCatalog.Find(client.Implementation);
        return new DownloadClientResource(
            client.Id,
            client.Name,
            implementation?.Implementation ?? client.Implementation,
            implementation?.Name ?? client.Implementation,
            client.Protocol,
            client.Enable,
            client.Priority,
            client.RemoveCompleted,
            FieldsOf(implementation, masked.Settings),
            health is null ? null : new DownloadClientStatusResource(health.Ok, health.Error, health.Field, health.Since, health.LastContact),
            implementation?.Description,
            implementation?.InfoLink);
    }

    /// <summary>A new client of this implementation with the default values (GET /schema).</summary>
    public static DownloadClientResource Template(DownloadClientImplementation implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        return new DownloadClientResource(
            0,
            implementation.Name,
            implementation.Implementation,
            implementation.Name,
            implementation.Protocol,
            true,
            1,
            true,
            FieldsOf(implementation, DownloadClientCatalog.Defaults(implementation)),
            null,
            implementation.Description,
            implementation.InfoLink);
    }

    private static List<DownloadClientFieldResource> FieldsOf(DownloadClientImplementation? implementation, IReadOnlyDictionary<string, JsonElement> settings) =>
        implementation is null
            ? []
            : [.. implementation.Fields.Select((f, i) => new DownloadClientFieldResource(
                i,
                f.Name,
                f.Label,
                f.Type,
                settings.TryGetValue(f.Name, out var value) ? value : f.Default,
                f.HelpText,
                f.Advanced,
                f.Required,
                f.Secret ? "password" : null))];
}

/// <summary>A field value sent by the UI.</summary>
public sealed record DownloadClientFieldValue(string Name, JsonElement Value);

/// <summary>
/// Body of create, update and test. Settings come as <see cref="Fields"/> (Radarr's shape: name and value) or as a
/// <see cref="Settings"/> object; "********" as a secret keeps the stored one. <see cref="Id"/> only matters for the test.
/// </summary>
public sealed record DownloadClientRequest(
    int? Id,
    string? Name,
    string? Implementation,
    bool? Enable,
    int? Priority,
    bool? RemoveCompleted,
    IReadOnlyList<DownloadClientFieldValue>? Fields,
    IReadOnlyDictionary<string, JsonElement>? Settings)
{
    public DownloadClientDefinition ToDefinition(int id)
    {
        var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in Fields ?? [])
        {
            if (!string.IsNullOrWhiteSpace(field.Name))
            {
                settings[field.Name] = field.Value;
            }
        }

        foreach (var (key, value) in Settings ?? new Dictionary<string, JsonElement>())
        {
            settings[key] = value;
        }

        var implementation = DownloadClientCatalog.Find(Implementation);
        return new DownloadClientDefinition(
            id,
            Name ?? string.Empty,
            Implementation ?? string.Empty,
            implementation?.Protocol ?? DownloadProtocol.Torrent,
            Enable ?? true,
            Priority ?? 1,
            settings)
        {
            RemoveCompleted = RemoveCompleted ?? true,
        };
    }
}

public sealed record DownloadClientTestResult(string Name, string Version, IReadOnlyList<string> Categories);

public sealed record RemotePathMappingRequest(string? Host, string? RemotePath, string? LocalPath);
