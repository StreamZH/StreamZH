// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Host.Auth;

/// <summary>
/// Authenticates scripts and CloudZH via the X-Api-Key header. 0.12 (security review S1-04): ?apikey= in the address only
/// for the live connection (/hub), where WebSocket clients cannot send headers; elsewhere a key in the address would end
/// up in browser histories, proxy logs and shared links (the iCal feed has its own read-only token).
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ConfigRepository config)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var provided = Provided(Request);

        if (string.IsNullOrEmpty(provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var expected = config.Get(ConfigKeys.ApiKey);
        if (expected is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "0"),
                new Claim(ClaimTypes.Name, "api"),
                new Claim(ClaimTypes.Role, nameof(UserRole.Owner)),
            ],
            Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    /// <summary>The key sent with the request: the header, or ?apikey= on /hub only; null when none.</summary>
    public static string? Provided(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers[AuthSchemes.ApiKeyHeader].FirstOrDefault() is { Length: > 0 } header)
        {
            return header;
        }

        return request.Path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase)
            && request.Query[AuthSchemes.ApiKeyQuery].FirstOrDefault() is { Length: > 0 } query
                ? query
                : null;
    }
}