// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;

namespace StreamZH.Library.Migration;

/// <summary>
/// Turns a path of the old app into the path StreamZH sees (longest matching mapping wins, case-insensitive on
/// Windows-style paths) and collects the old app's root paths for the wizard's path check.
/// </summary>
public sealed class PathMapper(IReadOnlyList<PathMapping>? mappings, Func<string, bool>? exists = null, Func<string, string?>? storageProblem = null)
{
    private readonly List<PathMapping> _mappings =
    [
        .. (mappings ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Remote) && !string.IsNullOrWhiteSpace(m.Local))
            .Select(m => new PathMapping(Trim(m.Remote), Trim(m.Local)))
            .OrderByDescending(m => m.Remote.Length),
    ];

    private readonly Func<string, bool> _exists = exists ?? Directory.Exists;
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public IReadOnlyList<PathMapping> Mappings => _mappings;

    /// <summary>The local path for a path of the old app (unchanged when no mapping matches).</summary>
    public string Map(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath))
        {
            return remotePath;
        }

        foreach (var mapping in _mappings)
        {
            if (StartsWith(remotePath, mapping.Remote))
            {
                var rest = remotePath[mapping.Remote.Length..].TrimStart('/', '\\');
                if (rest.Length == 0)
                {
                    return mapping.Local;
                }

                var separator = mapping.Local.Contains('\\', StringComparison.Ordinal) && !mapping.Local.Contains('/', StringComparison.Ordinal) ? '\\' : '/';
                rest = separator == '/' ? rest.Replace('\\', '/') : rest.Replace('/', '\\');
                return mapping.Local.EndsWith(separator) ? mapping.Local + rest : mapping.Local + separator + rest;
            }
        }

        return remotePath;
    }

    public bool IsMapped(string remotePath) => _mappings.Any(m => StartsWith(remotePath, m.Remote));

    /// <summary>
    /// 0.12 (SV3 W-03): why StreamZH does not look at <paramref name="local"/> (outside the storage locations), null when
    /// it may. Judged as text before anything opens the path, so a network path the old app reports (\\host\share) is
    /// never contacted unless the owner allowed it.
    /// </summary>
    public string? StorageProblem(string local) => string.IsNullOrWhiteSpace(local) ? null : storageProblem?.Invoke(local);

    /// <summary>The folder exists and may be looked at (inside the storage locations); never throws.</summary>
    public bool FolderExists(string local) => Probe(local, Directory.Exists);

    /// <summary>The file exists and may be looked at (inside the storage locations); never throws.</summary>
    public bool FileExists(string local) => Probe(local, File.Exists);

    private bool Probe(string local, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(local) || StorageProblem(local) is not null)
        {
            return false;
        }

        try
        {
            return exists(local);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Registers a root path the old app uses (root folder, download folder) for the path check.</summary>
    public void Use(string remotePath)
    {
        if (!string.IsNullOrWhiteSpace(remotePath))
        {
            _seen.TryAdd(Trim(remotePath), 0);
        }
    }

    public IReadOnlyList<MigrationPathCheck> Checks() =>
    [
        .. _seen.Keys.Order(StringComparer.Ordinal).Select(remote =>
        {
            var local = Map(remote);
            if (storageProblem?.Invoke(local) is not null)
            {
                // Not looked at: outside the storage locations (the wizard offers the owner to allow it).
                return new MigrationPathCheck(remote, local, false, IsMapped(remote), OutsideStorage: true);
            }

            bool exists;
            try
            {
                exists = _exists(local);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                exists = false;
            }

            return new MigrationPathCheck(remote, local, exists, IsMapped(remote));
        }),
    ];

    private static string Trim(string path)
    {
        var trimmed = path.Trim();
        return trimmed.Length > 1 ? trimmed.TrimEnd('/', '\\') is { Length: > 0 } t ? t : trimmed[..1] : trimmed;
    }

    private static bool StartsWith(string path, string prefix)
    {
        var comparison = prefix.Contains('\\', StringComparison.Ordinal) || (prefix.Length > 1 && prefix[1] == ':')
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (prefix == "/")
        {
            return path.StartsWith('/');
        }

        if (!path.StartsWith(prefix, comparison))
        {
            return false;
        }

        return path.Length == prefix.Length || path[prefix.Length] is '/' or '\\';
    }
}
