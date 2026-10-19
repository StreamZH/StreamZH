// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Users;

namespace StreamZH.Host.Api;

/// <summary>Settings → Users (0.10): accounts, roles, request settings. Admins only; owners are protected (see <see cref="UserService"/>).</summary>
public static class UserEndpoints
{
    /// <summary>User management, invites, Discord link codes and the built-in request defaults.</summary>
    public static IServiceCollection AddUserManagement(this IServiceCollection services)
    {
        services.AddSingleton<IDiscordLinkCodes, DiscordLinkCodes>();
        services.AddSingleton<UserService>();
        services.AddSingleton<InviteService>();
        services.AddSingleton<UserResourceBuilder>();

        // Settings → Requests registers its own defaults (AddSingleton), which replace these.
        services.TryAddSingleton<IRequestDefaults, DefaultRequestDefaults>();
        return services;
    }

    /// <summary>/api/v1/users, /api/v1/me and /api/v1/invites.</summary>
    public static IEndpointRouteBuilder MapUserManagementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapUserEndpoints();
        app.MapMeEndpoints();
        app.MapInviteEndpoints();
        return app;
    }

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").RequireAuthorization(AuthSchemes.AdminPolicy).WithTags("Users");

        group.MapGet("/", (UserService service, UserResourceBuilder build) => build.List(service.All()));

        group.MapGet("/{id:int}", (int id, UserService service, UserResourceBuilder build) =>
            service.Find(id) is { } user ? Results.Ok(build.One(user)) : Results.NotFound());

        group.MapPost("/", (UserRequest request, HttpContext http, UserService service, UserResourceBuilder build) =>
        {
            var result = service.Create(UserActor.From(http.User), request.ToInput(includeCredentials: true));
            return result.Ok ? Results.Created($"/api/v1/users/{result.Value!.Id}", build.One(result.Value)) : result.ToProblem();
        });

        group.MapPut("/{id:int}", (int id, UserRequest request, HttpContext http, UserService service, UserResourceBuilder build) =>
        {
            var result = service.Update(UserActor.From(http.User), id, request.ToInput(includeCredentials: false));
            return result.Ok ? Results.Ok(build.One(result.Value!)) : result.ToProblem();
        });

        group.MapDelete("/{id:int}", (int id, HttpContext http, UserService service) =>
        {
            var result = service.Delete(UserActor.From(http.User), id);
            return result.Ok ? Results.NoContent() : result.ToProblem();
        });

        group.MapPost("/{id:int}/password", async (int id, PasswordRequest request, HttpContext http, UserService service) =>
        {
            var actor = UserActor.From(http.User);
            var persistent = await MeEndpoints.CookieIsPersistentAsync(http);
            var result = service.ResetPassword(actor, id, request.Password);
            if (!result.Ok)
            {
                return result.ToProblem();
            }

            await MeEndpoints.RenewOwnSessionAsync(http, actor, result.Value!, persistent);
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/sessions/revoke", async (int id, HttpContext http, UserService service) =>
        {
            var actor = UserActor.From(http.User);
            var persistent = await MeEndpoints.CookieIsPersistentAsync(http);
            var result = service.RevokeSessions(actor, id);
            if (!result.Ok)
            {
                return result.ToProblem();
            }

            // "Sign out everywhere" on yourself keeps this browser signed in.
            await MeEndpoints.RenewOwnSessionAsync(http, actor, result.Value!, persistent);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}/discord", (int id, HttpContext http, UserService service) =>
        {
            var result = service.UnlinkDiscord(UserActor.From(http.User), id);
            return result.Ok ? Results.NoContent() : result.ToProblem();
        });

        return app;
    }

    internal static IResult ToProblem<T>(this UserResult<T> result) => result.Error switch
    {
        UserError.Invalid => Results.ValidationProblem(result.Errors ?? new Dictionary<string, string[]>()),
        UserError.NotFound => Results.Problem(result.Message, statusCode: StatusCodes.Status404NotFound),
        UserError.Forbidden => Results.Problem(result.Message, statusCode: StatusCodes.Status403Forbidden),
        UserError.Conflict => Results.Problem(result.Message, statusCode: StatusCodes.Status409Conflict),
        UserError.Gone => Results.Problem(result.Message, statusCode: StatusCodes.Status410Gone),
        _ => Results.Problem(result.Message),
    };
}

/// <summary>Create (username and password required) or update (both ignored) an account.</summary>
public sealed record UserRequest(
    string? Username,
    string? Password,
    string? Role,
    string? DisplayName,
    string? Email,
    bool? Enabled,
    bool? AutoApprove,
    int? MovieRequestLimit,
    int? SeriesRequestLimit,
    int? RequestLimitDays)
{
    public UserInput ToInput(bool includeCredentials) => new(
        includeCredentials ? Username : null,
        includeCredentials ? Password : null,
        Role,
        DisplayName,
        Email,
        Enabled,
        AutoApprove,
        MovieRequestLimit,
        SeriesRequestLimit,
        RequestLimitDays);
}

public sealed record PasswordRequest(string? Password);

public sealed record DiscordResource(string UserId, string? Name, DateTime Linked);

public sealed record UsageItem(int Used, int? Limit, int Days);

public sealed record UsageResource(UsageItem Movies, UsageItem Series);

public sealed record UserResource(
    int Id,
    string Username,
    string? DisplayName,
    string? Email,
    string Role,
    bool Enabled,
    DateTime CreatedAt,
    DateTime? LastLogin,
    bool? AutoApprove,
    bool EffectiveAutoApprove,
    int? MovieRequestLimit,
    int? SeriesRequestLimit,
    int? RequestLimitDays,
    bool CustomLimits,
    DiscordResource? Discord,
    int RequestCount,
    UsageResource Usage);

/// <summary>Builds the user resources: effective request settings, usage in the limit window and the Discord link.</summary>
public sealed class UserResourceBuilder(UserRepository users, RequestRepository requests, DiscordLinkRepository links, IRequestDefaults defaults, TimeProvider time)
{
    public IReadOnlyList<UserResource> List(IReadOnlyList<UserAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var counts = users.RequestCounts();
        var discord = links.All().ToDictionary(l => l.UserId);
        return [.. accounts.Select(a => Build(a, counts.GetValueOrDefault(a.Id), discord.GetValueOrDefault(a.Id)))];
    }

    public UserResource One(UserAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return Build(account, users.RequestCounts().GetValueOrDefault(account.Id), links.ForUser(account.Id));
    }

    public RequestPolicy Policy(UserAccount account) => UserRequestPolicy.For(account, defaults);

    public UsageResource Usage(UserAccount account, RequestPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(policy);
        var since = time.GetUtcNow().UtcDateTime.AddDays(-policy.LimitDays);
        return new UsageResource(
            new UsageItem(requests.CountSince(account.Id, MediaType.Movie, since), policy.MovieLimit, policy.LimitDays),
            new UsageItem(requests.CountSince(account.Id, MediaType.Series, since), policy.SeriesLimit, policy.LimitDays));
    }

    public static DiscordResource? Discord(DiscordLink? link) => link is null ? null : new DiscordResource(link.DiscordUserId, link.DiscordName, link.Linked);

    private UserResource Build(UserAccount a, int requestCount, DiscordLink? link)
    {
        var policy = Policy(a);
        return new UserResource(
            a.Id,
            a.Username,
            a.DisplayName,
            a.Email,
            UserRules.RoleName(a.Role),
            a.Enabled,
            a.CreatedAt,
            a.LastLogin,
            a.AutoApprove,
            policy.AutoApprove,
            a.MovieRequestLimit,
            a.SeriesRequestLimit,
            a.RequestLimitDays,
            a.HasCustomLimits,
            Discord(link),
            requestCount,
            Usage(a, policy));
    }
}
