// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core;

/// <summary>Resolves where StreamZH keeps its database, logs and incomplete downloads.</summary>
public sealed class AppPaths
{
    public const string DataDirEnvVar = "STREAMZH_DATA";

    public AppPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }
    public string DatabaseFile => Path.Combine(DataDirectory, "streamzh.db");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string IncompleteDirectory => Path.Combine(DataDirectory, "downloads", "incomplete");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(IncompleteDirectory);
    }

    /// <summary>Explicit value, then STREAMZH_DATA, then /config in containers, then the OS default
    /// (ProgramData on Windows, Application Support on macOS, ~/.config on Linux).</summary>
    public static AppPaths Resolve(string? explicitDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return new AppPaths(explicitDirectory);
        }

        var fromEnv = Environment.GetEnvironmentVariable(DataDirEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return new AppPaths(fromEnv);
        }

        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            return new AppPaths("/config");
        }

        if (OperatingSystem.IsWindows())
        {
            // ProgramData so the same data works for the Windows service and the tray app.
            return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamZH"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return OperatingSystem.IsMacOS()
            ? new AppPaths(Path.Combine(home, "Library", "Application Support", "StreamZH"))
            : new AppPaths(Path.Combine(home, ".config", "streamzh"));
    }
}