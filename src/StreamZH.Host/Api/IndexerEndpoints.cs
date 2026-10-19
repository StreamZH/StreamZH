// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Core.Net;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library;
using StreamZH.Library.Indexers;

namespace StreamZH.Host.Api;

/// <summary>
/// Indexer management: catalog, list with health and limits, add/edit/delete, details, test and test all, indexer history,
/// stats and the import from Prowlarr.
/// </summary>
public static class IndexerEndpoints
{
    public const int MaxHistoryPageSize = 200;
    public const int MaxStatsDays = 90;

    /// <summary>
    /// Repositories, <see cref="IndexerService"/> and <see cref="IIndexerProvider"/> (same instance), the Prowlarr import and
    /// <see cref="IIndexerClient"/> as <see cref="TrackedIndexerClient"/> around the protocol client registered under
    /// <see cref="IndexerClientKeys.Inner"/> (AddNewznabIndexers).
    /// </summary>
    public static IServiceCollection AddIndexerManagement(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IndexerRepository>();
        services.AddSingleton<IndexerStatusRepository>();
        services.AddSingleton<IndexerHistoryRepository>();
        services.AddSingleton<IndexerService>();
        services.AddSingleton<IIndexerProvider>(sp => sp.GetRequiredService<IndexerService>());
        services.TryAddSingleton<IConfiguredServiceHosts, ConfiguredServiceHosts>();

        // Every consumer (search, grab, test) gets the tracking decorator: indexer history and query/grab limits.
        services.AddTransient<IIndexerClient>(sp => new TrackedIndexerClient(
            sp.GetRequiredKeyedService<IIndexerClient>(IndexerClientKeys.Inner),
            sp.GetRequiredService<IndexerHistoryRepository>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<TrackedIndexerClient>>(),
            sp.GetRequiredService<IndexerService>()));

        services.AddHttpClient<ProwlarrImportService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = ResponseLimits.Json;
        });
        return services;
    }

    public static IEndpointRouteBuilder MapIndexerEndpoints(this IEndpointRouteBuilder app)
    {
        var indexers = app.MapGroup("/api/v1/indexer").RequireAuthorization().WithTags("Indexers");

        indexers.MapGet("/", (IndexerService service) =>
        {
            var health = service.Health();
            var usage = service.Usage();
            return service.All().Select(i => IndexerResource.From(
                i, health.GetValueOrDefault(i.Id) ?? IndexerHealth.Ok(i.Id), service.LimitStatus(i, usage.GetValueOrDefault(i.Id))));
        });
        // ?implementation=Torznab gives a torrent indexer to start from (0.11).
        indexers.MapGet("/schema", (string? implementation) => IndexerResource.From(IndexerService.Schema(implementation), null, null));
        indexers.MapGet("/definitions", () => IndexerCatalog.All);
        indexers.MapGet("/{id:int}", (int id, IndexerService service) =>
            service.Find(id) is { } i ? Results.Ok(Resource(service, i)) : Results.NotFound());

        indexers.MapGet("/{id:int}/details", (int id, IndexerService service) =>
            service.Details(id) is { } details
                ? Results.Ok(new IndexerDetailsResource(Resource(service, details.Indexer), details.Definition, details.Capabilities, details.CapabilitiesCheckedAt))
                : Results.NotFound());

        indexers.MapPost("/", (IndexerRequest request, IndexerService service) =>
        {
            var created = service.Create(request.ToDefinition(), request.CloneFromId);
            return Results.Created($"/api/v1/indexer/{created.Id}", Resource(service, created));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        indexers.MapPut("/{id:int}", (int id, IndexerRequest request, IndexerService service) =>
        {
            var updated = service.Update(id, request.ToDefinition());
            return Resource(service, updated);
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        indexers.MapDelete("/{id:int}", (int id, IndexerService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // Nothing is saved, except the capabilities when the tested connection is the one of the saved indexer.
        // A masked API key with the id of a saved indexer uses its stored key.
        indexers.MapPost("/test", async (IndexerRequest request, IndexerService service, [FromServices] IIndexerClient? client, CancellationToken cancellationToken) =>
        {
            if (client is null)
            {
                return ClientMissing();
            }

            var result = await service.TestAsync(request.ToDefinition(), client, cancellationToken, request.CloneFromId);
            return result.Capabilities is { } caps
                ? Results.Ok(caps)
                : Results.Problem(
                    title: result.Error,
                    detail: result.Error,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = "indexer-test-failed" });
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        indexers.MapPost("/testall", async (IndexerService service, [FromServices] IIndexerClient? client, CancellationToken cancellationToken) =>
            client is null ? ClientMissing() : Results.Ok(await service.TestAllAsync(client, cancellationToken)))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        indexers.MapGet("/history", (int? indexerId, string? eventType, int? page, int? pageSize, IndexerHistoryRepository history) =>
        {
            IndexerHistoryEventType? type = null;
            if (!string.IsNullOrWhiteSpace(eventType))
            {
                if (!Enum.TryParse<IndexerHistoryEventType>(eventType, ignoreCase: true, out var parsed) || int.TryParse(eventType, out _))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["eventType"] = ["Use query, rss, grab or caps."] });
                }

                type = parsed;
            }

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, MaxHistoryPageSize);
            var (records, total) = history.Page(p, size, indexerId, type);
            return Results.Ok(new IndexerHistoryPage(p, size, total, records));
        });

        indexers.MapGet("/stats", (int? days, IndexerHistoryRepository history, TimeProvider time) =>
        {
            var to = time.GetUtcNow().UtcDateTime;
            var from = to.AddDays(-Math.Clamp(days ?? 30, 1, MaxStatsDays));
            var perDay = history.Daily(from).ToDictionary(d => d.Date, StringComparer.Ordinal);
            var daily = new List<IndexerDailyRow>();
            for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
            {
                var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                daily.Add(perDay.GetValueOrDefault(key) ?? new IndexerDailyRow(key, 0, 0));
            }

            return new IndexerStatsResource(
                DateTime.SpecifyKind(from, DateTimeKind.Utc), DateTime.SpecifyKind(to, DateTimeKind.Utc), history.Stats(from), daily);
        });

        indexers.MapPost("/import/prowlarr/preview", (ProwlarrImportRequest request, ProwlarrImportService import, CancellationToken cancellationToken) =>
            import.PreviewAsync(request.Url, request.ApiKey, cancellationToken))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        indexers.MapPost("/import/prowlarr", (ProwlarrImportRequest request, ProwlarrImportService import, CancellationToken cancellationToken) =>
            import.ImportAsync(request.Url, request.ApiKey, request.Ids, cancellationToken, request.Keys))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }

    private static IndexerResource Resource(IndexerService service, IndexerDefinition indexer) =>
        IndexerResource.From(indexer, service.Health(indexer.Id), service.LimitStatus(indexer, service.Usage().GetValueOrDefault(indexer.Id)));

    private static IResult ClientMissing() => Results.Problem(
        title: "Indexer support is not available in this build.",
        statusCode: StatusCodes.Status503ServiceUnavailable,
        extensions: new Dictionary<string, object?> { ["code"] = "indexer-client-missing" });
}

/// <summary>
/// Health and limits as shown in the list. <see cref="DisabledTill"/> is only set while the indexer is skipped;
/// usage counts the rolling window of the indexer's limits unit.
/// </summary>
public sealed record IndexerStatusResource(
    bool Healthy,
    DateTimeOffset? DisabledTill,
    DateTimeOffset? MostRecentFailure,
    string? LastError,
    int EscalationLevel,
    int QueriesUsed = 0,
    int GrabsUsed = 0,
    IndexerLimitKind? LimitReached = null,
    int? VipExpiresInDays = null)
{
    public static IndexerStatusResource From(IndexerHealth h, IndexerLimitStatus? limits)
    {
        var l = limits ?? IndexerLimitStatus.None;
        return new(h.Healthy, h.DisabledTill, h.MostRecentFailure, h.LastError, h.EscalationLevel, l.QueriesUsed, l.GrabsUsed, l.LimitReached, l.VipExpiresInDays);
    }
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
    IndexerStatusResource? Status,
    bool Enable = true,
    string? DefinitionKey = null,
    string AdditionalParameters = "",
    string? VipExpiration = null,
    int? QueryLimit = null,
    int? GrabLimit = null,
    IndexerLimitsUnit LimitsUnit = IndexerLimitsUnit.Day,
    DownloadProtocol Protocol = DownloadProtocol.Usenet,
    int MinimumSeeders = 1,
    double? SeedRatio = null,
    int? SeedTimeMinutes = null,
    int? SeasonPackSeedTimeMinutes = null)
{
    public static IndexerResource From(IndexerDefinition i, IndexerHealth? health, IndexerLimitStatus? limits) => new(
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
        health is null ? null : IndexerStatusResource.From(health, limits),
        i.Enable,
        i.DefinitionKey,
        i.AdditionalParameters,
        i.VipExpiration,
        i.QueryLimit,
        i.GrabLimit,
        i.LimitsUnit,
        i.Protocol,
        i.MinimumSeeders,
        i.SeedRatio,
        i.SeedTimeMinutes,
        i.SeasonPackSeedTimeMinutes);
}

/// <summary>
/// Body for create, update and test. The status and the protocol are ignored (the implementation decides: Torznab is
/// torrent); "********" as API key keeps the stored key (of the indexer itself, or of <see cref="CloneFromId"/> when
/// creating or testing a clone). The seed settings count for Torznab indexers only (empty = the download client's setting).
/// </summary>
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
    bool EnableInteractiveSearch = true,
    bool Enable = true,
    string? DefinitionKey = null,
    string? AdditionalParameters = null,
    string? VipExpiration = null,
    int? QueryLimit = null,
    int? GrabLimit = null,
    string? LimitsUnit = null,
    int? CloneFromId = null,
    int? MinimumSeeders = null,
    double? SeedRatio = null,
    int? SeedTimeMinutes = null,
    int? SeasonPackSeedTimeMinutes = null)
{
    /// <exception cref="ValidationFailedException">The limits unit is neither "day" nor "hour".</exception>
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
        EnableInteractiveSearch)
    {
        Enable = Enable,
        DefinitionKey = DefinitionKey,
        AdditionalParameters = AdditionalParameters ?? string.Empty,
        VipExpiration = VipExpiration,
        QueryLimit = QueryLimit,
        GrabLimit = GrabLimit,
        LimitsUnit = ParseUnit(LimitsUnit),
        MinimumSeeders = MinimumSeeders ?? 1,
        SeedRatio = SeedRatio,
        SeedTimeMinutes = SeedTimeMinutes,
        SeasonPackSeedTimeMinutes = SeasonPackSeedTimeMinutes,
    };

    private static IndexerLimitsUnit ParseUnit(string? unit) => unit?.Trim().ToLowerInvariant() switch
    {
        null or "" or "day" => IndexerLimitsUnit.Day,
        "hour" => IndexerLimitsUnit.Hour,
        _ => throw new ValidationFailedException(new Dictionary<string, string[]> { ["limitsUnit"] = ["Use day or hour."] }),
    };
}

public sealed record IndexerDetailsResource(
    IndexerResource Indexer, IndexerCatalogEntry? Definition, IndexerCapabilities? Capabilities, DateTimeOffset? CapabilitiesCheckedAt);

public sealed record IndexerHistoryPage(int Page, int PageSize, int TotalRecords, IReadOnlyList<IndexerHistoryEntry> Records);

public sealed record IndexerStatsResource(DateTime From, DateTime To, IReadOnlyList<IndexerStatsRow> Indexers, IReadOnlyList<IndexerDailyRow> Daily);

/// <param name="Keys">Optional: per Prowlarr id the indexer's own API key, so it is used directly instead of through Prowlarr.</param>
public sealed record ProwlarrImportRequest(string? Url, string? ApiKey, IReadOnlyList<int>? Ids, IReadOnlyDictionary<int, string>? Keys = null);
