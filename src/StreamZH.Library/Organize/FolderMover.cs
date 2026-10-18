// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using StreamZH.Library.Import;

namespace StreamZH.Library.Organize;

/// <summary>
/// Folder and file operations of organize and move: renames that only change the letter case (two steps, so they work
/// on Windows and macOS), moves to another drive (copy everything first, then delete the source), the recycle bin for
/// whole folders, and the checks the preview shows (target exists, path too long).
/// </summary>
public sealed class FolderMover(ImportFileSystem files, ILogger<FolderMover> logger, TimeProvider? timeProvider = null)
{
    /// <summary>Windows: longer paths break Explorer and many players even when long paths are switched on.</summary>
    public const int WindowsMaxPath = 259;

    public const int UnixMaxPath = 4095;
    public const int MaxNameBytes = 255;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Null when the path can be used, otherwise why not.</summary>
    public static string? PathProblem(string path, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (windows)
        {
            return path.Length > WindowsMaxPath
                ? $"The new path is {path.Length.ToString(CultureInfo.InvariantCulture)} characters long; Windows allows {WindowsMaxPath.ToString(CultureInfo.InvariantCulture)}. Shorten the naming format."
                : null;
        }

        if (path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(name => Encoding.UTF8.GetByteCount(name) > MaxNameBytes))
        {
            return $"A name in the new path is longer than {MaxNameBytes.ToString(CultureInfo.InvariantCulture)} bytes. Shorten the naming format.";
        }

        return Encoding.UTF8.GetByteCount(path) > UnixMaxPath ? "The new path is too long. Shorten the naming format." : null;
    }

    /// <summary>Only the letter case differs ("dune (2021)" → "Dune (2021)").</summary>
    public static bool IsCaseOnly(string a, string b) =>
        !string.Equals(a, b, StringComparison.Ordinal) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="target"/> exists as another file or folder than <paramref name="source"/>. On Windows and macOS
    /// "Dune (2021)" and "DUNE (2021)" are the same folder, which is not a conflict.
    /// </summary>
    public static bool TargetTaken(string source, string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            return false;
        }

        if (!IsCaseOnly(Path.GetFullPath(source), Path.GetFullPath(target)))
        {
            return !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.Ordinal);
        }

        // Case-only: taken only when the folder lists an entry with exactly the target name (a case-sensitive file system).
        var parent = Path.GetDirectoryName(Path.GetFullPath(target));
        var name = Path.GetFileName(target);
        return parent is not null && Directory.Exists(parent)
            && Directory.EnumerateFileSystemEntries(parent).Any(e => string.Equals(Path.GetFileName(e), name, StringComparison.Ordinal))
            && Directory.EnumerateFileSystemEntries(parent).Any(e => string.Equals(Path.GetFileName(e), Path.GetFileName(source), StringComparison.Ordinal));
    }

    /// <summary>Renames or moves a file within one drive (two steps for a case-only change).</summary>
    public void MoveFile(string source, string target, MediaManagementValues settings)
    {
        if (!File.Exists(source))
        {
            throw new LibraryException($"{source} no longer exists. Refresh the item first.", LibraryError.Conflict);
        }

        if (TargetTaken(source, target))
        {
            throw new LibraryException($"{target} already exists. Remove or rename it first.", LibraryError.Conflict);
        }

        var folder = Path.GetDirectoryName(target)!;
        files.CreateFolder(folder, settings);
        try
        {
            if (IsCaseOnly(source, target))
            {
                var temp = TempName(source);
                File.Move(source, temp);
                File.Move(temp, target);
            }
            else
            {
                File.Move(source, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LibraryException(ImportFileSystem.Describe(ex, folder), LibraryError.Conflict);
        }
    }

    /// <summary>
    /// Renames or moves a folder. On the same drive this is one rename; to another drive everything is copied first and
    /// the source is deleted only after the copy is complete. Returns false when the source folder does not exist.
    /// </summary>
    /// <param name="progress">How far the copy to another drive is (0..1).</param>
    public bool MoveFolder(string source, string target, MediaManagementValues settings, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Directory.Exists(source))
        {
            return false;
        }

        if (TargetTaken(source, target))
        {
            throw new LibraryException($"The folder {target} already exists. Remove or rename it first.", LibraryError.Conflict);
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(target))!;
        try
        {
            files.CreateFolder(parent, settings);
            if (IsCaseOnly(source, target))
            {
                var temp = TempName(source);
                Directory.Move(source, temp);
                Directory.Move(temp, target);
                return true;
            }

            if (ImportFileSystem.SameDrive(source, parent))
            {
                try
                {
                    Directory.Move(source, target);
                    return true;
                }
                catch (IOException) when (Directory.Exists(source) && !Directory.Exists(target))
                {
                    // Another file system behind the same drive letter or mount (e.g. a network share): copy instead.
                }
            }

            CopyFolder(source, target, settings, progress);
            DeleteSource(source);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImportException)
        {
            throw new LibraryException(ImportFileSystem.Describe(ex, parent), LibraryError.Conflict);
        }
    }

    /// <summary>
    /// Moves a whole folder into the recycle bin (<c>&lt;bin&gt;/&lt;folder name&gt;</c>, "name (2)" when taken) and dates
    /// its files today for the cleanup, or deletes it for good when no recycle bin is set. Returns where it went.
    /// </summary>
    public string? RecycleFolder(string folder, MediaManagementValues settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.RecycleBin))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                logger.LogInformation("Deleted {Folder}", folder);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LibraryException(ImportFileSystem.Describe(ex, folder), LibraryError.Conflict);
            }
        }

        var target = ImportFileSystem.Unique(Path.Combine(settings.RecycleBin, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))));
        MoveFolder(folder, target, settings);
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(target, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            files.SetModified(file, now);
        }

        logger.LogInformation("Moved {Folder} to the recycle bin: {Target}", folder, target);
        return target;
    }

    /// <summary>Size of all files in the folder (0 when it does not exist).</summary>
    public static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        return new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Sum(f => f.Length);
    }

    private void CopyFolder(string source, string target, MediaManagementValues settings, Action<double>? progress)
    {
        var all = new DirectoryInfo(source).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList();
        var total = Math.Max(1, all.Sum(f => f.Length));
        var copied = 0L;
        try
        {
            files.CreateFolder(target, settings);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in all)
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file.FullName));
                ImportFileSystem.CopyCompletely(file.FullName, destination);
                files.SetModified(destination, file.LastWriteTimeUtc);
                copied += file.Length;
                progress?.Invoke((double)copied / total);
            }
        }
        catch
        {
            // Never leave half a copy behind: the source stays untouched.
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not remove the incomplete copy {Folder}: {Message}", target, cleanup.Message);
            }

            throw;
        }
    }

    private void DeleteSource(string source)
    {
        try
        {
            Directory.Delete(source, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The copy is complete and the library points to it; what is left of the source is only a leftover.
            logger.LogWarning("Copied, but could not delete the old folder {Folder}: {Message}", source, ex.Message);
        }
    }

    private static string TempName(string path) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $"{Path.GetFileName(path)}.organize-{Guid.NewGuid().ToString("N")[..8]}");
}
