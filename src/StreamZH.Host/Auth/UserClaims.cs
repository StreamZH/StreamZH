// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Security.Claims;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Host.Auth;

/// <summary>The claims of a signed-in account: id, name, role and (0.10) the session version the cookie was issued for.</summary>
public static class UserClaims
{
    public const string SessionVersion = "streamzh:sv";

    /// <summary>0.12: the server-side session of a sign-in cookie (see <see cref="SessionStore"/>).</summary>
    public const string SessionId = "streamzh:sid";

    public static ClaimsPrincipal Principal(User user, string scheme, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Principal(user.Id, user.Username, user.Role, user.SessionVersion, scheme, sessionId);
    }

    public static ClaimsPrincipal Principal(int id, string username, UserRole role, int sessionVersion, string scheme, string? sessionId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, role.ToString()),
            new(SessionVersion, sessionVersion.ToString(CultureInfo.InvariantCulture)),
        };
        if (sessionId is not null)
        {
            claims.Add(new Claim(SessionId, sessionId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    /// <summary>The session id of a sign-in cookie; null for cookies from before 0.12 and other sign-ins.</summary>
    public static string? Session(ClaimsPrincipal principal) => principal.FindFirstValue(SessionId) is { Length: > 0 } id ? id : null;

    /// <summary>The account id; 0 for the API key, the local network bypass and External authentication (owner without account).</summary>
    public static int UserId(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    /// <summary>The role of the principal; null when it has none we know.</summary>
    public static UserRole? Role(ClaimsPrincipal principal) =>
        Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var role) && Enum.IsDefined(role) ? role : null;

    /// <summary>Cookies from before 0.10 carry no version: they count as version 0, the starting value of every account.</summary>
    public static int Version(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(SessionVersion), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : 0;

    /// <summary>A real account (cookie or Basic sign-in), not the API key, local bypass or External.</summary>
    public static bool HasAccount(ClaimsPrincipal principal) =>
        principal.Identity?.AuthenticationType is AuthSchemes.Cookie or AuthSchemes.Basic && UserId(principal) > 0;
}
