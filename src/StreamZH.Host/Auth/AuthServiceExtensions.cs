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

namespace StreamZH.Host.Auth;

public static class AuthServiceExtensions
{
    public const string LoginRateLimit = "login";

    public static IServiceCollection AddStreamZhAuth(this IServiceCollection services, AppPaths paths)
    {
        // Persist keys so login cookies survive restarts and updates.
        services.AddDataProtection()
            .SetApplicationName("StreamZH")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.DataDirectory, "keys")));

        // Smart picks the API key, the cookie (Forms) or Basic per request; see SmartAuthenticationHandler.
        services.AddSingleton<BasicCredentialCache>();
        services.AddAuthentication(AuthSchemes.Smart)
            .AddScheme<AuthenticationSchemeOptions, SmartAuthenticationHandler>(AuthSchemes.Smart, null)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, null)
            .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(AuthSchemes.Basic, null)
            .AddCookie(AuthSchemes.Cookie, options =>
            {
                options.Cookie.Name = "StreamZH.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;

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

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthSchemes.AdminPolicy, policy => policy.RequireRole(nameof(UserRole.Owner), nameof(UserRole.Admin)));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
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
            if (context.User.Identity?.IsAuthenticated != true
                && !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
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