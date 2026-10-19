// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core;
using StreamZH.Host.Security;
using StreamZH.Host.Tray;

namespace StreamZH.Tests.Host;

/// <summary>
/// S1-02, S3-07, S1-13: the data folder (database with the API key and password hashes, sign-in keys, backups, logs)
/// and the tray token are readable only by StreamZH's account (and, on Windows, the users granted on purpose).
/// </summary>
public sealed class DataFolderPermissionsTests : IDisposable
{
    private const UnixFileMode Rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Rwx = Rw | UnixFileMode.UserExecute;
    private const UnixFileMode Mode644 = Rw | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Mode755 = Rwx | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    // Well-known and example SIDs (the user SIDs are made up).
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string CreatorOwner = "S-1-3-0";
    private const string ServiceSid = "S-1-5-80-1111111111-2222222222-3333333333-4444444444-555555555";
    private const string Installer = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    private const string OtherUser = "S-1-5-21-1000000001-1000000002-1000000003-1002";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"streamzh-perm-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ---- Linux, macOS, Docker ----

    [Fact]
    public void A_new_data_folder_is_private()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppPaths(Path.Combine(_dir, "new"));
        paths.EnsureCreated();

        Assert.Equal(Rwx, File.GetUnixFileMode(paths.DataDirectory));
    }

    [Fact]
    public void An_existing_data_folder_is_narrowed_once_and_then_left_alone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Created by an older StreamZH under umask 022 (or Docker's UMASK 002).
        var paths = new AppPaths(_dir);
        Directory.CreateDirectory(_dir);
        File.SetUnixFileMode(_dir, Mode755);
        var files = new[]
        {
            "streamzh.db", "streamzh.db-wal", "streamzh.db-shm", "tray.token", "keys/key-1.xml", "Backups/manual/b.zip",
            "restore/before-restore-1/streamzh.db", "logs/streamzh.txt",
        };
        foreach (var file in files)
        {
            var path = Path.Combine(_dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            File.SetUnixFileMode(path, Mode644 | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
        }

        foreach (var folder in new[] { "keys", "Backups", "Backups/manual", "restore", "logs" })
        {
            File.SetUnixFileMode(Path.Combine(_dir, folder), Mode755 | UnixFileMode.GroupWrite);
        }

        var outside = Path.Combine(Path.GetTempPath(), $"streamzh-perm-outside-{Guid.NewGuid():N}");
        File.WriteAllText(outside, "not ours");
        File.SetUnixFileMode(outside, Mode644);
        File.CreateSymbolicLink(Path.Combine(_dir, "keys", "link.xml"), outside);

        try
        {
            var report = DataFolderPermissions.Apply(paths);

            Assert.Empty(report.Problems);
            Assert.NotEmpty(report.Changed);
            Assert.Equal(Rwx | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(_dir));
            foreach (var file in new[] { "streamzh.db", "streamzh.db-wal", "streamzh.db-shm", "tray.token", "keys/key-1.xml", "Backups/manual/b.zip", "restore/before-restore-1/streamzh.db" })
            {
                Assert.Equal(Rw, File.GetUnixFileMode(Path.Combine(_dir, file)));
            }

            foreach (var folder in new[] { "keys", "Backups", "Backups/manual", "restore" })
            {
                Assert.Equal(Rwx, File.GetUnixFileMode(Path.Combine(_dir, folder)));
            }

            Assert.Equal(Rwx | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(Path.Combine(_dir, "logs")));
            Assert.Equal(Rw | UnixFileMode.GroupRead, File.GetUnixFileMode(Path.Combine(_dir, "logs", "streamzh.txt")));
            Assert.Equal(Mode644, File.GetUnixFileMode(outside));

            // Idempotent: nothing to do at the next start.
            Assert.Empty(DataFolderPermissions.Apply(paths).Changed);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void A_folder_the_installer_made_0700_stays_0700()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_dir);
        File.SetUnixFileMode(_dir, Rwx);

        Assert.Empty(DataFolderPermissions.Apply(new AppPaths(_dir)).Changed);
        Assert.Equal(Rwx, File.GetUnixFileMode(_dir));
    }

    [Fact]
    public async Task The_database_and_the_tray_token_of_a_running_streamzh_are_private()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var factory = new StreamZhFactory();
        await factory.CreateOwnerClientAsync();
        var paths = factory.Services.GetRequiredService<AppPaths>();

        Assert.Equal(Rw, File.GetUnixFileMode(paths.DatabaseFile));
        Assert.Equal(Rw, File.GetUnixFileMode(factory.Services.GetRequiredService<TrayAccess>().TokenPath));
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(paths.DataDirectory) & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
    }

    // ---- Windows access list (rules tested everywhere) ----

    private static AclEntry Entry(string sid, int rights, int inheritance = 3, int propagation = 0, bool allow = true) =>
        new(sid, rights, allow, inheritance, propagation);

    /// <summary>C:\ProgramData\StreamZH as it inherits from C:\ProgramData.</summary>
    private static List<AclEntry> ProgramDataDefault() =>
    [
        Entry(DataFolderAcl.LocalSystem, AclEntry.FullControl),
        Entry(DataFolderAcl.Administrators, AclEntry.FullControl),
        Entry(CreatorOwner, unchecked((int)0x10000000), propagation: 2),
        Entry(Users, 0x1200A9),
        Entry(Users, 0x116, inheritance: 1),
    ];

    [Fact]
    public void The_programdata_default_loses_the_users_group_and_gets_the_service_account()
    {
        var plan = DataFolderAcl.Plan(ProgramDataDefault(), isProtected: false, ServiceSid)!;

        Assert.DoesNotContain(plan, e => e.Sid == Users);
        Assert.Contains(plan, e => e is { Sid: DataFolderAcl.LocalSystem, Rights: AclEntry.FullControl, CoversAll: true });
        Assert.Contains(plan, e => e is { Sid: DataFolderAcl.Administrators, Rights: AclEntry.FullControl, CoversAll: true });
        Assert.Contains(plan, e => e is { Sid: ServiceSid, Rights: AclEntry.FullControl, CoversAll: true });
        Assert.Contains(plan, e => e.Sid == CreatorOwner);

        // Written once, the next start finds nothing to do.
        Assert.Null(DataFolderAcl.Plan(plan, isProtected: true, ServiceSid));
    }

    [Fact]
    public void Users_granted_on_purpose_and_deny_entries_stay()
    {
        List<AclEntry> current =
        [
            .. ProgramDataDefault(),
            Entry(Installer, AclEntry.Modify),
            Entry(AuthenticatedUsers, AclEntry.Modify),
            Entry(OtherUser, AclEntry.Modify, allow: false),
            Entry("S-1-1-0", AclEntry.Read, allow: false),
        ];

        var plan = DataFolderAcl.Plan(current, isProtected: true, Installer)!;

        Assert.Contains(plan, e => e.Sid == Installer && e.Rights == AclEntry.Modify);
        Assert.Contains(plan, e => e.Sid == OtherUser && !e.Allow);
        Assert.Contains(plan, e => e.Sid == "S-1-1-0" && !e.Allow);
        Assert.DoesNotContain(plan, e => e.Sid == AuthenticatedUsers);

        // The console user with Modify (granted by Install-Service.ps1) needs no full control on top.
        Assert.Single(plan, e => e.Sid == Installer);
    }

    [Fact]
    public void A_protected_folder_with_the_right_entries_is_left_alone()
    {
        List<AclEntry> current =
        [
            Entry(DataFolderAcl.LocalSystem, AclEntry.FullControl),
            Entry(DataFolderAcl.Administrators, AclEntry.FullControl),
            Entry(ServiceSid, AclEntry.FullControl),
            Entry(Installer, AclEntry.Modify),
        ];

        Assert.Null(DataFolderAcl.Plan(current, isProtected: true, ServiceSid));
        Assert.NotNull(DataFolderAcl.Plan(current, isProtected: false, ServiceSid));
        Assert.NotNull(DataFolderAcl.Plan([.. current, Entry(Users, AclEntry.Read)], isProtected: true, ServiceSid));
        Assert.NotNull(DataFolderAcl.Plan(current[..^1], isProtected: true, OtherUser));
    }

    [Fact]
    public void The_tray_token_is_for_the_tray_user_only()
    {
        List<AclEntry> folder =
        [
            Entry(DataFolderAcl.LocalSystem, AclEntry.FullControl),
            Entry(DataFolderAcl.Administrators, AclEntry.FullControl),
            Entry(ServiceSid, AclEntry.FullControl),
            Entry(Installer, AclEntry.Modify),
            Entry(OtherUser, AclEntry.Modify, allow: false),
        ];

        // Console window: the tray runs as the same user that writes the token.
        var console = TrayAccess.TokenAcl(Installer, TrayAccess.TrayReaders(isService: false, Installer, folder));
        Assert.Equal([DataFolderAcl.LocalSystem, Installer], console.Select(e => e.Sid));

        // Service: the installing user (granted on the data folder) may read it, nobody else.
        var service = TrayAccess.TokenAcl(ServiceSid, TrayAccess.TrayReaders(isService: true, ServiceSid, folder));
        Assert.Equal([DataFolderAcl.LocalSystem, ServiceSid, Installer], service.Select(e => e.Sid));
        Assert.Equal(AclEntry.Read | AclEntry.Synchronize, service[2].Rights);
        Assert.DoesNotContain(service, e => e.Sid is DataFolderAcl.Administrators or TrayAccess.InteractiveSid or OtherUser);

        // A service whose folder names no user (installed by hand): signed-in users, as before.
        var unknown = TrayAccess.TokenAcl(ServiceSid, TrayAccess.TrayReaders(isService: true, ServiceSid, folder[..3]));
        Assert.Contains(unknown, e => e.Sid == TrayAccess.InteractiveSid);
    }
}
