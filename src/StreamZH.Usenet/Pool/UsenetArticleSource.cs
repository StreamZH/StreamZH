// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Usenet.Nntp;
using StreamZH.Usenet.Yenc;

namespace StreamZH.Usenet.Pool;

/// <summary>Live state of one enabled server for the status API.</summary>
/// <param name="ConnectionsInUse">Connections fetching (or opening) right now.</param>
/// <param name="Healthy">False while the server is skipped after a refused login or when it could not be reached.</param>
/// <param name="LastError">The most recent connection problem; cleared when the server works again after a back-off.</param>
/// <param name="BytesToday">Bytes received from this server since local midnight.</param>
/// <param name="RetryAt">While unhealthy: when the server is tried again.</param>
public sealed record UsenetServerStatus(
    int Id,
    string Name,
    int ConnectionsInUse,
    int Connections,
    bool Healthy,
    string? LastError,
    long BytesToday,
    DateTimeOffset? RetryAt = null);

/// <param name="SpeedBps">All servers together, averaged over the last few seconds.</param>
public sealed record UsenetPoolStatus(IReadOnlyList<UsenetServerStatus> Servers, long SpeedBps);

/// <summary>
/// <see cref="IArticleSource"/> over the configured servers, each with its own pool of long-lived connections.
/// <list type="bullet">
/// <item>Servers are asked by <see cref="UsenetServer.Priority"/> (then id). 430, a broken yEnc body or an article older than the
/// server's retention moves on to the next server; <see cref="ArticleNotFoundException"/> only when every server was asked
/// (or skipped by retention) and none had it.</item>
/// <item>Transient errors (timeout, dropped connection, busy) are retried up to <see cref="UsenetPoolOptions.MaxAttempts"/> times
/// per server, each on another connection.</item>
/// <item>A refused login, a TLS failure, or a server where not a single connection can be opened is skipped for
/// <see cref="UsenetPoolOptions.FailureBackOff"/>. When no server could be used at all: <see cref="UsenetServerException"/>,
/// with <see cref="UsenetServerException.IsAuthError"/> when every server refused the login.</item>
/// </list>
/// Thread-safe. <see cref="Configure"/> swaps the server list at any time; fetches already running finish on the old pools.
/// </summary>
public sealed class UsenetArticleSource : IArticleSource, IAsyncDisposable
{
    private readonly TimeProvider _time;
    private readonly ILogger<UsenetArticleSource> _logger;
    private readonly UsenetPoolOptions _options;
    private readonly SpeedMeter _speed;
    private readonly ConcurrentDictionary<int, DayCounter> _bytesToday = new();
    private readonly Lock _configLock = new();
    private readonly ITimer _sweeper;
    private ServerPool[] _pools = [];
    private bool _disposed;

    public UsenetArticleSource(TimeProvider time, ILogger<UsenetArticleSource> logger, UsenetPoolOptions? options = null)
    {
        _time = time;
        _logger = logger;
        _options = options ?? new UsenetPoolOptions();
        _speed = new SpeedMeter(time, _options.SpeedWindow);
        var interval = TimeSpan.FromTicks(Math.Clamp(_options.IdleTimeout.Ticks / 4, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromSeconds(15).Ticks));
        _sweeper = time.CreateTimer(_ => CloseIdleConnections(), null, interval, interval);
    }

    public int MaxConnections => Volatile.Read(ref _pools).Sum(p => p.Server.Connections);

    /// <summary>Uses these servers from now on (disabled ones are ignored). Unchanged servers keep their connections and health.</summary>
    public void Configure(IEnumerable<UsenetServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ServerPool[] next;
        lock (_configLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _pools.ToDictionary(p => p.Server.Id);
            var pools = new List<ServerPool>();
            foreach (var server in servers.Where(s => s.Enabled && s.Connections > 0).OrderBy(s => s.Priority).ThenBy(s => s.Id))
            {
                if (current.Remove(server.Id, out var existing) && existing.ConnectionKey == ServerPool.KeyOf(server))
                {
                    existing.Server = server;
                    pools.Add(existing);
                    continue;
                }

                existing?.Retire();
                pools.Add(new ServerPool(server, _options, _time));
            }

            foreach (var removed in current.Values)
            {
                removed.Retire();
            }

            next = [.. pools];
            Volatile.Write(ref _pools, next);
        }

        _logger.LogInformation("Usenet: {Servers} server(s) enabled, {Connections} connections", next.Length, next.Sum(p => p.Server.Connections));
    }

    public async Task<DecodedArticle> FetchAsync(string messageId, IReadOnlyList<string> groups, DateTimeOffset posted, CancellationToken cancellationToken)
    {
        var id = NntpConnection.NormalizeMessageId(messageId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pools = Volatile.Read(ref _pools);
        if (pools.Length == 0)
        {
            throw new UsenetServerException("No Usenet server is enabled. Add one under Settings → Usenet servers.");
        }

        var askedAndMissing = false;
        var failures = new List<(string Server, string Message, bool IsAuth)>();
        foreach (var pool in pools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var server = pool.Server;
            var now = _time.GetUtcNow();
            if (server.RetentionDays > 0 && posted > DateTimeOffset.MinValue && posted < now.AddDays(-server.RetentionDays))
            {
                askedAndMissing = true; // older than this server keeps articles
                continue;
            }

            if (!pool.IsAvailable(now))
            {
                failures.Add((server.Name, pool.LastError ?? "Skipped after errors.", pool.AuthFailed));
                continue;
            }

            var outcome = await FetchFromAsync(pool, id, cancellationToken);
            if (outcome.Article is { } article)
            {
                return article;
            }

            if (outcome.Missing)
            {
                askedAndMissing = true;
            }
            else
            {
                failures.Add((server.Name, outcome.Error ?? "Failed.", outcome.IsAuthError));
            }
        }

        if (askedAndMissing)
        {
            throw new ArticleNotFoundException(id);
        }

        var message = failures.Count == 1
            ? failures[0].Message
            : string.Join(" ", failures.Select(f => $"{f.Server}: {f.Message}"));
        throw new UsenetServerException(message, isAuthError: failures.Count > 0 && failures.All(f => f.IsAuth));
    }

    /// <summary>Connections in use, health and bytes of every enabled server (by priority), plus the total speed.</summary>
    public UsenetPoolStatus Status()
    {
        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var servers = Volatile.Read(ref _pools).Select(p =>
        {
            var server = p.Server;
            return new UsenetServerStatus(
                server.Id,
                server.Name,
                p.InUse,
                server.Connections,
                p.IsAvailable(now),
                p.LastError,
                _bytesToday.TryGetValue(server.Id, out var counter) ? counter.Get(today) : 0,
                p.RetryAt(now));
        }).ToList();
        return new UsenetPoolStatus(servers, _speed.BytesPerSecond());
    }

    /// <summary>Bytes received from the server since local midnight (also for servers that are disabled by now).</summary>
    public long BytesToday(int serverId) =>
        _bytesToday.TryGetValue(serverId, out var counter) ? counter.Get(DateOnly.FromDateTime(_time.GetLocalNow().DateTime)) : 0;

    /// <summary>Closes connections idle for longer than the idle timeout. Runs on a timer; public for tests.</summary>
    public void CloseIdleConnections()
    {
        foreach (var pool in Volatile.Read(ref _pools))
        {
            pool.CloseIdle();
        }
    }

    public async ValueTask DisposeAsync()
    {
        ServerPool[] pools;
        lock (_configLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pools = _pools;
            _pools = [];
        }

        await _sweeper.DisposeAsync();
        foreach (var pool in pools)
        {
            pool.Retire();
        }
    }

    private async Task<FetchOutcome> FetchFromAsync(ServerPool pool, string messageId, CancellationToken cancellationToken)
    {
        string? lastError = null;
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            NntpConnection connection;
            try
            {
                connection = await pool.AcquireAsync(cancellationToken);
            }
            catch (NntpException ex)
            {
                // Login, TLS and "access denied" will not get better by retrying; neither will a server where not a single connection opens.
                if (ex.Kind is NntpErrorKind.Auth or NntpErrorKind.Tls or NntpErrorKind.Rejected || pool.Open == 0)
                {
                    pool.MarkUnavailable(ex.Message, ex.Kind == NntpErrorKind.Auth);
                    _logger.LogWarning("Usenet server '{Server}' skipped for {Minutes:0} minutes: {Message}", pool.Server.Name, _options.FailureBackOff.TotalMinutes, ex.Message);
                    return FetchOutcome.Failed(ex.Message, ex.Kind == NntpErrorKind.Auth);
                }

                pool.RecordError(ex.Message);
                lastError = ex.Message;
                _logger.LogDebug("Usenet server '{Server}': could not open another connection ({Message})", pool.Server.Name, ex.Message);
                continue;
            }

            var reuse = false;
            var before = connection.BytesReceived;
            try
            {
                var article = await connection.BodyAsync(messageId, cancellationToken);
                reuse = true;
                return FetchOutcome.Found(article);
            }
            catch (NntpArticleNotFoundException)
            {
                reuse = true;
                return FetchOutcome.NotFound;
            }
            catch (YencException ex)
            {
                // The whole body was read, so the connection is fine; this server's copy is broken.
                reuse = true;
                _logger.LogDebug("Article <{MessageId}> on '{Server}' is not valid yEnc: {Message}", messageId, pool.Server.Name, ex.Message);
                return FetchOutcome.NotFound;
            }
            catch (NntpException ex) when (ex.Kind == NntpErrorKind.Auth)
            {
                pool.MarkUnavailable(ex.Message, isAuthError: true);
                _logger.LogWarning("Usenet server '{Server}' skipped for {Minutes:0} minutes: {Message}", pool.Server.Name, _options.FailureBackOff.TotalMinutes, ex.Message);
                return FetchOutcome.Failed(ex.Message, isAuthError: true);
            }
            catch (NntpException ex)
            {
                pool.RecordError(ex.Message);
                lastError = ex.Message;
                _logger.LogDebug("Article <{MessageId}> from '{Server}' failed (try {Attempt} of {Max}): {Message}", messageId, pool.Server.Name, attempt, _options.MaxAttempts, ex.Message);
            }
            finally
            {
                Count(pool.Server.Id, connection.BytesReceived - before);
                pool.Release(connection, reuse);
            }
        }

        return FetchOutcome.Failed(lastError ?? "Failed.", isAuthError: false);
    }

    private void Count(int serverId, long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        _speed.Add(bytes);
        _bytesToday.GetOrAdd(serverId, _ => new DayCounter()).Add(DateOnly.FromDateTime(_time.GetLocalNow().DateTime), bytes);
    }

    private readonly record struct FetchOutcome(DecodedArticle? Article, bool Missing, string? Error, bool IsAuthError)
    {
        public static FetchOutcome NotFound => new(null, true, null, false);

        public static FetchOutcome Found(DecodedArticle article) => new(article, false, null, false);

        public static FetchOutcome Failed(string error, bool isAuthError) => new(null, false, error, isAuthError);
    }

    /// <summary>Bytes of the current local day; starts from zero on a new day.</summary>
    private sealed class DayCounter
    {
        private readonly Lock _lock = new();
        private DateOnly _day;
        private long _bytes;

        public void Add(DateOnly day, long bytes)
        {
            lock (_lock)
            {
                if (_day != day)
                {
                    _day = day;
                    _bytes = 0;
                }

                _bytes += bytes;
            }
        }

        public long Get(DateOnly day)
        {
            lock (_lock)
            {
                return _day == day ? _bytes : 0;
            }
        }
    }
}
