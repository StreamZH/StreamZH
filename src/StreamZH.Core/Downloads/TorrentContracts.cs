// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamZH.Core.Downloads;

/// <summary>0.11: how a release is downloaded.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DownloadProtocol>))]
public enum DownloadProtocol
{
    [JsonStringEnumMemberName("usenet")]
    Usenet = 1,

    [JsonStringEnumMemberName("torrent")]
    Torrent = 2,
}

/// <summary>
/// A configured download client (table DownloadClients, 0.11): an external torrent client (qBittorrent, Transmission,
/// Deluge) or StreamZH's own torrent engine. Usenet keeps using the built-in engine (or the blackhole folder) as before.
/// <see cref="Settings"/> holds the implementation's fields (host, port, useSsl, urlBase, username, password, category,
/// …) in camelCase; secrets in it are never returned by the API.
/// </summary>
public sealed record DownloadClientDefinition(
    int Id,
    string Name,
    string Implementation,
    DownloadProtocol Protocol,
    bool Enable,
    int Priority,
    IReadOnlyDictionary<string, JsonElement> Settings)
{
    public const string QBittorrent = "qBittorrent";
    public const string Transmission = "Transmission";
    public const string Deluge = "Deluge";

    /// <summary>StreamZH's own torrent engine.</summary>
    public const string BuiltInTorrent = "StreamZH";

    /// <summary>After import: remove the torrent (and its data) once the seed goals are met.</summary>
    public bool RemoveCompleted { get; init; } = true;

    public string? Text(string key) =>
        Settings.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public int? Number(string key) =>
        Settings.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    public bool Flag(string key, bool fallback = false) =>
        Settings.TryGetValue(key, out var value) ? value.ValueKind == JsonValueKind.True || (value.ValueKind != JsonValueKind.False && fallback) : fallback;
}

/// <summary>What the client reports about a torrent, normalised.</summary>
public enum TorrentState
{
    Queued,
    Checking,
    Downloading,
    Stalled,
    Paused,

    /// <summary>Done and uploading.</summary>
    Seeding,

    /// <summary>Done and stopped (seed goal reached or paused after completion).</summary>
    Completed,
    Error,
}

public sealed record TorrentStatus(
    string Hash,
    string Name,
    TorrentState State,
    long Size,
    long Downloaded,
    long Uploaded,
    double Ratio,
    long DownloadSpeed,
    long UploadSpeed,
    TimeSpan? Eta,
    int Seeders,
    int Peers,
    string? Category,
    string? SavePath,
    string? ContentPath,
    string? Error,
    DateTimeOffset? Added,
    DateTimeOffset? CompletedOn,
    TimeSpan SeedingTime)
{
    public bool IsDone => State is TorrentState.Seeding or TorrentState.Completed;

    public double Progress => Size <= 0 ? 0 : Math.Clamp((double)Downloaded / Size, 0, 1);
}

/// <summary>A torrent to add: a .torrent file or a magnet link.</summary>
public sealed record TorrentAddRequest(
    string Title,
    byte[]? File,
    string? MagnetUri,
    string? Category,
    string? SavePath = null,
    bool Paused = false,
    double? SeedRatio = null,
    int? SeedTimeMinutes = null);

public sealed record TorrentClientInfo(string Name, string Version, IReadOnlyList<string> Categories);

/// <summary>
/// One torrent client implementation (qBittorrent WebUI API v2, Transmission RPC, Deluge JSON-RPC, the built-in engine).
/// Stateless per call: the definition carries host/credentials. Errors throw <see cref="DownloadClientException"/>.
/// </summary>
public interface ITorrentClient
{
    /// <summary>The <see cref="DownloadClientDefinition.Implementation"/> it handles.</summary>
    string Implementation { get; }

    Task<TorrentClientInfo> TestAsync(DownloadClientDefinition client, CancellationToken cancellationToken);

    /// <returns>The info hash (hex, lower case).</returns>
    Task<string> AddAsync(DownloadClientDefinition client, TorrentAddRequest request, CancellationToken cancellationToken);

    /// <summary>Torrents in the client's StreamZH category (all when <paramref name="category"/> is null).</summary>
    Task<IReadOnlyList<TorrentStatus>> GetTorrentsAsync(DownloadClientDefinition client, string? category, CancellationToken cancellationToken);

    Task RemoveAsync(DownloadClientDefinition client, string hash, bool deleteData, CancellationToken cancellationToken);
}

/// <summary>A download client failed; <see cref="Field"/> names the setting to fix (host, password, …) when known.</summary>
public sealed class DownloadClientException(string message, string? field = null, Exception? inner = null) : Exception(message, inner)
{
    public string? Field { get; } = field;
}
