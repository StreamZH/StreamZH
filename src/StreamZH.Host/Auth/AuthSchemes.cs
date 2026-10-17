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
}