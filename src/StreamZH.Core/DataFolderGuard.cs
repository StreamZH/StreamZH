// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core;

/// <summary>What a folder is used for: decides which part of the data folder it may be in.</summary>
public enum DataFolderUse
{
    /// <summary>Downloads, categories, watch and NZB folders, torrent save paths, root folders, recycle bin, import targets: only &lt;data&gt;/downloads.</summary>
    Downloads,

    /// <summary>Backups: only &lt;data&gt;/Backups (the default backup folder).</summary>
    Backups,
}

/// <summary>
/// 0.12 (V-02): the data folder holds the database, the sign-in keys (&lt;data&gt;/keys) and the tools StreamZH runs
/// (&lt;data&gt;/tools). No folder that StreamZH writes downloads, imports, renames or backups to may be inside it (except
/// its own downloads/ or backups/ folder), and none may contain it; otherwise anyone who can change such a folder could
/// place a program that StreamZH later runs, or a key that signs them in. Checked when a folder is saved and again when
/// it is used (an older version or a restored database may hold such a path). Symbolic links and junctions on the way are
/// followed where they exist.
/// </summary>
public static class DataFolderGuard
{
    private static StringComparison Comparison => PathIdentity.Comparison;

    /// <summary>The folder below the data folder that is allowed for <paramref name="use"/>.</summary>
    public static string AllowedSubfolder(DataFolderUse use) => use == DataFolderUse.Backups ? "Backups" : "downloads";

    /// <summary>
    /// Null when <paramref name="folder"/> may be used, else why not (a sentence for the settings page). Relative or empty
    /// paths are not judged here (the callers resolve or refuse them).
    /// </summary>
    public static string? Problem(string dataDirectory, string? folder, DataFolderUse use = DataFolderUse.Downloads) =>
        Problem(dataDirectory, null, folder, use);

    /// <summary>
    /// Like <see cref="Problem(string, string?, DataFolderUse)"/>, and (0.12, SV2 N-01) the program folder
    /// <paramref name="appDirectory"/> is protected the same way, without any allowed subfolder: nothing may be written
    /// into the folder StreamZH runs from (a DLL or a tool placed there would run at the next start). Folders are compared
    /// by path text, by their resolved path (<see cref="PathIdentity.Real"/>) and, on Linux and macOS, by device and inode.
    /// </summary>
    public static string? Problem(string dataDirectory, string? appDirectory, string? folder, DataFolderUse use = DataFolderUse.Downloads)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder.Trim()))
        {
            return null;
        }

        string full;
        try
        {
            full = PathIdentity.Normalize(folder);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }

        var data = PathIdentity.Normalize(dataDirectory);
        if (Check(full, data, Path.Combine(data, AllowedSubfolder(use))) is { } dataProblem)
        {
            return dataProblem == Relation.Inside
                ? $"This folder is inside the StreamZH data folder ({data}), which holds the database, keys and tools. Use a folder outside it"
                  + $" (or below {Path.Combine(data, AllowedSubfolder(use))})."
                : $"This folder contains the StreamZH data folder ({data}). Use a folder that does not contain it.";
        }

        if (!string.IsNullOrWhiteSpace(appDirectory))
        {
            var app = PathIdentity.Normalize(appDirectory);
            if (Check(full, app, null) is { } appProblem)
            {
                return appProblem == Relation.Inside
                    ? $"This folder is inside the folder StreamZH runs from ({app}). Use a folder outside it."
                    : $"This folder contains the folder StreamZH runs from ({app}). Use a folder that does not contain it.";
            }
        }

        return null;
    }

    private enum Relation
    {
        Inside,
        Contains,
    }

    // Is full inside protected (and not inside allowed), or does it contain protected?
    private static Relation? Check(string full, string protectedFolder, string? allowed)
    {
        var folders = new[] { full, RealPath(full), PathIdentity.Real(full) }.Distinct(StringComparer.Ordinal).ToArray();
        var protects = new[] { protectedFolder, RealPath(protectedFolder), PathIdentity.Real(protectedFolder) }.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var p in protects)
        {
            var allowedHere = allowed is null ? null : Path.Combine(p, Path.GetFileName(allowed));
            foreach (var f in folders)
            {
                if (IsSameOrInside(f, p) && (allowedHere is null || !IsSameOrInside(f, allowedHere)))
                {
                    return Relation.Inside;
                }

                if (IsSameOrInside(p, f) && !IsSameOrInside(f, p))
                {
                    return Relation.Contains;
                }
            }
        }

        return IdentityCheck(folders[^1], protects[^1], allowed is null ? null : Path.GetFileName(allowed));
    }

    // The same folder under another name (bind mount, second mount of a volume): device and inode numbers (Linux, macOS).
    private static Relation? IdentityCheck(string realFull, string realProtected, string? allowedName)
    {
        if (PathIdentity.Identity(realProtected) is not { } protectedId)
        {
            return null;
        }

        foreach (var ancestor in PathIdentity.ExistingAncestors(realFull))
        {
            if (PathIdentity.Identity(ancestor) != protectedId)
            {
                continue;
            }

            // Inside the protected folder: allowed only below its allowed subfolder (which may not exist yet).
            var first = Path.GetRelativePath(ancestor, realFull).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 2)[0];
            return allowedName is not null && string.Equals(first, allowedName, Comparison) ? null : Relation.Inside;
        }

        if (Directory.Exists(realFull) && PathIdentity.Identity(realFull) is { } folderId)
        {
            foreach (var ancestor in PathIdentity.ExistingAncestors(realProtected))
            {
                if (PathIdentity.Identity(ancestor) == folderId)
                {
                    return Relation.Contains;
                }
            }
        }

        return null;
    }

    /// <summary>For a file or folder about to be written: throws when it is in a part of the data folder no feature may write to.</summary>
    /// <exception cref="UnauthorizedAccessException">The path is in the data folder (outside downloads/ or backups/) or contains it.</exception>
    public static void EnsureAllowed(string dataDirectory, string path, DataFolderUse use = DataFolderUse.Downloads)
    {
        if (Problem(dataDirectory, path, use) is not null)
        {
            throw new UnauthorizedAccessException($"StreamZH does not write to {path}: it is inside its data folder (database, keys and tools) or contains it.");
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or below it (by path text).</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        var f = Path.TrimEndingDirectorySeparator(folder);
        if (string.Equals(p, f, Comparison))
        {
            return true;
        }

        // A root ("/" or "C:\") keeps its separator.
        var prefix = f.EndsWith(Path.DirectorySeparatorChar) || f.EndsWith(Path.AltDirectorySeparatorChar) ? f : f + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, Comparison)
            || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar && p.StartsWith(f + Path.AltDirectorySeparatorChar, Comparison));
    }

    /// <summary>The path with symbolic links and junctions of its existing part resolved; the path itself when nothing can be resolved.</summary>
    public static string RealPath(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return fullPath;
            }

            var current = root;
            var parts = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var next = Path.Combine(current, parts[i]);
                var info = new DirectoryInfo(next);
                if (!info.Exists)
                {
                    // The rest does not exist yet: nothing more to resolve.
                    return Path.TrimEndingDirectorySeparator(Path.Combine([next, .. parts[(i + 1)..]]));
                }

                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    next = Path.GetFullPath(target.FullName);
                }

                current = next;
            }

            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return fullPath;
        }
    }
}
