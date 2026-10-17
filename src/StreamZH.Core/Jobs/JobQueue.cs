// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Threading.Channels;
using StreamZH.Core.Events;

namespace StreamZH.Core.Jobs;

/// <summary>A unit of background work, e.g. "Refresh series 12". Same key queued twice runs once.</summary>
public sealed record Job(string Key, string Name, Func<IServiceProvider, CancellationToken, Task> Run);

public sealed record JobStatus(int Queued, string? Running, int CompletedSinceStart, int FailedSinceStart);

public sealed record JobsChanged(JobStatus Status) : IAppEvent;

/// <summary>Simple in-process queue, processed one job at a time by the host's job runner.</summary>
public sealed class JobQueue(IEventBus events)
{
    private readonly Channel<Job> _channel = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private string? _running;
    private int _completed;
    private int _failed;

    public JobStatus Status => new(_pending.Count, _running, _completed, _failed);

    public ChannelReader<Job> Reader => _channel.Reader;

    /// <summary>Queues the job unless one with the same key is already waiting. Returns false if skipped.</summary>
    public bool Enqueue(Job job)
    {
        if (!_pending.TryAdd(job.Key, 0))
        {
            return false;
        }

        _channel.Writer.TryWrite(job);
        _ = events.PublishAsync(new JobsChanged(Status));
        return true;
    }

    public async ValueTask StartedAsync(Job job)
    {
        _pending.TryRemove(job.Key, out _);
        _running = job.Name;
        await events.PublishAsync(new JobsChanged(Status));
    }

    public async ValueTask FinishedAsync(bool success)
    {
        _running = null;
        if (success)
        {
            Interlocked.Increment(ref _completed);
        }
        else
        {
            Interlocked.Increment(ref _failed);
        }

        await events.PublishAsync(new JobsChanged(Status));
    }
}