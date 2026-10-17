// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
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
/// <param name="QuotaUsedBytes">Bytes counted against the quota; null when the server has no quota.</param>
/// <param name="QuotaExhausted">The quota is used up: the server is skipped until the counter is reset or the quota raised.</param>
public sealed record UsenetServerStatus(
    int Id,
    string Name,
    int ConnectionsInUse,
    int Connections,
    bool Healthy,
    string? LastError,
    long BytesToday,
    DateTimeOffset? RetryAt = null,
    long? QuotaUsedBytes = null,
    bool QuotaExhausted = false);

/// <param name="SpeedBps">All servers together, averaged over the last few seconds.</param>
public sealed record UsenetPoolStatus(IReadOnlyList<UsenetServerStatus> Servers, long SpeedBps);

/// <summary>
/// <see cref="IArticleSource"/> over the configured servers, each with its own pool of long-lived connections.
/// <list type="bullet">
/// <item>Servers are asked by <see cref="UsenetServer.Priority"/> (then id). 430, a broken yEnc body or an article older than the
/// server's retention moves on to the next server; <see cref="ArticleNotFoundException"/> only when every server was asked
/// (or skipped by retention) and none had it.</item>
/// <item>Transient errors (timeout, dropped connection, busy) are retried up to <see cref="UsenetPoolOptions.Attempts"/> times
/// per server, each on another connection.</item>
/// <item>A refused login, a TLS failure, or a server where not a single connection can be opened is skipped for
/// <see cref="UsenetPoolOptions.FailureBackOff"/> (an optional server for <see cref="UsenetPoolOptions.OptionalFailureBackOff"/>).
/// A server whose quota is used up is skipped, too. When no server could be used at all: <see cref="UsenetServerException"/>,
/// with <see cref="UsenetServerException.IsAuthError"/> when every server refused the login.</item>
/// <item>A required server (<see cref="ServerFailureAction.PauseDownloads"/>) is never skipped: while it cannot be used every
/// fetch fails with <see cref="UsenetServerException"/>, so the download engine pauses instead of marking articles as missing.</item>
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
    private readonly ConcurrentDictionary<int, QuotaCounter> _quota = new();
    private readonly Lock _configLock = new();
    private readonly Lock _quotaSaveLock = new();
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
        _sweeper = time.CreateTimer(_ => Sweep(), null, interval, interval);
    }

    /// <summary>
    /// A server became unusable or usable again, or its quota was used up or reset: what the status window shows changed.
    /// Raised on the fetching thread; handlers must be quick and must not throw.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Stores quota bytes counted since the last call (server id → bytes). Called every few seconds and on dispose;
    /// without it the counters only live in memory.
    /// </summary>
    public Action<IReadOnlyDictionary<int, long>>? SaveQuotaUsage { get; set; }

    /// <summary>Most fetches at once over all enabled servers: connections × articles per request.</summary>
    public int MaxConnections => Volatile.Read(ref _pools).Sum(p => p.Capacity);

    /// <summary>Uses these servers from now on (disabled ones are ignored). Unchanged servers keep their connections and health.</summary>
    public void Configure(IEnumerable<UsenetServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var list = servers.ToList();
        ServerPool[] next;
        lock (_configLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ConfigureQuota(list);
            var current = _pools.ToDictionary(p => p.Server.Id);
            var pools = new List<ServerPool>();
            foreach (var server in list.Where(s => s.Enabled && s.Connections > 0).OrderBy(s => s.Priority).ThenBy(s => s.Id))
            {
                if (current.Remove(server.Id, out var existing) && existing.ConnectionKey == ServerPool.KeyOf(server))
                {
                    existing.Server = server;
                    pools.Add(existing);
                    continue;
                }

                existing?.Retire();
                pools.Add(new ServerPool(server, _options, _time, RaiseStateChanged));
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

        // A required server that cannot be used stops everything, whatever its priority: downloads pause until it works.
        foreach (var pool in pools)
        {
            if (pool.Server.OnFailure == ServerFailureAction.PauseDownloads && Unusable(pool, _time.GetUtcNow()) is { } reason)
            {
                throw Required(pool.Server, reason.Message, reason.IsAuth);
            }
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

            if (Unusable(pool, now) is { } skipped)
            {
                failures.Add((server.Name, skipped.Message, skipped.IsAuth));
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
            else if (server.OnFailure == ServerFailureAction.PauseDownloads)
            {
                // Do not let the next server's "430" mark the article as missing while the required one could not answer.
                throw Required(server, outcome.Error ?? "Failed.", outcome.IsAuthError);
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
                p.BusyConnections,
                server.Connections,
                p.IsAvailable(now),
                p.LastError,
                _bytesToday.TryGetValue(server.Id, out var counter) ? counter.Get(today) : 0,
                p.RetryAt(now),
                QuotaUsed(server.Id),
                QuotaExhausted(server));
        }).ToList();
        return new UsenetPoolStatus(servers, _speed.BytesPerSecond());
    }

    /// <summary>Bytes received from the server since local midnight (also for servers that are disabled by now).</summary>
    public long BytesToday(int serverId) =>
        _bytesToday.TryGetValue(serverId, out var counter) ? counter.Get(DateOnly.FromDateTime(_time.GetLocalNow().DateTime)) : 0;

    /// <summary>Bytes counted against the server's quota (live, ahead of the stored value); null without a quota.</summary>
    public long? QuotaUsed(int serverId) => _quota.TryGetValue(serverId, out var counter) ? counter.Used : null;

    /// <summary>True when the server has a quota and it is used up.</summary>
    public bool QuotaExhausted(UsenetServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.QuotaBytes is { } quota && QuotaUsed(server.Id) is { } used && used >= quota;
    }

    /// <summary>
    /// Sets the quota counter of a server to zero. <paramref name="store"/> (reset the stored counter) runs under the same
    /// lock as <see cref="SaveQuotaUsage"/>, so bytes counted before the reset cannot be stored after it.
    /// </summary>
    public void ResetQuota(int serverId, Action? store = null)
    {
        bool wasExhausted;
        lock (_quotaSaveLock)
        {
            store?.Invoke();
            wasExhausted = Volatile.Read(ref _pools).FirstOrDefault(p => p.Server.Id == serverId)?.Server is { } server && QuotaExhausted(server);

            // No counter: the server has no quota (yet); Configure creates one from the stored value.
            if (_quota.TryGetValue(serverId, out var counter))
            {
                counter.Reset();
            }
        }

        if (wasExhausted)
        {
            RaiseStateChanged();
        }
    }

    /// <summary>Hands the quota bytes counted since the last call to <see cref="SaveQuotaUsage"/>. Runs on a timer; public for tests.</summary>
    public void FlushQuotaUsage()
    {
        if (SaveQuotaUsage is not { } save)
        {
            return;
        }

        lock (_quotaSaveLock)
        {
            var pending = new Dictionary<int, long>();
            foreach (var (id, counter) in _quota)
            {
                if (counter.TakeUnsaved() is > 0 and var bytes)
                {
                    pending[id] = bytes;
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            try
            {
                save(pending);
            }
            catch (Exception ex)
            {
                // Kept in memory; the next flush tries again.
                foreach (var (id, bytes) in pending)
                {
                    if (_quota.TryGetValue(id, out var counter))
                    {
                        counter.Unsave(bytes);
                    }
                }

                _logger.LogWarning("Could not store the Usenet quota counters: {Message}", ex.Message);
            }
        }
    }

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
        FlushQuotaUsage();
        foreach (var pool in pools)
        {
            pool.Retire();
        }
    }

    private void Sweep()
    {
        CloseIdleConnections();
        FlushQuotaUsage();
    }

    /// <summary>Why the server is skipped right now (back-off or quota used up), or null when it can be asked.</summary>
    private (string Message, bool IsAuth)? Unusable(ServerPool pool, DateTimeOffset now)
    {
        var server = pool.Server;
        if (!pool.IsAvailable(now))
        {
            return (pool.LastError ?? "Skipped after errors.", pool.AuthFailed);
        }

        if (QuotaExhausted(server))
        {
            return ($"The quota of {FormatGb(server.QuotaBytes!.Value)} is used up.", false);
        }

        return null;
    }

    private static UsenetServerException Required(UsenetServer server, string reason, bool isAuth)
    {
        var text = reason.TrimEnd();
        var sentence = text.Length > 0 && char.IsPunctuation(text[^1]) ? text : text + ".";
        return new UsenetServerException($"{server.Name} is required and cannot be used: {sentence} Downloads wait until it works again.", isAuth);
    }

    private async Task<FetchOutcome> FetchFromAsync(ServerPool pool, string messageId, CancellationToken cancellationToken)
    {
        string? lastError = null;
        var attempts = _options.Attempts;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            PooledConnection connection;
            try
            {
                connection = await pool.AcquireAsync(cancellationToken);
            }
            catch (NntpException ex)
            {
                // Login, TLS and "access denied" will not get better by retrying; neither will a server where not a single connection opens.
                if (ex.Kind is NntpErrorKind.Auth or NntpErrorKind.Tls or NntpErrorKind.Rejected || pool.Open == 0)
                {
                    MarkUnavailable(pool, ex.Message, ex.Kind == NntpErrorKind.Auth);
                    return FetchOutcome.Failed(ex.Message, ex.Kind == NntpErrorKind.Auth);
                }

                pool.RecordError(ex.Message);
                lastError = ex.Message;
                _logger.LogDebug("Usenet server '{Server}': could not open another connection ({Message})", pool.Server.Name, ex.Message);
                continue;
            }

            var reuse = false;
            var serverId = pool.Server.Id;
            try
            {
                var article = await connection.BodyAsync(messageId, bytes => Count(serverId, bytes), cancellationToken);
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
                MarkUnavailable(pool, ex.Message, isAuthError: true);
                return FetchOutcome.Failed(ex.Message, isAuthError: true);
            }
            catch (NntpException ex)
            {
                pool.RecordError(ex.Message);
                lastError = ex.Message;
                _logger.LogDebug("Article <{MessageId}> from '{Server}' failed (try {Attempt} of {Max}): {Message}", messageId, pool.Server.Name, attempt, attempts, ex.Message);
            }
            finally
            {
                pool.Release(connection, reuse);
            }
        }

        return FetchOutcome.Failed(lastError ?? "Failed.", isAuthError: false);
    }

    private void MarkUnavailable(ServerPool pool, string message, bool isAuthError)
    {
        pool.MarkUnavailable(message, isAuthError);
        var server = pool.Server;
        var minutes = _options.BackOffFor(server).TotalMinutes;
        if (server.OnFailure == ServerFailureAction.IgnoreLonger)
        {
            _logger.LogInformation("Optional Usenet server '{Server}' skipped for {Minutes:0} minutes: {Message}", server.Name, minutes, message);
        }
        else
        {
            _logger.LogWarning("Usenet server '{Server}' skipped for {Minutes:0} minutes: {Message}", server.Name, minutes, message);
        }
    }

    private void Count(int serverId, long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        _speed.Add(bytes);
        _bytesToday.GetOrAdd(serverId, _ => new DayCounter()).Add(DateOnly.FromDateTime(_time.GetLocalNow().DateTime), bytes);
        if (_quota.TryGetValue(serverId, out var quota)
            && Volatile.Read(ref _pools).FirstOrDefault(p => p.Server.Id == serverId)?.Server is { QuotaBytes: { } limit } server
            && quota.Add(bytes, limit))
        {
            _logger.LogWarning("Usenet server '{Server}' used up its quota of {Quota}; it is skipped until the counter is reset", server.Name, FormatGb(limit));
            RaiseStateChanged();
        }
    }

    /// <summary>Counters for servers with a quota: kept when they exist (memory is ahead of the database), dropped without a quota.</summary>
    private void ConfigureQuota(IReadOnlyList<UsenetServer> servers)
    {
        var withQuota = servers.Where(s => s.QuotaBytes is not null).ToDictionary(s => s.Id);
        foreach (var id in _quota.Keys.Where(id => !withQuota.ContainsKey(id)).ToList())
        {
            _quota.TryRemove(id, out _);
        }

        foreach (var server in withQuota.Values)
        {
            _quota.GetOrAdd(server.Id, _ => new QuotaCounter(server.QuotaUsedBytes));
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("A Usenet state handler failed: {Message}", ex.Message);
        }
    }

    private static string FormatGb(long bytes) =>
        $"{(bytes / (double)UsenetServer.BytesPerGb).ToString("0.##", CultureInfo.InvariantCulture)} GB";

    private readonly record struct FetchOutcome(DecodedArticle? Article, bool Missing, string? Error, bool IsAuthError)
    {
        public static FetchOutcome NotFound => new(null, true, null, false);

        public static FetchOutcome Found(DecodedArticle article) => new(article, false, null, false);

        public static FetchOutcome Failed(string error, bool isAuthError) => new(null, false, error, isAuthError);
    }

    /// <summary>Bytes counted against a quota, and how many of them are not stored yet.</summary>
    private sealed class QuotaCounter(long used)
    {
        private readonly Lock _lock = new();
        private long _used = used;
        private long _unsaved;

        public long Used
        {
            get
            {
                lock (_lock)
                {
                    return _used;
                }
            }
        }

        /// <returns>True when this addition used up the quota.</returns>
        public bool Add(long bytes, long limit)
        {
            lock (_lock)
            {
                var before = _used;
                _used += bytes;
                _unsaved += bytes;
                return before < limit && _used >= limit;
            }
        }

        public long TakeUnsaved()
        {
            lock (_lock)
            {
                var bytes = _unsaved;
                _unsaved = 0;
                return bytes;
            }
        }

        public void Unsave(long bytes)
        {
            lock (_lock)
            {
                _unsaved += bytes;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _used = 0;
                _unsaved = 0;
            }
        }
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
