// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamZH.Torrent;

/// <summary>
/// What StreamZH remembers about one torrent of the built-in engine, next to MonoTorrent's own fast-resume data:
/// where it is saved, its category and seed goals, the transfer totals and seeding time across restarts, and the
/// files (so data can be deleted while the engine is off). Stored in &lt;data&gt;/torrents/state.json.
/// </summary>
public sealed class TorrentEntry
{
    /// <summary>Info hash, hex, lower case (v1, or v2 for v2-only torrents).</summary>
    public string Hash { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string SavePath { get; set; } = string.Empty;

    /// <summary>The magnet link it was added from (may carry tracker passkeys: never logged, never returned).</summary>
    public string? MagnetUri { get; set; }

    /// <summary>A copy of the .torrent file is in &lt;data&gt;/torrents/files.</summary>
    public bool HasTorrentFile { get; set; }

    /// <summary>The torrent's own ratio goal; null = the engine's default, negative = no goal.</summary>
    public double? SeedRatio { get; set; }

    /// <summary>The torrent's own seeding time goal; null = the engine's default, negative = no goal.</summary>
    public int? SeedTimeMinutes { get; set; }

    public DateTimeOffset Added { get; set; }

    public DateTimeOffset? CompletedOn { get; set; }

    public double SeedingSeconds { get; set; }

    public long Uploaded { get; set; }

    public long Downloaded { get; set; }

    public long Size { get; set; }

    /// <summary>0..1, the last known progress (shown while the engine is off).</summary>
    public double Progress { get; set; }

    /// <summary>Paused by the user (or added paused).</summary>
    public bool Paused { get; set; }

    /// <summary>Stopped because the seed goal was reached.</summary>
    public bool SeedGoalReached { get; set; }

    /// <summary>The folder of a multi-file torrent, or <see cref="SavePath"/> for a single file.</summary>
    public string? ContainingDirectory { get; set; }

    /// <summary>The single file or the torrent's folder (like qBittorrent's content_path).</summary>
    public string? ContentPath { get; set; }

    /// <summary>File paths relative to <see cref="ContainingDirectory"/>.</summary>
    public List<string>? Files { get; set; }

    /// <summary>The last error, kept while the engine is off.</summary>
    public string? Error { get; set; }
}

/// <summary>Loads and saves <see cref="TorrentEntry"/> rows atomically (write to a temporary file, then replace).</summary>
public sealed class TorrentStateStore(string folder)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Folder { get; } = folder;

    public string StateFile => Path.Combine(Folder, "state.json");

    /// <summary>MonoTorrent's cache: fast-resume data, magnet metadata and the DHT node cache.</summary>
    public string CacheFolder => Path.Combine(Folder, "cache");

    /// <summary>Copies of added .torrent files, "{hash}.torrent".</summary>
    public string FilesFolder => Path.Combine(Folder, "files");

    public string TorrentFile(string hash) => Path.Combine(FilesFolder, $"{hash}.torrent");

    public List<TorrentEntry> Load()
    {
        if (!File.Exists(StateFile))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(StateFile);
            return JsonSerializer.Deserialize<StateDocument>(stream, Json)?.Torrents?
                .Where(e => IsHash(e.Hash) && Path.IsPathFullyQualified(e.SavePath))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            // A damaged file must not stop StreamZH; the previous copy is kept for inspection.
            File.Copy(StateFile, StateFile + ".damaged", overwrite: true);
            return [];
        }
    }

    public void Save(IEnumerable<TorrentEntry> entries)
    {
        Directory.CreateDirectory(Folder);
        var temporary = StateFile + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, new StateDocument(1, [.. entries.OrderBy(e => e.Added)]), Json);
        }

        File.Move(temporary, StateFile, overwrite: true);
    }

    /// <summary>40 (v1) or 64 (v2) lower-case hex characters; anything else is never used as a file name.</summary>
    public static bool IsHash(string? hash) =>
        hash is { Length: 40 or 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record StateDocument(int Version, List<TorrentEntry>? Torrents);
}
