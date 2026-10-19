// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core;

/// <summary>Resolves where StreamZH keeps its database, logs and incomplete downloads.</summary>
public sealed class AppPaths
{
    public const string DataDirEnvVar = "STREAMZH_DATA";

    /// <param name="appDirectory">The folder StreamZH runs from (default: <see cref="AppContext.BaseDirectory"/>); protected like the data folder.</param>
    public AppPaths(string dataDirectory, string? appDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        AppDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory));
    }

    public string DataDirectory { get; }

    /// <summary>The program folder (StreamZH.exe, its DLLs, wwwroot). Nothing StreamZH downloads, imports or renames goes there (0.12, SV2 N-01).</summary>
    public string AppDirectory { get; }

    public string DatabaseFile => Path.Combine(DataDirectory, "streamzh.db");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string IncompleteDirectory => Path.Combine(DataDirectory, "downloads", "incomplete");

    /// <summary>The default download folder (incomplete, complete, NZB folders below it); the first storage location of a new installation.</summary>
    public string DownloadsDirectory => Path.Combine(DataDirectory, "downloads");

    /// <summary>par2 and 7-Zip installed by StreamZH (&lt;data&gt;/tools/par2, &lt;data&gt;/tools/7-Zip); survives app updates.</summary>
    public string ToolsDirectory => Path.Combine(DataDirectory, "tools");

    /// <summary>The sign-in keys (ASP.NET Data Protection key ring).</summary>
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");

    private readonly object _storageLock = new();
    private IReadOnlyList<string>? _storageLocations;
    private System.Collections.Concurrent.ConcurrentDictionary<(string, DataFolderUse), (string? Problem, long Until)> _checks = new();

    /// <summary>
    /// 0.12 (SV2 N-03): the base folders the owner allows for media and downloads (Settings → General → Storage). Null
    /// until the app loaded them (unit tests that build services on their own): then only the data and program folder
    /// rules apply.
    /// </summary>
    public IReadOnlyList<string>? StorageLocations
    {
        get
        {
            lock (_storageLock)
            {
                return _storageLocations;
            }
        }
    }

    /// <summary>Sets the storage locations (at start and after the owner changed them).</summary>
    public void SetStorageLocations(IEnumerable<string> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        lock (_storageLock)
        {
            _storageLocations = [.. locations.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim())];
            _checks = new();
        }
    }

    /// <summary>
    /// 0.12: why <paramref name="folder"/> may not be used to write to, null when it may: a path form that hides what it
    /// points to (<see cref="PathIdentity.SyntaxProblem"/>), a folder outside every storage location
    /// (<see cref="StorageProblem"/>), or one inside or around the data or program folder (<see cref="DataFolderGuard"/>).
    /// Results are kept for a few seconds (the download engine asks on every tick).
    /// </summary>
    public string? FolderProblem(string? folder, DataFolderUse use = DataFolderUse.Downloads)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var checks = _checks;
        var key = (folder, use);
        var now = Environment.TickCount64;
        if (checks.TryGetValue(key, out var cached) && cached.Until > now)
        {
            return cached.Problem;
        }

        var problem = PathIdentity.SyntaxProblem(folder, OperatingSystem.IsWindows())
                      ?? StorageProblem(folder, use)
                      ?? DataFolderGuard.Problem(DataDirectory, AppDirectory, folder, use);
        if (checks.Count > 2000)
        {
            checks.Clear();
        }

        checks[key] = (problem, now + 5000);
        return problem;
    }

    /// <summary>The marker of <see cref="StorageProblem"/> messages (the web app offers to add the folder for the owner).</summary>
    public const string OutsideStorageCode = "outside-storage";

    /// <summary>
    /// Null when <paramref name="folder"/> is inside a storage location (or none are loaded), else why not. First by path
    /// text, before anything touches the folder (a network path an admin typed is never opened, SV2 N-06), then by the
    /// resolved paths of both (a link inside a storage location may not lead out of it). The default backup folder is
    /// always allowed for backups.
    /// </summary>
    public string? StorageProblem(string? folder, DataFolderUse use = DataFolderUse.Downloads)
    {
        var locations = StorageLocations;
        if (locations is null || string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder.Trim()))
        {
            return null;
        }

        string full;
        try
        {
            full = PathIdentity.Normalize(folder);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return "This is not a valid path.";
        }

        if (use == DataFolderUse.Backups && DataFolderGuard.IsSameOrInside(full, PathIdentity.Normalize(Path.Combine(DataDirectory, "Backups"))))
        {
            return null;
        }

        var candidates = new List<string>();
        foreach (var location in locations)
        {
            try
            {
                var normalized = PathIdentity.Normalize(location);
                if (DataFolderGuard.IsSameOrInside(full, normalized))
                {
                    candidates.Add(normalized);
                }
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
            }
        }

        if (candidates.Count == 0)
        {
            return OutsideMessage(full);
        }

        // Resolved: the folder (and a link on its way) must still be inside one of the storage locations.
        var real = PathIdentity.Real(full);
        foreach (var location in locations)
        {
            try
            {
                var normalized = PathIdentity.Normalize(location);
                if (DataFolderGuard.IsSameOrInside(real, normalized) || DataFolderGuard.IsSameOrInside(real, PathIdentity.Real(normalized)))
                {
                    return null;
                }
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
            }
        }

        return $"{full} leads to {real}, which is outside the storage locations. Choose a folder inside one of them (Settings → General → Storage).";
    }

    private static string OutsideMessage(string full) =>
        $"{full} is outside the storage locations. Choose a folder inside one of them; the owner sets them under Settings → General → Storage.";

    /// <summary>0.12 (V-02): throws <see cref="UnauthorizedAccessException"/> when <paramref name="path"/> may not be written to (<see cref="FolderProblem"/>).</summary>
    public void EnsureWritable(string path, DataFolderUse use = DataFolderUse.Downloads)
    {
        if (FolderProblem(path, use) is { } problem)
        {
            throw new UnauthorizedAccessException($"StreamZH does not write to {path}: {problem}");
        }
    }

    public void EnsureCreated()
    {
        // A new data folder is private (0700) on Linux and macOS; it holds the database and the sign-in keys.
        if (!OperatingSystem.IsWindows() && !Directory.Exists(DataDirectory))
        {
            Directory.CreateDirectory(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

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