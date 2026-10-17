// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using StreamZH.Core.Jobs;
using StreamZH.Data;

namespace StreamZH.Host.Jobs;

/// <summary>
/// Runs the registered <see cref="IScheduledTask"/>s when they are due (checked every 15 s) by queuing them as jobs.
/// The last run of each task is kept in the config table, so a restart does not run everything at once.
/// </summary>
public sealed class ScheduledTaskRunner(
    IEnumerable<IScheduledTask> tasks,
    JobQueue queue,
    ConfigRepository config,
    ILogger<ScheduledTaskRunner> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IReadOnlyList<IScheduledTask> _tasks = [.. tasks];
    private readonly ConcurrentDictionary<string, RunInfo> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);

    private sealed record RunInfo(DateTimeOffset Started, TimeSpan? Duration, bool? Success, string? Error);

    public IReadOnlyList<ScheduledTaskState> States()
    {
        return [.. _tasks.Select(task =>
        {
            var interval = SafeInterval(task);
            var last = LastRun(task.Key);
            DateTimeOffset? next = interval is { } i ? (last ?? _time.GetUtcNow()) + i : null;
            _runs.TryGetValue(task.Key, out var info);
            return new ScheduledTaskState(
                task.Key, task.Name, interval, last, next, info?.Duration, info?.Success, info?.Error,
                _running.ContainsKey(task.Key), _queued.ContainsKey(task.Key));
        }).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Queues a task now (System → Tasks "Run now"). False when the key is unknown or it is already waiting.</summary>
    public bool RunNow(string key)
    {
        var task = _tasks.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        return task is not null && Queue(task);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First runs only after startup has settled.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _time, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(CheckInterval, _time);
        do
        {
            CheckDue();
        }
        while (await NextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Queues every task whose interval has passed since its last run (never-run tasks count from now).</summary>
    public void CheckDue()
    {
        var now = _time.GetUtcNow();
        foreach (var task in _tasks)
        {
            if (SafeInterval(task) is not { } interval)
            {
                continue;
            }

            var last = LastRun(task.Key);
            if (last is null)
            {
                // Never run: start counting now, so a new install does not run every task at its first start.
                SaveLastRun(task.Key, now);
                continue;
            }

            if (now - last.Value >= interval)
            {
                Queue(task);
            }
        }
    }

    private bool Queue(IScheduledTask task)
    {
        if (!_queued.TryAdd(task.Key, 0))
        {
            return false;
        }

        var queued = queue.Enqueue(new Job($"task:{task.Key}", task.Name, async (services, ct) =>
        {
            _queued.TryRemove(task.Key, out _);
            _running[task.Key] = 0;
            var started = _time.GetUtcNow();
            SaveLastRun(task.Key, started);
            try
            {
                await task.RunAsync(services, ct);
                _runs[task.Key] = new RunInfo(started, _time.GetUtcNow() - started, true, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _runs[task.Key] = new RunInfo(started, _time.GetUtcNow() - started, false, ex.Message);
                logger.LogWarning(ex, "Scheduled task '{Task}' failed: {Message}", task.Name, ex.Message);
            }
            finally
            {
                _running.TryRemove(task.Key, out _);
            }
        }));

        if (!queued)
        {
            _queued.TryRemove(task.Key, out _);
        }

        return queued;
    }

    private TimeSpan? SafeInterval(IScheduledTask task)
    {
        try
        {
            return task.Interval is { } i && i > TimeSpan.Zero ? i : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Interval of scheduled task '{Task}' could not be read", task.Name);
            return null;
        }
    }

    private static string LastRunKey(string key) => $"Task.{key}.LastRun";

    private DateTimeOffset? LastRun(string key) =>
        DateTimeOffset.TryParse(config.Get(LastRunKey(key)), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;

    private void SaveLastRun(string key, DateTimeOffset value) => config.Set(LastRunKey(key), value.ToString("O", CultureInfo.InvariantCulture));
}
