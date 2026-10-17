// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Indexers;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.Indexers;

namespace StreamZH.Host.Api;

/// <summary>Indexer management: list with health, add/edit/delete, test (t=caps) and defaults for a new indexer.</summary>
public static class IndexerEndpoints
{
    /// <summary>
    /// Repositories, <see cref="IndexerService"/> and <see cref="IIndexerProvider"/> (same instance).
    /// <see cref="IIndexerClient"/> is not registered here; the test endpoint asks for it only when called.
    /// </summary>
    public static IServiceCollection AddIndexerManagement(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IndexerRepository>();
        services.AddSingleton<IndexerStatusRepository>();
        services.AddSingleton<IndexerService>();
        services.AddSingleton<IIndexerProvider>(sp => sp.GetRequiredService<IndexerService>());
        return services;
    }

    public static IEndpointRouteBuilder MapIndexerEndpoints(this IEndpointRouteBuilder app)
    {
        var indexers = app.MapGroup("/api/v1/indexer").RequireAuthorization().WithTags("Indexers");

        indexers.MapGet("/", (IndexerService service) =>
        {
            var health = service.Health();
            return service.All().Select(i => IndexerResource.From(i, health.GetValueOrDefault(i.Id) ?? IndexerHealth.Ok(i.Id)));
        });
        indexers.MapGet("/schema", () => IndexerResource.From(IndexerService.Schema(), null));
        indexers.MapGet("/{id:int}", (int id, IndexerService service) =>
            service.Find(id) is { } i ? Results.Ok(IndexerResource.From(i, service.Health(id))) : Results.NotFound());

        indexers.MapPost("/", (IndexerRequest request, IndexerService service) =>
        {
            var created = service.Create(request.ToDefinition());
            return Results.Created($"/api/v1/indexer/{created.Id}", IndexerResource.From(created, service.Health(created.Id)));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        indexers.MapPut("/{id:int}", (int id, IndexerRequest request, IndexerService service) =>
        {
            var updated = service.Update(id, request.ToDefinition());
            return IndexerResource.From(updated, service.Health(id));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        indexers.MapDelete("/{id:int}", (int id, IndexerService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // Nothing is saved. A masked API key with the id of a saved indexer uses its stored key.
        indexers.MapPost("/test", async (IndexerRequest request, IndexerService service, [FromServices] IIndexerClient? client, CancellationToken cancellationToken) =>
        {
            if (client is null)
            {
                return Results.Problem(
                    title: "Indexer support is not available in this build.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?> { ["code"] = "indexer-client-missing" });
            }

            var result = await service.TestAsync(request.ToDefinition(), client, cancellationToken);
            return result.Capabilities is { } caps
                ? Results.Ok(caps)
                : Results.Problem(
                    title: result.Error,
                    detail: result.Error,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = "indexer-test-failed" });
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }
}

/// <summary>Health as shown in the list. <see cref="DisabledTill"/> is only set while the indexer is skipped.</summary>
public sealed record IndexerStatusResource(bool Healthy, DateTimeOffset? DisabledTill, DateTimeOffset? MostRecentFailure, string? LastError, int EscalationLevel)
{
    public static IndexerStatusResource From(IndexerHealth h) => new(h.Healthy, h.DisabledTill, h.MostRecentFailure, h.LastError, h.EscalationLevel);
}

/// <summary>An indexer as the API returns it. The API key is never sent in plain text.</summary>
public sealed record IndexerResource(
    int Id,
    string Name,
    string Implementation,
    string BaseUrl,
    string ApiPath,
    string ApiKey,
    IReadOnlyList<int> Categories,
    IReadOnlyList<int> TvCategories,
    int Priority,
    bool EnableRss,
    bool EnableAutomaticSearch,
    bool EnableInteractiveSearch,
    IndexerStatusResource? Status)
{
    public static IndexerResource From(IndexerDefinition i, IndexerHealth? health) => new(
        i.Id,
        i.Name,
        i.Implementation,
        i.BaseUrl,
        i.ApiPath,
        string.IsNullOrEmpty(i.ApiKey) ? string.Empty : IndexerService.MaskedApiKey,
        i.Categories,
        i.TvCategories,
        i.Priority,
        i.EnableRss,
        i.EnableAutomaticSearch,
        i.EnableInteractiveSearch,
        health is null ? null : IndexerStatusResource.From(health));
}

/// <summary>Body for create, update and test. The status is ignored; "********" as API key keeps the stored key.</summary>
public sealed record IndexerRequest(
    int Id,
    string? Name,
    string? Implementation,
    string? BaseUrl,
    string? ApiPath,
    string? ApiKey,
    IReadOnlyList<int>? Categories,
    IReadOnlyList<int>? TvCategories,
    int? Priority,
    bool EnableRss = true,
    bool EnableAutomaticSearch = true,
    bool EnableInteractiveSearch = true)
{
    public IndexerDefinition ToDefinition() => new(
        Id,
        Name ?? string.Empty,
        Implementation ?? IndexerDefinition.Newznab,
        BaseUrl ?? string.Empty,
        ApiPath ?? string.Empty,
        ApiKey ?? string.Empty,
        Categories ?? [],
        TvCategories ?? [],
        Priority ?? IndexerService.DefaultPriority,
        EnableRss,
        EnableAutomaticSearch,
        EnableInteractiveSearch);
}
