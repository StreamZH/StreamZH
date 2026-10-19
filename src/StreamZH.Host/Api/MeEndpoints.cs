// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Authentication;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Users;

namespace StreamZH.Host.Api;

/// <summary>The signed-in user's own profile (0.10), for every role incl. Requester. Only ever the caller's own data.</summary>
public static class MeEndpoints
{
    private const string NoAccount = "This session has no user account (API key, local network or External sign-in).";

    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/me").RequireAuthorization(AuthSchemes.AnyUserPolicy).WithTags("Users");

        group.MapGet("/", (HttpContext http, UserRepository users, UserResourceBuilder build, DiscordLinkRepository links, IDiscordLinkCodes codes) =>
        {
            var id = AccountId(http);
            if (id is null)
            {
                return Results.Ok(Ownerless(http));
            }

            return users.FindAccount(id.Value) is { } account
                ? Results.Ok(Build(account, build, links, codes))
                : Results.Unauthorized();
        });

        group.MapPut("/", (ProfileRequest request, HttpContext http, UserService service, UserResourceBuilder build, DiscordLinkRepository links, IDiscordLinkCodes codes) =>
        {
            if (AccountId(http) is not { } id)
            {
                return Results.Problem(NoAccount, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = service.UpdateProfile(id, request.DisplayName, request.Email);
            return result.Ok ? Results.Ok(Build(result.Value!, build, links, codes)) : result.ToProblem();
        });

        // Rate-limited like sign-in: a stolen session must not be able to guess the current password quickly.
        group.MapPost("/password", async (OwnPasswordRequest request, HttpContext http, UserService service) =>
        {
            if (AccountId(http) is not { } id)
            {
                return Results.Problem(NoAccount, statusCode: StatusCodes.Status400BadRequest);
            }

            var persistent = await CookieIsPersistentAsync(http);
            var result = service.ChangeOwnPassword(id, request.CurrentPassword, request.NewPassword);
            if (!result.Ok)
            {
                return result.ToProblem();
            }

            await RenewOwnSessionAsync(http, UserActor.From(http.User), result.Value!, persistent);
            return Results.NoContent();
        }).RequireRateLimiting(AuthServiceExtensions.PasswordRateLimit);

        group.MapPost("/discord/link-code", (HttpContext http, UserRepository users, IDiscordLinkCodes codes) =>
        {
            if (AccountId(http) is not { } id || users.FindAccount(id) is null)
            {
                return Results.Problem(NoAccount, statusCode: StatusCodes.Status400BadRequest);
            }

            var code = codes.Create(id);
            return Results.Ok(new LinkCodeResource(code.Code, code.Expires));
        });

        group.MapDelete("/discord", (HttpContext http, DiscordLinkRepository links, IDiscordLinkCodes codes) =>
        {
            if (AccountId(http) is not { } id)
            {
                return Results.Problem(NoAccount, statusCode: StatusCodes.Status400BadRequest);
            }

            links.RemoveForUser(id);
            codes.Cancel(id);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>Whether the caller's sign-in cookie is "stay signed in"; false without a cookie.</summary>
    internal static async Task<bool> CookieIsPersistentAsync(HttpContext http)
    {
        if (http.User.Identity?.AuthenticationType != AuthSchemes.Cookie)
        {
            return false;
        }

        var result = await http.AuthenticateAsync(AuthSchemes.Cookie);
        return result.Properties?.IsPersistent ?? false;
    }

    /// <summary>After a change that ended the caller's own sessions, this browser gets a fresh cookie with the new version.</summary>
    internal static async Task RenewOwnSessionAsync(HttpContext http, UserActor actor, User user, bool persistent)
    {
        if (actor.Id == user.Id && http.User.Identity?.AuthenticationType == AuthSchemes.Cookie)
        {
            await AuthEndpoints.SignInAsync(http, user, persistent);
        }
    }

    private static int? AccountId(HttpContext http) => UserClaims.HasAccount(http.User) ? UserClaims.UserId(http.User) : null;

    private static MeResource Build(UserAccount account, UserResourceBuilder build, DiscordLinkRepository links, IDiscordLinkCodes codes)
    {
        var policy = build.Policy(account);
        var code = codes.Active(account.Id);
        return new MeResource(
            account.Id,
            account.Username,
            account.DisplayName,
            account.Email,
            UserRules.RoleName(account.Role),
            account.CreatedAt,
            account.LastLogin,
            HasAccount: true,
            new PermissionsResource(CanRequest: true, account.CanManageLibrary, account.IsAdmin),
            policy.AutoApprove,
            build.Usage(account, policy),
            UserResourceBuilder.Discord(links.ForUser(account.Id)),
            code is null ? null : new LinkCodeResource(code.Code, code.Expires));
    }

    /// <summary>Local network bypass, External: the owner without account; the API key: an Admin (0.12). Nothing to edit.</summary>
    private static MeResource Ownerless(HttpContext http) => new(
        0,
        http.User.Identity?.Name ?? "owner",
        null,
        null,
        UserRules.RoleName(UserClaims.Role(http.User) ?? UserRole.Admin),
        null,
        null,
        HasAccount: false,
        new PermissionsResource(true, true, true),
        true,
        new UsageResource(new UsageItem(0, null, 0), new UsageItem(0, null, 0)),
        null,
        null);
}

public sealed record ProfileRequest(string? DisplayName, string? Email);

public sealed record OwnPasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record LinkCodeResource(string Code, DateTimeOffset Expires);

public sealed record PermissionsResource(bool CanRequest, bool CanManageLibrary, bool IsAdmin);

/// <param name="HasAccount">False for the owner without account (API key, local network, External): nothing to edit.</param>
/// <param name="AutoApprove">Effective: the user's requests are approved without an admin.</param>
/// <param name="LinkCode">The Discord link code that is still valid, so the page can show it again after a reload.</param>
public sealed record MeResource(
    int Id,
    string Username,
    string? DisplayName,
    string? Email,
    string Role,
    DateTime? CreatedAt,
    DateTime? LastLogin,
    bool HasAccount,
    PermissionsResource Permissions,
    bool AutoApprove,
    UsageResource Usage,
    DiscordResource? Discord,
    LinkCodeResource? LinkCode);
