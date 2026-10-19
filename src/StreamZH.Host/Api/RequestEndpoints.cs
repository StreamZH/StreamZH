// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Requests;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.Requests;
using StreamZH.Library.Search;

namespace StreamZH.Host.Api;

/// <summary>
/// Requests (0.10): every signed-in user (requesters too) requests movies and series and sees their own requests; owners
/// and admins see all, approve, decline and retry. Settings → Requests for admins.
/// </summary>
public static class RequestEndpoints
{
    public const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/requests").WithTags("Requests").RequireAuthorization(AuthSchemes.AnyUserPolicy);

        group.MapGet("/", (string? status, string? mediaType, int? tmdbId, int? page, int? pageSize, bool? mine, HttpContext http, RequestRepository requests, UserRepository users) =>
        {
            var actor = RequestApi.Actor(http);
            var statuses = new List<RequestStatus>();
            foreach (var part in (status ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!RequestApi.TryStatus(part, out var parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["status"] = ["Use pending, approved, declined, processing, partiallyAvailable, available or failed (comma separated)."],
                    });
                }

                statuses.Add(parsed);
            }

            Core.Media.MediaType? type = null;
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                if (!RequestApi.TryType(mediaType, out var parsedType))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["Use movie or series."] });
                }

                type = parsedType;
            }

            // Friends and requesters only ever see their own; without an account (API key) nothing is "mine".
            int? owner = null;
            if (!actor.IsAdmin || mine == true)
            {
                if (actor.UserId is not { } id)
                {
                    return Results.Ok(new RequestPageResource(1, Math.Clamp(pageSize ?? 20, 1, MaxPageSize), 0, []));
                }

                owner = id;
            }

            var size = Math.Clamp(pageSize ?? 20, 1, MaxPageSize);
            var number = Math.Max(1, page ?? 1);
            RequestPage result;
            if (tmdbId is { } title)
            {
                // One title (the request panel of a library item): few rows, filtered here.
                var matching = (type is { } only ? requests.ForTitle(only, title) : [.. requests.ForTitle(Core.Media.MediaType.Movie, title), .. requests.ForTitle(Core.Media.MediaType.Series, title)])
                    .Where(r => (owner is null || r.RequestedBy == owner) && (statuses.Count == 0 || statuses.Contains(r.Status)))
                    .OrderByDescending(r => r.Created).ThenByDescending(r => r.Id)
                    .ToList();
                result = new RequestPage([.. matching.Skip((number - 1) * size).Take(size)], matching.Count, number, size);
            }
            else
            {
                result = requests.Query(new RequestQuery(statuses, owner, type, number, size));
            }

            var names = Names(users, result.Records);
            return Results.Ok(new RequestPageResource(result.Page, result.PageSize, result.TotalRecords, [.. result.Records.Select(r => RequestResource.From(r, names, actor))]));
        });

        group.MapGet("/count", (RequestRepository requests) => Results.Ok(new
        {
            pending = requests.Count(RequestStatus.Pending),
            approved = requests.Count(RequestStatus.Approved),
            processing = requests.Count(RequestStatus.Processing),
            partiallyAvailable = requests.Count(RequestStatus.PartiallyAvailable),
            available = requests.Count(RequestStatus.Available),
            declined = requests.Count(RequestStatus.Declined),
            failed = requests.Count(RequestStatus.Failed),
        })).RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapGet("/{id:int}", (int id, HttpContext http, RequestRepository requests, UserRepository users) =>
            requests.Find(id) is { } request && RequestService.CanSee(RequestApi.Actor(http), request)
                ? Results.Ok(Resource(users, request, RequestApi.Actor(http)))
                : Results.NotFound());

        group.MapPost("/", async (CreateRequestBody body, HttpContext http, RequestService service, UserRepository users, CancellationToken ct) =>
        {
            if (!RequestApi.TryType(body.MediaType, out var type))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["Use movie or series."] });
            }

            if (!TrySeasons(body.Seasons, out var seasons))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["seasons"] = ["Use a list of season numbers or \"all\"."] });
            }

            var actor = RequestApi.Actor(http);
            return await Run(users, actor, async () =>
            {
                var request = await service.CreateAsync(actor, new CreateRequestInput(type, body.TmdbId, seasons, body.Note), ct);
                return Results.Created($"/api/v1/requests/{request.Id}", Resource(users, request, actor));
            });
        });

        // The bodies of approve and decline are optional (a plain POST approves with the defaults).
        group.MapPost("/{id:int}/approve", async (int id, HttpContext http, RequestService service, UserRepository users, CancellationToken ct) =>
            await Run(users, RequestApi.Actor(http), async () =>
            {
                var body = await OptionalBodyAsync<ApproveRequestBody>(http, ct);
                var input = new ApproveRequestInput(body?.QualityProfileId, body?.RootFolderId, body?.Seasons);
                return Results.Ok(Resource(users, await service.ApproveAsync(RequestApi.Actor(http), id, input, ct), RequestApi.Actor(http)));
            })).RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapPost("/{id:int}/decline", async (int id, HttpContext http, RequestService service, UserRepository users, CancellationToken ct) =>
            await Run(users, RequestApi.Actor(http), async () =>
            {
                var body = await OptionalBodyAsync<DeclineRequestBody>(http, ct);
                return Results.Ok(Resource(users, await service.DeclineAsync(RequestApi.Actor(http), id, body?.Reason, ct), RequestApi.Actor(http)));
            })).RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapPost("/{id:int}/retry", async (int id, HttpContext http, RequestService service, UserRepository users, CancellationToken ct) =>
            await Run(users, RequestApi.Actor(http), async () => Results.Ok(Resource(users, await service.RetryAsync(RequestApi.Actor(http), id, ct), RequestApi.Actor(http)))))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapDelete("/{id:int}", async (int id, HttpContext http, RequestService service, UserRepository users, CancellationToken ct) =>
            await Run(users, RequestApi.Actor(http), async () =>
            {
                await service.DeleteAsync(RequestApi.Actor(http), id, ct);
                return Results.NoContent();
            }));

        var settings = app.MapGroup("/api/v1/settings/requests").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);
        settings.MapGet("/", (RequestSettings requestSettings, AutomationHold hold) => RequestSettingsResource.From(requestSettings.Values, hold.IsOn));
        settings.MapPut("/", (RequestSettingsResource body, RequestSettings requestSettings, AutomationHold hold, ILogger<RequestSettings> logger) =>
        {
            var values = body.ToValues();
            var errors = requestSettings.Validate(values);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            requestSettings.Save(values);
            logger.LogInformation("Request settings saved");
            return Results.Ok(RequestSettingsResource.From(requestSettings.Values, hold.IsOn));
        });

        return app;
    }

    /// <summary>A season list, "all" or nothing (= all seasons).</summary>
    public static bool TrySeasons(JsonElement? value, out IReadOnlyList<int>? seasons)
    {
        seasons = null;
        if (value is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return string.Equals(element.GetString()?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > RequestService.MaxSeasonNumber)
        {
            return false;
        }

        var list = new List<int>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number))
            {
                return false;
            }

            list.Add(number);
        }

        seasons = list;
        return true;
    }

    private static async Task<IResult> Run(UserRepository users, RequestActor actor, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (RequestException ex)
        {
            return RequestApi.Problem(ex, r => Resource(users, r, actor));
        }
        catch (JsonException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["The body is not valid JSON."] });
        }
    }

    private static async Task<T?> OptionalBodyAsync<T>(HttpContext http, CancellationToken cancellationToken)
        where T : class =>
        http.Request.HasJsonContentType() && http.Request.ContentLength is not 0
            ? await http.Request.ReadFromJsonAsync<T>(cancellationToken)
            : null;

    private static RequestResource Resource(UserRepository users, MediaRequest request, RequestActor viewer) =>
        RequestResource.From(request, Names(users, [request]), viewer);

    private static IReadOnlyDictionary<int, string> Names(UserRepository users, IEnumerable<MediaRequest> requests) =>
        RequestUserNames.Of(users, requests.SelectMany(r => new[] { r.RequestedBy, r.DecidedBy }).OfType<int>());
}

/// <summary>Settings → Requests. Null ids: the default quality profile, the first root folder. Null limits: no limit.</summary>
/// <param name="AutomationHold">Read only: side-by-side mode is on, approved requests are added but not searched.</param>
public sealed record RequestSettingsResource(
    bool Enabled = true,
    int? DefaultMovieProfileId = null,
    int? DefaultSeriesProfileId = null,
    int? DefaultMovieRootFolderId = null,
    int? DefaultSeriesRootFolderId = null,
    bool AutoApproveFriends = true,
    bool AutoApproveRequesters = false,
    int? DefaultMovieLimit = null,
    int? DefaultSeriesLimit = null,
    int LimitDays = RequestSettings.DefaultLimitDays,
    bool SearchOnApprove = true,
    string SeriesMonitor = RequestSeriesMonitor.Requested,
    bool NotifyOnAvailable = true,
    bool AutomationHold = false)
{
    public static RequestSettingsResource From(RequestSettingsValues v, bool automationHold)
    {
        ArgumentNullException.ThrowIfNull(v);
        return new(
            v.Enabled,
            v.DefaultMovieProfileId,
            v.DefaultSeriesProfileId,
            v.DefaultMovieRootFolderId,
            v.DefaultSeriesRootFolderId,
            v.AutoApproveFriends,
            v.AutoApproveRequesters,
            v.DefaultMovieLimit,
            v.DefaultSeriesLimit,
            v.LimitDays,
            v.SearchOnApprove,
            v.SeriesMonitor,
            v.NotifyOnAvailable,
            automationHold);
    }

    public RequestSettingsValues ToValues() => new(
        Enabled,
        DefaultMovieProfileId,
        DefaultSeriesProfileId,
        DefaultMovieRootFolderId,
        DefaultSeriesRootFolderId,
        AutoApproveFriends,
        AutoApproveRequesters,
        DefaultMovieLimit,
        DefaultSeriesLimit,
        LimitDays,
        SearchOnApprove,
        SeriesMonitor?.Trim() ?? string.Empty,
        NotifyOnAvailable);
}
