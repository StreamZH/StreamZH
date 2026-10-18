// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using StreamZH.Data;

namespace StreamZH.Library.PostProcessing;

public enum PostProcessTool
{
    Par2,
    SevenZip,

    /// <summary>ffprobe (FFmpeg) reads the media info of video files.</summary>
    FFprobe,
}

public enum ToolSource
{
    NotFound,
    Configured,

    /// <summary>Installed by StreamZH into &lt;data&gt;/tools.</summary>
    Installed,
    Bundled,
    Path,
    WindowsDefault,
}

/// <param name="Path">Full path of the executable, null when not found.</param>
/// <param name="ConfiguredPath">The path from the settings ("" = find automatically).</param>
/// <param name="Problem">Set when a configured path does not point to the tool; the automatic search is used instead.</param>
public sealed record ToolLocation(PostProcessTool Tool, string? Path, ToolSource Source, string ConfiguredPath, string? Problem)
{
    public bool Found => Path is not null;
}

/// <summary>The configured tool paths ("" or null = find automatically).</summary>
public interface IPostProcessToolSettings
{
    string? Par2Path { get; }

    string? SevenZipPath { get; }

    /// <summary>The configured ffprobe ("" or null = find automatically).</summary>
    string? FfprobePath => null;

    /// <summary>Install missing tools automatically (at startup and when the settings are saved).</summary>
    bool AutoInstall => true;
}

public sealed class ConfigPostProcessToolSettings(ConfigRepository config) : IPostProcessToolSettings
{
    public string? Par2Path => config.Get(ConfigKeys.Par2Path);

    public string? SevenZipPath => config.Get(ConfigKeys.SevenZipPath);

    public string? FfprobePath => config.Get(ConfigKeys.FfprobePath);

    /// <summary>On unless the setting is "false".</summary>
    public bool AutoInstall => !string.Equals(config.Get(ConfigKeys.ToolsAutoInstall), "false", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the tool search looks at; replaceable so tests do not depend on the machine.</summary>
public sealed record ToolEnvironment(string AppDirectory, string? PathVariable, bool IsWindows, IReadOnlyList<string> ProgramFilesDirectories)
{
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>
    /// &lt;data&gt;/tools: where StreamZH installs par2 and 7-Zip (writable, survives app updates). Null: nothing is
    /// installed or looked for there.
    /// </summary>
    public string? InstalledDirectory { get; init; }

    /// <summary>Operating system and processor, for picking the download that StreamZH installs.</summary>
    public ToolPlatform Platform { get; init; } = ToolPlatform.Current;

    public static ToolEnvironment Current { get; } = new(
        AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("PATH"),
        OperatingSystem.IsWindows(),
        OperatingSystem.IsWindows()
            ? new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") }
                .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => p!).ToArray()
            : []);

    /// <summary>Folder for tools shipped with (or downloaded next to) StreamZH: &lt;app&gt;/tools.</summary>
    public string BundledDirectory => System.IO.Path.Combine(AppDirectory, "tools");

    /// <summary>&lt;data&gt;/tools/par2 or &lt;data&gt;/tools/7-Zip; null without <see cref="InstalledDirectory"/>.</summary>
    public string? InstalledToolDirectory(PostProcessTool tool) =>
        InstalledDirectory is null ? null : System.IO.Path.Combine(InstalledDirectory, InstalledFolderName(tool));

    public static string InstalledFolderName(PostProcessTool tool) => tool switch
    {
        PostProcessTool.Par2 => "par2",
        PostProcessTool.FFprobe => "ffprobe",
        _ => "7-Zip",
    };
}

/// <summary>
/// Finds par2, 7-Zip and ffprobe: configured path, then the copy installed by StreamZH (&lt;data&gt;/tools/par2, &lt;data&gt;/tools/7-Zip),
/// then &lt;app&gt;/tools, then PATH, then the Windows default (C:\Program Files\7-Zip\7z.exe, 7-Zip only). Versions are read once
/// per executable.
/// </summary>
public sealed partial class PostProcessTools(IPostProcessToolSettings settings, IToolProcessRunner runner, ToolEnvironment environment)
{
    private readonly ConcurrentDictionary<string, string?> _versions = new(StringComparer.Ordinal);

    public ToolEnvironment Environment => environment;

    public ToolLocation Find(PostProcessTool tool)
    {
        var configured = (tool switch
        {
            PostProcessTool.Par2 => settings.Par2Path,
            PostProcessTool.FFprobe => settings.FfprobePath,
            _ => settings.SevenZipPath,
        })?.Trim() ?? string.Empty;
        string? problem = null;

        if (configured.Length > 0)
        {
            if (ResolveConfigured(tool, configured) is { } path)
            {
                return new ToolLocation(tool, path, ToolSource.Configured, configured, null);
            }

            problem = $"{DisplayName(tool)} was not found at {configured}.";
        }

        var names = ExecutableNames(tool);

        if (environment.InstalledToolDirectory(tool) is { } installedDirectory)
        {
            foreach (var name in names)
            {
                var installed = System.IO.Path.Combine(installedDirectory, name);
                if (environment.FileExists(installed))
                {
                    return new ToolLocation(tool, installed, ToolSource.Installed, configured, problem);
                }
            }
        }

        foreach (var name in names)
        {
            var bundled = System.IO.Path.Combine(environment.BundledDirectory, name);
            if (environment.FileExists(bundled))
            {
                return new ToolLocation(tool, bundled, ToolSource.Bundled, configured, problem);
            }
        }

        var separator = environment.IsWindows ? ';' : ':';
        var directories = (environment.PathVariable ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in names)
        {
            foreach (var directory in directories)
            {
                var candidate = System.IO.Path.Combine(directory.Trim('"'), name);
                if (environment.FileExists(candidate))
                {
                    return new ToolLocation(tool, candidate, ToolSource.Path, configured, problem);
                }
            }
        }

        if (environment.IsWindows && tool == PostProcessTool.SevenZip)
        {
            foreach (var programFiles in environment.ProgramFilesDirectories)
            {
                var candidate = System.IO.Path.Combine(programFiles, "7-Zip", "7z.exe");
                if (environment.FileExists(candidate))
                {
                    return new ToolLocation(tool, candidate, ToolSource.WindowsDefault, configured, problem);
                }
            }
        }

        return new ToolLocation(tool, null, ToolSource.NotFound, configured, problem);
    }

    /// <summary>A configured value can be the executable itself or the folder that contains it.</summary>
    public string? ResolveConfigured(PostProcessTool tool, string configured)
    {
        if (environment.FileExists(configured))
        {
            return configured;
        }

        return ExecutableNames(tool).Select(name => System.IO.Path.Combine(configured, name)).FirstOrDefault(environment.FileExists);
    }

    public IReadOnlyList<string> ExecutableNames(PostProcessTool tool) => (tool, environment.IsWindows) switch
    {
        (PostProcessTool.Par2, true) => ["par2.exe"],
        (PostProcessTool.Par2, false) => ["par2"],
        (PostProcessTool.SevenZip, true) => ["7z.exe", "7za.exe"],
        (PostProcessTool.FFprobe, true) => ["ffprobe.exe"],
        (PostProcessTool.FFprobe, false) => ["ffprobe"],
        _ => ["7z", "7zz", "7za"],
    };

    public static string DisplayName(PostProcessTool tool) => tool switch
    {
        PostProcessTool.Par2 => "par2",
        PostProcessTool.FFprobe => "ffprobe",
        _ => "7-Zip",
    };

    /// <summary>The key used in the API and the status window: par2, 7zip, ffprobe.</summary>
    public static string Key(PostProcessTool tool) => tool switch
    {
        PostProcessTool.Par2 => "par2",
        PostProcessTool.FFprobe => "ffprobe",
        _ => "7zip",
    };

    /// <summary>"par2cmdline 0.8.1", "7-Zip 23.01" or "ffprobe 9.0.2"; null when the tool does not run.</summary>
    public async Task<string?> GetVersionAsync(string path, CancellationToken cancellationToken)
    {
        var key = $"{path}|{SafeWriteTime(path)}";
        if (_versions.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string? version;
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            IReadOnlyList<string> arguments = name.StartsWith("7z", StringComparison.OrdinalIgnoreCase) ? []
                : name.StartsWith("ffprobe", StringComparison.OrdinalIgnoreCase) ? ["-version"]
                : ["-V"];
            var result = await runner.RunAsync(path, arguments, null, null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            version = ParseVersion(result.Output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or TimeoutException or UnauthorizedAccessException)
        {
            version = null;
        }

        _versions[key] = version;
        return version;
    }

    public static string? ParseVersion(string output)
    {
        var sevenZip = SevenZipVersionRegex().Match(output);
        if (sevenZip.Success)
        {
            return $"7-Zip {sevenZip.Groups[1].Value}";
        }

        var ffprobe = FfprobeVersionRegex().Match(output);
        if (ffprobe.Success)
        {
            return $"ffprobe {ffprobe.Groups[1].Value}";
        }

        var par2 = Par2VersionRegex().Match(output);
        return par2.Success ? $"{par2.Groups[1].Value} {par2.Groups[2].Value}" : null;
    }

    private static long SafeWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // "7-Zip 23.01 (x64)", "7-Zip [64] 16.02", "7-Zip (z) 23.01 (x64)", "7-Zip (a) 23.01"
    [GeneratedRegex(@"7-Zip(?: \(\w\))?(?: \[\d+\])? (\d+\.\d+)")]
    private static partial Regex SevenZipVersionRegex();

    // "par2cmdline version 0.8.1", "par2cmdline-turbo version 1.1.1"
    [GeneratedRegex(@"(par2\S*) version (\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex Par2VersionRegex();

    // "ffprobe version 9.0.2-essentials_build-www.gyan.dev", "ffprobe version 6.1.1-3ubuntu5", "ffprobe version n7.1"
    [GeneratedRegex(@"ffprobe version n?(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex FfprobeVersionRegex();
}
