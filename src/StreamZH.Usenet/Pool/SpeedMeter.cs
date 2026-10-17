// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Usenet.Pool;

/// <summary>Bytes per second over a sliding window of whole seconds (one bucket per second, fixed memory).</summary>
public sealed class SpeedMeter
{
    private readonly TimeProvider _time;
    private readonly int _seconds;
    private readonly long[] _bytes;
    private readonly long[] _stamps;
    private readonly Lock _lock = new();

    public SpeedMeter(TimeProvider time, TimeSpan window)
    {
        _time = time;
        _seconds = Math.Max(1, (int)Math.Ceiling(window.TotalSeconds));
        _bytes = new long[_seconds + 1];
        _stamps = new long[_seconds + 1];
        Array.Fill(_stamps, long.MinValue);
    }

    public void Add(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        var second = _time.GetUtcNow().ToUnixTimeSeconds();
        var index = (int)(((second % _bytes.Length) + _bytes.Length) % _bytes.Length);
        lock (_lock)
        {
            if (_stamps[index] != second)
            {
                _stamps[index] = second;
                _bytes[index] = 0;
            }

            _bytes[index] += bytes;
        }
    }

    /// <summary>Average over the window: the last complete seconds plus the running one.</summary>
    public long BytesPerSecond()
    {
        var now = _time.GetUtcNow();
        var second = now.ToUnixTimeSeconds();
        long total = 0;
        lock (_lock)
        {
            for (var i = 0; i < _bytes.Length; i++)
            {
                var age = second - _stamps[i];
                if (age >= 0 && age < _seconds)
                {
                    total += _bytes[i];
                }
            }
        }

        var elapsed = (_seconds - 1) + (now.ToUnixTimeMilliseconds() - (second * 1000)) / 1000d;
        return elapsed <= 0 ? 0 : (long)(total / Math.Max(elapsed, 1d));
    }
}
