// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Usenet.Nntp;

namespace StreamZH.Usenet.Pool;

/// <summary>
/// The connections of one server: at most <see cref="UsenetServer.Connections"/> in use at once (a semaphore, so waiting
/// callers are bounded by the callers themselves), opened lazily, reused while fresh, closed when idle or broken.
/// Also keeps the server's health (back-off after a refused login or when it cannot be reached at all).
/// </summary>
internal sealed class ServerPool
{
    private readonly Lock _lock = new();
    private readonly Stack<(NntpConnection Connection, DateTimeOffset LastUsed)> _idle = new();
    private readonly SemaphoreSlim _slots;
    private readonly NntpConnectionSettings _settings;
    private readonly UsenetPoolOptions _options;
    private readonly TimeProvider _time;
    private UsenetServer _server;
    private int _inUse;
    private int _open;
    private bool _retired;
    private DateTimeOffset? _retryAt;
    private bool _authFailed;
    private string? _lastError;

    public ServerPool(UsenetServer server, UsenetPoolOptions options, TimeProvider time)
    {
        _server = server;
        _options = options;
        _time = time;
        _settings = options.SettingsFor(server);
        _slots = new SemaphoreSlim(server.Connections, server.Connections);
        ConnectionKey = KeyOf(server);
    }

    /// <summary>Name, priority and retention may change without new connections; see <see cref="ConnectionKey"/>.</summary>
    public UsenetServer Server
    {
        get => Volatile.Read(ref _server);
        set => Volatile.Write(ref _server, value);
    }

    /// <summary>Everything that needs new connections when it changes.</summary>
    public string ConnectionKey { get; }

    public int InUse => Volatile.Read(ref _inUse);

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

    public static string KeyOf(UsenetServer server) =>
        string.Join('\n', server.Host, server.Port, server.UseSsl, server.Username, server.Password, server.Connections);

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

    /// <summary>Skips the server for the back-off period.</summary>
    public void MarkUnavailable(string error, bool isAuthError)
    {
        lock (_lock)
        {
            _retryAt = _time.GetUtcNow() + _options.FailureBackOff;
            _authFailed = isAuthError;
            _lastError = error;
        }

        CloseIdle(all: true);
    }

    public void RecordError(string error)
    {
        lock (_lock)
        {
            _lastError = error;
        }
    }

    /// <summary>
    /// Waits for a free slot, then hands out an idle connection or opens a new one.
    /// Every successful call must be followed by exactly one <see cref="Release"/>.
    /// </summary>
    /// <exception cref="NntpException">A new connection could not be opened.</exception>
    public async Task<NntpConnection> AcquireAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _inUse);
        try
        {
            var now = _time.GetUtcNow();
            while (true)
            {
                NntpConnection? connection;
                bool fresh;
                lock (_lock)
                {
                    if (!_idle.TryPop(out var idle))
                    {
                        break;
                    }

                    connection = idle.Connection;
                    fresh = connection.IsUsable && now - idle.LastUsed < _options.IdleTimeout;
                }

                if (fresh)
                {
                    return connection;
                }

                Close(connection);
            }

            var opened = await NntpConnection.ConnectAsync(_settings, cancellationToken);
            Interlocked.Increment(ref _open);
            lock (_lock)
            {
                // Reachable again after a back-off: the old error no longer applies.
                if (_retryAt is not null)
                {
                    _retryAt = null;
                    _authFailed = false;
                    _lastError = null;
                }
            }

            return opened;
        }
        catch
        {
            Interlocked.Decrement(ref _inUse);
            _slots.Release();
            throw;
        }
    }

    /// <summary>Gives the connection back: kept for reuse when it still works, otherwise closed.</summary>
    public void Release(NntpConnection connection, bool reuse)
    {
        var keep = reuse && connection.IsUsable;
        if (keep)
        {
            lock (_lock)
            {
                if (_retired)
                {
                    keep = false;
                }
                else
                {
                    _idle.Push((connection, _time.GetUtcNow()));
                }
            }
        }

        if (!keep)
        {
            Close(connection);
        }

        Interlocked.Decrement(ref _inUse);
        _slots.Release();
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
                    close.Add(entry.Connection);
                }
            }
        }

        close.ForEach(Close);
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

    private void Close(NntpConnection connection)
    {
        Interlocked.Decrement(ref _open);
        _ = QuitAndDisposeAsync(connection);
    }

    private static async Task QuitAndDisposeAsync(NntpConnection connection)
    {
        await connection.QuitAsync();
        await connection.DisposeAsync();
    }
}
