// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;

namespace StreamZH.Library.Downloads;

/// <summary>Tuning of the download engine. Tests shorten the intervals.</summary>
public sealed class DownloadEngineOptions
{
    /// <summary>Progress events, speed samples, checkpoints and the disk-space check run on this interval.</summary>
    public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Wait after "no server reachable" before the next attempt (doubles up to <see cref="MaxServerRetryDelay"/>).</summary>
    public TimeSpan ServerRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan MaxServerRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Without par2 recovery files, a download fails once more than this share of its bytes is missing.</summary>
    public double MaxMissingRatioWithoutPar2 { get; set; } = 0.10;
}

/// <summary>Free bytes on the volume of a folder; null when unknown. Replaced in tests.</summary>
public interface IFreeSpaceProvider
{
    long? FreeBytes(string folder);
}

public sealed class DriveFreeSpaceProvider : IFreeSpaceProvider
{
    public long? FreeBytes(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return new DriveInfo(folder).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Global speed limit as a token bucket: every article "pays" its size after it arrived; when the bucket is empty the
/// worker waits until enough tokens have accumulated. Burst = half a second of the limit. 0 = unlimited.
/// </summary>
public sealed class SpeedLimiter(Func<long> limitKBps)
{
    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _tokens;
    private double _last;
    private long _lastLimit;

    public async ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_lock)
        {
            var limit = limitKBps();
            var now = _clock.Elapsed.TotalSeconds;
            if (limit <= 0)
            {
                _lastLimit = 0;
                _last = now;
                return;
            }

            var rate = limit * 1024.0;
            var burst = rate / 2;
            if (limit != _lastLimit)
            {
                // New or changed limit: start with a full bucket instead of a debt from the old rate.
                _tokens = burst;
                _lastLimit = limit;
            }
            else
            {
                _tokens = Math.Min(burst, _tokens + ((now - _last) * rate));
            }

            _last = now;
            _tokens -= bytes;
            wait = _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / rate);
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }
    }
}

/// <summary>Bytes per second over the last few samples (one sample per engine tick).</summary>
internal sealed class SpeedMeter
{
    private const int Window = 5;
    private readonly Queue<(double Seconds, long Bytes)> _samples = new();

    public long BytesPerSecond { get; private set; }

    public void Sample(double seconds, long totalBytes)
    {
        _samples.Enqueue((seconds, totalBytes));
        while (_samples.Count > Window + 1)
        {
            _samples.Dequeue();
        }

        var first = _samples.Peek();
        var elapsed = seconds - first.Seconds;
        BytesPerSecond = elapsed <= 0 ? 0 : (long)Math.Max(0, (totalBytes - first.Bytes) / elapsed);
    }

    public void Reset()
    {
        _samples.Clear();
        BytesPerSecond = 0;
    }
}

/// <summary>One bit per segment, stored as a byte array in the database.</summary>
internal static class SegmentBits
{
    public static byte[] Create(int segments) => new byte[(segments + 7) / 8];

    public static bool Get(byte[] bits, int index) => index / 8 < bits.Length && (bits[index / 8] & (1 << (index % 8))) != 0;

    public static void Set(byte[] bits, int index) => bits[index / 8] |= (byte)(1 << (index % 8));

    /// <summary>A stored bitmap with the wrong length (NZB changed?) is treated as empty.</summary>
    public static byte[] Load(byte[]? stored, int segments) =>
        stored is not null && stored.Length == (segments + 7) / 8 ? (byte[])stored.Clone() : Create(segments);
}

/// <summary>A wake-up signal for idle workers: <see cref="Pulse"/> releases everyone waiting.</summary>
internal sealed class AsyncPulse
{
    private TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Pulse() => Interlocked.Exchange(ref _source, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pulse = Volatile.Read(ref _source).Task;
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var finished = await Task.WhenAny(pulse, Task.Delay(timeout, delay.Token));
        await delay.CancelAsync();
        if (finished != pulse)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
