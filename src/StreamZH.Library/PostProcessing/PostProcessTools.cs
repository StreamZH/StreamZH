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
/// <param name="AppDirectory">The program folder; not searched for tools since 0.12 (SV2 N-01).</param>
public sealed record ToolEnvironment(string AppDirectory, string? PathVariable, bool IsWindows, IReadOnlyList<string> ProgramFilesDirectories)
{
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>
    /// &lt;data&gt;/tools: where StreamZH installs par2 and 7-Zip (writable, survives app updates). Null: nothing is
    /// installed or looked for there.
    /// </summary>
    public string? InstalledDirectory { get; init; }

    /// <summary>
    /// 0.12 (V-02): true when a tool folder holds exactly the recorded files (<see cref="ToolFolderHashes.Matches"/>);
    /// replaceable for tests that only pretend files exist.
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, string>, bool> FolderMatches { get; init; } = ToolFolderHashes.Matches;

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
/// Finds par2, 7-Zip and ffprobe: configured path, then the copy installed by StreamZH (&lt;data&gt;/tools/par2, &lt;data&gt;/tools/7-Zip,
/// only when it matches what the installer recorded), then PATH, then the Windows default (C:\Program Files\7-Zip\7z.exe, 7-Zip
/// only). Since 0.12 the program folder (&lt;app&gt;/tools) is not searched. Versions are read once per executable.
/// </summary>
/// <param name="manifest">What StreamZH installed into &lt;data&gt;/tools (0.12); only those copies are run. Default: kept in memory.</param>
public sealed partial class PostProcessTools(IPostProcessToolSettings settings, IToolProcessRunner runner, ToolEnvironment environment, IToolManifest? manifest = null)
{
    private readonly ConcurrentDictionary<string, string?> _versions = new(StringComparer.Ordinal);

    public ToolEnvironment Environment => environment;

    /// <summary>The SHA-256 of the files StreamZH installed (<see cref="ToolInstaller"/> records them).</summary>
    public IToolManifest Manifest { get; } = manifest ?? new MemoryToolManifest();

    /// <summary>
    /// 0.12 (V-02): the copy in &lt;data&gt;/tools/&lt;tool&gt; is used only when its folder holds exactly the files
    /// StreamZH installed there, unchanged. Checked at every <see cref="Find"/>, i.e. before every run.
    /// </summary>
    public bool IsVerifiedInstall(PostProcessTool tool) =>
        environment.InstalledToolDirectory(tool) is { } folder
        && Manifest.Files(tool) is { Count: > 0 } files
        && environment.FolderMatches(folder, files);

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
                if (!environment.FileExists(installed))
                {
                    continue;
                }

                if (IsVerifiedInstall(tool))
                {
                    return new ToolLocation(tool, installed, ToolSource.Installed, configured, problem);
                }

                // Not installed by StreamZH, or changed since: never run (0.12, V-02).
                problem ??= $"{installed} was not installed by StreamZH or was changed since, so it is not used. Install {DisplayName(tool)} again on this page.";
                break;
            }
        }

        // 0.12 (SV2 N-01): no &lt;app&gt;/tools folder. Nothing ships there, and a file placed there would run unchecked;
        // StreamZH uses only its verified install, PATH and Program Files.
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

    /// <summary>
    /// A configured value can be the executable itself or the folder that contains it. Since 0.12 the file must carry
    /// the name of the tool (<see cref="IsToolFileName"/>), so a path to any other program is not run.
    /// </summary>
    public string? ResolveConfigured(PostProcessTool tool, string configured)
    {
        if (environment.FileExists(configured))
        {
            return IsToolFileName(tool, System.IO.Path.GetFileName(configured)) ? configured : null;
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

    /// <summary>
    /// The file names a configured tool may have: par2, par2.exe or par2j*(.exe) (MultiPar); 7z, 7za, 7zz (.exe);
    /// ffprobe(.exe). Case-insensitive.
    /// </summary>
    public static bool IsToolFileName(PostProcessTool tool, string fileName) => tool switch
    {
        PostProcessTool.Par2 => Par2NameRegex().IsMatch(fileName),
        PostProcessTool.FFprobe => FfprobeNameRegex().IsMatch(fileName),
        _ => SevenZipNameRegex().IsMatch(fileName),
    };

    /// <summary>"par2 (par2.exe on Windows)": how the file must be named, for messages.</summary>
    public static string FileNameHint(PostProcessTool tool) => tool switch
    {
        PostProcessTool.Par2 => "par2, par2.exe or par2j64.exe",
        PostProcessTool.FFprobe => "ffprobe or ffprobe.exe",
        _ => "7z, 7za, 7zz or 7z.exe",
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

    [GeneratedRegex(@"^(par2|par2j[A-Za-z0-9_-]{0,20})(\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Par2NameRegex();

    [GeneratedRegex(@"^(7z|7za|7zz)(\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SevenZipNameRegex();

    [GeneratedRegex(@"^ffprobe(\.exe)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FfprobeNameRegex();
}
