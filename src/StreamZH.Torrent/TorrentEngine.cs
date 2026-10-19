// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.PortForwarding;
using MonoTorrent.Trackers;
using StreamZH.Core.Downloads;
using MtState = MonoTorrent.Client.TorrentState;
using TorrentState = StreamZH.Core.Downloads.TorrentState;
using MtTorrent = MonoTorrent.Torrent;

namespace StreamZH.Torrent;

/// <summary>What the status window and the settings page show about the engine.</summary>
/// <param name="Wanted">A "StreamZH" download client is enabled (the engine runs only then, or while a torrent is added).</param>
/// <param name="PortState">"listening", "notFree" (another program has the port), "idle" (nothing to transfer, nothing bound) or "stopped".</param>
public sealed record TorrentEngineStatus(
    bool Wanted,
    bool Running,
    int ListenPort,
    string PortState,
    string? StartError,
    int Torrents,
    int Active,
    long DownloadSpeed,
    long UploadSpeed,
    string EngineVersion,
    IReadOnlyList<TorrentProblem> Problems);

/// <param name="Kind">"error", "diskFull" or "tracker".</param>
public sealed record TorrentProblem(string Hash, string Name, string Kind, string Message);

/// <summary>
/// StreamZH's own torrent client: one MonoTorrent <see cref="ClientEngine"/> listening only on the configured port,
/// without UPnP/NAT-PMP or local peer discovery unless switched on, never serving web seeds or HTTP streams. Torrents
/// download straight into their category's completed folder (so nothing is moved while seeding), stop when their seed
/// goal is reached and survive restarts (MonoTorrent fast resume + &lt;data&gt;/torrents/state.json). All public
/// members are thread-safe.
/// </summary>
public sealed class TorrentEngine : IAsyncDisposable
{
    public const int MaxTorrentFileBytes = 10 * 1024 * 1024;

    /// <summary>A torrent without data coming in for this long is "stalled".</summary>
    public static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);

    private readonly TorrentEngineSettings _settings;
    private readonly ITorrentSavePaths _savePaths;
    private readonly TimeProvider _time;
    private readonly ILogger<TorrentEngine> _logger;
    private readonly TorrentStateStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Magnet links: the metadata is fetched alone first and checked (TorrentPathSafety) before any file is written.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _metadataFetches = new(StringComparer.Ordinal);

    // Set once disposed: the host may still call StopAsync afterwards (a test host stopped after its services were disposed).
    private volatile bool _disposed;
    private readonly Dictionary<string, TorrentEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Live> _live = new(StringComparer.Ordinal);
    private readonly List<(IPEndPoint EndPoint, IListener Listener)> _peerListeners = [];
    private ClientEngine? _engine;
    private TorrentEngineOptions? _runningOptions;
    private string? _startError;
    private bool _loaded;
    private bool _dirty;
    private DateTimeOffset _lastSave;

    public TorrentEngine(TorrentEngineSettings settings, ITorrentSavePaths savePaths, TorrentStateStore store, TimeProvider time, ILogger<TorrentEngine> logger)
    {
        _settings = settings;
        _savePaths = savePaths;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>Set by the hosted service: an enabled "StreamZH" download client exists.</summary>
    public bool Wanted { get; set; }

    public bool IsRunning => Volatile.Read(ref _engine) is not null;

    /// <summary>The MonoTorrent version, for the client test and System → Status.</summary>
    public static string EngineVersion { get; } =
        typeof(ClientEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ClientEngine).Assembly.GetName().Version?.ToString(3) ?? "3";

    public string DataFolder => _store.Folder;

    // ---- Lifecycle ----

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops every torrent (fast-resume data is written), saves the state and frees the port.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>New settings: a running engine restarts with them (torrents keep their state).</summary>
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_engine is null)
            {
                return;
            }

            await StopCoreAsync();
            await StartCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None);
        _disposed = true;
        _gate.Dispose();
    }

    private async Task StartCoreAsync()
    {
        if (_engine is not null)
        {
            return;
        }

        LoadEntries();
        var options = _settings.Current;
        Directory.CreateDirectory(_store.CacheFolder);
        ClientEngine? engine = null;
        try
        {
            lock (_peerListeners)
            {
                _peerListeners.Clear();
            }

            engine = new ClientEngine(BuildEngineSettings(options, _store.CacheFolder), BuildFactories(options));
            _engine = engine;
            _runningOptions = options;
            _startError = null;
            foreach (var entry in _entries.Values.OrderBy(e => e.Added).ToList())
            {
                await AttachAsync(engine, entry, options);
            }

            _logger.LogInformation(
                "Torrent engine started (port {Port}{Bind}, {Count} torrents, DHT {Dht}, port forwarding {Forwarding})",
                options.ListenPort,
                options.BindAddress.Length > 0 ? $" on {options.BindAddress}" : string.Empty,
                _entries.Count,
                options.Dht ? "on" : "off",
                options.PortForwarding ? "on" : "off");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _startError = ex.Message;
            _logger.LogError(ex, "The torrent engine could not start");
            _live.Clear();
            _engine = null;
            engine?.Dispose();
        }
    }

    private async Task StopCoreAsync()
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        foreach (var hash in _metadataFetches.Keys.ToList())
        {
            if (_metadataFetches.TryRemove(hash, out var fetch))
            {
                fetch.Cancel();
            }
        }

        var now = _time.GetUtcNow();
        foreach (var (hash, live) in _live)
        {
            if (_entries.TryGetValue(hash, out var entry))
            {
                Update(entry, live, now);
            }
        }

        // All at once: each stop waits up to a few seconds for its final tracker announce.
        await Task.WhenAll(_live.Values.Select(live => StopManagerAsync(live.Manager)));
        foreach (var (hash, live) in _live)
        {
            if (_entries.TryGetValue(hash, out var entry))
            {
                Sample(entry, live);
            }
        }

        _live.Clear();
        SaveState();
        _engine = null;
        _runningOptions = null;
        engine.Dispose();
        lock (_peerListeners)
        {
            _peerListeners.Clear();
        }

        _logger.LogInformation("Torrent engine stopped");
    }

    // ---- Torrents ----

    /// <summary>Adds a .torrent file or magnet link and starts it (unless added paused). An existing torrent is kept.</summary>
    /// <returns>The info hash (hex, lower case).</returns>
    /// <exception cref="DownloadClientException">The torrent or magnet link is invalid, or the folder is not writable.</exception>
    public async Task<string> AddAsync(TorrentAddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (torrent, magnet) = Parse(request);
        var hash = HashOf(torrent?.InfoHashes ?? magnet!.InfoHashes);
        var savePath = ResolveSavePath(request);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            LoadEntries();
            if (_entries.ContainsKey(hash))
            {
                _logger.LogInformation("Torrent '{Name}' is already in the torrent engine", _entries[hash].Name);
                return hash;
            }

            try
            {
                Directory.CreateDirectory(savePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DownloadClientException($"The download folder {savePath} cannot be created: {ex.Message}", "savePath", ex);
            }

            var entry = new TorrentEntry
            {
                Hash = hash,
                Name = torrent?.Name is { Length: > 0 } name ? name : magnet?.Name is { Length: > 0 } magnetName ? magnetName : request.Title,
                Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
                SavePath = savePath,
                SeedRatio = request.SeedRatio,
                SeedTimeMinutes = request.SeedTimeMinutes,
                Added = _time.GetUtcNow(),
                Paused = request.Paused,
                Size = torrent?.Size ?? 0,
            };

            if (torrent is not null)
            {
                Directory.CreateDirectory(_store.FilesFolder);
                await File.WriteAllBytesAsync(_store.TorrentFile(hash), request.File!, cancellationToken);
                entry.HasTorrentFile = true;
                FillMetadata(entry, torrent);
            }
            else
            {
                entry.MagnetUri = request.MagnetUri!.Trim();
            }

            _entries[hash] = entry;
            SaveState();

            await StartCoreAsync();
            if (_engine is { } engine && _runningOptions is { } options)
            {
                await AttachAsync(engine, entry, options);
            }

            _logger.LogInformation(
                "Torrent '{Name}' added to the torrent engine ({Source}, category {Category}){Paused}",
                entry.Name,
                torrent is null ? "magnet link" : "torrent file",
                entry.Category ?? "none",
                entry.Paused ? ", paused" : string.Empty);
            return hash;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TorrentStatus>> GetTorrentsAsync(string? category, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LoadEntries();
            var now = _time.GetUtcNow();
            return [.. _entries.Values
                .Where(e => category is null || string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Added)
                .Select(e => _live.TryGetValue(e.Hash, out var live) ? LiveStatus(e, live, now) : OfflineStatus(e))];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>False when the torrent is unknown.</returns>
    public async Task<bool> PauseAsync(string hash, CancellationToken cancellationToken)
    {
        hash = NormalizeHash(hash);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LoadEntries();
            if (!_entries.TryGetValue(hash, out var entry))
            {
                return false;
            }

            entry.Paused = true;
            if (_live.TryGetValue(hash, out var live))
            {
                Update(entry, live, _time.GetUtcNow());
                await StopManagerAsync(live.Manager);
                Sample(entry, live);
            }

            SaveState();
            _logger.LogInformation("Torrent '{Name}' paused", entry.Name);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a paused or finished torrent again (a reached seed goal is cleared; it applies again later).</summary>
    /// <returns>False when the torrent is unknown.</returns>
    public async Task<bool> ResumeAsync(string hash, CancellationToken cancellationToken)
    {
        hash = NormalizeHash(hash);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LoadEntries();
            if (!_entries.TryGetValue(hash, out var entry))
            {
                return false;
            }

            entry.Paused = false;
            entry.SeedGoalReached = false;
            entry.Error = null;
            SaveState();
            await StartCoreAsync();
            if (_live.TryGetValue(hash, out var live) && live.Manager.State is MtState.Stopped or MtState.Error or MtState.Paused)
            {
                await StartManagerAsync(entry, live.Manager);
            }

            _logger.LogInformation("Torrent '{Name}' resumed", entry.Name);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a torrent; with <paramref name="deleteData"/> its downloaded files too (also while the engine is off).</summary>
    /// <returns>False when the torrent is unknown.</returns>
    public async Task<bool> RemoveAsync(string hash, bool deleteData, CancellationToken cancellationToken)
    {
        hash = NormalizeHash(hash);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LoadEntries();
            if (!_entries.TryGetValue(hash, out var entry))
            {
                return false;
            }

            if (_metadataFetches.TryRemove(hash, out var fetch))
            {
                fetch.Cancel();
            }

            if (_live.Remove(hash, out var live))
            {
                Update(entry, live, _time.GetUtcNow());
                await StopManagerAsync(live.Manager);
                try
                {
                    // Fast-resume data and cached metadata go; the downloaded files are deleted below (or kept).
                    await _engine!.RemoveAsync(live.Manager, RemoveMode.CacheDataOnly);
                }
                catch (Exception ex) when (ex is TorrentException or InvalidOperationException)
                {
                    _logger.LogWarning("Torrent '{Name}' could not be removed from the engine cleanly: {Error}", entry.Name, ex.Message);
                }
            }

            _entries.Remove(hash);
            TryDelete(_store.TorrentFile(hash));
            // MonoTorrent names its cache files with the upper-case hash (gone already when the engine ran).
            TryDelete(Path.Combine(_store.CacheFolder, "fastresume", $"{hash.ToUpperInvariant()}.fresume"));
            TryDelete(Path.Combine(_store.CacheFolder, "metadata", $"{hash.ToUpperInvariant()}.torrent"));
            if (deleteData)
            {
                DeleteData(entry);
            }

            SaveState();
            _logger.LogInformation("Torrent '{Name}' removed{Data}", entry.Name, deleteData ? " with its data" : string.Empty);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Housekeeping, every few seconds while the engine runs: totals and seeding time, metadata of magnet links,
    /// completion, seed goals (the torrent stops), and saving the state.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_engine is null)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var options = _settings.Current;
            foreach (var (hash, live) in _live.ToList())
            {
                if (!_entries.TryGetValue(hash, out var entry))
                {
                    continue;
                }

                Update(entry, live, now);
                if (live.Manager.State == MtState.Seeding && SeedGoalReached(entry, options) is { } reason)
                {
                    entry.SeedGoalReached = true;
                    _dirty = true;
                    _logger.LogInformation("Torrent '{Name}' reached its seed goal ({Reason}), seeding stopped", entry.Name, reason);
                    await StopManagerAsync(live.Manager);
                    Sample(entry, live);
                }
            }

            if (_dirty && now - _lastSave >= SaveInterval)
            {
                SaveState();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public TorrentEngineStatus Status()
    {
        var options = _runningOptions ?? _settings.Current;
        var engine = _engine;
        var problems = new List<TorrentProblem>();
        var active = 0;
        long down = 0;
        long up = 0;
        if (_gate.Wait(TimeSpan.FromSeconds(2)))
        {
            try
            {
                foreach (var entry in _entries.Values)
                {
                    if (_live.TryGetValue(entry.Hash, out var live))
                    {
                        var manager = live.Manager;
                        if (manager.State is MtState.Downloading or MtState.Seeding or MtState.Metadata or MtState.Hashing or MtState.Starting or MtState.FetchingHashes)
                        {
                            active++;
                        }

                        down += manager.Monitor.DownloadRate;
                        up += manager.Monitor.UploadRate;
                        if (manager.State == MtState.Error && manager.Error is { } error)
                        {
                            problems.Add(new TorrentProblem(entry.Hash, entry.Name, IsDiskFull(error.Exception) ? "diskFull" : "error", ErrorText(manager, error)));
                        }
                        else if (TrackerProblem(manager) is { } tracker)
                        {
                            problems.Add(new TorrentProblem(entry.Hash, entry.Name, "tracker", tracker));
                        }
                    }
                    else if (entry.Error is { Length: > 0 } error)
                    {
                        problems.Add(new TorrentProblem(entry.Hash, entry.Name, "error", error));
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        return new TorrentEngineStatus(
            Wanted,
            engine is not null,
            options.ListenPort,
            PortState(engine),
            _startError,
            _entries.Count,
            active,
            down,
            up,
            EngineVersion,
            problems);
    }

    /// <summary>
    /// Whether the listen port can be bound right now: false when another program has it. A running engine answers
    /// from its own listener; otherwise the port is bound for a moment and released.
    /// </summary>
    public bool ListenPortAvailable()
    {
        var engine = _engine;
        if (engine is not null && PortState(engine) is "listening")
        {
            return true;
        }

        if (engine is not null && PortState(engine) is "notFree")
        {
            return false;
        }

        var options = _settings.Current;
        try
        {
            var listener = new TcpListener(options.BindIpAddress ?? IPAddress.Any, options.ListenPort);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // ---- MonoTorrent setup ----

    /// <summary>The MonoTorrent settings for <paramref name="options"/>: listen on the configured port only.</summary>
    public static EngineSettings BuildEngineSettings(TorrentEngineOptions options, string cacheFolder)
    {
        ArgumentNullException.ThrowIfNull(options);
        var bind = options.BindIpAddress;
        var listen = new Dictionary<string, IPEndPoint>();
        if (bind is null)
        {
            listen["ipv4"] = new IPEndPoint(IPAddress.Any, options.ListenPort);
            listen["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, options.ListenPort);
        }
        else
        {
            listen[bind.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4"] = new IPEndPoint(bind, options.ListenPort);
        }

        // MonoTorrent's DHT is IPv4 only: with an IPv6 bind address it stays off rather than binding all interfaces.
        IPEndPoint? dht = !options.Dht ? null
            : bind is null ? new IPEndPoint(IPAddress.Any, options.ListenPort)
            : bind.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(bind, options.ListenPort)
            : null;

        var builder = new EngineSettingsBuilder
        {
            AllowedEncryption = options.Encryption switch
            {
                TorrentEncryptionMode.Require => [EncryptionType.RC4Full, EncryptionType.RC4Header],
                TorrentEncryptionMode.Allow => [EncryptionType.PlainText, EncryptionType.RC4Header, EncryptionType.RC4Full],
                _ => [EncryptionType.RC4Full, EncryptionType.RC4Header, EncryptionType.PlainText],
            },
            AllowLocalPeerDiscovery = options.LocalPeerDiscovery,
            AllowPortForwarding = options.PortForwarding,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            CacheDirectory = cacheFolder,
            DhtEndPoint = dht,
            FastResumeMode = FastResumeMode.BestEffort,
            ListenEndPoints = listen,
            MaximumConnections = options.MaxConnections,
            MaximumDownloadRate = (int)Math.Min(int.MaxValue, options.DownloadLimitKBps * 1024),
            MaximumUploadRate = (int)Math.Min(int.MaxValue, options.UploadLimitKBps * 1024),
        };
        return builder.ToSettings();
    }

    public static TorrentSettings BuildTorrentSettings(TorrentEngineOptions options, bool isPrivate)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TorrentSettingsBuilder
        {
            AllowDht = options.Dht && !isPrivate,
            AllowPeerExchange = options.Pex && !isPrivate,
            CreateContainingDirectory = true,
            MaximumConnections = options.MaxConnectionsPerTorrent,
        }.ToSettings();
    }

    private Factories BuildFactories(TorrentEngineOptions options)
    {
        var factories = Factories.Default.WithPeerConnectionListenerCreator(endPoint =>
        {
            var listener = Factories.Default.CreatePeerConnectionListener(endPoint);
            lock (_peerListeners)
            {
                _peerListeners.Add((endPoint, listener));
            }

            return listener;
        });

        // No UPnP/NAT-PMP discovery at all unless it is switched on.
        return options.PortForwarding ? factories : factories.WithPortForwarderCreator(() => new NoPortForwarder());
    }

    private string PortState(ClientEngine? engine)
    {
        if (engine is null)
        {
            return "stopped";
        }

        List<(IPEndPoint EndPoint, IListener Listener)> listeners;
        lock (_peerListeners)
        {
            listeners = [.. _peerListeners];
        }

        // An IPv6 listener that fails next to a working IPv4 one is a host without IPv6, not a taken port.
        var failed = listeners.Where(l => l.Listener.Status == ListenerStatus.PortNotFree).ToList();
        if (failed.Count > 0 && (failed.Count == listeners.Count || failed.Any(l => l.EndPoint.AddressFamily == AddressFamily.InterNetwork)))
        {
            return "notFree";
        }

        return listeners.Any(l => l.Listener.Status == ListenerStatus.Listening) ? "listening" : "idle";
    }

    private async Task AttachAsync(ClientEngine engine, TorrentEntry entry, TorrentEngineOptions options)
    {
        if (_live.ContainsKey(entry.Hash))
        {
            return;
        }

        try
        {
            TorrentManager manager;
            var file = _store.TorrentFile(entry.Hash);
            if (entry.HasTorrentFile && File.Exists(file))
            {
                var torrent = await MtTorrent.LoadAsync(file);
                if (TorrentPathSafety.Problem(torrent) is { } problem)
                {
                    entry.Error = $"Not downloaded: {problem}";
                    _dirty = true;
                    _logger.LogWarning("Torrent '{Name}' was not started: {Problem}", entry.Name, problem);
                    return;
                }

                manager = await engine.AddAsync(torrent, entry.SavePath, BuildTorrentSettings(options, torrent.IsPrivate));

                // Second line of defence: every file MonoTorrent will write must stay inside the save folder.
                if (TorrentPathSafety.OutsideFolder(entry.SavePath, manager) is not null)
                {
                    await engine.RemoveAsync(manager, RemoveMode.CacheDataOnly);
                    entry.Error = "Not downloaded: a file of this torrent would be written outside the download folder.";
                    _dirty = true;
                    _logger.LogWarning("Torrent '{Name}' was not started: a file would be written outside {Folder}", entry.Name, entry.SavePath);
                    return;
                }
            }
            else if (entry.MagnetUri is { } uri && MagnetLink.TryParse(uri, out var magnet))
            {
                // Only the metadata first; the torrent is added from it once its file names are checked.
                StartMetadataFetch(engine, entry, magnet);
                return;
            }
            else
            {
                entry.Error = "The torrent file is missing. Remove the torrent and grab it again.";
                _dirty = true;
                return;
            }

            var live = new Live(manager) { LastUpdate = _time.GetUtcNow() };
            _live[entry.Hash] = live;
            if (entry.Files is null && manager.HasMetadata)
            {
                FillPaths(entry, manager);
                _dirty = true;
            }

            if (!entry.Paused && !entry.SeedGoalReached)
            {
                await StartManagerAsync(entry, manager);
            }
        }
        catch (Exception ex) when (ex is TorrentException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            entry.Error = ex.Message;
            _dirty = true;
            _logger.LogWarning("Torrent '{Name}' could not be loaded: {Error}", entry.Name, ex.Message);
        }
    }

    private void StartMetadataFetch(ClientEngine engine, TorrentEntry entry, MagnetLink magnet)
    {
        var cts = new CancellationTokenSource();
        if (!_metadataFetches.TryAdd(entry.Hash, cts))
        {
            cts.Dispose();
            return;
        }

        var hash = entry.Hash;
        _ = Task.Run(async () =>
        {
            try
            {
                var data = await engine.DownloadMetadataAsync(magnet, cts.Token);
                await OnMetadataAsync(engine, hash, data.ToArray());
            }
            catch (OperationCanceledException)
            {
                // Removed or the engine stopped; the next start fetches again.
            }
            catch (Exception ex)
            {
                _logger.LogWarning("The metadata of torrent {Hash} could not be fetched: {Error}", hash, ex.Message);
            }
            finally
            {
                if (_metadataFetches.TryGetValue(hash, out var current) && ReferenceEquals(current, cts))
                {
                    _metadataFetches.TryRemove(hash, out _);
                }

                cts.Dispose();
            }
        });
    }

    private async Task OnMetadataAsync(ClientEngine engine, string hash, byte[] metadata)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_entries.TryGetValue(hash, out var entry) || !ReferenceEquals(_engine, engine) || _runningOptions is not { } options)
            {
                return;
            }

            if (metadata.Length > MaxTorrentFileBytes || !MtTorrent.TryLoad(metadata, out var torrent))
            {
                entry.Error = "The torrent's metadata is not valid.";
                _dirty = true;
                return;
            }

            if (TorrentPathSafety.Problem(torrent) is { } problem)
            {
                entry.Error = $"Not downloaded: {problem}";
                _dirty = true;
                _logger.LogWarning("Torrent '{Name}' was not started: {Problem}", torrent.Name, problem);
                return;
            }

            Directory.CreateDirectory(_store.FilesFolder);
            await File.WriteAllBytesAsync(_store.TorrentFile(hash), metadata);
            entry.HasTorrentFile = true;
            FillMetadata(entry, torrent);
            _dirty = true;
            SaveState();
            await AttachAsync(engine, entry, options);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartManagerAsync(TorrentEntry entry, TorrentManager manager)
    {
        try
        {
            await manager.StartAsync();
        }
        catch (Exception ex) when (ex is TorrentException or IOException or InvalidOperationException)
        {
            entry.Error = ex.Message;
            _dirty = true;
            _logger.LogWarning("Torrent '{Name}' could not start: {Error}", entry.Name, ex.Message);
        }
    }

    private async Task StopManagerAsync(TorrentManager manager)
    {
        if (manager.State is MtState.Stopped or MtState.Stopping)
        {
            return;
        }

        try
        {
            await manager.StopAsync(StopTimeout);
        }
        catch (Exception ex) when (ex is TorrentException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug("Torrent '{Name}' did not stop cleanly: {Error}", manager.Name, ex.Message);
        }
    }

    // ---- Status ----

    private TorrentStatus LiveStatus(TorrentEntry entry, Live live, DateTimeOffset now)
    {
        var manager = live.Manager;
        Sample(entry, live);
        var size = manager.Torrent?.Size ?? entry.Size;
        var progress = manager.HasMetadata && (manager.HashChecked || manager.State != MtState.Stopped) ? manager.Progress / 100.0 : entry.Progress;
        var downloaded = (long)Math.Round(size * Math.Clamp(progress, 0, 1));
        var downRate = manager.Monitor.DownloadRate;
        var upRate = manager.Monitor.UploadRate;
        var complete = manager.HasMetadata && manager.Complete;
        var state = MapState(entry, manager, live, now, complete);
        TimeSpan? eta = state is TorrentState.Downloading && downRate > 0 && size > downloaded ? TimeSpan.FromSeconds((size - downloaded) / (double)downRate) : null;
        var (seeds, leechs) = PeerCounts(manager);
        var error = manager.State == MtState.Error && manager.Error is { } e ? ErrorText(manager, e) : entry.Error;
        return new TorrentStatus(
            entry.Hash,
            entry.Name,
            state,
            size,
            downloaded,
            entry.Uploaded,
            Ratio(entry.Uploaded, size),
            state is TorrentState.Downloading or TorrentState.Stalled ? downRate : 0,
            state is TorrentState.Downloading or TorrentState.Stalled or TorrentState.Seeding ? upRate : 0,
            eta,
            seeds,
            leechs,
            entry.Category,
            entry.SavePath,
            entry.ContentPath ?? entry.SavePath,
            state == TorrentState.Error ? error : null,
            entry.Added,
            entry.CompletedOn,
            TimeSpan.FromSeconds(entry.SeedingSeconds));
    }

    private static TorrentStatus OfflineStatus(TorrentEntry entry)
    {
        var done = entry.Progress >= 1 || entry.CompletedOn is not null;
        var state = entry.Error is { Length: > 0 } ? TorrentState.Error
            : done && (entry.SeedGoalReached || entry.Paused) ? TorrentState.Completed
            : entry.Paused ? TorrentState.Paused
            : done ? TorrentState.Completed
            : TorrentState.Queued;
        var downloaded = (long)Math.Round(entry.Size * Math.Clamp(entry.Progress, 0, 1));
        return new TorrentStatus(
            entry.Hash,
            entry.Name,
            state,
            entry.Size,
            downloaded,
            entry.Uploaded,
            Ratio(entry.Uploaded, entry.Size),
            0,
            0,
            null,
            0,
            0,
            entry.Category,
            entry.SavePath,
            entry.ContentPath ?? entry.SavePath,
            state == TorrentState.Error ? entry.Error : null,
            entry.Added,
            entry.CompletedOn,
            TimeSpan.FromSeconds(entry.SeedingSeconds));
    }

    private static TorrentState MapState(TorrentEntry entry, TorrentManager manager, Live live, DateTimeOffset now, bool complete) => manager.State switch
    {
        MtState.Error => TorrentState.Error,
        MtState.Seeding => TorrentState.Seeding,
        MtState.Hashing or MtState.HashingPaused or MtState.FetchingHashes => TorrentState.Checking,
        MtState.Starting => complete ? TorrentState.Seeding : TorrentState.Checking,
        MtState.Paused => complete ? TorrentState.Completed : TorrentState.Paused,
        MtState.Downloading or MtState.Metadata => live.StalledSince is { } since && now - since >= StalledAfter ? TorrentState.Stalled : TorrentState.Downloading,
        _ => entry.Error is { Length: > 0 } ? TorrentState.Error
            : complete && (entry.SeedGoalReached || entry.Paused) ? TorrentState.Completed
            : entry.Paused ? TorrentState.Paused
            : complete ? TorrentState.Completed
            : TorrentState.Queued,
    };

    private static (int Seeds, int Leechs) PeerCounts(TorrentManager manager)
    {
        var seeds = manager.Peers.Seeds;
        var leechs = manager.Peers.Leechs;
        foreach (var tier in manager.TrackerManager.Tiers)
        {
            foreach (var info in tier.ScrapeInfo.Values)
            {
                seeds = Math.Max(seeds, info.Complete);
                leechs = Math.Max(leechs, info.Incomplete);
            }
        }

        return (seeds, leechs);
    }

    private static double Ratio(long uploaded, long size) => size <= 0 ? 0 : Math.Round(uploaded / (double)size, 3);

    /// <summary>Why the goal is reached ("ratio 1.00", "seeded 60 min"), or null.</summary>
    private static string? SeedGoalReached(TorrentEntry entry, TorrentEngineOptions options)
    {
        var ratioGoal = entry.SeedRatio ?? options.SeedRatio;
        var timeGoal = entry.SeedTimeMinutes ?? options.SeedTimeMinutes;
        var ratio = Ratio(entry.Uploaded, entry.Size);
        if (ratioGoal is >= 0 and var goal && ratio >= goal)
        {
            return FormattableString.Invariant($"ratio {ratio:0.00} of {goal:0.##}");
        }

        if (timeGoal is >= 0 and var minutes && entry.SeedingSeconds >= minutes * 60.0)
        {
            return FormattableString.Invariant($"seeded {minutes} min");
        }

        return null;
    }

    /// <summary>Totals, seeding time, metadata, completion and errors of a live torrent into its entry.</summary>
    private void Update(TorrentEntry entry, Live live, DateTimeOffset now)
    {
        var manager = live.Manager;
        Sample(entry, live);
        if (manager.State == MtState.Seeding)
        {
            var elapsed = (now - live.LastUpdate).TotalSeconds;
            if (elapsed > 0 && elapsed < 600)
            {
                entry.SeedingSeconds += elapsed;
                _dirty = true;
            }
        }

        live.LastUpdate = now;

        if (manager.Torrent is { } torrent && entry.Files is null)
        {
            FillMetadata(entry, torrent);
            FillPaths(entry, manager);
            if (torrent.IsPrivate && _runningOptions is { } options)
            {
                _ = manager.UpdateSettingsAsync(BuildTorrentSettings(options, isPrivate: true));
            }

            _dirty = true;
        }

        if (manager.HasMetadata && (manager.HashChecked || manager.State != MtState.Stopped))
        {
            var progress = manager.Progress / 100.0;
            if (Math.Abs(progress - entry.Progress) > 0.0001)
            {
                entry.Progress = progress;
                _dirty = true;
            }

            if (manager.Complete && entry.CompletedOn is null)
            {
                entry.CompletedOn = now;
                entry.Progress = 1;
                _dirty = true;
                _logger.LogInformation("Torrent '{Name}' finished downloading", entry.Name);
            }
        }

        if (manager.State == MtState.Error && manager.Error is { } error)
        {
            var text = ErrorText(manager, error);
            if (entry.Error != text)
            {
                entry.Error = text;
                _dirty = true;
                _logger.LogWarning("Torrent '{Name}' stopped with an error: {Error}", entry.Name, text);
            }
        }
        else if (entry.Error is not null && manager.State is not MtState.Stopped)
        {
            entry.Error = null;
            _dirty = true;
        }

        var receiving = manager.Monitor.DownloadRate > 0;
        if (manager.State is MtState.Downloading or MtState.Metadata && !receiving)
        {
            live.StalledSince ??= now;
        }
        else
        {
            live.StalledSince = null;
        }
    }

    /// <summary>Adds what the manager transferred since the last sample (the monitor restarts at 0 after a stop).</summary>
    private void Sample(TorrentEntry entry, Live live)
    {
        var sent = live.Manager.Monitor.DataBytesSent;
        var received = live.Manager.Monitor.DataBytesReceived;
        var sentDelta = sent >= live.LastSent ? sent - live.LastSent : sent;
        var receivedDelta = received >= live.LastReceived ? received - live.LastReceived : received;
        live.LastSent = sent;
        live.LastReceived = received;
        if (sentDelta > 0 || receivedDelta > 0)
        {
            entry.Uploaded += sentDelta;
            entry.Downloaded += receivedDelta;
            _dirty = true;
        }
    }

    private static void FillMetadata(TorrentEntry entry, MtTorrent torrent)
    {
        entry.Name = torrent.Name is { Length: > 0 } name ? name : entry.Name;
        entry.Size = torrent.Size;
    }

    /// <summary>Where MonoTorrent puts the files (its own escaping of names), for the content path and deleting data.</summary>
    private static void FillPaths(TorrentEntry entry, TorrentManager manager)
    {
        if (!manager.HasMetadata || manager.ContainingDirectory is not { Length: > 0 } containing || manager.Files.Count == 0)
        {
            return;
        }

        entry.ContainingDirectory = Path.GetFullPath(containing);
        entry.Files = [.. manager.Files.Select(f => Path.GetRelativePath(entry.ContainingDirectory, f.DownloadCompleteFullPath))];
        entry.ContentPath = manager.Files.Count == 1 ? manager.Files[0].DownloadCompleteFullPath : entry.ContainingDirectory;
    }

    private static string ErrorText(TorrentManager manager, Error error)
    {
        var message = error.Reason switch
        {
            Reason.ReadFailure => $"Reading the files failed: {error.Exception.Message}",
            Reason.WriteFailure when IsDiskFull(error.Exception) => "The disk is full.",
            Reason.WriteFailure => $"Writing the files failed: {error.Exception.Message}",
            _ => error.Exception.Message,
        };
        return TrackerUrls.Scrub(message, Trackers(manager));
    }

    private static string? TrackerProblem(TorrentManager manager)
    {
        if (manager.State is not (MtState.Downloading or MtState.Seeding or MtState.Metadata))
        {
            return null;
        }

        // A tier counts as failing when its last announce failed or its tracker is offline / answered with a failure
        // (MonoTorrent reports an unreachable HTTP tracker as Offline while the announce itself "succeeds").
        static bool Failing(TrackerTier tier) =>
            tier.TimeSinceLastAnnounce != TimeSpan.MaxValue
            && (!tier.LastAnnounceSucceeded
                || tier.ActiveTracker.Status is TrackerState.Offline or TrackerState.InvalidResponse
                || tier.ActiveTracker.FailureMessage is { Length: > 0 });

        var tiers = manager.TrackerManager.Tiers;
        if (tiers.Count == 0 || !tiers.All(Failing))
        {
            return null;
        }

        var tracker = tiers[0].ActiveTracker;
        var reason = tracker.FailureMessage is { Length: > 0 } failure
            ? TrackerUrls.Scrub(failure, Trackers(manager))
            : tracker.Status switch
            {
                TrackerState.InvalidResponse => "invalid answer",
                TrackerState.Connecting => "connecting",
                _ => "not reachable",
            };
        return $"{TrackerUrls.Redact(tracker.Uri)}: {reason}";
    }

    private static IEnumerable<Uri> Trackers(TorrentManager manager) =>
        manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri);

    public static bool IsDiskFull(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is IOException io)
            {
                var code = io.HResult & 0xFFFF;
                if (code is 0x70 or 0x27 || (!OperatingSystem.IsWindows() && io.HResult == 28)
                    || io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---- Parsing, paths, files ----

    private static (MtTorrent? Torrent, MagnetLink? Magnet) Parse(TorrentAddRequest request)
    {
        if (request.File is { Length: > 0 } bytes)
        {
            if (bytes.Length > MaxTorrentFileBytes)
            {
                throw new DownloadClientException("The torrent file is too large.");
            }

            if (!MtTorrent.TryLoad(bytes, out var torrent))
            {
                throw new DownloadClientException("The torrent file is not valid.");
            }

            if (TorrentPathSafety.Problem(torrent) is { } problem)
            {
                throw new DownloadClientException($"The torrent was not added: {problem}");
            }

            return (torrent, null);
        }

        if (request.MagnetUri is { Length: > 0 } uri)
        {
            if (!uri.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || !MagnetLink.TryParse(uri.Trim(), out var magnet))
            {
                throw new DownloadClientException("The magnet link is not valid.");
            }

            return (null, magnet);
        }

        throw new DownloadClientException("Neither a torrent file nor a magnet link was given.");
    }

    private string ResolveSavePath(TorrentAddRequest request)
    {
        if (request.SavePath is { Length: > 0 } explicitPath)
        {
            if (!Path.IsPathFullyQualified(explicitPath))
            {
                throw new DownloadClientException("The save path must be a full path.", "savePath");
            }

            return Path.GetFullPath(explicitPath);
        }

        return Path.GetFullPath(_savePaths.FolderFor(request.Category));
    }

    public static string HashOf(InfoHashes hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        return hashes.V1OrV2.ToHex().ToLowerInvariant();
    }

    private static string NormalizeHash(string hash) => (hash ?? string.Empty).Trim().ToLowerInvariant();

    private void DeleteData(TorrentEntry entry)
    {
        if (entry.ContainingDirectory is not { } folder || entry.Files is not { } files)
        {
            return;
        }

        var root = Path.GetFullPath(folder);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        foreach (var relative in files)
        {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(rootWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                continue;
            }

            TryDelete(path);
            TryDelete(path + ".!mt");
            DeleteEmptyFolders(Path.GetDirectoryName(path), root);
        }

        // A multi-file torrent's own folder goes when nothing else is left in it; the save path itself stays.
        if (!string.Equals(root, Path.GetFullPath(entry.SavePath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
        {
            DeleteEmptyFolders(root, Path.GetDirectoryName(root));
        }
    }

    /// <summary>Deletes <paramref name="folder"/> and its parents while they are empty, never <paramref name="stopAt"/> or above.</summary>
    private static void DeleteEmptyFolders(string? folder, string? stopAt)
    {
        static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var limit = stopAt is null ? null : Trim(Path.GetFullPath(stopAt));
        while (folder is not null && Directory.Exists(folder))
        {
            var current = Trim(Path.GetFullPath(folder));
            if (limit is null || string.Equals(current, limit, StringComparison.Ordinal) || !current.StartsWith(limit, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any())
                {
                    return;
                }

                Directory.Delete(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            folder = Path.GetDirectoryName(current);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete {Path}: {Error}", path, ex.Message);
        }
    }

    // ---- State ----

    private void LoadEntries()
    {
        if (_loaded)
        {
            return;
        }

        foreach (var entry in _store.Load())
        {
            _entries[entry.Hash] = entry;
        }

        _loaded = true;
    }

    private void SaveState()
    {
        try
        {
            _store.Save(_entries.Values);
            _dirty = false;
            _lastSave = _time.GetUtcNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The torrent list could not be saved: {Error}", ex.Message);
        }
    }

    private sealed class Live(TorrentManager manager)
    {
        public TorrentManager Manager { get; } = manager;

        public long LastSent { get; set; }

        public long LastReceived { get; set; }

        public DateTimeOffset? StalledSince { get; set; }

        /// <summary>When seeding time was last counted.</summary>
        public DateTimeOffset LastUpdate { get; set; }
    }

    /// <summary>Port forwarding switched off: no UPnP/NAT-PMP discovery, no mappings.</summary>
    private sealed class NoPortForwarder : IPortForwarder
    {
        public event EventHandler? MappingsChanged
        {
            add { }
            remove { }
        }

        public bool Active => false;

        public Mappings Mappings => Mappings.Empty;

        public Task RegisterMappingAsync(Mapping mapping) => Task.CompletedTask;

        public Task UnregisterMappingAsync(Mapping mapping, CancellationToken token) => Task.CompletedTask;

        public Task StartAsync(CancellationToken token) => Task.CompletedTask;

        public Task StopAsync(CancellationToken token) => Task.CompletedTask;

        public Task StopAsync(bool removeExistingMappings, CancellationToken token) => Task.CompletedTask;
    }
}

/// <summary>Where torrents of a category are saved: the category's completed folder (Settings → Downloads).</summary>
public interface ITorrentSavePaths
{
    string FolderFor(string? category);

    /// <summary>The category names (for the client test).</summary>
    IReadOnlyList<string> Categories();
}
