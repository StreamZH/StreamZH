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

    /// <summary>The Run entry: the tray exe, plus --data when StreamZH uses another data folder than the default.</summary>
    public static string RunEntry(string trayExe, string? dataDirectory, string defaultDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(trayExe);
        var command = Quote(trayExe);
        return string.IsNullOrWhiteSpace(dataDirectory) || SamePath(dataDirectory, defaultDataDirectory)
            ? command
            : $"{command} --data {Quote(dataDirectory)}";
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
