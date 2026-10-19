// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using StreamZH.Core.Users;
using StreamZH.Host.General;
using StreamZH.Host.Security;

namespace StreamZH.Host.Auth;

/// <summary>
/// The default scheme: the API key when one is sent, else the sign-in of the chosen method (Forms = cookie, Basic =
/// browser dialog, External = none, the reverse proxy did it). With "Authentication required: Disabled for local
/// addresses" a request from the local network without a session counts as the owner.
/// 0.12 (security review S1-01/S1-06): the sign-in-free access (local addresses, External) needs a Host name an attacker
/// cannot point at StreamZH (<see cref="HostAccess"/>); External only from the trusted proxies (or, none configured, local
/// addresses) and, when configured, with the user header; local addresses are the "Trusted local networks". Requests
/// that do not qualify get the normal sign-in (the owner can always sign in with the account created at setup).
/// </summary>
public sealed class SmartAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SecuritySettings security,
    AccessSettings access,
    HostAccess hosts)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    // One warning per refused host name, not one per request.
    private static readonly ConcurrentDictionary<string, byte> WarnedHosts = new(StringComparer.Ordinal);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (ApiKeyAuthenticationHandler.Provided(Request) is not null)
        {
            return await Context.AuthenticateAsync(AuthSchemes.ApiKey);
        }

        var method = security.Method;
        if (method == AuthenticationMethod.External && ExternalUser() is { } externalUser)
        {
            return Bypass(AuthSchemes.External, externalUser);
        }

        var result = await Context.AuthenticateAsync(method == AuthenticationMethod.Basic ? AuthSchemes.Basic : AuthSchemes.Cookie);
        if (result.Succeeded)
        {
            return result;
        }

        if (method != AuthenticationMethod.External
            && security.Required == AuthenticationRequired.DisabledForLocalAddresses
            && LocalAddress.IsLocalRequest(Context, access.IsTrustedLocal)
            && HostAllowed())
        {
            return Bypass(AuthSchemes.Local, "local");
        }

        return result;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Context.ChallengeAsync(security.Method == AuthenticationMethod.Basic ? AuthSchemes.Basic : AuthSchemes.Cookie, properties);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => Context.ForbidAsync(AuthSchemes.Cookie, properties);

    /// <summary>The name for an External sign-in, or null when this request does not qualify for it.</summary>
    private string? ExternalUser()
    {
        var peer = ProxyForwarding.Peer(Context);
        if (!access.IsExternalSource(peer))
        {
            Logger.LogDebug("External authentication: {Address} is not a trusted proxy", peer);
            return null;
        }

        string? name = null;
        if (access.ExternalUserHeader is { } header)
        {
            name = Request.Headers[header].FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                Logger.LogDebug("External authentication: header {Header} missing", header);
                return null;
            }
        }

        if (!HostAllowed())
        {
            return null;
        }

        return name is { Length: > 0 and <= 100 } ? name : "external";
    }

    private bool HostAllowed()
    {
        if (hosts.IsAllowed(Context))
        {
            return true;
        }

        var host = HostAccess.Normalize(Request.Host.Host);
        if (WarnedHosts.Count < 100 && WarnedHosts.TryAdd(host, 0))
        {
            Logger.LogWarning(
                "Requests for host name '{Host}' get no sign-in-free access (DNS rebinding protection). If this is your address for StreamZH, add it under Settings → General → Security → Allowed host names or set it as application URL",
                host);
        }

        return false;
    }

    /// <summary>
    /// The owner without an account (id 0). The local network bypass and External authentication stay the owner: they are
    /// the owner's own way in (no account to pick a role from), and turning them on is itself owner-only.
    /// </summary>
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
