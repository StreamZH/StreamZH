// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using StreamZH.Core;

namespace StreamZH.Host.Security;

/// <summary>What the start-up check of the data folder changed and what it could not change.</summary>
public sealed record DataFolderPermissionReport(IReadOnlyList<string> Changed, IReadOnlyList<string> Problems)
{
    public static DataFolderPermissionReport None { get; } = new([], []);
}

/// <summary>
/// Keeps the data folder private at every start (S1-02, S3-07): it holds the database with the API key, password
/// hashes and indexer keys, the sign-in keys, backups and logs. Only narrows, never widens, and is idempotent.
/// Linux/macOS/Docker: the folder loses all rights for others and group write (0700 for a new folder, an installer's
/// 0750 stays), the database, keys, backups and tray token become 0600, logs at most 0640.
/// Windows: see <see cref="DataFolderAcl"/>.
/// </summary>
public static class DataFolderPermissions
{
    public const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode OwnerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The data folder and logs: the owner everything, the group (when an installer set it) reading.</summary>
    public const UnixFileMode GroupMayRead = OwnerOnlyFolder | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private const UnixFileMode GroupMayReadFile = OwnerOnlyFile | UnixFileMode.GroupRead;

    /// <summary>Secret folders below the data folder (keys, default backups, restore staging).</summary>
    public static IReadOnlyList<string> SecretFolders { get; } = ["keys", "Backups", "restore"];

    /// <summary>Checks and narrows the permissions; never throws.</summary>
    public static DataFolderPermissionReport Apply(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var changed = new List<string>();
        var problems = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ApplyWindows(paths.DataDirectory, changed, problems);
            }
            else
            {
                ApplyUnix(paths, changed, problems);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException
                                       or IdentityNotMappedException or PrivilegeNotHeldException)
        {
            problems.Add($"{paths.DataDirectory}: {ex.Message}");
        }

        return new DataFolderPermissionReport(changed, problems);
    }

    /// <summary>
    /// A file with secrets that is written later (a new backup): 0600 on Linux and macOS. On Windows it inherits the
    /// data folder's access list (a backup folder outside the data folder keeps that folder's rules).
    /// </summary>
    public static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Narrow(path, OwnerOnlyFile, [], []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Network shares and some file systems have no Unix modes; nothing to do there.
        }
    }

    // ---- Linux, macOS, Docker ----

    [UnsupportedOSPlatform("windows")]
    private static void ApplyUnix(AppPaths paths, List<string> changed, List<string> problems)
    {
        var data = paths.DataDirectory;
        Narrow(data, GroupMayRead, changed, problems);

        foreach (var name in new[] { "streamzh.db", "streamzh.db-wal", "streamzh.db-shm", "streamzh.db-journal", "tray.token" })
        {
            Narrow(Path.Combine(data, name), OwnerOnlyFile, changed, problems);
        }

        // The sign-in keys are written later by ASP.NET Core; their folder is private from the start.
        var keys = Path.Combine(data, SecretFolders[0]);
        if (!Directory.Exists(keys))
        {
            Directory.CreateDirectory(keys, OwnerOnlyFolder);
        }

        foreach (var folder in SecretFolders.Select(f => Path.Combine(data, f)))
        {
            NarrowTree(folder, OwnerOnlyFolder, OwnerOnlyFile, changed, problems);
        }

        NarrowTree(paths.LogDirectory, GroupMayRead, GroupMayReadFile, changed, problems);
    }

    [UnsupportedOSPlatform("windows")]
    private static void NarrowTree(string folder, UnixFileMode folderMode, UnixFileMode fileMode, List<string> changed, List<string> problems)
    {
        if (!Directory.Exists(folder) || new DirectoryInfo(folder).LinkTarget is not null)
        {
            return;
        }

        Narrow(folder, folderMode, changed, problems);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
        {
            Narrow(entry.FullName, entry is DirectoryInfo ? folderMode : fileMode, changed, problems);
        }
    }

    /// <summary>Takes away every right that <paramref name="allowed"/> does not contain; links are left alone.</summary>
    [UnsupportedOSPlatform("windows")]
    public static bool Narrow(string path, UnixFileMode allowed, List<string> changed, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(problems);
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null)
        {
            return false;
        }

        var mode = info.UnixFileMode;
        var narrowed = mode & allowed;
        if (narrowed == mode)
        {
            return false;
        }

        try
        {
            info.UnixFileMode = narrowed;
            changed.Add($"{path}: {Octal(mode)} -> {Octal(narrowed)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path} is {Octal(mode)} and could not be changed to {Octal(narrowed)}: {ex.Message}");
            return false;
        }
    }

    public static string Octal(UnixFileMode mode) => Convert.ToString((int)mode & 0xFFF, 8).PadLeft(4, '0');

    // ---- Windows ----

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(string folder, List<string> changed, List<string> problems)
    {
        var directory = new DirectoryInfo(folder);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        var me = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The account StreamZH runs as has no SID.");
        var plan = DataFolderAcl.Plan(WindowsAcl.Entries(security), security.AreAccessRulesProtected, me);
        if (plan is null)
        {
            return;
        }

        var desired = new DirectorySecurity();
        desired.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var entry in plan)
        {
            desired.AddAccessRule(WindowsAcl.Rule(entry));
        }

        try
        {
            directory.SetAccessControl(desired);
            changed.Add($"{folder}: only SYSTEM, Administrators, this account and granted users may open it now");
        }
        catch (UnauthorizedAccessException ex)
        {
            problems.Add($"{folder} can be read by other users of this computer, and this account may not change that ({ex.Message}). "
                         + "Run tools\\Install-Service.ps1 (or tools\\Uninstall-Service.ps1 when StreamZH runs in a console window) once as administrator.");
        }
    }
}

/// <summary>Conversions between <see cref="AclEntry"/> and the Windows access rules.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsAcl
{
    public static List<AclEntry> Entries(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        return [.. security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => new AclEntry(
                r.IdentityReference.Value, (int)r.FileSystemRights, r.AccessControlType == AccessControlType.Allow, (int)r.InheritanceFlags, (int)r.PropagationFlags))];
    }

    public static FileSystemAccessRule Rule(AclEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new FileSystemAccessRule(
            new SecurityIdentifier(entry.Sid),
            (FileSystemRights)entry.Rights,
            (InheritanceFlags)entry.Inheritance,
            (PropagationFlags)entry.Propagation,
            entry.Allow ? AccessControlType.Allow : AccessControlType.Deny);
    }
}
