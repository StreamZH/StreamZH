// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.SignalR;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Host.Api;
using StreamZH.Library;
using StreamZH.Library.Indexers;
using StreamZH.Library.Search;

namespace StreamZH.Host.Hubs;

/// <summary>Forwards in-process events to the browsers connected to /hub.</summary>
public sealed class HubNotifier(IEventBus events, IHubContext<EventsHub> hub) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Track(events.Subscribe<LibraryChanged>((e, ct) =>
            new ValueTask(hub.Clients.All.SendAsync("libraryChanged", new { mediaType = e.MediaType.ToString(), id = e.Id }, ct))));

        Track(events.Subscribe<JobsChanged>((e, ct) =>
            new ValueTask(hub.Clients.All.SendAsync("jobs", e.Status, ct))));

        Track(events.Subscribe<ReleaseGrabbed>((e, ct) => new ValueTask(hub.Clients.All.SendAsync("releaseGrabbed", e, ct))));
        Track(events.Subscribe<AutomaticSearchFinished>((e, ct) => new ValueTask(hub.Clients.All.SendAsync("searchFinished", e, ct))));
        Track(events.Subscribe<IndexersChanged>((_, ct) => new ValueTask(hub.Clients.All.SendAsync("indexersChanged", ct))));

        // Status window: re-read /api/v1/status/ongoing.
        Track(events.Subscribe<OngoingChanged>((_, ct) => new ValueTask(hub.Clients.All.SendAsync("ongoing", ct))));

        // Downloads: state changes (UI refetches) and throttled progress for live bars.
        Track(events.Subscribe<DownloadChanged>((e, ct) =>
            new ValueTask(hub.Clients.All.SendAsync("downloadChanged", new { downloadId = e.DownloadId }, ct))));
        Track(events.Subscribe<DownloadProgress>((e, ct) => new ValueTask(hub.Clients.All.SendAsync(
            "downloadProgress",
            new
            {
                items = e.Items.Select(i => new
                {
                    id = i.Id,
                    status = DownloadEndpoints.Camel(i.Status.ToString()),
                    progress = i.Progress,
                    speedBps = i.SpeedBps,
                    etaSeconds = i.EtaSeconds is { } eta ? Math.Round(eta) : (double?)null,
                    stepProgress = i.StepProgress,
                    statusMessage = i.StatusMessage,
                }).ToList(),
            },
            ct))));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Start and stop can overlap when the host shuts down while it is still starting: take the subscriptions out
        // under the lock, dispose them outside it.
        IDisposable[] subscriptions;
        lock (_subscriptions)
        {
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }

        return Task.CompletedTask;
    }

    private void Track(IDisposable subscription)
    {
        lock (_subscriptions)
        {
            _subscriptions.Add(subscription);
        }
    }
}