// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;

namespace StreamZH.Tray.Core;

/// <summary>Command lines of the tray: the "Start with Windows" entry (HKCU\…\Run) and starting StreamZH.exe.</summary>
public static class TrayCommandLine
{
    /// <summary>Value name under HKCU\Software\Microsoft\Windows\CurrentVersion\Run (tools\Install-Service.ps1 uses the same).</summary>
    public const string RunValueName = "StreamZH Tray";

    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// "--start": when there is no service, the tray starts StreamZH.exe (minimized) if it is not running. The Windows
    /// installer's "tray only" choice puts it in the Run entry, so StreamZH starts at sign-in without a service.
    /// </summary>
    public const string StartFlag = "--start";

    /// <summary>The Run entry: the tray exe, plus --data when StreamZH uses another data folder than the default, plus --start.</summary>
    public static string RunEntry(string trayExe, string? dataDirectory, string defaultDataDirectory, bool startHost = false)
    {
        ArgumentNullException.ThrowIfNull(trayExe);
        var command = Quote(trayExe);
        if (!string.IsNullOrWhiteSpace(dataDirectory) && !SamePath(dataDirectory, defaultDataDirectory))
        {
            command = $"{command} --data {Quote(dataDirectory)}";
        }

        return startHost ? $"{command} {StartFlag}" : command;
    }

    /// <summary>The SID of LocalSystem.</summary>
    public const string LocalSystemSid = "S-1-5-18";

    /// <summary>
    /// SV4 X-05: why the tray must not start StreamZH.exe itself as the account <paramref name="accountSid"/>, or null.
    /// A tray running as SYSTEM (a silent "tray only" install by Intune, SCCM or an RMM tool runs as SYSTEM) would start
    /// StreamZH as LocalSystem, which 0.12 moved away from: any way from StreamZH's settings to running a program would
    /// then control Windows.
    /// </summary>
    public static string? HostStartRefusal(string? accountSid) =>
        string.Equals(accountSid, LocalSystemSid, StringComparison.OrdinalIgnoreCase)
            ? "Not started: the tray icon runs as SYSTEM, and StreamZH must not run as SYSTEM. Install it as a Windows service (tools\\Install-Service.ps1), or sign in and start the tray icon as your user."
            : null;

    /// <summary>True when the tray was started with <see cref="StartFlag"/>.</summary>
    public static bool WantsStart(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(a => a is StartFlag or "/start" or "-start");
    }

    /// <summary>The arguments for StreamZH.exe in a console window: --data only for a data folder other than the default.</summary>
    public static string HostArguments(string dataDirectory, string defaultDataDirectory) =>
        SamePath(dataDirectory, defaultDataDirectory) ? string.Empty : $"--data {Quote(dataDirectory)}";

    /// <summary>
    /// Quotes one argument for CommandLineToArgvW: always in double quotes; backslashes before a quote and at the end are
    /// doubled (so "C:\Data\" does not swallow the closing quote).
    /// </summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var text = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                text.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                text.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
