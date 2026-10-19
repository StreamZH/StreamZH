// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Status;
using StreamZH.Library.Metadata;
using StreamZH.Library.Notifications;

namespace StreamZH.Host.Api;

/// <summary>
/// Settings → Connect: media servers and notification services (list, catalog with fields, add/edit/delete, test).
/// Secrets are never sent back in plain text: they come as "********", which keeps the stored value when saved.
/// </summary>
public static class ConnectEndpoints
{
    /// <summary>Connections, the metadata writers and the health watcher (one line in Program.cs).</summary>
    public static IServiceCollection AddConnect(this IServiceCollection services)
    {
        services.AddConnectNotifications();
        services.AddMetadataWriters();
        services.TryAddSingleton(ConnectHealthOptions.Default);
        services.AddHostedService<ConnectHealthWatcher>();
        return services;
    }

    public static IEndpointRouteBuilder MapConnectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/connection").RequireAuthorization(AuthSchemes.AdminPolicy).WithTags("Connect");

        group.MapGet("/", (ConnectionService service) => service.All().Select(ConnectionResource.From));

        group.MapGet("/schema", () => ConnectCatalog.All);

        group.MapGet("/{id:int}", (int id, ConnectionService service) =>
            service.Find(id) is { } c ? Results.Ok(ConnectionResource.From(c)) : Results.NotFound());

        group.MapPost("/", (ConnectionRequest request, ConnectionService service) =>
        {
            var created = service.Create(request.ToInput());
            return Results.Created($"/api/v1/connection/{created.Id}", ConnectionResource.From(created));
        });

        group.MapPut("/{id:int}", (int id, ConnectionRequest request, ConnectionService service) =>
            ConnectionResource.From(service.Update(id, request.ToInput())));

        group.MapDelete("/{id:int}", (int id, ConnectionService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        });

        // Nothing is saved. With the id of a saved connection, masked secrets use the stored ones.
        group.MapPost("/test", async (ConnectionRequest request, ConnectionService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var message = await service.TestAsync(request.ToInput(), request.Id > 0 ? request.Id : null, cancellationToken);
                return Results.Ok(new ConnectionTestResult(message));
            }
            catch (ConnectException ex)
            {
                var type = ConnectCatalog.Find(request.Implementation)?.Name ?? request.Implementation;
                var text = $"{type}: {ex.Message}";
                return Results.Problem(
                    title: text,
                    detail: text,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = "connection-test-failed" });
            }
        });

        return app;
    }
}

public sealed record ConnectionStatusResource(bool Failing, DateTime? LastSuccess, DateTime? LastFailure, string? LastError);

/// <summary>A connection as the API returns it; <see cref="Settings"/> holds every field of the type, secrets masked.</summary>
public sealed record ConnectionResource(
    int Id,
    string Name,
    string Implementation,
    string ImplementationName,
    string Kind,
    bool Enable,
    bool OnGrab,
    bool OnImport,
    bool OnUpgrade,
    bool OnRename,
    bool OnDownloadFailure,
    bool OnHealthIssue,
    bool OnHealthRestored,
    bool IncludeHealthWarnings,
    IReadOnlyDictionary<string, object?> Settings,
    ConnectionStatusResource Status,
    bool OnRequestCreated = false,
    bool OnRequestApproved = false,
    bool OnRequestDeclined = false,
    bool OnRequestAvailable = false)
{
    public static ConnectionResource From(ConnectionDefinition c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var type = ConnectCatalog.Find(c.Implementation);
        var t = c.Triggers;
        return new ConnectionResource(
            c.Id,
            c.Name,
            c.Implementation,
            type?.Name ?? c.Implementation,
            type?.Kind ?? ConnectCatalog.Notification,
            c.Enable,
            t.OnGrab,
            t.OnImport,
            t.OnUpgrade,
            t.OnRename,
            t.OnDownloadFailure,
            t.OnHealthIssue,
            t.OnHealthRestored,
            t.IncludeHealthWarnings,
            type is null ? new Dictionary<string, object?>() : ConnectionSettings.ToResource(type, c.Settings),
            new ConnectionStatusResource(c.Status.Failing, c.Status.LastSuccess, c.Status.LastFailure, c.Status.LastError),
            t.OnRequestCreated,
            t.OnRequestApproved,
            t.OnRequestDeclined,
            t.OnRequestAvailable);
    }
}

/// <summary>Body of create, update and test. Settings not sent keep their stored value; "********" keeps a stored secret.</summary>
public sealed record ConnectionRequest(
    int Id,
    string? Name,
    string? Implementation,
    bool Enable = true,
    bool OnGrab = false,
    bool OnImport = false,
    bool OnUpgrade = false,
    bool OnRename = false,
    bool OnDownloadFailure = false,
    bool OnHealthIssue = false,
    bool OnHealthRestored = false,
    bool IncludeHealthWarnings = false,
    Dictionary<string, JsonElement>? Settings = null,
    bool OnRequestCreated = false,
    bool OnRequestApproved = false,
    bool OnRequestDeclined = false,
    bool OnRequestAvailable = false)
{
    public ConnectionInput ToInput() => new(
        Name,
        Implementation,
        Enable,
        new ConnectionTriggers(
            OnGrab, OnImport, OnUpgrade, OnRename, OnDownloadFailure, OnHealthIssue, OnHealthRestored, IncludeHealthWarnings,
            OnRequestCreated, OnRequestApproved, OnRequestDeclined, OnRequestAvailable),
        Settings);
}

public sealed record ConnectionTestResult(string Message);
