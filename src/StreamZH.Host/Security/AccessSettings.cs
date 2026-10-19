// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text.RegularExpressions;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.General;

namespace StreamZH.Host.Security;

/// <summary>
/// Settings → General → Security, advanced part (0.12): which host names may use the sign-in-free modes, which reverse
/// proxies are trusted, which networks count as "local" and which header a reverse proxy must send in External mode.
/// Read on every request, so the parsed lists are cached until the stored text changes.
/// </summary>
public sealed partial class AccessSettings(SettingsCache cache)
{
    private Parsed<NetworkList>? _proxies;
    private Parsed<NetworkList>? _local;
    private Parsed<IReadOnlyList<string>>? _hosts;

    /// <summary>Extra host names (besides IP addresses, local names, this computer's names and the application URL).</summary>
    public IReadOnlyList<string> AllowedHostNames => Read(ConfigKeys.AllowedHostNames, ref _hosts, ParseHostNames);

    /// <summary>Reverse proxies whose X-Forwarded-For/-Proto StreamZH believes; empty = none.</summary>
    public NetworkList TrustedProxies => Read(ConfigKeys.TrustedProxies, ref _proxies, NetworkList.Parse);

    /// <summary>The networks of "Disabled for local addresses"; empty = the private networks (<see cref="LocalAddress.IsLocal"/>).</summary>
    public NetworkList TrustedLocalNetworks => Read(ConfigKeys.TrustedLocalNetworks, ref _local, NetworkList.Parse);

    /// <summary>External authentication: the header the reverse proxy sets with the signed-in user; null = not required.</summary>
    public string? ExternalUserHeader => cache.Get(ConfigKeys.ExternalUserHeader);

    public string AllowedHostNamesText => cache.Get(ConfigKeys.AllowedHostNames) ?? string.Empty;

    public string TrustedProxiesText => cache.Get(ConfigKeys.TrustedProxies) ?? string.Empty;

    public string TrustedLocalNetworksText => cache.Get(ConfigKeys.TrustedLocalNetworks) ?? string.Empty;

    /// <summary>The address counts as local for "Disabled for local addresses".</summary>
    public bool IsTrustedLocal(IPAddress? address)
    {
        var configured = TrustedLocalNetworks;
        return configured.IsEmpty ? LocalAddress.IsLocal(address) : configured.Contains(address);
    }

    /// <summary>
    /// The address may send the External sign-in: a configured trusted proxy, or (none configured) any local address,
    /// so a reverse proxy on this computer, in a Docker network or on the LAN keeps working.
    /// </summary>
    public bool IsExternalSource(IPAddress? address)
    {
        var proxies = TrustedProxies;
        return proxies.IsEmpty ? LocalAddress.IsLocal(address) : proxies.Contains(address);
    }

    public void Update(string? allowedHostNames, string? trustedProxies, string? trustedLocalNetworks, string? externalUserHeader)
    {
        if (allowedHostNames is not null)
        {
            cache.Set(ConfigKeys.AllowedHostNames, string.Join(", ", ParseHostNames(allowedHostNames)));
        }

        if (trustedProxies is not null)
        {
            cache.Set(ConfigKeys.TrustedProxies, NetworkList.Parse(trustedProxies).Format());
        }

        if (trustedLocalNetworks is not null)
        {
            cache.Set(ConfigKeys.TrustedLocalNetworks, NetworkList.Parse(trustedLocalNetworks).Format());
        }

        if (externalUserHeader is not null)
        {
            cache.Set(ConfigKeys.ExternalUserHeader, externalUserHeader.Trim());
        }
    }

    public static string? ValidateHostNames(string? text)
    {
        foreach (var entry in NetworkList.Split(text))
        {
            if (!HostNamePattern().IsMatch(entry))
            {
                return $"\"{entry}\" is not a host name (e.g. streamzh.example.com or *.example.com).";
            }
        }

        return null;
    }

    public static string? ValidateNetworks(string? text)
    {
        NetworkList.Parse(text, out var problem);
        return problem;
    }

    public static string? ValidateHeaderName(string? text)
    {
        var name = text?.Trim() ?? string.Empty;
        return name.Length == 0 || HeaderNamePattern().IsMatch(name) ? null : "Enter a header name, e.g. Remote-User.";
    }

    public static IReadOnlyList<string> ParseHostNames(string? text) =>
        [.. NetworkList.Split(text).Where(h => HostNamePattern().IsMatch(h)).Select(h => h.TrimEnd('.').ToLowerInvariant()).Distinct(StringComparer.Ordinal)];

    private T Read<T>(string key, ref Parsed<T>? slot, Func<string?, T> parse)
    {
        var raw = cache.Get(key);
        var current = slot;
        if (current is not null && string.Equals(current.Raw, raw, StringComparison.Ordinal))
        {
            return current.Value;
        }

        var parsed = new Parsed<T>(raw, parse(raw));
        slot = parsed;
        return parsed.Value;
    }

    private sealed record Parsed<T>(string? Raw, T Value);

    [GeneratedRegex(@"^(\*\.)?[A-Za-z0-9]([A-Za-z0-9-]{0,62})(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}))*\.?$")]
    private static partial Regex HostNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,100}$")]
    private static partial Regex HeaderNamePattern();
}
