// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Usenet.Nntp;

namespace StreamZH.Usenet.Pool;

/// <summary>
/// The connections of one server: at most <see cref="UsenetServer.Connections"/> open at once, each serving up to
/// <see cref="UsenetServer.ArticlesPerRequest"/> fetches (a semaphore of connections × articles per request, so waiting
/// callers are bounded by the callers themselves), opened lazily, reused while fresh, closed when idle or broken.
/// Also keeps the server's health (back-off after a refused login or when it cannot be reached at all).
/// </summary>
internal sealed class ServerPool
{
    /// <summary>Upper limit of <see cref="UsenetServer.ArticlesPerRequest"/>.</summary>
    public const int MaxArticlesPerRequest = 20;

    private readonly Lock _lock = new();
    private readonly Stack<PooledConnection> _idle = new();

    // With pipelining: connections in use that can take more fetches.
    private readonly List<PooledConnection> _shared = [];
    private readonly SemaphoreSlim _slots;
    private readonly NntpConnectionSettings _settings;
    private readonly UsenetPoolOptions _options;
    private readonly TimeProvider _time;
    private readonly Action _healthChanged;
    private readonly int _connections;
    private readonly int _depth;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private UsenetServer _server;
    private int _inUse;
    private int _open;
    private int _opening;
    private bool _retired;
    private DateTimeOffset? _retryAt;
    private bool _authFailed;
    private string? _lastError;

    public ServerPool(UsenetServer server, UsenetPoolOptions options, TimeProvider time, Action? healthChanged = null)
    {
        _server = server;
        _options = options;
        _time = time;
        _healthChanged = healthChanged ?? (() => { });
        _settings = options.SettingsFor(server);
        _connections = server.Connections;
        _depth = DepthOf(server);
        _slots = new SemaphoreSlim(_connections * _depth, _connections * _depth);
        ConnectionKey = KeyOf(server);
    }

    /// <summary>Name, priority, retention, quota and the failure action may change without new connections; see <see cref="ConnectionKey"/>.</summary>
    public UsenetServer Server
    {
        get => Volatile.Read(ref _server);
        set => Volatile.Write(ref _server, value);
    }

    /// <summary>Everything that needs new connections when it changes.</summary>
    public string ConnectionKey { get; }

    /// <summary>Fetches running right now.</summary>
    public int InUse => Volatile.Read(ref _inUse);

    /// <summary>Connections fetching (or opening) right now; with pipelining several fetches share one.</summary>
    public int BusyConnections
    {
        get
        {
            if (_depth == 1)
            {
                return InUse;
            }

            lock (_lock)
            {
                return _shared.Count + _opening;
            }
        }
    }

    /// <summary>Most fetches at once: connections × articles per request.</summary>
    public int Capacity => _connections * _depth;

    /// <summary>Open connections, idle or in use.</summary>
    public int Open => Volatile.Read(ref _open);

    public string? LastError
    {
        get
        {
            lock (_lock)
            {
                return _lastError;
            }
        }
    }

    public bool AuthFailed
    {
        get
        {
            lock (_lock)
            {
                return _authFailed;
            }
        }
    }

    public static int DepthOf(UsenetServer server) => Math.Clamp(server.ArticlesPerRequest, 1, MaxArticlesPerRequest);

    public static string KeyOf(UsenetServer server) =>
        string.Join(
            '\n',
            server.Host,
            server.Port,
            server.UseSsl,
            server.Username,
            server.Password,
            server.Connections,
            server.TimeoutSeconds,
            server.CertificateCheck,
            DepthOf(server));

    public bool IsAvailable(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _retryAt is not { } retry || now >= retry;
        }
    }

    /// <summary>When the server is tried again; null while it is healthy.</summary>
    public DateTimeOffset? RetryAt(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _retryAt is { } retry && retry > now ? retry : null;
        }
    }

    /// <summary>Skips the server for the back-off period (longer for an optional server).</summary>
    public void MarkUnavailable(string error, bool isAuthError)
    {
        lock (_lock)
        {
            _retryAt = _time.GetUtcNow() + _options.BackOffFor(Server);
            _authFailed = isAuthError;
            _lastError = error;
        }

        CloseIdle(all: true);
        _healthChanged();
    }

    public void RecordError(string error)
    {
        lock (_lock)
        {
            _lastError = error;
        }
    }

    /// <summary>
    /// Waits for a free slot, then hands out an idle connection, opens a new one, or (with pipelining, once all
    /// connections are open) shares the least busy one. Every successful call must be followed by exactly one <see cref="Release"/>.
    /// </summary>
    /// <exception cref="NntpException">A new connection could not be opened.</exception>
    public async Task<PooledConnection> AcquireAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _inUse);
        try
        {
            while (true)
            {
                var now = _time.GetUtcNow();
                PooledConnection? chosen = null;
                Task? changed = null;
                var open = false;
                List<NntpConnection> stale = [];
                lock (_lock)
                {
                    while (_idle.TryPop(out var idle))
                    {
                        if (idle.Connection.IsUsable && now - idle.LastUsed < _options.IdleTimeout)
                        {
                            chosen = idle;
                            break;
                        }

                        Interlocked.Decrement(ref _open);
                        stale.Add(idle.Connection);
                    }

                    if (chosen is null)
                    {
                        // One article per request: the slot itself guarantees a free connection.
                        if (_depth == 1 || Open + _opening < _connections)
                        {
                            _opening++;
                            open = true;
                        }
                        else
                        {
                            chosen = _shared.Where(c => c.Users < _depth && !c.Discarded && c.Connection.IsUsable).MinBy(c => c.Users);
                            changed = chosen is null ? _changed.Task : null;
                        }
                    }

                    if (chosen is not null)
                    {
                        chosen.Users++;
                        if (_depth > 1 && chosen.Users == 1)
                        {
                            _shared.Add(chosen);
                        }
                    }
                }

                stale.ForEach(QuitLater);
                if (chosen is not null)
                {
                    return chosen;
                }

                if (open)
                {
                    return await OpenAsync(cancellationToken);
                }

                // All connections are open and full (or broken and about to be given back): wait for a change.
                await changed!.WaitAsync(cancellationToken);
            }
        }
        catch
        {
            Interlocked.Decrement(ref _inUse);
            _slots.Release();
            throw;
        }
    }

    /// <summary>Gives the connection back: kept for reuse when it still works, otherwise closed once no fetch uses it.</summary>
    public void Release(PooledConnection connection, bool reuse)
    {
        var close = false;
        lock (_lock)
        {
            connection.Users--;
            connection.LastUsed = _time.GetUtcNow();
            if (!reuse)
            {
                connection.Discarded = true;
            }

            if (connection.Users <= 0)
            {
                _shared.Remove(connection);
                if (!connection.Discarded && connection.Connection.IsUsable && !_retired)
                {
                    _idle.Push(connection);
                }
                else
                {
                    Interlocked.Decrement(ref _open);
                    close = true;
                }
            }
        }

        if (close)
        {
            QuitLater(connection.Connection);
        }

        Interlocked.Decrement(ref _inUse);
        _slots.Release();
        Signal();
    }

    /// <summary>Closes idle connections older than the idle timeout (or all of them).</summary>
    public void CloseIdle(bool all = false)
    {
        var now = _time.GetUtcNow();
        List<NntpConnection> close = [];
        lock (_lock)
        {
            if (_idle.Count == 0)
            {
                return;
            }

            var entries = _idle.ToArray(); // most recently used first
            _idle.Clear();
            for (var i = entries.Length - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (!all && entry.Connection.IsUsable && now - entry.LastUsed < _options.IdleTimeout)
                {
                    _idle.Push(entry);
                }
                else
                {
                    Interlocked.Decrement(ref _open);
                    close.Add(entry.Connection);
                }
            }
        }

        close.ForEach(QuitLater);
        if (close.Count > 0)
        {
            Signal();
        }
    }

    /// <summary>The server was removed or its connection settings changed: idle connections close now, busy ones when released.</summary>
    public void Retire()
    {
        lock (_lock)
        {
            _retired = true;
        }

        CloseIdle(all: true);
    }

    private async Task<PooledConnection> OpenAsync(CancellationToken cancellationToken)
    {
        NntpConnection opened;
        try
        {
            opened = await NntpConnection.ConnectAsync(_settings, cancellationToken);
        }
        catch
        {
            lock (_lock)
            {
                _opening--;
            }

            Signal();
            throw;
        }

        Interlocked.Increment(ref _open);
        var pooled = new PooledConnection(opened, _depth) { Users = 1 };
        var recovered = false;
        lock (_lock)
        {
            _opening--;
            if (_depth > 1)
            {
                _shared.Add(pooled);
            }

            // Reachable again after a back-off: the old error no longer applies.
            if (_retryAt is not null)
            {
                _retryAt = null;
                _authFailed = false;
                _lastError = null;
                recovered = true;
            }
        }

        Signal();
        if (recovered)
        {
            _healthChanged();
        }

        return pooled;
    }

    /// <summary>Wakes fetches waiting for a share of a pipelined connection.</summary>
    private void Signal()
    {
        if (_depth > 1)
        {
            Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
    }

    private static void QuitLater(NntpConnection connection) => _ = QuitAndDisposeAsync(connection);

    private static async Task QuitAndDisposeAsync(NntpConnection connection)
    {
        await connection.QuitAsync();
        await connection.DisposeAsync();
    }
}
