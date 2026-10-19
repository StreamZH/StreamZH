// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using StreamZH.Core;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.General;
using StreamZH.Host.Security;
using StreamZH.Host.Service;

namespace StreamZH.Host.Auth;

public static class AuthServiceExtensions
{
    public const string LoginRateLimit = "login";

    /// <summary>0.12: first-run setup (setup code attempts), invitation accept and own password change have their own buckets.</summary>
    public const string SetupRateLimit = "setup";
    public const string InviteRateLimit = "invite";
    public const string PasswordRateLimit = "password";

    /// <summary>A sign-in without "Remember me": the cookie ends with the browser session or after this time without use.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    /// <summary>"Remember me": 30 days, renewed while StreamZH is used.</summary>
    public static readonly TimeSpan RememberLifetime = TimeSpan.FromDays(30);

    public static IServiceCollection AddStreamZhAuth(this IServiceCollection services, AppPaths paths)
    {
        // Persist keys so login cookies survive restarts and updates.
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("StreamZH")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.DataDirectory, "keys")));

        // Windows: new keys are encrypted with DPAPI (for the computer when StreamZH is a service, so a switch between
        // service accounts keeps them readable; for the user in a console window). Older unencrypted keys stay readable.
        // Linux, macOS and Docker have no DPAPI: the key files are 0600 in a private folder (DataFolderPermissions).
        if (OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: RunningAs.IsWindowsService);
        }

        // Smart picks the API key, the cookie (Forms) or Basic per request; see SmartAuthenticationHandler.
        services.AddSingleton<BasicCredentialCache>();
        services.AddSingleton<UserSessionCache>();
        services.AddSingleton<SessionRepository>();
        services.AddSingleton<SessionStore>();
        services.AddStreamZhSecurity();
        services.AddAuthentication(AuthSchemes.Smart)
            .AddScheme<AuthenticationSchemeOptions, SmartAuthenticationHandler>(AuthSchemes.Smart, null)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, null)
            .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(AuthSchemes.Basic, null)
            .AddCookie(AuthSchemes.Cookie, options =>
            {
                options.Cookie.Name = "StreamZH.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                // Secure when the request is HTTPS, also behind a trusted reverse proxy that says so (X-Forwarded-Proto,
                // see ProxyForwarding). The path is the URL base (set below).
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = RememberLifetime;
                options.SlidingExpiration = true;

                // 0.10: the account must still exist, be enabled and have the same session version and role.
                options.Events.OnValidatePrincipal = UserSessionCache.ValidateCookieAsync;

                // The API answers with status codes, the React app shows the login page.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        // 0.12 (S1-10): the cookie belongs to StreamZH's URL base only, not to other apps on the same host.
        services.AddOptions<CookieAuthenticationOptions>(AuthSchemes.Cookie).Configure<RunningHost>((options, running) =>
            options.Cookie.Path = running.Values.UrlBase.Length > 0 ? running.Values.UrlBase : "/");

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthSchemes.AdminPolicy, policy => policy.RequireRole(nameof(UserRole.Owner), nameof(UserRole.Admin)))
            .AddPolicy(AuthSchemes.OwnerPolicy, policy => policy.RequireRole(nameof(UserRole.Owner)))
            .AddPolicy(AuthSchemes.AnyUserPolicy, policy => policy.RequireAuthenticatedUser())
            // Plain RequireAuthorization(): members only. Requesters (0.10) are kept out of everything that does not
            // opt in with AnyUserPolicy.
            .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(nameof(UserRole.Owner), nameof(UserRole.Admin), nameof(UserRole.Friend))
                .Build());

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };

            // Per client (IPv6 per /64; behind a trusted proxy the forwarded client, see ProxyForwarding), one bucket each.
            foreach (var name in new[] { LoginRateLimit, SetupRateLimit, InviteRateLimit, PasswordRateLimit })
            {
                options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
                    ClientAddress.Partition(context),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
            }
        });

        return services;
    }

    /// <summary>
    /// With Basic authentication every request without valid credentials gets the browser's login dialog, the web app
    /// itself too (like Radarr). Before the first account exists, the setup page stays reachable.
    /// </summary>
    public static IApplicationBuilder UseBasicAuthenticationChallenge(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            // Too many failed Basic sign-ins from this client or for this account (LoginThrottle).
            if (context.User.Identity?.IsAuthenticated != true && context.Items[BasicAuthenticationHandler.ThrottledKey] is TimeSpan wait)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return;
            }

            if (context.User.Identity?.IsAuthenticated != true
                && !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
                // The tray icon signs in with its own token on its endpoints (StreamZH.Host.Tray).
                && !context.Request.Path.StartsWithSegments(StreamZH.Core.Tray.TrayContract.ApiPath, StringComparison.OrdinalIgnoreCase)
                && context.RequestServices.GetRequiredService<SecuritySettings>().Method == AuthenticationMethod.Basic
                && context.RequestServices.GetRequiredService<UserRepository>().Count() > 0)
            {
                await context.ChallengeAsync(AuthSchemes.Basic);
                return;
            }

            await next(context);
        });
    }
}