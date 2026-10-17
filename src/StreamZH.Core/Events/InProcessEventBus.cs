// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;

namespace StreamZH.Core.Events;

public sealed class InProcessEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, ImmutableHandlerList> _handlers = new();

    public async ValueTask PublishAsync<TEvent>(TEvent appEvent, CancellationToken cancellationToken = default)
        where TEvent : IAppEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var list))
        {
            return;
        }

        foreach (var handler in list.Items)
        {
            await ((Func<TEvent, CancellationToken, ValueTask>)handler)(appEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : IAppEvent
    {
        _handlers.AddOrUpdate(typeof(TEvent), _ => new([handler]), (_, list) => new([.. list.Items, handler]));
        return new Subscription(() =>
            _handlers.AddOrUpdate(typeof(TEvent), _ => new([]), (_, list) => new([.. list.Items.Where(h => !ReferenceEquals(h, handler))])));
    }

    private sealed record ImmutableHandlerList(Delegate[] Items);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                dispose();
            }
        }
    }
}