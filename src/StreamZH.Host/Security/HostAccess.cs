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
/// an IP address, localhost (and *.localhost), this computer's own names, the host of the application URL and the names
/// under "Allowed host names". Since 0.12 (V-05) other local names ("nas", "*.local", "*.lan", "*.home.arpa") are not
/// trusted by default: any device on the network can answer for them (mDNS, LLMNR, the router's DNS) and point them at a
/// browser it wants to misuse. Users who open StreamZH by such a name add it under "Allowed host names". Other names get
/// the normal sign-in.
/// </summary>
public sealed class HostAccess(AccessSettings access, HostSettings host)
{
    private static readonly Lazy<string[]> MachineNames = new(ReadMachineNames);

    public bool IsAllowed(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsAllowed(HostName(context));
    }

    public bool IsAllowed(string? hostHeader)
    {
        var name = Normalize(hostHeader);
        if (name.Length == 0)
        {
            return false;
        }

        if (IsThisComputersName(name))
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

    /// <summary>
    /// 0.12 (V-06): a Host header ASP.NET cannot read (e.g. "xn--nas", a punycode label that does not decode) makes every
    /// later use of <c>Request.Host</c> throw. Such requests are answered with 400 at once, without a stack trace.
    /// </summary>
    public static IApplicationBuilder UseHostHeaderCheck(IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!HasReadableHost(context))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Invalid Host header.");
            return;
        }

        await next(context);
    });

    /// <summary>The Host header is missing (HTTP/1.0) or a host name or address that ASP.NET can read.</summary>
    public static bool HasReadableHost(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var raw = context.Request.Headers.Host.ToString();
        if (raw.Length == 0)
        {
            return true;
        }

        if (ParseHostHeader(raw).Name.Length == 0)
        {
            return false;
        }

        try
        {
            _ = context.Request.Host.Host;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// An IP address, localhost or *.localhost (browsers resolve these to this computer themselves), or one of this
    /// computer's own names (host name, FQDN, host name.local). Nobody else can make a browser send these for their server.
    /// </summary>
    public static bool IsThisComputersName(string? hostName)
    {
        var name = Normalize(hostName);
        return name.Length > 0
            && (IPAddress.TryParse(name, out _)
                || name == "localhost"
                || name.EndsWith(".localhost", StringComparison.Ordinal)
                || MachineNames.Value.Contains(name, StringComparer.Ordinal));
    }

    /// <summary>
    /// The host name of the request's Host header: lower case, without port and brackets; "" when there is none or it is
    /// not a plain host name or address. Never throws (0.12, V-06: <c>HostString.Host</c> throws on a bad punycode label).
    /// </summary>
    public static string HostName(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ParseHostHeader(context.Request.Headers.Host.ToString()).Name;
    }

    /// <summary>"Example.com:8080" → ("example.com", 8080); "[::1]" → ("::1", null); anything unusual → ("", null).</summary>
    public static (string Name, int? Port) ParseHostHeader(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length is 0 or > 300)
        {
            return (string.Empty, null);
        }

        string name;
        string? port = null;
        if (text[0] == '[')
        {
            var end = text.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
            {
                return (string.Empty, null);
            }

            name = text[1..end];
            var rest = text[(end + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return (string.Empty, null);
                }

                port = rest[1..];
            }
        }
        else
        {
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 && text.IndexOf(':', colon + 1) < 0)
            {
                name = text[..colon];
                port = text[(colon + 1)..];
            }
            else
            {
                // No port, or a bare IPv6 address.
                name = text;
            }
        }

        name = Normalize(name);
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':')))
        {
            return (string.Empty, null);
        }

        int? number = null;
        if (port is not null)
        {
            if (!int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            {
                return (string.Empty, null);
            }

            number = p;
        }

        return (name, number);
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

        // The computer's own mDNS name (Bonjour, Avahi) too.
        names.AddRange([.. names.Where(n => !string.IsNullOrWhiteSpace(n) && !n.Contains('.', StringComparison.Ordinal)).Select(n => n + ".local")]);
        return [.. names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Normalize).Distinct(StringComparer.Ordinal)];
    }
}
