// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace StreamZH.Host.Service;

/// <summary>How this process was started: "docker", "windowsService", "systemd" or "console" (System → Status, "Runs as").</summary>
public static class RunningAs
{
    public const string Docker = "docker";
    public const string WindowsService = "windowsService";
    public const string Systemd = "systemd";
    public const string Console = "console";

    private static readonly Lazy<string> Detected = new(() => Detect(new RuntimeProbe(
        Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService(),
        OperatingSystem.IsLinux() ? LinuxParentProcessName() : null)));

    /// <summary>Detected once per process; an in-process restart does not change it.</summary>
    public static string Mode => Detected.Value;

    public static bool IsWindowsService => Mode == WindowsService;

    public static string Detect(RuntimeProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.Environment("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            return Docker;
        }

        if (probe.IsWindowsService)
        {
            return WindowsService;
        }

        // systemd starts services directly (system and user managers both run as "systemd"); a shell in between is a console.
        return probe.ParentProcessName == "systemd" ? Systemd : Console;
    }

    /// <summary>The name of the parent process from /proc (field 4 of /proc/self/stat, then /proc/&lt;ppid&gt;/comm), or null.</summary>
    public static string? LinuxParentProcessName()
    {
        try
        {
            return ParentPid(File.ReadAllText("/proc/self/stat")) is { } parent
                ? File.ReadAllText($"/proc/{parent.ToString(CultureInfo.InvariantCulture)}/comm").Trim()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>"pid (comm) state ppid …"; comm may contain spaces and parentheses, so the fields start after the last ')'.</summary>
    public static int? ParentPid(string stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        var end = stat.LastIndexOf(')');
        if (end < 0)
        {
            return null;
        }

        var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid) ? ppid : null;
    }
}

/// <summary>What <see cref="RunningAs.Detect"/> looks at, so tests can describe any platform.</summary>
public sealed record RuntimeProbe(Func<string, string?> Environment, bool IsWindowsService, string? ParentProcessName);
