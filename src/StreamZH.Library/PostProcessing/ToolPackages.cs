// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;

namespace StreamZH.Library.PostProcessing;

public enum ToolOs
{
    Other,
    Windows,
    Linux,
    MacOS,
}

/// <summary>Operating system and processor StreamZH runs on, for picking the right download.</summary>
public sealed record ToolPlatform(ToolOs Os, Architecture Architecture)
{
    public static ToolPlatform Current { get; } = new(
        OperatingSystem.IsWindows() ? ToolOs.Windows
            : OperatingSystem.IsLinux() ? ToolOs.Linux
            : OperatingSystem.IsMacOS() ? ToolOs.MacOS
            : ToolOs.Other,
        RuntimeInformation.OSArchitecture);

    public override string ToString() => $"{(Os == ToolOs.Other ? RuntimeInformation.OSDescription : Os.ToString())} {Architecture}";
}

/// <summary>One file of an official release, pinned by its SHA-256.</summary>
public sealed record ToolDownload(string FileName, Uri Url, string Sha256);

public enum ToolInstallMethod
{
    /// <summary>Zip archive with the executable (par2cmdline-turbo).</summary>
    Zip,

    /// <summary>7-Zip .msi, unpacked with an administrative install (msiexec /a, no admin rights needed).</summary>
    WindowsMsi,

    /// <summary>7-Zip installer .exe (a 7z self-extracting archive), unpacked with 7zr.exe.</summary>
    SevenZipInstaller,

    /// <summary>.tar.xz archive, unpacked with tar (Linux and macOS 7-Zip).</summary>
    TarXz,
}

/// <param name="Name">Name of the project, e.g. "par2cmdline-turbo".</param>
/// <param name="Files">Files that are copied into the tool folder; the first one is the executable.</param>
/// <param name="Helper">7zr.exe for <see cref="ToolInstallMethod.SevenZipInstaller"/> (and the fallback of the .msi).</param>
/// <param name="Fallback">Installer .exe used with <see cref="Helper"/> when the .msi cannot be unpacked.</param>
public sealed record ToolPackage(
    PostProcessTool Tool,
    string Name,
    string Version,
    string License,
    ToolInstallMethod Method,
    ToolDownload Download,
    IReadOnlyList<string> Files,
    ToolDownload? Helper = null,
    ToolDownload? Fallback = null)
{
    public string Executable => Files[0];
}

/// <summary>Picks the package to install; replaceable in tests, which cannot use the real downloads.</summary>
public sealed record ToolCatalog(Func<PostProcessTool, ToolPlatform, ToolPackage?> Find)
{
    public static ToolCatalog Official { get; } = new(ToolPackages.For);
}

/// <summary>
/// The pinned par2cmdline-turbo and 7-Zip releases StreamZH installs by itself (official GitHub releases, SHA-256 from the
/// release pages). Both are separate programs under their own licenses; StreamZH only calls them.
/// </summary>
public static class ToolPackages
{
    public const string Par2Name = "par2cmdline-turbo";
    public const string Par2Version = "1.5.0";
    public const string Par2License = "GPL-2.0-or-later";
    public const string SevenZipName = "7-Zip";
    public const string SevenZipVersion = "26.03";
    public const string SevenZipLicense = "LGPL-2.1-or-later with the unRAR restriction";

    private const string Par2Base = "https://github.com/animetosho/par2cmdline-turbo/releases/download/v1.5.0/";
    private const string SevenZipBase = "https://github.com/ip7z/7zip/releases/download/26.03/";

    public static ToolDownload Par2WinX64 { get; } = Par2("par2cmdline-turbo-1.5.0-win-x64.zip", "873a6f25822415f432224cc6f734407ca2732004184e002960fc68ed1e99c5d7");
    public static ToolDownload Par2WinArm64 { get; } = Par2("par2cmdline-turbo-1.5.0-win-arm64.zip", "bb73312747b706dcedd13b8d4de7acd20e17367ebb0b26ebc3ea7d55ea744735");
    public static ToolDownload Par2LinuxX64 { get; } = Par2("par2cmdline-turbo-1.5.0-linux-amd64.zip", "5a9f64386813456693c2ea1fb7649436fe7544bbdf97fd73b3483dfcc8aca464");
    public static ToolDownload Par2LinuxArm64 { get; } = Par2("par2cmdline-turbo-1.5.0-linux-arm64.zip", "3afb2f0b319fc4e6353c6d4261994757cbe3189f968a72420b4ba1d63f9c9a70");
    public static ToolDownload Par2LinuxArm { get; } = Par2("par2cmdline-turbo-1.5.0-linux-armhf.zip", "ca41c348c60368afbcd3f2cc76eafcd825616391a388d6646224ecd93f6ce2a7");
    public static ToolDownload Par2MacOs { get; } = Par2("par2cmdline-turbo-1.5.0-macos-universal.zip", "dcd32f194a4f7144e9a2c58069476cc954c90625b266d57fa012218673b8bd34");

    public static ToolDownload SevenZipWinX64Msi { get; } = SevenZip("7z2603-x64.msi", "c0680064d698a62dd4a5a47f403db356a6531a5473e4c4b1d090ea2590513926");
    public static ToolDownload SevenZipWinX64Exe { get; } = SevenZip("7z2603-x64.exe", "0859c524b8a63551848f0c246abddcb1d0b7b656b0fbfe879f8d85e61a9e6edd");
    public static ToolDownload SevenZipWinArm64Exe { get; } = SevenZip("7z2603-arm64.exe", "e22ce71c11dcf503c448fe51e56f41830eb4e1344fa5c7731ae63bce533a8e8e");
    public static ToolDownload SevenZipReduced { get; } = SevenZip("7zr.exe", "ad4c82fadcbdf93c03b4fc440f300509c7d60c5c2f4d183e35d9d70d6957037d");
    public static ToolDownload SevenZipLinuxX64 { get; } = SevenZip("7z2603-linux-x64.tar.xz", "dc99eff5008f1ab79bd7084c68513701547a808a89502bf4133683535ab3c695");
    public static ToolDownload SevenZipLinuxArm64 { get; } = SevenZip("7z2603-linux-arm64.tar.xz", "2389ba20e4d8295e8709c20b6263b69bd1ec4972fe38a04ad7a1badbf595b996");
    public static ToolDownload SevenZipMacOs { get; } = SevenZip("7z2603-mac.tar.xz", "5ca87677072c59f5602e5c49baa27d4694bacd2259b4e507f0094249d4281480");

    /// <summary>License and readme files copied along when the download has them.</summary>
    public static IReadOnlyList<string> NoticeFiles { get; } = ["License.txt", "license.txt", "COPYING", "LICENSE", "readme.txt", "README.md"];

    /// <summary>The package for this platform, or null when StreamZH cannot install the tool there (e.g. FreeBSD, 32-bit x86).</summary>
    public static ToolPackage? For(PostProcessTool tool, ToolPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return tool == PostProcessTool.Par2 ? ForPar2(platform) : ForSevenZip(platform);
    }

    private static ToolPackage? ForPar2(ToolPlatform platform)
    {
        var download = (platform.Os, platform.Architecture) switch
        {
            (ToolOs.Windows, Architecture.X64) => Par2WinX64,
            (ToolOs.Windows, Architecture.Arm64) => Par2WinArm64,
            (ToolOs.Linux, Architecture.X64) => Par2LinuxX64,
            (ToolOs.Linux, Architecture.Arm64) => Par2LinuxArm64,
            (ToolOs.Linux, Architecture.Arm) => Par2LinuxArm,
            (ToolOs.MacOS, Architecture.X64 or Architecture.Arm64) => Par2MacOs,
            _ => null,
        };

        return download is null
            ? null
            : new ToolPackage(
                PostProcessTool.Par2, Par2Name, Par2Version, Par2License, ToolInstallMethod.Zip, download,
                [platform.Os == ToolOs.Windows ? "par2.exe" : "par2"]);
    }

    private static ToolPackage? ForSevenZip(ToolPlatform platform)
    {
        // 7z.dll brings the RAR codec; 7z.exe alone cannot unpack RAR.
        string[] windowsFiles = ["7z.exe", "7z.dll"];
        return (platform.Os, platform.Architecture) switch
        {
            (ToolOs.Windows, Architecture.X64) => new ToolPackage(
                PostProcessTool.SevenZip, SevenZipName, SevenZipVersion, SevenZipLicense, ToolInstallMethod.WindowsMsi, SevenZipWinX64Msi,
                windowsFiles, SevenZipReduced, SevenZipWinX64Exe),
            (ToolOs.Windows, Architecture.Arm64) => new ToolPackage(
                PostProcessTool.SevenZip, SevenZipName, SevenZipVersion, SevenZipLicense, ToolInstallMethod.SevenZipInstaller, SevenZipWinArm64Exe,
                windowsFiles, SevenZipReduced),
            (ToolOs.Linux, Architecture.X64) => Unix(SevenZipLinuxX64),
            (ToolOs.Linux, Architecture.Arm64) => Unix(SevenZipLinuxArm64),
            (ToolOs.MacOS, Architecture.X64 or Architecture.Arm64) => Unix(SevenZipMacOs),
            _ => null,
        };

        static ToolPackage Unix(ToolDownload download) => new(
            PostProcessTool.SevenZip, SevenZipName, SevenZipVersion, SevenZipLicense, ToolInstallMethod.TarXz, download, ["7zz"]);
    }

    private static ToolDownload Par2(string file, string sha256) => new(file, new Uri(Par2Base + file), sha256);

    private static ToolDownload SevenZip(string file, string sha256) => new(file, new Uri(SevenZipBase + file), sha256);
}
