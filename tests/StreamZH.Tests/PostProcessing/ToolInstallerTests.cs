// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Library.PostProcessing;

namespace StreamZH.Tests.PostProcessing;

public sealed class ToolInstallerTests : IDisposable
{
    private static readonly ToolPlatform WindowsX64 = new(ToolOs.Windows, Architecture.X64);
    private static readonly ToolPlatform WindowsArm64 = new(ToolOs.Windows, Architecture.Arm64);
    private static readonly ToolPlatform LinuxX64 = new(ToolOs.Linux, Architecture.X64);

    private readonly TempFolder _folder = new();
    private readonly FakeToolDownloads _downloads = new();
    private readonly InProcessEventBus _events = new();
    private int _published;

    public ToolInstallerTests()
    {
        _events.Subscribe<OngoingChanged>((_, _) =>
        {
            Interlocked.Increment(ref _published);
            return ValueTask.CompletedTask;
        });
    }

    private string ToolsFolder => _folder.Combine("data", "tools");

    // ---- Packages ----

    [Theory]
    [InlineData(ToolOs.Windows, Architecture.X64, "par2cmdline-turbo-1.5.0-win-x64.zip", "7z2603-x64.msi")]
    [InlineData(ToolOs.Windows, Architecture.Arm64, "par2cmdline-turbo-1.5.0-win-arm64.zip", "7z2603-arm64.exe")]
    [InlineData(ToolOs.Linux, Architecture.X64, "par2cmdline-turbo-1.5.0-linux-amd64.zip", "7z2603-linux-x64.tar.xz")]
    [InlineData(ToolOs.Linux, Architecture.Arm64, "par2cmdline-turbo-1.5.0-linux-arm64.zip", "7z2603-linux-arm64.tar.xz")]
    [InlineData(ToolOs.Linux, Architecture.Arm, "par2cmdline-turbo-1.5.0-linux-armhf.zip", null)]
    [InlineData(ToolOs.MacOS, Architecture.Arm64, "par2cmdline-turbo-1.5.0-macos-universal.zip", "7z2603-mac.tar.xz")]
    [InlineData(ToolOs.MacOS, Architecture.X64, "par2cmdline-turbo-1.5.0-macos-universal.zip", "7z2603-mac.tar.xz")]
    [InlineData(ToolOs.Windows, Architecture.X86, null, null)]
    [InlineData(ToolOs.Other, Architecture.X64, null, null)]
    public void Picks_the_download_for_the_platform(ToolOs os, Architecture architecture, string? par2, string? sevenZip)
    {
        var platform = new ToolPlatform(os, architecture);

        var par2Package = ToolPackages.For(PostProcessTool.Par2, platform);
        var sevenZipPackage = ToolPackages.For(PostProcessTool.SevenZip, platform);

        Assert.Equal(par2, par2Package?.Download.FileName);
        Assert.Equal(sevenZip, sevenZipPackage?.Download.FileName);
        if (par2Package is not null)
        {
            Assert.Equal("1.5.0", par2Package.Version);
            Assert.Equal(ToolInstallMethod.Zip, par2Package.Method);
            Assert.Equal([os == ToolOs.Windows ? "par2.exe" : "par2"], par2Package.Files);
            Assert.Equal($"https://github.com/animetosho/par2cmdline-turbo/releases/download/v1.5.0/{par2}", par2Package.Download.Url.AbsoluteUri);
            Assert.Matches("^[0-9a-f]{64}$", par2Package.Download.Sha256);
        }

        if (sevenZipPackage is not null)
        {
            Assert.Equal("26.03", sevenZipPackage.Version);
            Assert.StartsWith("https://github.com/ip7z/7zip/releases/download/26.03/", sevenZipPackage.Download.Url.AbsoluteUri);
            Assert.Equal(os == ToolOs.Windows ? ["7z.exe", "7z.dll"] : ["7zz"], sevenZipPackage.Files);
        }
    }

    [Theory]
    [InlineData(ToolOs.Windows, Architecture.X64, true)]
    [InlineData(ToolOs.Windows, Architecture.Arm64, false)]
    [InlineData(ToolOs.Linux, Architecture.X64, false)]
    [InlineData(ToolOs.MacOS, Architecture.Arm64, false)]
    public void Ffprobe_is_installed_on_windows_x64_only(ToolOs os, Architecture architecture, bool installable)
    {
        var package = ToolPackages.For(PostProcessTool.FFprobe, new ToolPlatform(os, architecture));

        Assert.Equal(installable, package is not null);
        if (package is not null)
        {
            Assert.Equal("9.0.2", package.Version);
            Assert.Equal(ToolInstallMethod.Zip, package.Method);
            Assert.Equal(["ffprobe.exe"], package.Files);
            Assert.Equal("https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip", package.Download.Url.AbsoluteUri);
            Assert.Equal("60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba", package.Download.Sha256);
            Assert.StartsWith("GPL", package.License, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Ffprobe_zip_unpacks_only_ffprobe_and_the_license_files()
    {
        const string root = "ffmpeg-9.0.2-essentials_build/";
        var data = TestToolPackages.Zip(
            (root + "bin/ffmpeg.exe", "ffmpeg"),
            (root + "bin/ffplay.exe", "ffplay"),
            (root + "bin/ffprobe.exe", "ffprobe"),
            (root + "doc/ffprobe.html", "<html>"),
            (root + "presets/libvpx-720p.ffpreset", "preset"),
            (root + "LICENSE", "GPL v3"),
            (root + "README.txt", "Source Code: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b"));
        var download = TestToolPackages.Download("ffmpeg-9.0.2-essentials_build.zip", data);
        _downloads.Add(download, data);
        var package = new ToolPackage(PostProcessTool.FFprobe, ToolPackages.FfprobeName, "9.0.2", ToolPackages.FfprobeLicense, ToolInstallMethod.Zip, download, ["ffprobe.exe"]);
        var installer = Installer(WindowsX64, new ToolCatalog((tool, _) => tool == PostProcessTool.FFprobe ? package : null), NoProcesses());

        var result = await installer.InstallAsync(PostProcessTool.FFprobe, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var folder = Path.Combine(ToolsFolder, "ffprobe");
        Assert.Equal(
            ["LICENSE", "README.txt", "ffprobe.exe", ToolInstaller.VersionFileName],
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("9.0.2", installer.Installed(PostProcessTool.FFprobe)!.Version);
        Assert.False(Directory.Exists(Path.Combine(ToolsFolder, ToolInstaller.DownloadFolderName)));
    }

    [Fact]
    public void Windows_7zip_uses_the_msi_with_the_installer_exe_as_fallback()
    {
        var x64 = ToolPackages.For(PostProcessTool.SevenZip, WindowsX64)!;
        Assert.Equal(ToolInstallMethod.WindowsMsi, x64.Method);
        Assert.Equal("c0680064d698a62dd4a5a47f403db356a6531a5473e4c4b1d090ea2590513926", x64.Download.Sha256);
        Assert.Equal("7z2603-x64.exe", x64.Fallback!.FileName);
        Assert.Equal("7zr.exe", x64.Helper!.FileName);
        Assert.Equal("ad4c82fadcbdf93c03b4fc440f300509c7d60c5c2f4d183e35d9d70d6957037d", x64.Helper.Sha256);

        var arm64 = ToolPackages.For(PostProcessTool.SevenZip, WindowsArm64)!;
        Assert.Equal(ToolInstallMethod.SevenZipInstaller, arm64.Method);
        Assert.Equal("7zr.exe", arm64.Helper!.FileName);

        Assert.Equal(ToolInstallMethod.TarXz, ToolPackages.For(PostProcessTool.SevenZip, LinuxX64)!.Method);
    }

    [Fact]
    public void Nothing_is_installable_without_an_install_folder()
    {
        var installer = Installer(LinuxX64, ToolCatalog.Official, NoProcesses(), installedDirectory: null);

        Assert.False(installer.IsInstallable(PostProcessTool.Par2));
        Assert.Null(installer.Package(PostProcessTool.SevenZip));
    }

    // ---- Zip ----

    [Fact]
    public async Task Zip_is_unpacked_into_the_tool_folder_with_a_version_file()
    {
        var (package, data) = TestToolPackages.Par2();
        _downloads.Add(package.Download, data);
        var installer = Installer(LinuxX64, Catalog(par2: package), NoProcesses());

        var result = await installer.InstallAsync(PostProcessTool.Par2, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("1.5.0", result.Version);
        var executable = Path.Combine(ToolsFolder, "par2", package.Executable);
        Assert.True(File.Exists(executable));
        Assert.True(File.Exists(Path.Combine(ToolsFolder, "par2", "COPYING")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(executable).HasFlag(UnixFileMode.UserExecute));
        }

        var info = installer.Installed(PostProcessTool.Par2)!;
        Assert.Equal("par2cmdline-turbo", info.Name);
        Assert.Equal("1.5.0", info.Version);
        Assert.Equal(package.Download.Sha256, info.Sha256);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(ToolsFolder, "par2", ToolInstaller.VersionFileName)));
        Assert.Equal("1.5.0", json.RootElement.GetProperty("version").GetString());

        Assert.False(Directory.Exists(Path.Combine(ToolsFolder, ToolInstaller.DownloadFolderName)));
        Assert.Equal(ToolInstallStatus.Idle, installer.Status(PostProcessTool.Par2));
        Assert.True(_published >= 2);
    }

    [Fact]
    public async Task Reinstall_replaces_the_old_copy()
    {
        TestToolPackages.Create(Path.Combine(ToolsFolder, "par2"), "par2", "par2.exe", "old-file.txt");
        var (package, data) = TestToolPackages.Par2();
        _downloads.Add(package.Download, data);
        var installer = Installer(LinuxX64, Catalog(par2: package), NoProcesses());

        var result = await installer.InstallAsync(PostProcessTool.Par2, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(Path.Combine(ToolsFolder, "par2", "old-file.txt")));
        Assert.Contains("par2cmdline-turbo", File.ReadAllText(Path.Combine(ToolsFolder, "par2", package.Executable)));
    }

    [Fact]
    public async Task Hash_mismatch_is_refused_and_leaves_nothing_installed()
    {
        var (package, data) = TestToolPackages.Par2();
        var tampered = package with { Download = package.Download with { Sha256 = new string('0', 64) } };
        _downloads.Add(tampered.Download, data);
        var installer = Installer(LinuxX64, Catalog(par2: tampered), NoProcesses());

        var result = await installer.InstallAsync(PostProcessTool.Par2, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.Error);
        Assert.Contains("Nothing was installed", result.Error);
        Assert.False(Directory.Exists(Path.Combine(ToolsFolder, "par2")));
        Assert.False(Directory.Exists(Path.Combine(ToolsFolder, ToolInstaller.DownloadFolderName)));
        var status = installer.Status(PostProcessTool.Par2);
        Assert.False(status.Installing);
        Assert.Equal(result.Error, status.Error);
    }

    [Fact]
    public async Task Failed_reinstall_keeps_the_old_copy()
    {
        TestToolPackages.Create(Path.Combine(ToolsFolder, "par2"), "par2");
        var (package, _) = TestToolPackages.Par2();
        var installer = Installer(LinuxX64, Catalog(par2: package), NoProcesses());

        var result = await installer.InstallAsync(PostProcessTool.Par2, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("HTTP 404", result.Error);
        Assert.Equal("par2", File.ReadAllText(Path.Combine(ToolsFolder, "par2", "par2")));
    }

    [Fact]
    public async Task Zip_without_the_executable_is_refused()
    {
        var data = TestToolPackages.Zip(("readme.txt", "nothing here"));
        var (package, _) = TestToolPackages.Par2();
        var broken = package with { Download = TestToolPackages.Download("broken.zip", data) };
        _downloads.Add(broken.Download, data);
        var installer = Installer(LinuxX64, Catalog(par2: broken), NoProcesses());

        var result = await installer.InstallAsync(PostProcessTool.Par2, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not found in broken.zip", result.Error);
        Assert.False(Directory.Exists(Path.Combine(ToolsFolder, "par2")));
    }

    // ---- Windows 7-Zip ----

    [Fact]
    public async Task Windows_7zip_is_unpacked_with_an_administrative_msi_install()
    {
        var package = WindowsSevenZip(out _, out _);
        var runner = new FakeToolRunner((file, arguments) =>
        {
            Assert.EndsWith("msiexec.exe", file);
            var target = arguments.Single(a => a.StartsWith("TARGETDIR=", StringComparison.Ordinal))["TARGETDIR=".Length..];
            TestToolPackages.Create(target, Path.Combine("Files", "7-Zip", "7z.exe"), Path.Combine("Files", "7-Zip", "7z.dll"), Path.Combine("Files", "7-Zip", "License.txt"));
            return new ToolProcessResult(0, string.Empty);
        });
        var installer = Installer(WindowsX64, Catalog(sevenZip: package), runner);

        var result = await installer.InstallAsync(PostProcessTool.SevenZip, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("/a", call.Arguments[0]);
        Assert.EndsWith("7z2603-x64.msi", call.Arguments[1]);
        Assert.Equal("/qn", call.Arguments[2]);
        Assert.StartsWith("TARGETDIR=", call.Arguments[3]);
        Assert.Equal(["https://downloads.test/7z2603-x64.msi"], _downloads.Requests.Select(u => u.AbsoluteUri));

        var folder = Path.Combine(ToolsFolder, "7-Zip");
        Assert.True(File.Exists(Path.Combine(folder, "7z.exe")));
        Assert.True(File.Exists(Path.Combine(folder, "7z.dll")));
        Assert.True(File.Exists(Path.Combine(folder, "License.txt")));
        Assert.Equal("26.03", installer.Installed(PostProcessTool.SevenZip)!.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows_7zip_falls_back_to_7zr_when_msiexec_fails(bool msiexecMissing)
    {
        var package = WindowsSevenZip(out var sevenZr, out var installerExe);
        var runner = new FakeToolRunner((file, arguments) =>
        {
            if (file.EndsWith("msiexec.exe", StringComparison.Ordinal))
            {
                return msiexecMissing ? throw new Win32Exception(2, "msiexec.exe not found") : new ToolProcessResult(1603, "Fatal error during installation.");
            }

            Assert.EndsWith("7zr.exe", file);
            Assert.Equal("x", arguments[0]);
            Assert.EndsWith(installerExe.FileName, arguments[1]);
            Assert.StartsWith("-o", arguments[2]);
            Assert.Equal("-y", arguments[3]);
            TestToolPackages.Create(arguments[2][2..], "7z.exe", "7z.dll", "7zFM.exe", "License.txt");
            return new ToolProcessResult(0, "Everything is Ok");
        });
        var installer = Installer(WindowsX64, Catalog(sevenZip: package), runner);

        var result = await installer.InstallAsync(PostProcessTool.SevenZip, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(
            ["https://downloads.test/7z2603-x64.msi", sevenZr.Url.AbsoluteUri, installerExe.Url.AbsoluteUri],
            _downloads.Requests.Select(u => u.AbsoluteUri));
        var folder = Path.Combine(ToolsFolder, "7-Zip");
        Assert.True(File.Exists(Path.Combine(folder, "7z.exe")));
        Assert.True(File.Exists(Path.Combine(folder, "7z.dll")));
        Assert.False(File.Exists(Path.Combine(folder, "7zFM.exe")));
    }

    [Fact]
    public async Task Windows_7zip_without_7z_dll_is_refused()
    {
        var package = WindowsSevenZip(out _, out _);
        var runner = new FakeToolRunner((file, arguments) =>
        {
            if (file.EndsWith("msiexec.exe", StringComparison.Ordinal))
            {
                return new ToolProcessResult(1, "failed");
            }

            TestToolPackages.Create(arguments[2][2..], "7z.exe");
            return new ToolProcessResult(0, string.Empty);
        });
        var installer = Installer(WindowsX64, Catalog(sevenZip: package), runner);

        var result = await installer.InstallAsync(PostProcessTool.SevenZip, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("7z.dll", result.Error);
        Assert.False(Directory.Exists(Path.Combine(ToolsFolder, "7-Zip")));
    }

    // ---- Linux and macOS 7-Zip ----

    [Fact]
    public async Task Tar_xz_is_unpacked_with_tar()
    {
        var data = TestData.Random(500, 7);
        var download = TestToolPackages.Download("7z2603-linux-x64.tar.xz", data);
        _downloads.Add(download, data);
        var package = new ToolPackage(PostProcessTool.SevenZip, "7-Zip", "26.03", "LGPL", ToolInstallMethod.TarXz, download, ["7zz"]);
        var runner = new FakeToolRunner((file, arguments) =>
        {
            Assert.Equal("tar", file);
            Assert.Equal("-xJf", arguments[0]);
            Assert.EndsWith(download.FileName, arguments[1]);
            Assert.Equal("-C", arguments[2]);
            TestToolPackages.Create(arguments[3], "7zz", "7zzs", "License.txt", Path.Combine("MANUAL", "start.htm"));
            return new ToolProcessResult(0, string.Empty);
        });
        var installer = Installer(LinuxX64, Catalog(sevenZip: package), runner);

        var result = await installer.InstallAsync(PostProcessTool.SevenZip, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var executable = Path.Combine(ToolsFolder, "7-Zip", "7zz");
        Assert.True(File.Exists(executable));
        Assert.False(File.Exists(Path.Combine(ToolsFolder, "7-Zip", "7zzs")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(executable).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Theory]
    [InlineData("missing", "tar was not found")]
    [InlineData("no-xz", "xz is missing")]
    [InlineData("broken", "exit code 2")]
    public async Task Tar_problems_are_explained(string problem, string expected)
    {
        var data = TestData.Random(100, 8);
        var download = TestToolPackages.Download("7z.tar.xz", data);
        _downloads.Add(download, data);
        var package = new ToolPackage(PostProcessTool.SevenZip, "7-Zip", "26.03", "LGPL", ToolInstallMethod.TarXz, download, ["7zz"]);
        var runner = new FakeToolRunner((_, _) => problem switch
        {
            "missing" => throw new Win32Exception(2, "No such file or directory"),
            "no-xz" => new ToolProcessResult(2, "tar (child): xz: Cannot exec: No such file or directory\ntar: Error is not recoverable: exiting now"),
            _ => new ToolProcessResult(2, "tar: Unexpected EOF in archive"),
        });
        var installer = Installer(LinuxX64, Catalog(sevenZip: package), runner);

        var result = await installer.InstallAsync(PostProcessTool.SevenZip, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(expected, result.Error);
        Assert.False(Directory.Exists(Path.Combine(ToolsFolder, "7-Zip")));
    }

    // ---- Running installations ----

    [Fact]
    public async Task A_second_request_joins_the_running_installation()
    {
        var (package, data) = TestToolPackages.Par2();
        _downloads.Add(package.Download, data);
        _downloads.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = Installer(LinuxX64, Catalog(par2: package), NoProcesses());

        var first = installer.InstallAsync(PostProcessTool.Par2, CancellationToken.None);
        var second = installer.InstallAsync(PostProcessTool.Par2, CancellationToken.None);
        Assert.Same(installer.StartInstall(PostProcessTool.Par2), installer.StartInstall(PostProcessTool.Par2));

        var status = installer.Status(PostProcessTool.Par2);
        Assert.True(status.Installing);
        Assert.NotNull(status.Progress);

        _downloads.Gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.True(r.Success, r.Error));
        Assert.Single(_downloads.Requests);
        Assert.False(installer.Status(PostProcessTool.Par2).Installing);
    }

    [Fact]
    public async Task Missing_tools_are_installed_when_automatic_installation_is_on()
    {
        var (package, data) = TestToolPackages.Par2();
        _downloads.Add(package.Download, data);

        // 7-Zip is found on PATH, par2 is not.
        var sevenZip = Path.Combine("/usr/bin", OperatingSystem.IsWindows() ? "7z.exe" : "7z");
        var installer = Installer(LinuxX64, Catalog(par2: package), NoProcesses(), pathVariable: "/usr/bin", existing: [sevenZip]);

        await installer.InstallMissingAsync(CancellationToken.None);

        Assert.Single(_downloads.Requests);
        Assert.NotNull(installer.Installed(PostProcessTool.Par2));
        Assert.Null(installer.Installed(PostProcessTool.SevenZip));
    }

    [Theory]
    [InlineData("7-Zip [64] 16.02 : Copyright (c) 1999-2016 Igor Pavlov : 2016-05-21\np7zip Version 16.02", true)]
    [InlineData("7-Zip (a) 23.01 (x64) : Copyright (c) 1999-2023 Igor Pavlov : 2023-06-20", false)]
    [InlineData("something else", false)]
    public async Task An_old_7zip_on_path_gets_the_pinned_one_installed_next_to_it(string versionOutput, bool installs)
    {
        // S2-5: p7zip 16.02 on older NAS and Linux systems handles links in archives less carefully than 7-Zip 22+.
        var data = TestData.Random(500, 9);
        var download = TestToolPackages.Download("7z2603-linux-x64.tar.xz", data);
        _downloads.Add(download, data);
        var package = new ToolPackage(PostProcessTool.SevenZip, "7-Zip", "26.03", "LGPL", ToolInstallMethod.TarXz, download, ["7zz"]);
        var onPath = Path.Combine("/usr/bin", "7z");
        var runner = new FakeToolRunner((file, arguments) =>
        {
            if (file == onPath)
            {
                return new ToolProcessResult(0, versionOutput);
            }

            TestToolPackages.Create(arguments[3], "7zz");
            return new ToolProcessResult(0, string.Empty);
        });
        var installer = Installer(LinuxX64, Catalog(sevenZip: package), runner, pathVariable: "/usr/bin", existing: [onPath]);

        await installer.InstallMissingAsync(CancellationToken.None);

        // The copy in <data>/tools comes before PATH in the search (PostProcessToolsTests), so it is the one used.
        Assert.Equal(installs, installer.Installed(PostProcessTool.SevenZip) is not null);
        Assert.Equal(installs, File.Exists(Path.Combine(ToolsFolder, "7-Zip", "7zz")));
    }

    [Theory]
    [InlineData("7-Zip 16.02", true)]
    [InlineData("7-Zip 9.20", true)]
    [InlineData("7-Zip 21.07", true)]
    [InlineData("7-Zip 22.01", false)]
    [InlineData("7-Zip 26.03", false)]
    [InlineData("par2cmdline 0.8.1", false)]
    [InlineData(null, false)]
    public void Old_7zip_versions(string? version, bool outdated) => Assert.Equal(outdated, ToolInstaller.IsOutdatedSevenZip(version));

    [Fact]
    public async Task Hosted_service_installs_missing_tools_after_startup_and_never_throws()
    {
        var (package, data) = TestToolPackages.Par2();
        _downloads.Add(package.Download, data);
        var sevenZip = package with { Tool = PostProcessTool.SevenZip, Download = TestToolPackages.Download("missing.zip", [1]) };
        var installer = Installer(LinuxX64, Catalog(par2: package, sevenZip: sevenZip), NoProcesses());
        using var service = new ToolAutoInstaller(installer, new ToolAutoInstallOptions(TimeSpan.Zero), TimeProvider.System, NullLogger<ToolAutoInstaller>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        Assert.NotNull(installer.Installed(PostProcessTool.Par2));
        Assert.Contains("HTTP 404", installer.Status(PostProcessTool.SevenZip).Error);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Automatic_installation_can_be_switched_off()
    {
        var (package, data) = TestToolPackages.Par2();
        _downloads.Add(package.Download, data);
        var installer = Installer(LinuxX64, Catalog(par2: package), NoProcesses(), settings: new AutoInstallSettings(false));

        await installer.InstallMissingAsync(CancellationToken.None);

        Assert.False(installer.StartIfMissing(PostProcessTool.Par2));
        Assert.Empty(_downloads.Requests);
    }

    [Fact]
    public async Task Unsupported_platform_reports_why()
    {
        var installer = Installer(new ToolPlatform(ToolOs.Other, Architecture.X64), ToolCatalog.Official, NoProcesses());

        var result = await installer.InstallAsync(PostProcessTool.Par2, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cannot install par2", result.Error);
        Assert.Empty(_downloads.Requests);
    }

    // ---- Helpers ----

    private ToolInstaller Installer(
        ToolPlatform platform,
        ToolCatalog catalog,
        IToolProcessRunner runner,
        string? installedDirectory = "",
        string pathVariable = "",
        string[]? existing = null,
        IPostProcessToolSettings? settings = null)
    {
        var fake = new HashSet<string>(existing ?? [], StringComparer.Ordinal);
        var environment = new ToolEnvironment(_folder.Combine("app"), pathVariable, platform.Os == ToolOs.Windows, [])
        {
            InstalledDirectory = installedDirectory == string.Empty ? ToolsFolder : installedDirectory,
            Platform = platform,
            // Real files only inside the test folder, so tools installed on this machine do not count.
            FileExists = path => fake.Contains(path) || (path.StartsWith(_folder.Path, StringComparison.Ordinal) && File.Exists(path)),
        };
        settings ??= new FixedToolSettings();
        var tools = new PostProcessTools(settings, runner, environment);
        return new ToolInstaller(
            new FakeHttpClientFactory(_downloads), runner, environment, catalog, tools, settings, _events, TimeProvider.System, NullLogger<ToolInstaller>.Instance);
    }

    private static ToolCatalog Catalog(ToolPackage? par2 = null, ToolPackage? sevenZip = null) =>
        new((tool, _) => tool switch { PostProcessTool.Par2 => par2, PostProcessTool.SevenZip => sevenZip, _ => null });

    private static FakeToolRunner NoProcesses() => new((file, _) => throw new InvalidOperationException($"{file} must not be started"));

    private ToolPackage WindowsSevenZip(out ToolDownload sevenZr, out ToolDownload installerExe)
    {
        var msiData = TestData.Random(300, 1);
        var exeData = TestData.Random(300, 2);
        var sevenZrData = TestData.Random(100, 3);
        var msi = TestToolPackages.Download("7z2603-x64.msi", msiData);
        installerExe = TestToolPackages.Download("7z2603-x64.exe", exeData);
        sevenZr = TestToolPackages.Download("7zr.exe", sevenZrData);
        _downloads.Add(msi, msiData);
        _downloads.Add(installerExe, exeData);
        _downloads.Add(sevenZr, sevenZrData);
        return new ToolPackage(PostProcessTool.SevenZip, "7-Zip", "26.03", "LGPL", ToolInstallMethod.WindowsMsi, msi, ["7z.exe", "7z.dll"], sevenZr, installerExe);
    }

    private sealed class AutoInstallSettings(bool autoInstall) : IPostProcessToolSettings
    {
        public string? Par2Path => null;

        public string? SevenZipPath => null;

        public bool AutoInstall => autoInstall;
    }

    public void Dispose()
    {
        _downloads.Dispose();
        _folder.Dispose();
    }
}
