// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.SignalR;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Library;
using StreamZH.Library.Search;

namespace StreamZH.Host.Hubs;

/// <summary>Forwards in-process events to the browsers connected to /hub.</summary>
public sealed class HubNotifier(IEventBus events, IHubContext<EventsHub> hub) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(events.Subscribe<LibraryChanged>((e, ct) =>
            new ValueTask(hub.Clients.All.SendAsync("libraryChanged", new { mediaType = e.MediaType.ToString(), id = e.Id }, ct))));

        _subscriptions.Add(events.Subscribe<JobsChanged>((e, ct) =>
            new ValueTask(hub.Clients.All.SendAsync("jobs", e.Status, ct))));

        _subscriptions.Add(events.Subscribe<ReleaseGrabbed>((e, ct) => new ValueTask(hub.Clients.All.SendAsync("releaseGrabbed", e, ct))));
        _subscriptions.Add(events.Subscribe<AutomaticSearchFinished>((e, ct) => new ValueTask(hub.Clients.All.SendAsync("searchFinished", e, ct))));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscriptions.ForEach(s => s.Dispose());
        return Task.CompletedTask;
    }
}