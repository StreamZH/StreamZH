// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Host.Security;

/// <summary>
/// One entry of a Windows access list in plain values (SID as text, the rights mask, inheritance bits), so the rules
/// below can be built and tested on any system. <see cref="WindowsAcl"/> converts to and from the Windows types.
/// </summary>
/// <param name="Inheritance">Bits of InheritanceFlags: 1 = ContainerInherit (subfolders), 2 = ObjectInherit (files).</param>
/// <param name="Propagation">Bits of PropagationFlags: 1 = NoPropagateInherit, 2 = InheritOnly.</param>
public sealed record AclEntry(string Sid, int Rights, bool Allow, int Inheritance, int Propagation)
{
    public const int FullControl = 0x1F01FF;
    public const int Modify = 0x1301BF;
    public const int Synchronize = 0x100000;

    /// <summary>ReadData, ReadAttributes, ReadExtendedAttributes, ReadPermissions.</summary>
    public const int Read = 0x20089;

    public const int ContainerInherit = 1;
    public const int ObjectInherit = 2;

    /// <summary>Full control for the folder, its subfolders and files.</summary>
    public static AclEntry FullControlFor(string sid) => new(sid, FullControl, true, ContainerInherit | ObjectInherit, 0);

    /// <summary>Applies to the folder itself and to everything below it.</summary>
    public bool CoversAll => Inheritance == (ContainerInherit | ObjectInherit) && Propagation == 0;
}

/// <summary>
/// The access list of the data folder on Windows (S1-02, S3-07). The folder holds the database (API key, password
/// hashes, indexer keys) and the sign-in keys, so only these may open it: SYSTEM, Administrators, the account StreamZH
/// runs as, and user accounts someone granted on purpose (Install-Service.ps1 grants the installing user, so the tray
/// icon can read tray.json and the logs). Groups that stand for "everyone on this computer" (Users, Authenticated
/// Users, Everyone, Interactive, ...) lose their access; inheritance from C:\ProgramData is switched off.
/// </summary>
public static class DataFolderAcl
{
    public const string LocalSystem = "S-1-5-18";
    public const string Administrators = "S-1-5-32-544";

    /// <summary>Groups every local user (or every app) is part of.</summary>
    public static IReadOnlySet<string> BroadSids { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-1-0",      // Everyone
        "S-1-5-1",      // Dialup
        "S-1-5-2",      // Network
        "S-1-5-4",      // Interactive
        "S-1-5-7",      // Anonymous
        "S-1-5-11",     // Authenticated Users
        "S-1-5-13",     // Terminal Server Users
        "S-1-5-14",     // Remote Interactive Logon
        "S-1-5-32-545", // Users
        "S-1-5-32-546", // Guests
        "S-1-5-32-547", // Power Users
        "S-1-5-32-555", // Remote Desktop Users
        "S-1-15-2-1",   // All application packages
        "S-1-15-2-2",   // All restricted application packages
    };

    /// <summary>Read and write is enough for the account StreamZH runs as (Install-Service.ps1 grants Modify to a user).</summary>
    private const int ReadWrite = AclEntry.Modify | AclEntry.Synchronize;

    public static bool IsBroad(string sid) => BroadSids.Contains(sid);

    /// <summary>A person's account: local or domain (S-1-5-21-…) or Microsoft Entra ID (S-1-12-1-…).</summary>
    public static bool IsUserAccount(string sid) =>
        sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The access list the data folder should have, or null when it has it already (nothing to write). Keeps every entry
    /// except "allow" entries of broad groups (inherited ones become explicit), and adds full control for SYSTEM and
    /// Administrators and for <paramref name="processSid"/> unless that account can already read and write everything.
    /// </summary>
    public static IReadOnlyList<AclEntry>? Plan(IReadOnlyList<AclEntry> current, bool isProtected, string processSid)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrEmpty(processSid);

        var kept = current.Where(e => !(e.Allow && IsBroad(e.Sid))).ToList();
        var missing = new List<string>();
        foreach (var (sid, needed) in new[] { (LocalSystem, AclEntry.FullControl), (Administrators, AclEntry.FullControl), (processSid, ReadWrite) })
        {
            if (!missing.Contains(sid, StringComparer.OrdinalIgnoreCase) && !Grants(kept, sid, needed))
            {
                missing.Add(sid);
            }
        }

        if (isProtected && kept.Count == current.Count && missing.Count == 0)
        {
            return null;
        }

        return [.. kept.Distinct(), .. missing.Select(AclEntry.FullControlFor)];
    }

    /// <summary>
    /// Who may read tray.token besides SYSTEM and the account that writes it: the user accounts the data folder grants
    /// (Install-Service.ps1 adds the installing user). Empty when there are none.
    /// </summary>
    public static IReadOnlyList<string> TrayReaders(IEnumerable<AclEntry> folder, string writerSid)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return [.. folder
            .Where(e => e.Allow && IsUserAccount(e.Sid) && !string.Equals(e.Sid, writerSid, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Sid)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool Grants(IEnumerable<AclEntry> entries, string sid, int needed)
    {
        var rights = entries
            .Where(e => e.Allow && e.CoversAll && string.Equals(e.Sid, sid, StringComparison.OrdinalIgnoreCase))
            .Aggregate(0, (all, e) => all | e.Rights);
        return (rights & needed) == needed;
    }
}
