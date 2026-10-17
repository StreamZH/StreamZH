// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using StreamZH.Core.Users;
using StreamZH.Host.General;

namespace StreamZH.Host.Auth;

/// <summary>
/// The default scheme: the API key when one is sent, else the sign-in of the chosen method (Forms = cookie, Basic =
/// browser dialog, External = none, the reverse proxy did it). With "Authentication required: Disabled for local
/// addresses" a request from the local network without a session counts as the owner.
/// </summary>
public sealed class SmartAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SecuritySettings security)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey(AuthSchemes.ApiKeyHeader) || Request.Query.ContainsKey(AuthSchemes.ApiKeyQuery))
        {
            return await Context.AuthenticateAsync(AuthSchemes.ApiKey);
        }

        var method = security.Method;
        if (method == AuthenticationMethod.External)
        {
            return Bypass(AuthSchemes.External, "external");
        }

        var result = await Context.AuthenticateAsync(method == AuthenticationMethod.Basic ? AuthSchemes.Basic : AuthSchemes.Cookie);
        if (result.Succeeded)
        {
            return result;
        }

        if (security.Required == AuthenticationRequired.DisabledForLocalAddresses && LocalAddress.IsLocalRequest(Context))
        {
            return Bypass(AuthSchemes.Local, "local");
        }

        return result;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Context.ChallengeAsync(security.Method == AuthenticationMethod.Basic ? AuthSchemes.Basic : AuthSchemes.Cookie, properties);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => Context.ForbidAsync(AuthSchemes.Cookie, properties);

    /// <summary>The owner without an account (id 0), like the API key.</summary>
    private static AuthenticateResult Bypass(string scheme, string name)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "0"),
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, nameof(UserRole.Owner)),
            ],
            scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), scheme));
    }
}
