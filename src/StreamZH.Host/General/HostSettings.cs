// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using StreamZH.Data;

namespace StreamZH.Host.General;

/// <summary>The values that decide where StreamZH listens. Read once at startup; a change needs a restart.</summary>
public sealed record HostValues(string BindAddress, int Port, string UrlBase, bool EnableSsl, int SslPort, string? SslCertPath, string? SslCertPassword);

/// <summary>Settings → General → Host (names like Radarr): bind address, port, URL base, instance name, application URL, SSL.</summary>
public sealed partial class HostSettings(SettingsCache cache)
{
    public const string AllAddresses = "*";
    public const int DefaultPort = 9797;
    public const int DefaultSslPort = 9798;
    public const string DefaultInstanceName = "StreamZH";

    /// <summary>First path segments the URL base must not take, they are StreamZH's own routes.</summary>
    private static readonly string[] ReservedSegments = ["api", "hub", "health", "assets"];

    public string BindAddress => cache.Get(ConfigKeys.BindAddress) ?? AllAddresses;

    public int Port => cache.GetInt(ConfigKeys.Port, DefaultPort);

    /// <summary>"" or "/streamzh" (leading slash, no trailing slash).</summary>
    public string UrlBase => NormalizeUrlBase(cache.Get(ConfigKeys.UrlBase));

    public string InstanceName => cache.Get(ConfigKeys.InstanceName) ?? DefaultInstanceName;

    /// <summary>The address people use to open StreamZH from outside, for links in notifications. Null = not set.</summary>
    public string? ApplicationUrl => cache.Get(ConfigKeys.ApplicationUrl);

    public bool EnableSsl => cache.GetBool(ConfigKeys.EnableSsl, false);

    public int SslPort => cache.GetInt(ConfigKeys.SslPort, DefaultSslPort);

    public string? SslCertPath => cache.Get(ConfigKeys.SslCertPath);

    public string? SslCertPassword => cache.Get(ConfigKeys.SslCertPassword);

    public HostValues Current => new(BindAddress, Port, UrlBase, EnableSsl, SslPort, SslCertPath, SslCertPassword);

    public static string NormalizeUrlBase(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().Trim('/');
        return trimmed.Length == 0 ? string.Empty : "/" + trimmed;
    }

    /// <summary>Null when valid. Accepts "streamzh", "/streamzh", "/media/streamzh/".</summary>
    public static string? ValidateUrlBase(string? value)
    {
        var normalized = NormalizeUrlBase(value);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > 100 || !UrlBaseRegex().IsMatch(normalized) || normalized.Split('/').Any(s => s is "." or ".."))
        {
            return "Use letters, numbers, dashes, dots and slashes, e.g. /streamzh.";
        }

        var first = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
        return ReservedSegments.Contains(first, StringComparer.OrdinalIgnoreCase) ? $"\"/{first}\" is used by StreamZH itself; choose another name." : null;
    }

    /// <summary>Null when valid: "*" or an IP address.</summary>
    public static string? ValidateBindAddress(string? value)
    {
        var address = value?.Trim();
        return address == AllAddresses || IPAddress.TryParse(address, out _) ? null : "Use * for all addresses or one IP address of this computer.";
    }

    public static string? ValidateApplicationUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Query.Length == 0
            ? null
            : "Use a full address like https://streamzh.example.com.";
    }

    /// <summary>Loads the PFX certificate; null and a problem text when it cannot be used.</summary>
    public static X509Certificate2? LoadCertificate(string? path, string? password, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            problem = "Choose the certificate file (.pfx).";
            return null;
        }

        if (!File.Exists(path))
        {
            problem = "The certificate file does not exist.";
            return null;
        }

        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                problem = "The certificate file has no private key.";
                return null;
            }

            return certificate;
        }
        catch (CryptographicException)
        {
            problem = "The certificate cannot be read: wrong password or not a .pfx file.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"The certificate cannot be read: {ex.Message}";
            return null;
        }
    }

    [GeneratedRegex("^(/[A-Za-z0-9._~-]+)+$")]
    private static partial Regex UrlBaseRegex();
}
