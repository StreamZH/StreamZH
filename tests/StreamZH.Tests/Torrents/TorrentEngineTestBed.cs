// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections.TrackerServer;
using MonoTorrent.TrackerServer;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Torrent;

namespace StreamZH.Tests.Torrents;

/// <summary>
/// Everything a torrent engine test needs on localhost only: a temporary folder, a database for the engine settings,
/// a MonoTorrent HTTP tracker, a plain MonoTorrent engine that seeds generated data, and StreamZH engines that download
/// it. Nothing binds anything but 127.0.0.1; DHT, local peer discovery and port forwarding are off.
/// </summary>
public sealed class TorrentEngineTestBed : IAsyncDisposable
{
    private readonly List<TorrentEngine> _engines = [];
    private readonly TrackerServer _tracker = new() { AllowUnregisteredTorrents = true };
    private ITrackerListener? _trackerListener;
    private ClientEngine? _seeder;

    public TorrentEngineTestBed()
    {
        Directory.CreateDirectory(Root);
        Database = new Database(Path.Combine(Root, "streamzh.db"));
        Database.Migrate();
        Config = new ConfigRepository(Database);
        Settings = new TorrentEngineSettings(Config);
        Settings.Save(TorrentEngineOptions.Default with
        {
            ListenPort = FreePort(),
            BindAddress = "127.0.0.1",
            Dht = false,
            LocalPeerDiscovery = false,
            PortForwarding = false,
            SeedRatio = null,
        });
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-torrent-{Guid.NewGuid():N}");

    public Database Database { get; }

    public ConfigRepository Config { get; }

    public TorrentEngineSettings Settings { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    public ITorrentSavePaths SavePaths => new FolderSavePaths(DownloadFolder);

    public string? AnnounceUrl { get; private set; }

    /// <summary>The downloaded data of the StreamZH engine: &lt;root&gt;/downloads/&lt;category&gt;.</summary>
    public string DownloadFolder => Path.Combine(Root, "downloads");

    public string StateFolder => Path.Combine(Root, "torrents");

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A StreamZH engine on the shared settings and state folder (a second call = the same data after a restart).</summary>
    public TorrentEngine CreateEngine(TimeProvider? time = null)
    {
        var engine = new TorrentEngine(
            Settings,
            SavePaths,
            new TorrentStateStore(StateFolder),
            time ?? TimeProvider.System,
            Logs.CreateLogger<TorrentEngine>());
        _engines.Add(engine);
        return engine;
    }

    /// <summary>Starts the local HTTP tracker; <paramref name="pathSecret"/> goes into the announce path like a passkey.</summary>
    public string StartTracker(string pathSecret = "announce")
    {
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/{pathSecret}/";
        _trackerListener = TrackerListenerFactory.CreateHttp(prefix);
        _tracker.RegisterListener(_trackerListener);
        _trackerListener.Start();
        AnnounceUrl = prefix;
        return prefix;
    }

    /// <summary>Random data (not compressible, nothing copyrighted) in a single file or a folder of files.</summary>
    public string CreateData(string name, params int[] sizes)
    {
        var source = Path.Combine(Root, "seed");
        if (sizes.Length == 1)
        {
            var file = Path.Combine(source, name);
            Directory.CreateDirectory(source);
            File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(sizes[0]));
            return file;
        }

        var folder = Path.Combine(source, name);
        Directory.CreateDirectory(Path.Combine(folder, "Extras"));
        for (var i = 0; i < sizes.Length; i++)
        {
            var path = i == 0 ? Path.Combine(folder, $"{name}.mkv") : Path.Combine(folder, "Extras", $"part{i}.bin");
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(sizes[i]));
        }

        return folder;
    }

    /// <summary>A v1 .torrent for <paramref name="path"/> announcing to the local tracker (or <paramref name="announce"/>).</summary>
    public static async Task<byte[]> CreateTorrentAsync(string path, string? announce)
    {
        var creator = new TorrentCreator(TorrentType.V1Only) { PieceLength = 32 * 1024 };
        if (announce is not null)
        {
            creator.Announces.Add([announce]);
        }

        var dictionary = await creator.CreateAsync(new TorrentFileSource(path));
        return dictionary.Encode();
    }

    /// <summary>A plain MonoTorrent engine seeding <paramref name="dataPath"/>; returns once it seeds and has announced.</summary>
    public async Task<TorrentManager> SeedAsync(byte[] torrentFile, string dataPath)
    {
        _seeder ??= new ClientEngine(new EngineSettingsBuilder
        {
            AllowLocalPeerDiscovery = false,
            AllowPortForwarding = false,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false,
            CacheDirectory = Path.Combine(Root, "seeder-cache"),
            DhtEndPoint = null,
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, FreePort()) },
        }.ToSettings());

        var torrent = MonoTorrent.Torrent.Load(torrentFile);
        var saveDirectory = Path.GetDirectoryName(dataPath)!;
        var manager = await _seeder.AddAsync(torrent, saveDirectory);
        await manager.StartAsync();
        await WaitUntilAsync(() => manager.State == MonoTorrent.Client.TorrentState.Seeding, "the seeder to seed");
        if (torrent.AnnounceUrls.Count > 0)
        {
            await WaitUntilAsync(() => manager.TrackerManager.Tiers.Any(t => t.LastAnnounceSucceeded), "the seeder to announce");
        }

        return manager;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(100);
        }
    }

    /// <summary>Ticks the engine until <paramref name="condition"/> holds for the torrent's status.</summary>
    public static async Task<TorrentStatus> WaitForAsync(TorrentEngine engine, string hash, Func<TorrentStatus, bool> condition, string what, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        TorrentStatus? last = null;
        while (true)
        {
            await engine.TickAsync(CancellationToken.None);
            last = (await engine.GetTorrentsAsync(null, CancellationToken.None)).FirstOrDefault(t => t.Hash == hash);
            if (last is not null && condition(last))
            {
                return last;
            }

            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Timed out waiting for {what}; last state {last?.State}, {last?.Progress:P0}, error {last?.Error}.");
            }

            await Task.Delay(200);
        }
    }

    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var engine in _engines)
        {
            await engine.StopAsync(CancellationToken.None);
        }

        if (_seeder is not null)
        {
            await _seeder.StopAllAsync();
            _seeder.Dispose();
        }

        _trackerListener?.Stop();
        _tracker.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FolderSavePaths(string root) : ITorrentSavePaths
    {
        public string FolderFor(string? category) => Path.Combine(root, string.IsNullOrWhiteSpace(category) ? "other" : category);

        public IReadOnlyList<string> Categories() => ["movies", "series", "other"];
    }
}

/// <summary>Keeps every log line (formatted) so tests can check what is never logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger<T> CreateLogger<T>() => new Logger<T>(new Factory(this));

    public ILogger CreateLogger(string categoryName) => new Capturing(this);

    public void Dispose()
    {
    }

    private sealed class Factory(CapturingLoggerProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider loggerProvider)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }

    private sealed class Capturing(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
