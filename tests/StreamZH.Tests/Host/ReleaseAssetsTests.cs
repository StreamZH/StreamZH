// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;
using StreamZH.Core;
using StreamZH.Host.General;
using StreamZH.Host.Service;

namespace StreamZH.Tests.Host;

/// <summary>The update check offers the release file that fits the installation (names from the release workflow).</summary>
public sealed class ReleaseAssetsTests
{
    private static readonly ReleaseAsset[] Release =
    [
        .. new[]
        {
            "SHA256SUMS",
            "StreamZH-1.0.0-win-x64-setup.exe",
            "StreamZH-1.0.0-win-x64.zip",
            "StreamZH-1.0.0-linux-x64.tar.gz",
            "StreamZH-1.0.0-linux-arm64.tar.gz",
            "streamzh_1.0.0_amd64.deb",
            "streamzh_1.0.0_arm64.deb",
            "streamzh-1.0.0.x86_64.rpm",
            "streamzh-1.0.0.aarch64.rpm",
            "StreamZH-1.0.0-osx-arm64.zip",
            "StreamZH-1.0.0-osx-x64.zip",
        }.Select(name => new ReleaseAsset(name, $"{AppInfo.RepositoryUrl}/releases/download/v1.0.0/{name}")),
    ];

    public static TheoryData<string, Architecture, string, string?> Cases => new()
    {
        { "windows", Architecture.X64, ReleaseAssets.WindowsInstaller, "StreamZH-1.0.0-win-x64-setup.exe" },
        { "windows", Architecture.X64, ReleaseAssets.Archive, "StreamZH-1.0.0-win-x64.zip" },
        { "windows", Architecture.Arm64, ReleaseAssets.Archive, null },
        { "linux", Architecture.X64, ReleaseAssets.Archive, "StreamZH-1.0.0-linux-x64.tar.gz" },
        { "linux", Architecture.Arm64, ReleaseAssets.Archive, "StreamZH-1.0.0-linux-arm64.tar.gz" },
        { "linux", Architecture.X64, ReleaseAssets.Deb, "streamzh_1.0.0_amd64.deb" },
        { "linux", Architecture.Arm64, ReleaseAssets.Deb, "streamzh_1.0.0_arm64.deb" },
        { "linux", Architecture.X64, ReleaseAssets.Rpm, "streamzh-1.0.0.x86_64.rpm" },
        { "linux", Architecture.Arm64, ReleaseAssets.Rpm, "streamzh-1.0.0.aarch64.rpm" },
        { "linux", Architecture.X64, ReleaseAssets.Docker, null },
        { "linux", Architecture.Arm, ReleaseAssets.Archive, null },
        { "osx", Architecture.Arm64, ReleaseAssets.Archive, "StreamZH-1.0.0-osx-arm64.zip" },
        { "osx", Architecture.X64, ReleaseAssets.Archive, "StreamZH-1.0.0-osx-x64.zip" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_file_that_fits_the_platform_and_package_is_picked(string os, Architecture architecture, string kind, string? expected)
    {
        var platform = os switch
        {
            "windows" => OSPlatform.Windows,
            "osx" => OSPlatform.OSX,
            _ => OSPlatform.Linux,
        };

        Assert.Equal(expected, ReleaseAssets.Pick(Release, ReleaseAssets.Suffix(platform, architecture, kind))?.Name);
    }

    [Fact]
    public void The_package_marker_decides_the_install_kind()
    {
        var folder = Directory.CreateTempSubdirectory("streamzh-marker-");
        try
        {
            Assert.Equal(ReleaseAssets.Archive, ReleaseAssets.InstallKind(folder.FullName, RunningAs.Console));
            Assert.Equal(ReleaseAssets.Docker, ReleaseAssets.InstallKind(folder.FullName, RunningAs.Docker));

            var marker = Path.Combine(folder.FullName, ReleaseAssets.MarkerFile);
            foreach (var kind in new[] { ReleaseAssets.WindowsInstaller, ReleaseAssets.Deb, ReleaseAssets.Rpm })
            {
                File.WriteAllText(marker, kind.ToUpperInvariant() + "\n");
                Assert.Equal(kind, ReleaseAssets.InstallKind(folder.FullName, RunningAs.Systemd));
            }

            File.WriteAllText(marker, "something-else");
            Assert.Equal(ReleaseAssets.Archive, ReleaseAssets.InstallKind(folder.FullName, RunningAs.Console));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_repository_is_one_constant()
    {
        Assert.Equal("streamzh/streamzh", AppInfo.Repository);
        Assert.Equal("https://github.com/streamzh/streamzh", AppInfo.RepositoryUrl);
        Assert.Equal($"https://api.github.com/repos/{AppInfo.Repository}/releases/latest", UpdateChecker.LatestReleaseUrl);
    }

    /// <summary>SV4 X-01: only release links of StreamZH's own repository become the "Download" button.</summary>
    [Theory]
    [InlineData("https://github.com/streamzh/streamzh/releases/tag/v1.0.0", true)]
    [InlineData("https://github.com/streamzh/streamzh/releases/download/v1.0.0/StreamZH-1.0.0-win-x64-setup.exe", true)]
    [InlineData("https://github.com/StreamZH/StreamZH/releases/tag/v1.0.0", true)]
    [InlineData("https://github.com/StreamZH/streamzh/releases/tag/v1.0.0", true)]
    [InlineData("https://github.com/cloudzh/streamzh/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com/attacker/streamzh/releases/tag/v99.0.0", false)]
    [InlineData("https://github.com/attacker/streamzh/releases/download/v99.0.0/StreamZH-99.0.0-win-x64-setup.exe", false)]
    [InlineData("https://github.com/streamzh/streamzh-fork/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com/streamzh/streamzh/issues/1", false)]
    [InlineData("https://github.com/streamzh/streamzh/releases", false)]
    [InlineData("https://github.com/streamzh/streamzh/releases/../../../attacker/x/releases/download/a.exe", false)]
    [InlineData("https://github.com/streamzh/streamzh/releases/%2e%2e/%2e%2e/%2e%2e/attacker/x/releases/download/a.exe", false)]
    [InlineData("https://github.com/streamzh/streamzh/releases/%2E%2E/a.exe", false)]
    [InlineData("http://github.com/streamzh/streamzh/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com:8443/streamzh/streamzh/releases/tag/v1.0.0", false)]
    [InlineData("https://user@github.com/streamzh/streamzh/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com.evil.example/streamzh/streamzh/releases/tag/v1.0.0", false)]
    [InlineData("https://evil.example/streamzh/streamzh/releases/tag/v1.0.0", false)]
    [InlineData("/streamzh/streamzh/releases/tag/v1.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Release_links_must_belong_to_the_repository(string? url, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsGitHubReleaseUrl(url));
}
