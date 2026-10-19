// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using StreamZH.Data;

namespace StreamZH.Torrent;

/// <summary>How the engine treats peer connections that are not encrypted (BitTorrent message stream encryption).</summary>
public enum TorrentEncryptionMode
{
    /// <summary>Try encrypted connections first, accept plain ones (default, like qBittorrent's "Allow encryption").</summary>
    Prefer,

    /// <summary>Only encrypted connections; fewer peers.</summary>
    Require,

    /// <summary>Plain connections first, encrypted ones when the other side asks.</summary>
    Allow,
}

/// <summary>The settings of the built-in torrent engine, parsed.</summary>
/// <param name="ListenPort">TCP port for incoming peers (DHT uses the same number on UDP).</param>
/// <param name="BindAddress">Empty = all interfaces; otherwise the only address the engine listens on.</param>
/// <param name="DownloadLimitKBps">0 = unlimited.</param>
/// <param name="UploadLimitKBps">0 = unlimited.</param>
/// <param name="SeedRatio">Stop seeding at this ratio unless the torrent has its own goal; null (or 0 when saved) = no ratio goal.</param>
/// <param name="SeedTimeMinutes">Stop seeding after this many minutes unless the torrent has its own goal; null (or 0 when saved) = none.</param>
public sealed record TorrentEngineOptions(
    int ListenPort,
    string BindAddress,
    int MaxConnections,
    int MaxConnectionsPerTorrent,
    long DownloadLimitKBps,
    long UploadLimitKBps,
    bool Dht,
    bool Pex,
    bool LocalPeerDiscovery,
    TorrentEncryptionMode Encryption,
    bool PortForwarding,
    double? SeedRatio,
    int? SeedTimeMinutes)
{
    /// <summary>One above Transmission's 51413, so both can run side by side during a move.</summary>
    public const int DefaultListenPort = 51414;

    public const int MinListenPort = 1024;
    public const int MaxListenPort = 65535;
    public const int DefaultMaxConnections = 200;
    public const int MaxMaxConnections = 5000;
    public const int DefaultMaxConnectionsPerTorrent = 50;
    public const int MaxMaxConnectionsPerTorrent = 1000;

    /// <summary>MonoTorrent limits are int bytes per second: about 2 GB/s.</summary>
    public const long MaxLimitKBps = int.MaxValue / 1024;

    public const double DefaultSeedRatio = 1.0;
    public const double MaxSeedRatio = 1000;

    /// <summary>One year.</summary>
    public const int MaxSeedTimeMinutes = 525_600;

    public static TorrentEngineOptions Default { get; } = new(
        DefaultListenPort,
        string.Empty,
        DefaultMaxConnections,
        DefaultMaxConnectionsPerTorrent,
        0,
        0,
        Dht: true,
        Pex: true,
        LocalPeerDiscovery: false,
        TorrentEncryptionMode.Prefer,
        PortForwarding: false,
        DefaultSeedRatio,
        null);

    /// <summary>The parsed <see cref="BindAddress"/>, null for all interfaces.</summary>
    public IPAddress? BindIpAddress => BindAddress.Length > 0 && IPAddress.TryParse(BindAddress, out var address) ? address : null;

    /// <summary>Field errors keyed by the camelCase names of the API resource; empty when valid.</summary>
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (ListenPort is < MinListenPort or > MaxListenPort)
        {
            errors["listenPort"] = [$"Enter a port from {MinListenPort} to {MaxListenPort}."];
        }

        if (BindAddress.Length > 0)
        {
            if (!IPAddress.TryParse(BindAddress, out var address)
                || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                || IsMulticastOrBroadcast(address))
            {
                errors["bindAddress"] = ["Enter an IP address of this computer (for example a VPN interface), or leave it empty for all interfaces."];
            }
        }

        if (MaxConnections is < 1 or > MaxMaxConnections)
        {
            errors["maxConnections"] = [$"Enter a number from 1 to {MaxMaxConnections}."];
        }

        if (MaxConnectionsPerTorrent is < 1 or > MaxMaxConnectionsPerTorrent)
        {
            errors["maxConnectionsPerTorrent"] = [$"Enter a number from 1 to {MaxMaxConnectionsPerTorrent}."];
        }

        if (DownloadLimitKBps is < 0 or > MaxLimitKBps)
        {
            errors["downloadLimitKBps"] = [$"Enter 0 (unlimited) up to {MaxLimitKBps} KB/s."];
        }

        if (UploadLimitKBps is < 0 or > MaxLimitKBps)
        {
            errors["uploadLimitKBps"] = [$"Enter 0 (unlimited) up to {MaxLimitKBps} KB/s."];
        }

        if (SeedRatio is { } ratio && (double.IsNaN(ratio) || ratio is < 0 or > MaxSeedRatio))
        {
            errors["seedRatio"] = [$"Enter a ratio from 0 to {MaxSeedRatio}, or leave it empty to seed without a ratio goal."];
        }

        if (SeedTimeMinutes is < 0 or > MaxSeedTimeMinutes)
        {
            errors["seedTimeMinutes"] = [$"Enter 0 to {MaxSeedTimeMinutes} minutes, or leave it empty for no time goal."];
        }

        return errors;
    }

    private static bool IsMulticastOrBroadcast(IPAddress address) =>
        address.IsIPv6Multicast
        || address.Equals(IPAddress.Broadcast)
        || (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239);
}

/// <summary>Reads and writes <see cref="TorrentEngineOptions"/> in the Config table (cached until saved).</summary>
public sealed class TorrentEngineSettings(ConfigRepository config)
{
    private readonly Lock _lock = new();
    private TorrentEngineOptions? _current;

    /// <summary>Raised after <see cref="Save"/> stored new values (the engine restarts with them).</summary>
    public event Action<TorrentEngineOptions>? Changed;

    public TorrentEngineOptions Current
    {
        get
        {
            lock (_lock)
            {
                return _current ??= Read();
            }
        }
    }

    /// <summary>
    /// Stores valid options (call <see cref="TorrentEngineOptions.Validate"/> first). A default goal of 0 is stored as
    /// "no goal", like the settings page says ("Empty or 0: no limit").
    /// </summary>
    public void Save(TorrentEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validate().Count > 0)
        {
            throw new ArgumentException("The torrent engine settings are not valid.", nameof(options));
        }

        options = options with
        {
            SeedRatio = options.SeedRatio is 0 ? null : options.SeedRatio,
            SeedTimeMinutes = options.SeedTimeMinutes is 0 ? null : options.SeedTimeMinutes,
        };

        config.Set(ConfigKeys.TorrentEngineListenPort, Text(options.ListenPort));
        config.Set(ConfigKeys.TorrentEngineBindAddress, options.BindAddress.Trim());
        config.Set(ConfigKeys.TorrentEngineMaxConnections, Text(options.MaxConnections));
        config.Set(ConfigKeys.TorrentEngineMaxConnectionsPerTorrent, Text(options.MaxConnectionsPerTorrent));
        config.Set(ConfigKeys.TorrentEngineDownloadLimitKBps, Text(options.DownloadLimitKBps));
        config.Set(ConfigKeys.TorrentEngineUploadLimitKBps, Text(options.UploadLimitKBps));
        config.Set(ConfigKeys.TorrentEngineDht, Flag(options.Dht));
        config.Set(ConfigKeys.TorrentEnginePex, Flag(options.Pex));
        config.Set(ConfigKeys.TorrentEngineLocalPeerDiscovery, Flag(options.LocalPeerDiscovery));
        config.Set(ConfigKeys.TorrentEngineEncryption, EncryptionName(options.Encryption));
        config.Set(ConfigKeys.TorrentEnginePortForwarding, Flag(options.PortForwarding));
        config.Set(ConfigKeys.TorrentEngineSeedRatio, options.SeedRatio is { } ratio ? ratio.ToString("R", CultureInfo.InvariantCulture) : "none");
        config.Set(ConfigKeys.TorrentEngineSeedTimeMinutes, options.SeedTimeMinutes is { } minutes ? Text(minutes) : "none");

        TorrentEngineOptions saved;
        lock (_lock)
        {
            _current = null;
            saved = _current = Read();
        }

        Changed?.Invoke(saved);
    }

    public static string EncryptionName(TorrentEncryptionMode mode) => mode switch
    {
        TorrentEncryptionMode.Require => "require",
        TorrentEncryptionMode.Allow => "allow",
        _ => "prefer",
    };

    public static TorrentEncryptionMode? ParseEncryption(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "prefer" or "preferred" => TorrentEncryptionMode.Prefer,
        "require" or "required" => TorrentEncryptionMode.Require,
        "allow" or "allowed" => TorrentEncryptionMode.Allow,
        _ => null,
    };

    private TorrentEngineOptions Read()
    {
        var defaults = TorrentEngineOptions.Default;
        var options = new TorrentEngineOptions(
            Int(ConfigKeys.TorrentEngineListenPort) ?? defaults.ListenPort,
            config.Get(ConfigKeys.TorrentEngineBindAddress)?.Trim() ?? defaults.BindAddress,
            Int(ConfigKeys.TorrentEngineMaxConnections) ?? defaults.MaxConnections,
            Int(ConfigKeys.TorrentEngineMaxConnectionsPerTorrent) ?? defaults.MaxConnectionsPerTorrent,
            Long(ConfigKeys.TorrentEngineDownloadLimitKBps) ?? defaults.DownloadLimitKBps,
            Long(ConfigKeys.TorrentEngineUploadLimitKBps) ?? defaults.UploadLimitKBps,
            Bool(ConfigKeys.TorrentEngineDht) ?? defaults.Dht,
            Bool(ConfigKeys.TorrentEnginePex) ?? defaults.Pex,
            Bool(ConfigKeys.TorrentEngineLocalPeerDiscovery) ?? defaults.LocalPeerDiscovery,
            ParseEncryption(config.Get(ConfigKeys.TorrentEngineEncryption)) ?? defaults.Encryption,
            Bool(ConfigKeys.TorrentEnginePortForwarding) ?? defaults.PortForwarding,
            config.Get(ConfigKeys.TorrentEngineSeedRatio) switch
            {
                null => defaults.SeedRatio,
                "none" => null,
                var text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) ? ratio : defaults.SeedRatio,
            },
            config.Get(ConfigKeys.TorrentEngineSeedTimeMinutes) switch
            {
                null or "none" => defaults.SeedTimeMinutes,
                var text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ? minutes : defaults.SeedTimeMinutes,
            });

        // A value edited by hand outside the valid range falls back to the default of that field.
        var errors = options.Validate();
        return errors.Count == 0 ? options : options with
        {
            ListenPort = errors.ContainsKey("listenPort") ? defaults.ListenPort : options.ListenPort,
            BindAddress = errors.ContainsKey("bindAddress") ? defaults.BindAddress : options.BindAddress,
            MaxConnections = errors.ContainsKey("maxConnections") ? defaults.MaxConnections : options.MaxConnections,
            MaxConnectionsPerTorrent = errors.ContainsKey("maxConnectionsPerTorrent") ? defaults.MaxConnectionsPerTorrent : options.MaxConnectionsPerTorrent,
            DownloadLimitKBps = errors.ContainsKey("downloadLimitKBps") ? defaults.DownloadLimitKBps : options.DownloadLimitKBps,
            UploadLimitKBps = errors.ContainsKey("uploadLimitKBps") ? defaults.UploadLimitKBps : options.UploadLimitKBps,
            SeedRatio = errors.ContainsKey("seedRatio") ? defaults.SeedRatio : options.SeedRatio,
            SeedTimeMinutes = errors.ContainsKey("seedTimeMinutes") ? defaults.SeedTimeMinutes : options.SeedTimeMinutes,
        };
    }

    private int? Int(string key) =>
        int.TryParse(config.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private long? Long(string key) =>
        long.TryParse(config.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private bool? Bool(string key) => config.Get(key)?.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "true" : "false";
}
