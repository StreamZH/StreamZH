// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Data;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>
/// Remote path mappings (Radarr's Settings → Download Clients → Remote Path Mappings): a download client on another
/// machine or in a container reports "/downloads/movies/X"; StreamZH sees the same folder as "/mnt/torrents/movies/X".
/// The host must match the client's host setting (case-insensitive); the longest matching remote path wins.
/// </summary>
public sealed class RemotePathMappingService(RemotePathMappingRepository repository)
{
    public IReadOnlyList<RemotePathMapping> All() => repository.All();

    public RemotePathMapping? Find(int id) => repository.All().FirstOrDefault(m => m.Id == id);

    /// <exception cref="ValidationFailedException">Missing or invalid fields, or the same host and remote path exist.</exception>
    public RemotePathMapping Create(string? host, string? remotePath, string? localPath)
    {
        var (h, r, l) = Validate(null, host, remotePath, localPath);
        var id = repository.Add(h, r, l);
        return new RemotePathMapping(id, h, r, l);
    }

    /// <exception cref="LibraryException">Not found.</exception>
    public RemotePathMapping Update(int id, string? host, string? remotePath, string? localPath)
    {
        _ = Find(id) ?? throw new LibraryException($"Remote path mapping {id} not found.", LibraryError.NotFound);
        var (h, r, l) = Validate(id, host, remotePath, localPath);
        var mapping = new RemotePathMapping(id, h, r, l);
        repository.Update(mapping);
        return mapping;
    }

    public void Delete(int id)
    {
        _ = Find(id) ?? throw new LibraryException($"Remote path mapping {id} not found.", LibraryError.NotFound);
        repository.Delete(id);
    }

    /// <summary>The local path for a path the client on <paramref name="host"/> reports; unchanged when no mapping matches.</summary>
    public string RemoteToLocal(string? host, string remotePath) => Map(repository.All(), host, remotePath);

    public static string Map(IEnumerable<RemotePathMapping> mappings, string? host, string remotePath)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        if (string.IsNullOrEmpty(remotePath))
        {
            return remotePath;
        }

        var match = mappings
            .Where(m => string.Equals(m.Host.Trim(), host?.Trim(), StringComparison.OrdinalIgnoreCase) && IsUnder(remotePath, m.RemotePath))
            .MaxBy(m => m.RemotePath.Length);
        if (match is null)
        {
            return remotePath;
        }

        var rest = remotePath[Trim(match.RemotePath).Length..].TrimStart('/', '\\');
        if (rest.Length == 0)
        {
            return Trim(match.LocalPath);
        }

        // The rest keeps the client's separators; turn them into ours.
        rest = rest.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(Trim(match.LocalPath), rest);
    }

    private static bool IsUnder(string path, string folder)
    {
        var root = Trim(folder);
        if (root.Length == 0)
        {
            return false;
        }

        // Windows clients report "D:\Torrents"; compare without case there.
        var comparison = root.Contains('\\', StringComparison.Ordinal) || (root.Length >= 2 && root[1] == ':')
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return path.Equals(root, comparison)
            || (path.Length > root.Length && path.StartsWith(root, comparison) && path[root.Length] is '/' or '\\')
            || (root is "/" && path.StartsWith('/'));
    }

    private static string Trim(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') is { Length: > 0 } t ? t : path[..1] : path;

    private (string Host, string Remote, string Local) Validate(int? id, string? host, string? remotePath, string? localPath)
    {
        var errors = new Dictionary<string, string[]>();
        var h = host?.Trim() ?? string.Empty;
        var r = remotePath?.Trim() ?? string.Empty;
        var l = localPath?.Trim() ?? string.Empty;
        if (h.Length == 0)
        {
            errors["host"] = ["Enter the host of the download client exactly as in its settings."];
        }
        else if (h.Length > 255 || h.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\'))
        {
            errors["host"] = ["Enter only the host name or IP address, e.g. 192.168.1.20 or torrent-box."];
        }

        if (r.Length == 0)
        {
            errors["remotePath"] = ["Enter the folder as the download client sees it, e.g. /downloads/."];
        }
        else if (r.Length > 1000 || !(r.StartsWith('/') || r.StartsWith(@"\\", StringComparison.Ordinal) || (r.Length >= 3 && char.IsAsciiLetter(r[0]) && r[1] == ':' && r[2] is '\\' or '/')))
        {
            errors["remotePath"] = ["Use a full path, e.g. /downloads/ or D:\\Torrents\\."];
        }

        if (l.Length == 0)
        {
            errors["localPath"] = ["Enter the same folder as StreamZH sees it."];
        }
        else if (l.Length > 1000 || !Path.IsPathFullyQualified(l))
        {
            errors["localPath"] = ["Use a full path on this computer."];
        }
        else if (!Directory.Exists(l))
        {
            errors["localPath"] = ["This folder does not exist on this computer."];
        }

        if (errors.Count == 0 && repository.All().Any(m => m.Id != id
            && string.Equals(m.Host, h, StringComparison.OrdinalIgnoreCase) && string.Equals(Trim(m.RemotePath), Trim(r), StringComparison.Ordinal)))
        {
            errors["remotePath"] = ["A mapping for this host and remote path exists already."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return (h, AddSeparator(r), AddSeparator(l));
    }

    /// <summary>Stored like Radarr: folders end with their separator.</summary>
    private static string AddSeparator(string path)
    {
        var separator = path.Contains('\\', StringComparison.Ordinal) && !path.Contains('/', StringComparison.Ordinal) ? '\\' : '/';
        if (!OperatingSystem.IsWindows() && path.StartsWith('/'))
        {
            separator = '/';
        }

        return path.EndsWith('/') || path.EndsWith('\\') ? path : path + separator;
    }
}
