// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using StreamZH.Core;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Tray;
using StreamZH.Host.General;
using StreamZH.Host.Security;
using StreamZH.Host.Service;

namespace StreamZH.Host.Tray;

/// <summary>
/// The tray icon's access (StreamZH.Tray.exe on Windows): a random token per run, written to &lt;data&gt;/tray.token
/// with a narrow ACL (Windows: SYSTEM and this account full, the tray's user read, see <see cref="TokenAcl"/>; elsewhere 0600),
/// and &lt;data&gt;/tray.json with the address on this computer. Also remembers the last finished downloads, so the tray
/// can show a notification for each.
/// </summary>
public sealed partial class TrayAccess(
    AppPaths paths,
    RunningHost running,
    IEventBus events,
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<TrayAccess> logger) : IHostedService, IDisposable
{
    public const int MaxFinished = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly List<TrayFinishedDownload> _finished = [];
    private long _number;
    private IDisposable? _subscription;
    private CancellationTokenRegistration _started;

    /// <summary>The token of this run (64 hex characters).</summary>
    public string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Changes with every run (also an in-process restart), so the tray knows the numbering started again.</summary>
    public string Run { get; } = Guid.NewGuid().ToString("N");

    public string TokenPath => Path.Combine(paths.DataDirectory, TrayContract.TokenFile);

    public string EndpointPath => Path.Combine(paths.DataDirectory, TrayContract.EndpointFile);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = events.Subscribe<DownloadFinished>((e, _) =>
        {
            Remember(e.Item);
            return ValueTask.CompletedTask;
        });

        // The real addresses are known once Kestrel listens.
        _started = lifetime.ApplicationStarted.Register(WriteFiles);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _subscription?.Dispose();
        _started.Dispose();
    }

    public void Remember(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            _number++;
            _finished.Add(new TrayFinishedDownload(_number, item.Id, item.Title, item.Status == DownloadStatus.Failed, item.Error, time.GetUtcNow()));
            if (_finished.Count > MaxFinished)
            {
                _finished.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Finished downloads after <paramref name="after"/> when <paramref name="run"/> is this run; all remembered ones when the
    /// tray knew another run (StreamZH restarted) or none. Also the number of the last one.
    /// </summary>
    public (IReadOnlyList<TrayFinishedDownload> Items, long Last) Finished(string? run, long? after)
    {
        lock (_gate)
        {
            var since = run == Run ? after ?? 0 : 0;
            return ([.. _finished.Where(f => f.Number > since)], _number);
        }
    }

    public void WriteFiles()
    {
        try
        {
            WriteToken(TokenPath, Token);
            var (url, dashboard) = LoopbackUrls(ServerAddresses(), running.Values);
            var endpoint = new TrayEndpoint(url, dashboard, AppInfo.Version, RunningAs.Mode, Environment.ProcessId, paths.LogDirectory, time.GetUtcNow());
            File.WriteAllText(EndpointPath, JsonSerializer.Serialize(endpoint, Json));
            StartupCheck.Clear(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            logger.LogWarning("Could not write {File} for the tray icon: {Message}", TokenPath, ex.Message);
        }
    }

    /// <summary>
    /// The address the tray uses (loopback, URL base, no trailing slash) and the dashboard address for the browser.
    /// Takes the first HTTP address Kestrel listens on (HTTPS when there is none); wildcards become 127.0.0.1 (the
    /// browser gets "localhost"). Without addresses (tests): the host settings.
    /// </summary>
    public static (string Url, string Dashboard) LoopbackUrls(IEnumerable<string> addresses, HostValues values)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(values);
        var parsed = addresses.Select(a => AddressRegex().Match(a.Trim())).Where(m => m.Success).ToList();
        var match = parsed.FirstOrDefault(m => m.Groups["scheme"].Value.Equals("http", StringComparison.OrdinalIgnoreCase)) ?? parsed.FirstOrDefault();

        string scheme, host;
        int port;
        if (match is null)
        {
            scheme = "http";
            host = values.BindAddress;
            port = values.Port;
        }
        else
        {
            scheme = match.Groups["scheme"].Value.ToLowerInvariant();
            host = match.Groups["host"].Value;
            port = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture) : scheme == "https" ? 443 : 80;
        }

        var (apiHost, browserHost) = host.ToLowerInvariant() switch
        {
            "*" or "+" or "0.0.0.0" or "[::]" or "::" or "localhost" or "" => ("127.0.0.1", "localhost"),
            "[::1]" or "::1" => ("[::1]", "[::1]"),
            "127.0.0.1" => ("127.0.0.1", "localhost"),
            _ when host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') => ($"[{host}]", $"[{host}]"),
            _ => (host, host),
        };

        var authority = string.Create(CultureInfo.InvariantCulture, $"{scheme}://{apiHost}:{port}");
        var browser = string.Create(CultureInfo.InvariantCulture, $"{scheme}://{browserHost}:{port}");
        return (authority + values.UrlBase, browser + values.UrlBase + "/");
    }

    /// <summary>Writes the token so only SYSTEM, this account and the user of the tray icon can open it (Linux, macOS: 0600).</summary>
    public static void WriteToken(string path, string token)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(token);

        // A new file each time: the access rules are set when it is created.
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        if (OperatingSystem.IsWindows())
        {
            WriteWindows(path, token);
            return;
        }

        // Only the account StreamZH runs as (the tray icon exists on Windows only).
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(token);
        }

        // The umask can only take rights away; set it exactly anyway.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Who may open the token on Windows (S1-13): SYSTEM and the writing account fully; the user accounts in
    /// <paramref name="readers"/> may read it. Null readers (no user known, see <see cref="TrayReaders"/>) falls back to
    /// signed-in users, as before 0.12.
    /// </summary>
    public static IReadOnlyList<AclEntry> TokenAcl(string writerSid, IReadOnlyList<string>? readers)
    {
        ArgumentException.ThrowIfNullOrEmpty(writerSid);
        List<AclEntry> entries =
        [
            new(DataFolderAcl.LocalSystem, AclEntry.FullControl, true, 0, 0),
            new(writerSid, AclEntry.FullControl, true, 0, 0),
        ];
        foreach (var reader in readers ?? [InteractiveSid])
        {
            entries.Add(new AclEntry(reader, AclEntry.Read | AclEntry.Synchronize, true, 0, 0));
        }

        return [.. entries.DistinctBy(e => e.Sid, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"Signed-in users": only for a service whose data folder grants no user account.</summary>
    public const string InteractiveSid = "S-1-5-4";

    /// <summary>
    /// The tray runs as the signed-in user. In a console window that is the account writing the token, so nobody else
    /// needs it. As a service: the user accounts the data folder grants (Install-Service.ps1 adds the installing user);
    /// null when there are none.
    /// </summary>
    public static IReadOnlyList<string>? TrayReaders(bool isService, string writerSid, IEnumerable<AclEntry> dataFolder)
    {
        if (!isService)
        {
            return [];
        }

        var users = DataFolderAcl.TrayReaders(dataFolder, writerSid);
        return users.Count > 0 ? users : null;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string path, string token)
    {
        var me = WindowsIdentity.GetCurrent().User?.Value ?? DataFolderAcl.LocalSystem;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var readers = TrayReaders(RunningAs.IsWindowsService, me, WindowsAcl.Entries(new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access)));
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var entry in TokenAcl(me, readers))
        {
            security.AddAccessRule(WindowsAcl.Rule(entry));
        }

        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.WriteData | FileSystemRights.Synchronize, FileShare.None, 4096, FileOptions.None, security);
        using var writer = new StreamWriter(stream);
        writer.Write(token);
    }

    private IEnumerable<string> ServerAddresses() =>
        services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];

    [GeneratedRegex(@"^(?<scheme>https?)://(?<host>\[[^\]]*\]|[^:/]*)(?::(?<port>\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();
}
