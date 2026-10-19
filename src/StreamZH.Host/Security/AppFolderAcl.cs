// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.Versioning;
using System.Security.AccessControl;
using StreamZH.Core;

namespace StreamZH.Host.Security;

/// <summary>
/// 0.12 (SV2 N-02): the program folder (StreamZH.exe, StreamZH.Tray.exe, their DLLs) must not be changeable by every
/// user of the computer, or any local account could replace the program the service and the tray icon run. Windows
/// gives folders made below C:\ "Authenticated Users: Modify"; Update-Local.ps1, Install-Service.ps1 and Install-Tray.ps1
/// cut that off, and the setup installs to C:\Program Files\StreamZH, which only administrators may change.
/// StreamZH only checks (it does not change its own program folder) and shows a status notice; on Linux, macOS and in
/// Docker the package manager or image owns the folder.
/// </summary>
public static class AppFolderAcl
{
    /// <summary>Rights that change a folder or what is in it (write, append, attributes, delete, permissions, owner).</summary>
    public const int ChangeRights = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000;

    /// <summary>The broad groups (Users, Authenticated Users, Everyone, …) that may change the folder or files created in it.</summary>
    public static IReadOnlyList<string> BroadWriters(IEnumerable<AclEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries
            .Where(e => e.Allow && DataFolderAcl.IsBroad(e.Sid) && (AclEntry.SpecificRights(e.Rights) & ChangeRights) != 0)
            .Select(e => e.Sid)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The notice text when the program folder is open to every user, else null (also off Windows and on errors).</summary>
    public static string? Problem(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return ProblemWindows(paths.AppDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ProblemWindows(string folder)
    {
        var security = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access);
        var writers = BroadWriters(WindowsAcl.Entries(security));
        return writers.Count == 0
            ? null
            : $"Every user of this computer may change {folder}, so they could replace StreamZH.exe. Install StreamZH with the setup (C:\\Program Files\\StreamZH), or run tools\\Install-Service.ps1 or tools\\Install-Tray.ps1 once as administrator to protect it.";
    }
}

/// <summary>The result of <see cref="AppFolderAcl.Problem"/> at start, for the status notice.</summary>
public sealed record AppFolderCheck(string? Problem);
