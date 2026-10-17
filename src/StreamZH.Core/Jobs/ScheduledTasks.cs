// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Jobs;

/// <summary>
/// Work that runs on its own every <see cref="Interval"/> (RSS sync, backups, cleanup …), like Radarr's scheduled tasks.
/// Register with <c>services.AddSingleton&lt;IScheduledTask, MyTask&gt;()</c>; the scheduler runs it through the job queue,
/// so it shows in the status window and never runs twice at the same time.
/// </summary>
public interface IScheduledTask
{
    /// <summary>Stable id, e.g. "rss-sync". Used in the API and to remember the last run.</summary>
    string Key { get; }

    /// <summary>Shown in System → Tasks and in the status window, e.g. "RSS sync".</summary>
    string Name { get; }

    /// <summary>How often it runs; read again before every check, so a changed setting applies at once. Null = off.</summary>
    TimeSpan? Interval { get; }

    /// <summary>Runs in its own DI scope.</summary>
    Task RunAsync(IServiceProvider services, CancellationToken cancellationToken);
}

/// <param name="Interval">Null when the task is switched off.</param>
public sealed record ScheduledTaskState(
    string Key,
    string Name,
    TimeSpan? Interval,
    DateTimeOffset? LastRun,
    DateTimeOffset? NextRun,
    TimeSpan? LastDuration,
    bool? LastSuccess,
    string? LastError,
    bool Running,
    bool Queued);
