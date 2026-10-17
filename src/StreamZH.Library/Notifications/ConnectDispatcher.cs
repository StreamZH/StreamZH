// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Library.Notifications;

/// <summary>Retry back-off of deliveries: the first try is at once, then one more try after each delay.</summary>
public sealed record ConnectOptions(IReadOnlyList<TimeSpan> RetryDelays)
{
    /// <summary>4 tries within about 6 minutes.</summary>
    public static ConnectOptions Default { get; } = new([TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)]);
}

/// <summary>
/// Sends grabs, imports, upgrades, renames, failed downloads and health changes to the enabled connections whose
/// triggers ask for them. Every delivery runs in the background (the import never waits), one at a time per connection
/// so messages arrive in order, with retries on network and server errors. The last error is stored and shows in the
/// connection list and the status window until a delivery works again.
/// </summary>
public sealed class ConnectDispatcher(
    IEventBus events,
    ConnectionService connections,
    ConnectionRepository repository,
    ConnectEventFactory factory,
    ConnectOptions options,
    TimeProvider time,
    ILogger<ConnectDispatcher> logger) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly CancellationTokenSource _stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_subscriptions)
        {
            _subscriptions.Add(events.Subscribe<ReleaseGrabbed>((e, _) => Queue(() => factory.Grab(e))));
            _subscriptions.Add(events.Subscribe<MediaImported>((e, _) => Queue(() => factory.Imported(e))));
            _subscriptions.Add(events.Subscribe<MediaRenamed>((e, _) => Queue(() => factory.Renamed(e))));
            _subscriptions.Add(events.Subscribe<DownloadFinished>((e, _) =>
                e.Item.Status == DownloadStatus.Failed ? Queue(() => factory.Failed(e.Item)) : ValueTask.CompletedTask));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        await _stopping.CancelAsync();
        try
        {
            await Task.WhenAll(_running.Keys).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    /// <summary>True when an enabled connection reacts to health issues or their end (the watcher only checks then).</summary>
    public bool WantsHealth() =>
        repository.All().Any(c => c.Enable && (c.Triggers.OnHealthIssue || c.Triggers.OnHealthRestored));

    /// <summary>A status-window notice appeared (or disappeared: <paramref name="restored"/>).</summary>
    public void HealthChanged(ConnectHealth health, bool restored)
    {
        ArgumentNullException.ThrowIfNull(health);
        _ = Queue(() => ConnectEventFactory.HealthChanged(health, restored));
    }

    /// <summary>Sends an event built by <paramref name="build"/> (in the background, since it reads the database).</summary>
    public ValueTask Queue(Func<ConnectEvent> build)
    {
        Track(Task.Run(() => Dispatch(build()), _stopping.Token));
        return ValueTask.CompletedTask;
    }

    /// <summary>Waits until every queued delivery is done (tests, shutdown).</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (!_running.IsEmpty)
        {
            await Task.WhenAll(_running.Keys).WaitAsync(cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>Starts one delivery per enabled connection that wants this event.</summary>
    public void Dispatch(ConnectEvent connectEvent)
    {
        ArgumentNullException.ThrowIfNull(connectEvent);
        var trigger = ConnectTriggers.For(connectEvent.Type);
        foreach (var connection in repository.All())
        {
            if (!connection.Enable || trigger is null || !ConnectTriggers.IsOn(connection.Triggers, trigger)
                || ConnectCatalog.Find(connection.Implementation) is not { } type || !ConnectCatalog.Supports(type, connectEvent.Type))
            {
                continue;
            }

            if (connectEvent.Health is { Level: not "error" } && !connection.Triggers.IncludeHealthWarnings)
            {
                continue;
            }

            Track(DeliverAsync(connection, connectEvent));
        }
    }

    private async Task DeliverAsync(ConnectionDefinition connection, ConnectEvent connectEvent)
    {
        if (connections.Sender(connection.Implementation) is not { } sender)
        {
            return;
        }

        var token = _stopping.Token;
        var gate = _gates.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var context = connections.Context(connection);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await sender.SendAsync(context, connectEvent, token);
                    Succeeded(connection);
                    logger.LogDebug("{Connection}: {Subject} sent", connection.Name, connectEvent.Subject);
                    return;
                }
                catch (ConnectException ex) when (ex.Retryable && attempt < options.RetryDelays.Count && !token.IsCancellationRequested)
                {
                    logger.LogDebug("{Connection}: {Error}, retrying in {Delay}", connection.Name, ex.Message, options.RetryDelays[attempt]);
                    await Task.Delay(options.RetryDelays[attempt], time, token);
                }
                catch (ConnectException ex)
                {
                    Failed(connection, connectEvent, ex.Message, attempt + 1);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A bug in a sender must not take the app down; it is logged with the stack trace.
                    logger.LogError(ex, "{Connection}: sending {Subject} failed", connection.Name, connectEvent.Subject);
                    Failed(connection, connectEvent, ConnectHttp.Short(ex.Message), attempt + 1);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            gate.Release();
        }
    }

    private void Succeeded(ConnectionDefinition connection)
    {
        var failing = repository.Find(connection.Id)?.Status.Failing == true;
        repository.SetSuccess(connection.Id, time.GetUtcNow().UtcDateTime);
        if (failing)
        {
            logger.LogInformation("{Connection} works again", connection.Name);
            _ = events.PublishAsync(new OngoingChanged());
        }
    }

    private void Failed(ConnectionDefinition connection, ConnectEvent connectEvent, string error, int tries)
    {
        logger.LogWarning(
            "{Connection}: could not send {Subject} ({Summary}) after {Tries} {TryWord}: {Error}",
            connection.Name,
            connectEvent.Subject,
            connectEvent.Summary,
            tries,
            tries == 1 ? "try" : "tries",
            error);
        if (repository.Find(connection.Id) is null)
        {
            return;
        }

        repository.SetFailure(connection.Id, time.GetUtcNow().UtcDateTime, error);
        _ = events.PublishAsync(new OngoingChanged());
    }

    private void Track(Task task)
    {
        _running.TryAdd(task, 0);
        _ = task.ContinueWith(
            t =>
            {
                _running.TryRemove(t, out _);
                if (t.Exception is { } ex)
                {
                    logger.LogError(ex, "Connection delivery failed");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
