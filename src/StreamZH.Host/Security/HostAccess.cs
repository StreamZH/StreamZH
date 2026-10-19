// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.NetworkInformation;
using StreamZH.Host.Auth;
using StreamZH.Host.General;

namespace StreamZH.Host.Security;

/// <summary>
/// DNS rebinding protection (0.12, security review S1-01) for the modes without sign-in ("Disabled for local
/// addresses" and External). A web page on the internet can make its own host name point at StreamZH's address; the
/// browser then sends that name as Host header and treats StreamZH as the page's own origin. So the sign-in-free access
/// is only given to requests whose Host is a name an attacker cannot control:
/// an IP address, localhost and local names ("nas", "*.local", "*.lan", "*.home.arpa"), this computer's names, the host
/// of the application URL and the names under "Allowed host names". Other names get the normal sign-in.
/// </summary>
public sealed class HostAccess(AccessSettings access, HostSettings host)
{
    private static readonly Lazy<string[]> MachineNames = new(ReadMachineNames);

    public bool IsAllowed(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsAllowed(context.Request.Host.Host);
    }

    public bool IsAllowed(string? hostHeader)
    {
        var name = Normalize(hostHeader);
        if (name.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(name, out _) || LocalAddress.IsLocalHost(name) || MachineNames.Value.Contains(name, StringComparer.Ordinal))
        {
            return true;
        }

        if (host.ApplicationUrl is { } applicationUrl && Uri.TryCreate(applicationUrl, UriKind.Absolute, out var url)
            && string.Equals(Normalize(url.Host), name, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var allowed in access.AllowedHostNames)
        {
            if (allowed.StartsWith("*.", StringComparison.Ordinal)
                ? name.EndsWith(allowed[1..], StringComparison.Ordinal) && name.Length > allowed.Length - 1
                : string.Equals(allowed, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>"Example.COM." → "example.com"; "[::1]" → "::1".</summary>
    public static string Normalize(string? host) => (host ?? string.Empty).Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();

    private static string[] ReadMachineNames()
    {
        var names = new List<string>();
        try
        {
            names.Add(Dns.GetHostName());
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            names.Add(properties.HostName);
            if (!string.IsNullOrEmpty(properties.DomainName))
            {
                names.Add($"{properties.HostName}.{properties.DomainName}");
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or System.Net.Sockets.SocketException or PlatformNotSupportedException)
        {
            // Without them only IP addresses and local names count.
        }

        return [.. names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Normalize).Distinct(StringComparer.Ordinal)];
    }
}
