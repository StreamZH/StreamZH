// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using StreamZH.Host.Security;

namespace StreamZH.Host.Auth;

/// <summary>
/// HTTP Basic authentication with the StreamZH accounts (Settings → General → Authentication "Basic"). The browser sends
/// the credentials with every request, so a successful check is remembered for a few minutes (password hashing is slow
/// on purpose). 0.12 (security review S1-05): failed credentials are remembered for <see cref="BasicCredentialCache.FailedLifetime"/>
/// (the browser repeats them with every request; they are not hashed and counted again), and failures count towards the
/// per-account and per-client limits of <see cref="LoginThrottle"/>; over the limit the request is answered with 429.
/// </summary>
public sealed class BasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PasswordService passwords,
    BasicCredentialCache cache,
    UserSessionCache sessions,
    StreamZH.Data.UserRepository users,
    LoginThrottle throttle,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Realm = "StreamZH";

    /// <summary>Set in HttpContext.Items when the sign-in was refused because of too many failures (answered with 429).</summary>
    public const string ThrottledKey = "streamzh:basic-throttled";

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
        // A remembered sign-in still has to match the account (disabled, role changed, sessions ended: check again).
        if (cache.TryGet(key, now) is { } remembered)
        {
            if (sessions.Check(remembered, out _) == SessionCheck.Valid)
            {
                return Task.FromResult(Success(remembered));
            }

            cache.Forget(key);
        }

        // The same wrong credentials again (the browser resends them with every request): no new check, no new count.
        if (cache.IsKnownBad(key, now))
        {
            return Task.FromResult(AuthenticateResult.Fail("Username or password is wrong."));
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
        var client = ClientAddress.Partition(Context);
        if (throttle.RetryAfter(username, client) is { } wait)
        {
            Context.Items[ThrottledKey] = wait;
            return Task.FromResult(AuthenticateResult.Fail("Too many failed sign-ins. Please wait."));
        }

        var user = passwords.Verify(username.Trim(), password);
        if (user is null)
        {
            Logger.LogWarning("Failed login for '{Username}' from {Address} (Basic)", username, Context.Connection.RemoteIpAddress);
            throttle.Failed(username, client);
            cache.RememberBad(key, now);
            return Task.FromResult(AuthenticateResult.Fail("Username or password is wrong."));
        }

        throttle.Succeeded(username);

        var principal = UserClaims.Principal(user, Scheme.Name);
        cache.Remember(key, principal, now);
        users.SetLastLogin(user.Id, now.UtcDateTime);
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

    /// <summary>How long wrong credentials are answered from memory.</summary>
    public static readonly TimeSpan FailedLifetime = TimeSpan.FromSeconds(60);

    private const int MaxEntries = 100;
    private const int MaxBad = 1000;

    private readonly ConcurrentDictionary<string, (ClaimsPrincipal Principal, DateTimeOffset Until)> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _bad = new(StringComparer.Ordinal);

    public bool IsKnownBad(string key, DateTimeOffset now)
    {
        if (_bad.TryGetValue(key, out var until))
        {
            if (until > now)
            {
                return true;
            }

            _bad.TryRemove(key, out _);
        }

        return false;
    }

    public void RememberBad(string key, DateTimeOffset now)
    {
        if (_bad.Count >= MaxBad)
        {
            _bad.Clear();
        }

        _bad[key] = now + FailedLifetime;
    }

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

    public void Forget(string key) => _entries.TryRemove(key, out _);

    public void Clear()
    {
        _entries.Clear();
        _bad.Clear();
    }
}
