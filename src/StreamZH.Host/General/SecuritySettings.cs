// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Data;

namespace StreamZH.Host.General;

/// <summary>How people sign in (Radarr's "Authentication").</summary>
public enum AuthenticationMethod
{
    /// <summary>StreamZH's sign-in page (default).</summary>
    Forms,

    /// <summary>The browser's own login dialog (HTTP Basic), with the same accounts.</summary>
    Basic,

    /// <summary>No sign-in in StreamZH: a reverse proxy in front of it (e.g. CloudZH's single sign-on) asks for the login.</summary>
    External,
}

/// <summary>Radarr's "Authentication Required".</summary>
public enum AuthenticationRequired
{
    Enabled,

    /// <summary>Requests from the local network (and this computer) need no sign-in.</summary>
    DisabledForLocalAddresses,
}

/// <summary>Checks of HTTPS certificates on outgoing connections (indexers, TMDB, updates).</summary>
public enum CertificateValidation
{
    Enabled,
    DisabledForLocalAddresses,
    Disabled,
}

/// <summary>
/// Settings → General → Security. The command-line switch <c>--auth-method forms</c> overrides the stored method,
/// a way back in when a wrong choice locks everyone out.
/// </summary>
public sealed class SecuritySettings(SettingsCache cache, IConfiguration configuration)
{
    public const string OverrideKey = "auth-method";

    public AuthenticationMethod StoredMethod => cache.GetEnum(ConfigKeys.AuthenticationMethod, AuthenticationMethod.Forms);

    /// <summary>From the command line (<c>--auth-method</c>), or null.</summary>
    public AuthenticationMethod? MethodOverride =>
        Enum.TryParse<AuthenticationMethod>(configuration[OverrideKey], ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : null;

    public AuthenticationMethod Method => MethodOverride ?? StoredMethod;

    public AuthenticationRequired Required => cache.GetEnum(ConfigKeys.AuthenticationRequired, AuthenticationRequired.Enabled);

    public CertificateValidation CertificateValidation => cache.GetEnum(ConfigKeys.CertificateValidation, CertificateValidation.Enabled);

    public void Update(AuthenticationMethod? method, AuthenticationRequired? required, CertificateValidation? validation)
    {
        if (method is { } m)
        {
            cache.Set(ConfigKeys.AuthenticationMethod, m.ToString());
        }

        if (required is { } r)
        {
            cache.Set(ConfigKeys.AuthenticationRequired, r.ToString());
        }

        if (validation is { } v)
        {
            cache.Set(ConfigKeys.CertificateValidation, v.ToString());
        }
    }
}
