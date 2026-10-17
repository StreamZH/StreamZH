// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using StreamZH.Data;

namespace StreamZH.Host.General;

public enum ProxyType
{
    Http,
    Socks4,
    Socks5,
}

/// <summary>Settings → General → Proxy (names like Radarr). Applies to every outgoing HTTP request, not to Usenet (NNTP).</summary>
public sealed partial class ProxySettings(SettingsCache cache)
{
    public const int DefaultPort = 8080;

    public bool Enabled => cache.GetBool(ConfigKeys.ProxyEnabled, false);

    public ProxyType Type => cache.GetEnum(ConfigKeys.ProxyType, ProxyType.Http);

    public string? Hostname => cache.Get(ConfigKeys.ProxyHostname);

    public int Port => cache.GetInt(ConfigKeys.ProxyPort, DefaultPort);

    public string? Username => cache.Get(ConfigKeys.ProxyUsername);

    public string? Password => cache.Get(ConfigKeys.ProxyPassword);

    /// <summary>Comma separated hosts that go direct; "*" wildcards allowed ("*.example.com", "192.168.1.*").</summary>
    public string BypassFilter => cache.Get(ConfigKeys.ProxyBypassFilter) ?? string.Empty;

    public bool BypassLocalAddresses => cache.GetBool(ConfigKeys.ProxyBypassLocalAddresses, true);

    /// <summary>The proxy address, e.g. "socks5://proxy:1080"; null when off or incomplete.</summary>
    public Uri? ProxyUri
    {
        get
        {
            if (!Enabled || Hostname is not { } host)
            {
                return null;
            }

            var scheme = Type switch
            {
                ProxyType.Socks4 => "socks4",
                ProxyType.Socks5 => "socks5",
                _ => "http",
            };
            return Uri.TryCreate(string.Create(CultureInfo.InvariantCulture, $"{scheme}://{FormatHost(host)}:{Port}"), UriKind.Absolute, out var uri) ? uri : null;
        }
    }

    /// <summary>True when requests to this host go direct (bypass filter or local address).</summary>
    public bool IsBypassed(string host)
    {
        if (BypassLocalAddresses && Auth.LocalAddress.IsLocalHost(host))
        {
            return true;
        }

        return ParseFilter(BypassFilter).Any(pattern => Matches(pattern, host));
    }

    public static IReadOnlyList<string> ParseFilter(string? filter) =>
        [.. (filter ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Null when valid.</summary>
    public static string? ValidateFilter(string? filter)
    {
        var bad = ParseFilter(filter).FirstOrDefault(entry => !FilterEntryRegex().IsMatch(entry));
        return bad is null ? null : $"\"{bad}\" is not a host name, IP address or pattern like *.example.com.";
    }

    /// <summary>Null when valid: a host name or IP address, without scheme or port.</summary>
    public static string? ValidateHostname(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "Enter the proxy's host name or IP address.";
        }

        var value = host.Trim();
        return IPAddress.TryParse(value.Trim('[', ']'), out _) || Uri.CheckHostName(value) == UriHostNameType.Dns
            ? null
            : "Enter only the host name or IP address, without http:// or port.";
    }

    public static bool Matches(string pattern, string host)
    {
        var regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(host, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private static string FormatHost(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : host;

    [GeneratedRegex(@"^[A-Za-z0-9*._:\-\[\]]+$")]
    private static partial Regex FilterEntryRegex();
}
