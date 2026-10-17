// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.Usenet;
using StreamZH.Usenet.Pool;

namespace StreamZH.Host.Api;

/// <summary>Usenet provider accounts (add/edit/delete/test) and the live state of their connections.</summary>
public static class UsenetEndpoints
{
    /// <summary>
    /// Repository, <see cref="UsenetServerService"/>, and <see cref="UsenetArticleSource"/> (also as <see cref="IArticleSource"/>,
    /// same instance) loaded with the stored servers. <see cref="UsenetPoolOptions"/> can be replaced before this runs (tests).
    /// </summary>
    public static IServiceCollection AddUsenet(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new UsenetPoolOptions());
        services.AddSingleton<UsenetServerRepository>();
        services.AddSingleton(sp =>
        {
            var source = new UsenetArticleSource(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<UsenetArticleSource>>(),
                sp.GetRequiredService<UsenetPoolOptions>());
            source.Configure(sp.GetRequiredService<UsenetServerRepository>().All());
            return source;
        });
        services.AddSingleton<IArticleSource>(sp => sp.GetRequiredService<UsenetArticleSource>());
        services.AddSingleton<UsenetServerService>();
        return services;
    }

    public static IEndpointRouteBuilder MapUsenetEndpoints(this IEndpointRouteBuilder app)
    {
        var usenet = app.MapGroup("/api/v1/usenet").RequireAuthorization().WithTags("Usenet");

        usenet.MapGet("/server", (UsenetServerService service) => service.All().Select(UsenetServerResource.From));
        usenet.MapGet("/server/{id:int}", (int id, UsenetServerService service) =>
            service.Find(id) is { } server ? Results.Ok(UsenetServerResource.From(server)) : Results.NotFound());

        usenet.MapPost("/server", (UsenetServerRequest request, UsenetServerService service) =>
        {
            var created = service.Create(request.ToServer());
            return Results.Created($"/api/v1/usenet/server/{created.Id}", UsenetServerResource.From(created));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        usenet.MapPut("/server/{id:int}", (int id, UsenetServerRequest request, UsenetServerService service) =>
            UsenetServerResource.From(service.Update(id, request.ToServer())))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        usenet.MapDelete("/server/{id:int}", (int id, UsenetServerService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // Nothing is saved. "********" as password with the id of a stored server uses its stored password.
        usenet.MapPost("/server/test", async (UsenetServerRequest request, UsenetServerService service, CancellationToken cancellationToken) =>
        {
            var result = await service.TestAsync(request.ToServer(), cancellationToken);
            return result.Ok
                ? Results.Ok(new UsenetTestResource(true, result.Message, result.LatencyMs))
                : Results.Problem(
                    title: result.Message,
                    detail: result.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = "server-test-failed" });
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        usenet.MapGet("/status", (UsenetServerService service) => service.Status());

        return app;
    }
}

/// <summary>A server as the API returns it. The password is never sent: "********" when one is stored, else empty.</summary>
public sealed record UsenetServerResource(
    int Id,
    string Name,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Password,
    int Connections,
    int Priority,
    bool Enabled,
    int RetentionDays)
{
    public static UsenetServerResource From(UsenetServer s) => new(
        s.Id,
        s.Name,
        s.Host,
        s.Port,
        s.UseSsl,
        s.Username,
        string.IsNullOrEmpty(s.Password) ? string.Empty : UsenetServerService.MaskedPassword,
        s.Connections,
        s.Priority,
        s.Enabled,
        s.RetentionDays);
}

/// <summary>Body for create, update and test. Missing fields get the defaults of a new server (563, SSL, 20 connections, primary).</summary>
public sealed record UsenetServerRequest(
    int Id,
    string? Name,
    string? Host,
    int? Port,
    bool? UseSsl,
    string? Username,
    string? Password,
    int? Connections,
    int? Priority,
    bool? Enabled,
    int? RetentionDays)
{
    public UsenetServer ToServer()
    {
        var defaults = UsenetServerService.Schema();
        return new UsenetServer(
            Id,
            Name ?? string.Empty,
            Host ?? string.Empty,
            Port ?? defaults.Port,
            UseSsl ?? defaults.UseSsl,
            Username ?? string.Empty,
            Password ?? string.Empty,
            Connections ?? defaults.Connections,
            Priority ?? defaults.Priority,
            Enabled ?? defaults.Enabled,
            RetentionDays ?? defaults.RetentionDays);
    }
}

public sealed record UsenetTestResource(bool Ok, string Message, int? LatencyMs);
