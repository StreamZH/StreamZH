// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using StreamZH.Host.Service;

namespace StreamZH.Host.General;

/// <summary>One file of a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string Url);

/// <summary>
/// Which file of a release fits this installation. The packages write a marker file next to the program
/// (<see cref="MarkerFile"/>: "windows-installer", "deb" or "rpm"); without it StreamZH came from an archive (.zip or
/// .tar.gz). The file names follow the release workflow (.github/workflows/release.yml, docs/PACKAGING.md).
/// </summary>
public static class ReleaseAssets
{
    public const string MarkerFile = "package-type";

    public const string WindowsInstaller = "windows-installer";
    public const string Deb = "deb";
    public const string Rpm = "rpm";
    public const string Archive = "archive";
    public const string Docker = "docker";

    /// <summary>How this copy was installed: docker, windows-installer, deb, rpm or archive.</summary>
    public static string InstallKind(string appDirectory, string runtimeMode)
    {
        ArgumentNullException.ThrowIfNull(appDirectory);
        if (runtimeMode == RunningAs.Docker)
        {
            return Docker;
        }

        try
        {
            var marker = Path.Combine(appDirectory, MarkerFile);
            if (File.Exists(marker))
            {
                var kind = File.ReadAllText(marker).Trim().ToLowerInvariant();
                if (kind is WindowsInstaller or Deb or Rpm)
                {
                    return kind;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable marker: offer the archive.
        }

        return Archive;
    }

    /// <summary>The end of the file name that fits: e.g. "-win-x64-setup.exe", "_amd64.deb", "-linux-arm64.tar.gz"; null for Docker or an unknown platform.</summary>
    public static string? Suffix(OSPlatform os, Architecture architecture, string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (arch is null || kind == Docker)
        {
            return null;
        }

        if (os == OSPlatform.Windows)
        {
            return arch != "x64" ? null : kind == WindowsInstaller ? "-win-x64-setup.exe" : "-win-x64.zip";
        }

        if (os == OSPlatform.Linux)
        {
            return kind switch
            {
                Deb => arch == "x64" ? "_amd64.deb" : "_arm64.deb",
                Rpm => arch == "x64" ? ".x86_64.rpm" : ".aarch64.rpm",
                _ => $"-linux-{arch}.tar.gz",
            };
        }

        return os == OSPlatform.OSX ? $"-osx-{arch}.zip" : null;
    }

    /// <summary>The asset whose name ends with <paramref name="suffix"/>, or null.</summary>
    public static ReleaseAsset? Pick(IEnumerable<ReleaseAsset> assets, string? suffix)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return suffix is null ? null : assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The platform this process runs on.</summary>
    public static OSPlatform CurrentOs() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OperatingSystem.IsLinux() ? OSPlatform.Linux
        : OSPlatform.Create("other");
}
