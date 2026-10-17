// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Events;

/// <summary>Marker for events published inside the process (job progress, imports, health).</summary>
public interface IAppEvent;

/// <summary>In-process publish/subscribe. Modules never call each other over HTTP.</summary>
public interface IEventBus
{
    ValueTask PublishAsync<TEvent>(TEvent appEvent, CancellationToken cancellationToken = default)
        where TEvent : IAppEvent;

    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : IAppEvent;
}