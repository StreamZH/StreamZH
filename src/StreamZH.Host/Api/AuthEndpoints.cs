// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Hubs;
using StreamZH.Host.Security;
using StreamZH.Host.Users;

namespace StreamZH.Host.Api;

public static class AuthEndpoints
{
    public const int MinPasswordLength = UserRules.MinPasswordLength;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapGet("/state", (HttpContext http, UserRepository users, ConfigRepository config, SetupCode setupCode) =>
        {
            var setupRequired = users.Count() == 0;
            var current = CurrentUser(http, users);
            return new AuthState(
                setupRequired,
                current is not null,
                current,
                config.Get(ConfigKeys.InstanceName) ?? "StreamZH",
                current is not null && http.User.Identity?.AuthenticationType == AuthSchemes.Cookie,
                setupRequired && setupCode.IsRequired(http));
        });

        // 0.12 (S1-14/S3-01): from another device the one-time setup code is needed (console, log, <data>/setup-code, tray).
        group.MapPost("/setup", async (SetupRequest request, HttpContext http, UserRepository users, PasswordService passwords, SetupCode setupCode, ILogger<SetupRequest> logger) =>
        {
            if (users.Count() > 0)
            {
                return Results.Problem("Setup has already been completed.", statusCode: StatusCodes.Status409Conflict);
            }

            if (setupCode.IsRequired(http) && !setupCode.Verify(request.SetupCode))
            {
                logger.LogWarning("First-run setup from {Address} refused: wrong or missing setup code", http.Connection.RemoteIpAddress);
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["setupCode"] = [string.IsNullOrWhiteSpace(request.SetupCode)
                        ? "Enter the setup code. StreamZH shows it in its console and log at start."
                        : "The setup code is wrong."],
                });
            }

            var errors = UserRules.ValidateCredentials(request.Username, request.Password);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var id = users.CreateOwnerIfNone(request.Username.Trim(), passwords.Hash(request.Password));
            if (id is null)
            {
                return Results.Problem("Setup has already been completed.", statusCode: StatusCodes.Status409Conflict);
            }

            setupCode.Complete();
            var user = users.FindById(id.Value)!;
            await SignInAsync(http, user, persistent: true);
            users.SetLastLogin(user.Id, DateTime.UtcNow);
            logger.LogInformation("Owner account '{Username}' created from {Address}", user.Username, http.Connection.RemoteIpAddress);
            return Results.Ok(UserInfo.From(user));
        }).RequireRateLimiting(AuthServiceExtensions.SetupRateLimit);

        group.MapPost("/login", async (LoginRequest request, HttpContext http, PasswordService passwords, UserRepository users, LoginThrottle throttle, ILogger<LoginRequest> logger) =>
        {
            // 0.12 (S1-09): too many failures for this account or from this client: wait, without checking the password.
            var client = ClientAddress.Partition(http);
            if (throttle.RetryAfter(request.Username, client) is { } wait)
            {
                http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Results.Problem("Too many failed sign-ins. Please wait a few minutes.", statusCode: StatusCodes.Status429TooManyRequests);
            }

            // Disabled accounts get the same answer as a wrong password.
            var user = passwords.Verify(request.Username?.Trim() ?? string.Empty, request.Password ?? string.Empty);
            if (user is null)
            {
                logger.LogWarning("Failed login for '{Username}' from {Address}", request.Username, http.Connection.RemoteIpAddress);
                throttle.Failed(request.Username, client);
                return Results.Problem("Username or password is wrong.", statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.Succeeded(request.Username);
            await SignInAsync(http, user, request.RememberMe);
            users.SetLastLogin(user.Id, DateTime.UtcNow);
            logger.LogInformation("User '{Username}' logged in", user.Username);
            return Results.Ok(UserInfo.From(user));
        }).RequireRateLimiting(AuthServiceExtensions.LoginRateLimit);

        // 0.12 (S1-10): ends the session on the server too, so a copy of the cookie stops working.
        group.MapPost("/logout", async (HttpContext http, SessionStore sessions, HubConnections connections) =>
        {
            if (http.User.Identity?.AuthenticationType == AuthSchemes.Cookie && UserClaims.Session(http.User) is { } sessionId)
            {
                sessions.End(sessionId);
                connections.Revalidate(UserClaims.UserId(http.User));
            }

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

    /// <summary>
    /// Issues the sign-in cookie with the account's current session version and a new server-side session. Without
    /// "Remember me" the cookie is a browser-session cookie and ends after <see cref="AuthServiceExtensions.SessionLifetime"/>
    /// without use; with it, it lasts 30 days, renewed while used. A session this browser had before ends.
    /// </summary>
    internal static Task SignInAsync(HttpContext http, User user, bool persistent)
    {
        var sessions = http.RequestServices.GetRequiredService<SessionStore>();
        if (http.User.Identity?.AuthenticationType == AuthSchemes.Cookie && UserClaims.Session(http.User) is { } previous)
        {
            sessions.End(previous);
        }

        var sessionId = sessions.Create(user.Id, persistent);
        var properties = new AuthenticationProperties { IsPersistent = persistent };
        if (!persistent)
        {
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            properties.IssuedUtc = now;
            properties.ExpiresUtc = now + AuthServiceExtensions.SessionLifetime;
        }

        return http.SignInAsync(AuthSchemes.Cookie, UserClaims.Principal(user, AuthSchemes.Cookie, sessionId), properties);
    }
}

/// <param name="SetupCode">0.12: the one-time setup code; needed only when setup is done from another device.</param>
public sealed record SetupRequest(string Username, string Password, string? SetupCode = null);

public sealed record LoginRequest(string? Username, string? Password, bool RememberMe);

public sealed record UserInfo(int Id, string Username, string Role)
{
    public static UserInfo From(User user) => new(user.Id, user.Username, user.Role.ToString());
}

/// <param name="CanSignOut">False for sessions without a sign-in cookie (Basic, External, local network, API key).</param>
/// <param name="SetupCodeRequired">0.12: the setup page must ask for the setup code (setup from another device).</param>
public sealed record AuthState(bool SetupRequired, bool Authenticated, UserInfo? User, string InstanceName, bool CanSignOut = false, bool SetupCodeRequired = false);