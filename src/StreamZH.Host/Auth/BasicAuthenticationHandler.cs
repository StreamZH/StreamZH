// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace StreamZH.Host.Auth;

/// <summary>
/// HTTP Basic authentication with the StreamZH accounts (Settings → General → Authentication "Basic"). The browser sends
/// the credentials with every request, so a successful check is remembered for a few minutes (password hashing is slow
/// on purpose).
/// </summary>
public sealed class BasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PasswordService passwords,
    BasicCredentialCache cache,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Realm = "StreamZH";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(header) || !AuthenticationHeaderValue.TryParse(header, out var value)
            || !string.Equals(value.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(value.Parameter))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Parameter)));
        var now = time.GetUtcNow();
        if (cache.TryGet(key, now) is { } remembered)
        {
            return Task.FromResult(Success(remembered));
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value.Parameter));
        }
        catch (FormatException)
        {
            return Task.FromResult(AuthenticateResult.Fail("Malformed Basic credentials."));
        }

        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        var username = colon < 0 ? decoded : decoded[..colon];
        var password = colon < 0 ? string.Empty : decoded[(colon + 1)..];
        var user = passwords.Verify(username.Trim(), password);
        if (user is null)
        {
            Logger.LogWarning("Failed login for '{Username}' from {Address} (Basic)", username, Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Username or password is wrong."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
            ],
            Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        cache.Remember(key, principal, now);
        return Task.FromResult(Success(principal));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }

    private AuthenticateResult Success(ClaimsPrincipal principal) => AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
}

/// <summary>Remembers verified Basic credentials (by hash) for <see cref="Lifetime"/>; cleared when accounts change.</summary>
public sealed class BasicCredentialCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxEntries = 100;

    private readonly ConcurrentDictionary<string, (ClaimsPrincipal Principal, DateTimeOffset Until)> _entries = new(StringComparer.Ordinal);

    public ClaimsPrincipal? TryGet(string key, DateTimeOffset now)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.Until > now)
            {
                return entry.Principal;
            }

            _entries.TryRemove(key, out _);
        }

        return null;
    }

    public void Remember(string key, ClaimsPrincipal principal, DateTimeOffset now)
    {
        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }

        _entries[key] = (principal, now + Lifetime);
    }

    public void Clear() => _entries.Clear();
}
