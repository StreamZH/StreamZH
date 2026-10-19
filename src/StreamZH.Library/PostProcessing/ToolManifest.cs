// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using StreamZH.Data;

namespace StreamZH.Library.PostProcessing;

/// <summary>
/// 0.12 (V-02): what StreamZH installed into &lt;data&gt;/tools. When a tool is installed, the SHA-256 of every file in
/// its folder is recorded; a copy in &lt;data&gt;/tools is only run while its folder holds exactly these files with these
/// hashes. A program placed there any other way (a download into that folder, a copy from an older version) is ignored.
/// </summary>
public interface IToolManifest
{
    /// <summary>File (relative to the tool folder, "/" separated) → SHA-256 (lower-case hex); null when nothing is recorded.</summary>
    IReadOnlyDictionary<string, string>? Files(PostProcessTool tool);

    void Record(PostProcessTool tool, IReadOnlyDictionary<string, string> files);
}

/// <summary>The manifest in the database (Config <c>ToolManifest</c>), which is as private as the data folder.</summary>
public sealed class ConfigToolManifest(ConfigRepository config) : IToolManifest
{
    private readonly Lock _lock = new();

    public IReadOnlyDictionary<string, string>? Files(PostProcessTool tool) =>
        Load().TryGetValue(PostProcessTools.Key(tool), out var files) && files.Count > 0 ? files : null;

    public void Record(PostProcessTool tool, IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        lock (_lock)
        {
            var all = Load();
            all[PostProcessTools.Key(tool)] = new Dictionary<string, string>(files, StringComparer.Ordinal);
            config.Set(ConfigKeys.ToolManifest, JsonSerializer.Serialize(all));
        }
    }

    private Dictionary<string, Dictionary<string, string>> Load()
    {
        try
        {
            return config.Get(ConfigKeys.ToolManifest) is { Length: > 0 } json
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>A manifest that lives only as long as the process (tests, and setups without a database).</summary>
public sealed class MemoryToolManifest : IToolManifest
{
    private readonly ConcurrentDictionary<PostProcessTool, IReadOnlyDictionary<string, string>> _files = new();

    public IReadOnlyDictionary<string, string>? Files(PostProcessTool tool) => _files.GetValueOrDefault(tool);

    public void Record(PostProcessTool tool, IReadOnlyDictionary<string, string> files) =>
        _files[tool] = new Dictionary<string, string>(files, StringComparer.Ordinal);
}

/// <summary>
/// Hashes of a tool folder. A file is hashed again whenever its size, write time or creation time changed (a library
/// scan runs ffprobe for every file, so an unchanged file is not read again each time).
/// </summary>
public static class ToolFolderHashes
{
    private static readonly ConcurrentDictionary<string, (long Length, long Written, long Created, string Hash)> Cache = new(StringComparer.Ordinal);

    /// <summary>Every file below <paramref name="folder"/> (relative, "/" separated) → SHA-256.</summary>
    public static Dictionary<string, string> Compute(string folder)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            result[Relative(folder, file)] = Sha256(file) ?? throw new IOException($"{file} cannot be read.");
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="folder"/> holds exactly the files of <paramref name="expected"/>, each with its hash (no
    /// extra file: on Windows a program also loads DLLs from its own folder).
    /// </summary>
    public static bool Matches(string folder, IReadOnlyDictionary<string, string> expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        try
        {
            var seen = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                if (!expected.TryGetValue(Relative(folder, file), out var hash) || !string.Equals(Sha256(file), hash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                seen++;
            }

            return seen == expected.Count && seen > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Lower-case hex SHA-256, null when the file cannot be read.</summary>
    public static string? Sha256(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // A link could point anywhere; StreamZH never installs one.
                return null;
            }

            var key = Path.GetFullPath(path);
            var written = info.LastWriteTimeUtc.Ticks;
            var created = info.CreationTimeUtc.Ticks;
            if (Cache.TryGetValue(key, out var cached) && cached.Length == info.Length && cached.Written == written && cached.Created == created)
            {
                return cached.Hash;
            }

            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            Cache[key] = (info.Length, written, created, hash);
            return hash;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Relative(string folder, string file) => Path.GetRelativePath(folder, file).Replace('\\', '/');
}
