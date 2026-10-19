// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamZH.Core;
using StreamZH.Core.Tray;
using StreamZH.Host.Logging;
using StreamZH.Host.Service;

namespace StreamZH.Host.General;

/// <summary>
/// 1.0 RC: before the first run starts anything, checks that StreamZH can listen where it should. When another program
/// has the port, StreamZH prints one line that says which port and how to choose another one, writes it to the log, the
/// Windows event log (service) and <see cref="TrayContract.StartupErrorFile"/> (tray icon), and exits with
/// <see cref="ExitCode"/>, instead of a stack trace after half the services have started.
/// </summary>
public static partial class StartupCheck
{
    /// <summary>The process exit code when StreamZH cannot listen.</summary>
    public const int ExitCode = 1;

    /// <summary>One endpoint Kestrel will listen on: an address (Any = every address) and a port.</summary>
    public sealed record Endpoint(IPAddress Address, int Port, string Url);

    /// <summary>The endpoints of <paramref name="addresses"/> ("http://*:9797", "https://[::1]:9798", "http://localhost:5000").</summary>
    public static IReadOnlyList<Endpoint> Endpoints(IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var endpoints = new List<Endpoint>();
        foreach (var raw in addresses)
        {
            var match = UrlRegex().Match(raw.Trim());
            if (!match.Success)
            {
                continue;
            }

            var https = match.Groups["scheme"].Value.Equals("https", StringComparison.OrdinalIgnoreCase);
            var port = match.Groups["port"].Success && int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : https ? 443 : 80;
            if (port is <= 0 or > 65535)
            {
                continue;
            }

            var host = match.Groups["host"].Value.Trim('[', ']');
            if (host is "*" or "+" or "" or "0.0.0.0" or "::")
            {
                endpoints.Add(new Endpoint(IPAddress.IPv6Any, port, raw));
            }
            else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                endpoints.Add(new Endpoint(IPAddress.Loopback, port, raw));
            }
            else if (IPAddress.TryParse(host, out var ip))
            {
                endpoints.Add(new Endpoint(ip, port, raw));
            }
            else
            {
                // Kestrel listens on every address for other host names.
                endpoints.Add(new Endpoint(IPAddress.IPv6Any, port, raw));
            }
        }

        return endpoints;
    }

    /// <summary>Why StreamZH cannot listen on <paramref name="endpoints"/>: (summary, the whole line), or null when it can.</summary>
    public static (string Summary, string Message)? Problem(IReadOnlyList<Endpoint> endpoints, string? explicitUrls)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        foreach (var endpoint in endpoints)
        {
            if (TryBind(endpoint) is { } error)
            {
                return Describe(endpoint, error, explicitUrls);
            }
        }

        return null;
    }

    /// <summary>The text for a failed bind (also used when Kestrel itself fails after the check).</summary>
    public static (string Summary, string Message) Describe(Endpoint endpoint, SocketError error, string? explicitUrls)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var port = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        // SV4 X-09: the same address with only the port changed (http://localhost:7301 -> http://localhost:7311), so a
        // copied suggestion never widens a localhost-only start to every address.
        var suggestion = WithPort(endpoint.Url, endpoint.Port == 65535 ? 9797 : endpoint.Port + 10);
        var summary = error switch
        {
            SocketError.AddressAlreadyInUse => $"Port {port} is already in use",
            SocketError.AddressNotAvailable => $"The address {endpoint.Address} is not on this computer",
            SocketError.AccessDenied => $"No permission to use port {port}",
            _ => $"Cannot listen on {endpoint.Url} ({error})",
        };
        var why = error switch
        {
            SocketError.AddressAlreadyInUse => $"port {port} is already in use by another program (is StreamZH already running?).",
            SocketError.AddressNotAvailable => $"the address {endpoint.Address} does not belong to this computer.",
            SocketError.AccessDenied => $"it may not use port {port} (ports below 1024 need extra rights, and some ports are reserved).",
            _ => $"it cannot listen on {endpoint.Url} ({error}).",
        };
        var fix = explicitUrls is null
            ? $"Start StreamZH with another port, for example --urls {suggestion} or the environment variable ASPNETCORE_URLS={suggestion}, then set that port in Settings → General → Host."
            : $"StreamZH was started with the addresses {explicitUrls} (--urls, ASPNETCORE_URLS or appsettings.json): choose another port there, for example {suggestion}.";
        var stop = error == SocketError.AddressAlreadyInUse ? "Stop that program, or choose another port. " : string.Empty;
        return (summary, $"StreamZH could not start: {why} {stop}{fix}");
    }

    /// <summary>
    /// <paramref name="url"/> with <paramref name="port"/> instead of its port; scheme, host and path stay as they are.
    /// A text that is no http(s) address becomes http://localhost:&lt;port&gt; (never every address).
    /// </summary>
    public static string WithPort(string url, int port)
    {
        ArgumentNullException.ThrowIfNull(url);
        var text = url.Trim();
        var number = port.ToString(CultureInfo.InvariantCulture);
        var match = UrlRegex().Match(text);
        if (!match.Success)
        {
            return $"http://localhost:{number}";
        }

        var host = match.Groups["host"];
        var end = match.Groups["port"].Success ? match.Groups["port"].Index + match.Groups["port"].Length : host.Index + host.Length;
        return $"{text[..(host.Index + host.Length)]}:{number}{text[end..]}";
    }

    /// <summary>
    /// Reports a start that failed: one line on the console (stderr), the log file, the Windows event log (service) and the
    /// file for the tray icon; sets the exit code.
    /// </summary>
    public static void Report(string summary, string message, AppPaths paths, FileLoggerProvider? log, ServiceSession? service, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(paths);
        Console.Error.WriteLine(message);
        log?.CreateLogger(AppInfo.Name).LogCritical("{Message}", message);
        service?.Fail(message);
        WriteFile(paths, new StartupError(summary, message, (time ?? TimeProvider.System).GetUtcNow()));
        Environment.ExitCode = ExitCode;
    }

    public static string FilePath(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.DataDirectory, TrayContract.StartupErrorFile);
    }

    /// <summary>Removes the file after a successful start; never throws.</summary>
    public static void Clear(AppPaths paths)
    {
        try
        {
            File.Delete(FilePath(paths));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void WriteFile(AppPaths paths, StartupError error)
    {
        try
        {
            File.WriteAllText(FilePath(paths), JsonSerializer.Serialize(error, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The console and the log have it.
        }
    }

    /// <summary>Binds a socket like Kestrel does (every address: IPv6 with IPv4 mapped, else IPv4) and closes it again.</summary>
    private static SocketError? TryBind(Endpoint endpoint)
    {
        var error = Bind(endpoint.Address, endpoint.Port);
        if (endpoint.Address.Equals(IPAddress.IPv6Any) && error is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
        {
            error = Bind(IPAddress.Any, endpoint.Port);
        }

        return error is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported ? null : error;
    }

    private static SocketError? Bind(IPAddress address, int port)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (address.Equals(IPAddress.IPv6Any))
            {
                socket.DualMode = true;
            }

            // Linux and macOS: like Kestrel, a port with connections in TIME_WAIT after a restart is free; a listening
            // program still makes the bind fail. (On Windows SO_REUSEADDR would allow taking a port in use.)
            if (!OperatingSystem.IsWindows())
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            socket.Bind(new IPEndPoint(address, port));
            return null;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode;
        }
    }

    [GeneratedRegex(@"^(?<scheme>https?)://(?<host>\[[^\]]*\]|[^:/]*)(?::(?<port>\d+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
