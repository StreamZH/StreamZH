// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using StreamZH.Core;
using StreamZH.Core.Users;

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

        services.AddAuthentication(AuthSchemes.Smart)
            .AddPolicyScheme(AuthSchemes.Smart, "Cookie or API key", options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(AuthSchemes.ApiKeyHeader) || context.Request.Query.ContainsKey(AuthSchemes.ApiKeyQuery)
                        ? AuthSchemes.ApiKey
                        : AuthSchemes.Cookie;
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, null)
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
}