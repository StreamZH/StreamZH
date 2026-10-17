// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using StreamZH.Data;
using StreamZH.Host.Auth;

namespace StreamZH.Host.Api;

public static partial class AuthEndpoints
{
    public const int MinPasswordLength = 8;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapGet("/state", (HttpContext http, UserRepository users, ConfigRepository config) =>
        {
            var setupRequired = users.Count() == 0;
            var current = CurrentUser(http, users);
            return new AuthState(
                setupRequired,
                current is not null,
                current,
                config.Get(ConfigKeys.InstanceName) ?? "StreamZH",
                current is not null && http.User.Identity?.AuthenticationType == AuthSchemes.Cookie);
        });

        group.MapPost("/setup", async (SetupRequest request, HttpContext http, UserRepository users, PasswordService passwords) =>
        {
            var errors = Validate(request.Username, request.Password);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var id = users.CreateOwnerIfNone(request.Username.Trim(), passwords.Hash(request.Password));
            if (id is null)
            {
                return Results.Problem("Setup has already been completed.", statusCode: StatusCodes.Status409Conflict);
            }

            var user = users.FindById(id.Value)!;
            await SignInAsync(http, user, persistent: true);
            return Results.Ok(UserInfo.From(user));
        }).RequireRateLimiting(AuthServiceExtensions.LoginRateLimit);

        group.MapPost("/login", async (LoginRequest request, HttpContext http, PasswordService passwords, ILogger<LoginRequest> logger) =>
        {
            var user = passwords.Verify(request.Username?.Trim() ?? string.Empty, request.Password ?? string.Empty);
            if (user is null)
            {
                logger.LogWarning("Failed login for '{Username}' from {Address}", request.Username, http.Connection.RemoteIpAddress);
                return Results.Problem("Username or password is wrong.", statusCode: StatusCodes.Status401Unauthorized);
            }

            await SignInAsync(http, user, request.RememberMe);
            logger.LogInformation("User '{Username}' logged in", user.Username);
            return Results.Ok(UserInfo.From(user));
        }).RequireRateLimiting(AuthServiceExtensions.LoginRateLimit);

        group.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AuthSchemes.Cookie);
            return Results.NoContent();
        });

        return app;
    }

    private static UserInfo? CurrentUser(HttpContext http, UserRepository users)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // API key, local network without sign-in, External authentication: the owner without an account.
        if (http.User.Identity.AuthenticationType is AuthSchemes.ApiKey or AuthSchemes.Local or AuthSchemes.External)
        {
            return new UserInfo(0, http.User.Identity.Name ?? "api", nameof(Core.Users.UserRole.Owner));
        }

        // The account may have been removed since the cookie was issued.
        return int.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && users.FindById(id) is { } user
            ? UserInfo.From(user)
            : null;
    }

    private static Task SignInAsync(HttpContext http, User user, bool persistent)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
            ],
            AuthSchemes.Cookie);

        return http.SignInAsync(AuthSchemes.Cookie, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = persistent });
    }

    private static Dictionary<string, string[]> Validate(string? username, string? password)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(username) || !UsernameRegex().IsMatch(username.Trim()))
        {
            errors["username"] = ["Use 3 to 50 characters: letters, numbers, dot, dash or underscore."];
        }

        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            errors["password"] = [$"Use at least {MinPasswordLength} characters."];
        }

        return errors;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,50}$")]
    private static partial Regex UsernameRegex();
}

public sealed record SetupRequest(string Username, string Password);

public sealed record LoginRequest(string? Username, string? Password, bool RememberMe);

public sealed record UserInfo(int Id, string Username, string Role)
{
    public static UserInfo From(User user) => new(user.Id, user.Username, user.Role.ToString());
}

/// <param name="CanSignOut">False for sessions without a sign-in cookie (Basic, External, local network, API key).</param>
public sealed record AuthState(bool SetupRequired, bool Authenticated, UserInfo? User, string InstanceName, bool CanSignOut = false);