// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Host.General;

/// <summary>
/// Restart and shut down from the web app. A restart stops the host like Ctrl+C and Program.cs runs the whole startup
/// again in the same process (new settings, database reopened, a staged backup restored first), so it works the same
/// in a console, as a service and in Docker without a supervisor. Shut down ends the process.
/// </summary>
public class AppLifecycle(IHostApplicationLifetime lifetime, ILogger<AppLifecycle> logger)
{
    /// <summary>Time for the HTTP answer to reach the browser before the host stops.</summary>
    public static readonly TimeSpan StopDelay = TimeSpan.FromMilliseconds(500);

    private int _stopping;

    public bool RestartRequested { get; private set; }

    public bool Stopping => Volatile.Read(ref _stopping) == 1;

    /// <summary>False when a restart or shutdown is already under way.</summary>
    public virtual bool Restart(string requestedBy)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1)
        {
            return false;
        }

        RestartRequested = true;
        logger.LogWarning("Restart requested by {User}", requestedBy);
        StopSoon();
        return true;
    }

    public virtual bool Shutdown(string requestedBy)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1)
        {
            return false;
        }

        logger.LogWarning("Shutdown requested by {User}", requestedBy);
        StopSoon();
        return true;
    }

    private void StopSoon() => _ = Task.Run(async () =>
    {
        await Task.Delay(StopDelay);
        lifetime.StopApplication();
    });
}
