// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using StreamZH.Core;

namespace StreamZH.Library.Import;

/// <summary>An import step that failed with a message meant for the user (shown in Activity and the status window).</summary>
public sealed class ImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How a file got into the library.</summary>
public enum TransferKind
{
    Moved,
    Copied,
    HardLinked,
}

/// <summary>
/// File operations of the import and rename: move, copy or hard-link, the recycle bin, permissions and dates, free space,
/// and readable errors (access denied, Windows "Controlled folder access", full disk). Never deletes a source file
/// before its copy is complete.
/// </summary>
/// <param name="paths">0.12 (V-02): nothing is written into the data folder (database, keys, tools) outside downloads/.</param>
public sealed partial class ImportFileSystem(ILogger<ImportFileSystem> logger, AppPaths? paths = null)
{
    private const int ErrorDiskFull = 0x70;
    private const int ErrorHandleDiskFull = 0x27;
    private const int EnoSpc = 28;

    /// <summary>Moves (or copies, hard-linking first when asked) <paramref name="source"/> to <paramref name="destination"/>, which must not exist.</summary>
    public TransferKind Transfer(string source, string destination, ImportMode mode, bool useHardlinks)
    {
        Guard(destination);
        var folder = Path.GetDirectoryName(destination)!;
        try
        {
            if (mode == ImportMode.Move)
            {
                File.Move(source, destination, overwrite: false);
                return TransferKind.Moved;
            }

            if (useHardlinks && TryHardLink(source, destination))
            {
                return TransferKind.HardLinked;
            }

            CopyCompletely(source, destination);
            return TransferKind.Copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportException(Describe(ex, folder), ex);
        }
    }

    /// <summary>Refuses a target in the data folder outside downloads/ (or one that contains the data folder).</summary>
    /// <exception cref="ImportException">The target is not allowed.</exception>
    public void Guard(string target)
    {
        if (Problem(target) is { } problem)
        {
            throw new ImportException(problem);
        }
    }

    /// <summary>Why StreamZH does not write to <paramref name="target"/>, null when it may.</summary>
    public string? Problem(string target) =>
        paths?.FolderProblem(target) is { } problem ? $"StreamZH does not write to {target}. {problem}" : null;

    /// <summary>Copies to a temporary name first, so an interrupted copy never looks like a finished file.</summary>
    public static void CopyCompletely(string source, string destination)
    {
        var temp = destination + ".partial~";
        try
        {
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, destination, overwrite: false);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    /// <summary>Creates a hard link; false when that is not possible (other drive, file system without links).</summary>
    public bool TryHardLink(string source, string destination)
    {
        Guard(destination);
        try
        {
            var ok = OperatingSystem.IsWindows() ? CreateHardLinkW(destination, source, IntPtr.Zero) : link(source, destination) == 0;
            if (!ok)
            {
                logger.LogDebug("Hard link from {Source} to {Destination} not possible (error {Error}), copying instead", source, destination, Marshal.GetLastPInvokeError());
            }

            return ok;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Moves a file into the recycle bin (<c>&lt;bin&gt;/&lt;item folder&gt;/&lt;name&gt;</c>, "name (2).ext" when taken) and marks
    /// it with today's date for the cleanup, or deletes it when no recycle bin is set. Returns where it went (null: deleted).
    /// </summary>
    public string? Recycle(string path, string recycleBin, string itemFolderName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(recycleBin))
            {
                File.Delete(path);
                logger.LogInformation("Deleted {Path}", path);
                return null;
            }

            var folder = Path.Combine(recycleBin, Safe(itemFolderName));
            Guard(folder);
            Directory.CreateDirectory(folder);
            var target = Unique(Path.Combine(folder, Path.GetFileName(path)));
            File.Move(path, target);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            logger.LogInformation("Moved {Path} to the recycle bin: {Target}", path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportException(Describe(ex, string.IsNullOrWhiteSpace(recycleBin) ? Path.GetDirectoryName(path)! : recycleBin), ex);
        }
    }

    /// <summary>"name.ext", or "name (2).ext" … when the path exists.</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i.ToString(CultureInfo.InvariantCulture)}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Creates the folder (and parents) with readable errors; applies the folder permissions to new folders.</summary>
    public void CreateFolder(string folder, MediaManagementValues settings)
    {
        Guard(folder);
        try
        {
            var missing = new List<string>();
            for (var current = folder; !string.IsNullOrEmpty(current) && !Directory.Exists(current); current = Path.GetDirectoryName(current))
            {
                missing.Add(current);
            }

            Directory.CreateDirectory(folder);
            foreach (var created in missing)
            {
                ApplyPermissions(created, isFolder: true, settings);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportException(Describe(ex, folder), ex);
        }
    }

    /// <summary>chmod/chown on Linux and macOS when "Set permissions" is on. Files get the folder mode without execute bits.</summary>
    public void ApplyPermissions(string path, bool isFolder, MediaManagementValues settings)
    {
        if (OperatingSystem.IsWindows() || !settings.SetPermissions)
        {
            return;
        }

        try
        {
            var mode = Convert.ToInt32(settings.ChmodFolder, 8);
            if (!isFolder)
            {
                mode &= ~0b001_001_001;
            }

            File.SetUnixFileMode(path, (UnixFileMode)mode);
            if (settings.ChownGroup.Length > 0)
            {
                if (GroupId(settings.ChownGroup) is { } gid)
                {
                    if (chown(path, -1, gid) != 0)
                    {
                        logger.LogWarning("Could not change the group of {Path} to {Group} (error {Error})", path, settings.ChownGroup, Marshal.GetLastPInvokeError());
                    }
                }
                else
                {
                    logger.LogWarning("Group {Group} not found; the group of {Path} was not changed", settings.ChownGroup, path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning("Could not set the permissions of {Path}: {Message}", path, ex.Message);
        }
    }

    /// <summary>Numeric group id, or the id of a group name from /etc/group.</summary>
    public static int? GroupId(string group)
    {
        if (int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }

        try
        {
            foreach (var line in File.ReadLines("/etc/group"))
            {
                var parts = line.Split(':');
                if (parts.Length >= 3 && parts[0] == group && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var gid))
                {
                    return gid;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    public void SetModified(string path, DateTime utc)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, utc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            logger.LogWarning("Could not change the date of {Path}: {Message}", path, ex.Message);
        }
    }

    /// <summary>Free bytes on the drive of <paramref name="path"/> (the nearest existing parent folder); null when unknown.</summary>
    public long? FreeBytes(string path)
    {
        try
        {
            var existing = path;
            while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
            }

            return string.IsNullOrEmpty(existing) ? null : new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Both paths are on the same drive (a move is a rename and needs no space).</summary>
    public static bool SameDrive(string a, string b)
    {
        try
        {
            static string Root(string path)
            {
                var existing = path;
                while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing) && !File.Exists(existing))
                {
                    existing = Path.GetDirectoryName(existing);
                }

                return OperatingSystem.IsWindows()
                    ? Path.GetPathRoot(Path.GetFullPath(existing ?? path)) ?? string.Empty
                    : DriveInfo.GetDrives()
                        .Select(d => d.RootDirectory.FullName)
                        .Where(r => (existing ?? path).StartsWith(r, StringComparison.Ordinal))
                        .MaxBy(r => r.Length) ?? "/";
            }

            return string.Equals(Root(a), Root(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Files the operating system leaves in folders (thumbnail caches, folder settings); never media, subtitles or NFOs.</summary>
    public static IReadOnlySet<string> JunkFileNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db", "ehthumbs.db", "ehthumbs_vista.db", "desktop.ini", ".DS_Store", ".localized",
    };

    /// <summary>True when the folder holds no subfolders and no files except <see cref="JunkFileNames"/> (listed in <paramref name="junk"/>).</summary>
    public static bool OnlyJunk(string folder, out IReadOnlyList<string> junk)
    {
        var found = new List<string>();
        junk = found;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (Directory.Exists(entry) || !JunkFileNames.Contains(Path.GetFileName(entry)))
            {
                return false;
            }

            found.Add(entry);
        }

        return true;
    }

    /// <summary>
    /// Removes empty folders below <paramref name="folder"/> (and the folder itself when <paramref name="includeSelf"/>);
    /// a folder with only system junk (<see cref="JunkFileNames"/>) counts as empty.
    /// </summary>
    public void DeleteEmptyFolders(string folder, bool includeSelf)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                DeleteEmptyFolders(sub, includeSelf: true);
            }

            if (includeSelf && OnlyJunk(folder, out var junk))
            {
                foreach (var file in junk)
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }

                Directory.Delete(folder);
                logger.LogDebug("Deleted empty folder {Folder}", folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("Could not delete empty folder {Folder}: {Message}", folder, ex.Message);
        }
    }

    /// <summary>Deletes a folder with everything in it; problems are only logged (the import itself succeeded).</summary>
    public void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
                logger.LogInformation("Deleted the download folder {Folder}", folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not delete the download folder {Folder}: {Message}", folder, ex.Message);
        }
    }

    /// <summary>A message the user can act on, e.g. "Could not write to D:\Movies: access denied. If Windows 'Controlled folder access' is on, allow StreamZH.exe."</summary>
    public static string Describe(Exception ex, string folder)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is ImportException)
        {
            return ex.Message;
        }

        if (ex is UnauthorizedAccessException || (ex is IOException io && (io.HResult & 0xFFFF) is 5 or 13))
        {
            var hint = OperatingSystem.IsWindows()
                ? " If Windows 'Controlled folder access' is on, allow StreamZH.exe (Windows Security → Ransomware protection), or check the folder permissions."
                : " Check that the user running StreamZH may write there.";
            return $"Could not write to {folder}: access denied.{hint}";
        }

        if (ex is IOException full && (full.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull or EnoSpc)
        {
            return $"Could not write to {folder}: the disk is full.";
        }

        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return $"A file is missing: {ex.Message}";
        }

        return $"Could not write to {folder}: {ex.Message}";
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

#pragma warning disable SYSLIB1054, CA2101 // Plain DllImport: no unsafe code needed for these three calls.
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int link(string oldpath, string newpath);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int chown(string path, int owner, int group);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
#pragma warning restore SYSLIB1054, CA2101
}
