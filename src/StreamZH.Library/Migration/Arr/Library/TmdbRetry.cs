// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>
/// The library sections ask TMDB one item at a time; when TMDB still answers "too many requests" (429), the call waits a
/// little longer each time and tries again, so a library of thousands of items gets through without failing items.
/// </summary>
public static class TmdbRetry
{
    /// <summary>Wait before the first retry; doubled each time (tests set it to zero).</summary>
    public static TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

    public const int Attempts = 4;

    public static async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var delay = Delay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (TmdbException ex) when (ex.StatusCode == 429 && attempt < Attempts)
            {
                await Task.Delay(delay, cancellationToken);
                delay += delay;
            }
        }
    }
}
