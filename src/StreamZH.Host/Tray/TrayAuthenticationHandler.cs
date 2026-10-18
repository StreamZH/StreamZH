// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using StreamZH.Core.Tray;

namespace StreamZH.Host.Tray;

/// <summary>
/// The tray token (header X-StreamZH-Tray). Only used by the tray endpoints (their policy names this scheme; the default
/// scheme never looks at the header), and only from this computer: loopback or the connection's own address, without
/// forwarding headers (a reverse proxy on this computer makes every request look local).
/// </summary>
public sealed class TrayAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TrayAccess access)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "StreamZH.Tray";
    public const string Role = "Tray";

    private static readonly string[] ForwardedHeaders = ["X-Forwarded-For", "X-Real-IP", "Forwarded", "X-Forwarded-Host"];

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var provided = Request.Headers[TrayContract.Header].FirstOrDefault();
        if (string.IsNullOrEmpty(provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!IsThisComputer(Context))
        {
            Logger.LogWarning("Tray token from {Address} refused: only accepted from this computer", Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("The tray token is only accepted from this computer."));
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(access.Token)))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid tray token."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "0"),
                new Claim(ClaimTypes.Name, "tray"),
                new Claim(ClaimTypes.Role, Role),
            ],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>Loopback, or a connection to this computer's own address from that same address; no forwarding headers.</summary>
    public static bool IsThisComputer(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var remote = Normalize(context.Connection.RemoteIpAddress);
        if (remote is null)
        {
            return false;
        }

        if (ForwardedHeaders.Any(h => context.Request.Headers.ContainsKey(h)))
        {
            return false;
        }

        return IPAddress.IsLoopback(remote) || remote.Equals(Normalize(context.Connection.LocalIpAddress));
    }

    private static IPAddress? Normalize(IPAddress? address) => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
}
