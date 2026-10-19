// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Host.Auth;

public static class AuthSchemes
{
    public const string Cookie = "StreamZH.Cookie";
    public const string ApiKey = "StreamZH.ApiKey";
    public const string Smart = "StreamZH.Smart";
    public const string Basic = "StreamZH.Basic";

    /// <summary>Identity type of a request let in without sign-in from the local network.</summary>
    public const string Local = "StreamZH.Local";

    /// <summary>Identity type of every request while authentication is External (done by a reverse proxy).</summary>
    public const string External = "StreamZH.External";

    public const string ApiKeyHeader = "X-Api-Key";
    public const string ApiKeyQuery = "apikey";

    /// <summary>Policy for configuration access: Owner and Admin.</summary>
    public const string AdminPolicy = "Admin";

    /// <summary>
    /// 0.12: only the owner. Everything that would let an Admin (or the API key, which acts as an Admin) become the owner
    /// or run programs on the host: security and proxy settings, the API key itself, backups, tool paths and arguments.
    /// The local network bypass and External authentication count as the owner (they are the owner's own access).
    /// </summary>
    public const string OwnerPolicy = "Owner";

    /// <summary>
    /// 0.10: every signed-in user incl. Requester (discover, requests, own profile). The default policy
    /// (plain <c>RequireAuthorization()</c>) admits only Owner, Admin and Friend, so a Requester never reaches the
    /// library, downloads or anything else unless an endpoint opts in with this policy.
    /// </summary>
    public const string AnyUserPolicy = "AnyUser";
}