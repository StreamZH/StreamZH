// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Host.Auth;

public static class AuthSchemes
{
    public const string Cookie = "StreamZH.Cookie";
    public const string ApiKey = "StreamZH.ApiKey";
    public const string Smart = "StreamZH.Smart";

    public const string ApiKeyHeader = "X-Api-Key";
    public const string ApiKeyQuery = "apikey";

    /// <summary>Policy for configuration access: Owner and Admin.</summary>
    public const string AdminPolicy = "Admin";
}