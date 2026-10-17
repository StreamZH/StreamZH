// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;

namespace StreamZH.Library.Renamer;

/// <summary>The rename (or undo) running right now, for the status window.</summary>
public sealed record RenamerActivity(int RunId, string Title, int Total, int Done, bool Undo);

/// <summary>Progress of the running rename job; tells the status window to refresh (at most every 300 ms).</summary>
public sealed class RenamerProgress(IEventBus events, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(300);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private RenamerActivity? _current;
    private DateTimeOffset _lastPublish;

    public RenamerActivity? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public ValueTask StartAsync(int runId, string title, int total, bool undo)
    {
        lock (_lock)
        {
            _current = new RenamerActivity(runId, title, total, 0, undo);
            _lastPublish = _time.GetUtcNow();
        }

        return events.PublishAsync(new OngoingChanged());
    }

    public ValueTask AdvanceAsync()
    {
        bool publish;
        lock (_lock)
        {
            if (_current is null)
            {
                return ValueTask.CompletedTask;
            }

            _current = _current with { Done = Math.Min(_current.Total, _current.Done + 1) };
            var now = _time.GetUtcNow();
            publish = now - _lastPublish >= PublishInterval;
            if (publish)
            {
                _lastPublish = now;
            }
        }

        return publish ? events.PublishAsync(new OngoingChanged()) : ValueTask.CompletedTask;
    }

    public ValueTask FinishAsync()
    {
        lock (_lock)
        {
            _current = null;
        }

        return events.PublishAsync(new OngoingChanged());
    }
}

/// <summary>Runs left "running" by a stop of StreamZH are marked failed at startup.</summary>
public sealed class RenamerStartup(RenamerRepository repository, TimeProvider? timeProvider = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        repository.FailInterrupted((timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class RenamerServiceCollectionExtensions
{
    /// <summary>Tools → File renamer.</summary>
    public static IServiceCollection AddRenamer(this IServiceCollection services)
    {
        services.AddSingleton<RenamerRepository>();
        services.AddSingleton<RenamerTmdbCache>();
        services.AddSingleton<RenamerProgress>();
        services.AddScoped<RenamerCatalog>();
        services.AddScoped<RenamerService>();
        services.AddHostedService<RenamerStartup>();
        return services;
    }
}
