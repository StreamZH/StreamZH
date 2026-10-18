// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Media;

namespace StreamZH.Library.Organize;

/// <summary>
/// The one organize, undo or move run that is queued or running, and the result of the last one: for the progress in
/// the dialog and the status window. Only one run at a time; a second one is refused while the first waits or runs.
/// </summary>
public sealed class LibraryRunTracker(IEventBus events, TimeProvider? timeProvider = null)
{
    /// <summary>Progress updates reach the browsers at most this often (each one makes them re-read the status window).</summary>
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(750);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private LibraryRunProgress? _current;
    private LibraryRunResult? _last;
    private DateTimeOffset _published = DateTimeOffset.MinValue;

    public LibraryRunProgress? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public LibraryRunResult? Last
    {
        get
        {
            lock (_lock)
            {
                return _last;
            }
        }
    }

    /// <summary>Reserves the slot for a run about to be queued; false when another one is queued or running.</summary>
    public bool TryQueue(LibraryRunKind kind, MediaType mediaType, string title, int total)
    {
        lock (_lock)
        {
            if (_current is not null)
            {
                return false;
            }

            _current = new LibraryRunProgress(kind, mediaType, title, 0, total, null, 0, Started: false);
        }

        Publish(force: true);
        return true;
    }

    /// <summary>The queued run gives its slot back (it could not be queued).</summary>
    public void Release()
    {
        lock (_lock)
        {
            _current = null;
        }

        Publish(force: true);
    }

    public void Start(LibraryRunKind kind, MediaType mediaType, string title, int total)
    {
        lock (_lock)
        {
            _current = new LibraryRunProgress(kind, mediaType, title, 0, total, null, 0, Started: true);
        }

        Publish(force: true);
    }

    /// <param name="done">Items finished.</param>
    /// <param name="current">Title of the item being worked on.</param>
    /// <param name="itemFraction">How far the current item is (0..1), e.g. while copying to another drive.</param>
    public void Report(int done, string? current, double itemFraction = 0)
    {
        lock (_lock)
        {
            if (_current is not { } run)
            {
                return;
            }

            var progress = run.Total == 0 ? 1 : Math.Clamp((done + Math.Clamp(itemFraction, 0, 1)) / run.Total, 0, 1);
            _current = run with { Done = done, Current = current, Progress = progress };
        }

        Publish(force: false);
    }

    public void Finish(LibraryRunResult result)
    {
        lock (_lock)
        {
            _current = null;
            _last = result;
        }

        Publish(force: true);
    }

    private void Publish(bool force)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!force && now - _published < PublishInterval)
            {
                return;
            }

            _published = now;
        }

        _ = events.PublishAsync(new OngoingChanged());
    }
}
