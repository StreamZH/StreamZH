// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Host.Service;

/// <summary>
/// The link between a service manager (the Windows service control manager) and the runs of StreamZH in this process.
/// It lives for the whole process: an in-process restart (Settings → General, see AppLifecycle) starts a new run but
/// keeps the session, so Windows sees one service that keeps running. A stop from the service manager stops the
/// current run (or the next one as soon as it has started) and no further run follows.
/// </summary>
public class ServiceSession
{
    /// <summary>How long a stop from the service manager waits for StreamZH to finish (the host itself allows 30 s).</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(45);

    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _finished = new();
    private IHostApplicationLifetime? _current;
    private bool _stopRequested;

    /// <summary>The session of this process when a service manager started it (Windows service), else null.</summary>
    public static ServiceSession? Connect() => OperatingSystem.IsWindows() ? WindowsServiceSession.StartIfWindowsService() : null;

    /// <summary>True after the service manager asked to stop: Program does not start another run.</summary>
    public bool StopRequested
    {
        get
        {
            lock (_gate)
            {
                return _stopRequested;
            }
        }
    }

    /// <summary>True after <see cref="Finish"/>: the last run is over.</summary>
    public bool Finished => _finished.IsSet;

    /// <summary>A run starts. If a stop was asked for in between, the run stops again as soon as it has started.</summary>
    public void Attach(IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        bool stop;
        lock (_gate)
        {
            _current = lifetime;
            stop = _stopRequested;
        }

        if (stop)
        {
            StopWhenStarted(lifetime);
        }
    }

    /// <summary>A run is stopping (restart or end).</summary>
    public void Detach(IHostApplicationLifetime lifetime)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, lifetime))
            {
                _current = null;
            }
        }
    }

    /// <summary>
    /// The service manager asks to stop (service stop or system shutdown): stops the current run and waits until Program
    /// has finished, at most <paramref name="timeout"/>. False when StreamZH did not finish in time.
    /// </summary>
    public bool RequestStop(TimeSpan timeout)
    {
        IHostApplicationLifetime? current;
        lock (_gate)
        {
            _stopRequested = true;
            current = _current;
        }

        if (current is not null)
        {
            StopWhenStarted(current);
        }

        return _finished.Wait(timeout);
    }

    /// <summary>Program ends: no run follows. Releases a waiting <see cref="RequestStop"/>.</summary>
    public virtual void Finish() => _finished.Set();

    // Stopping a host before it has started makes its StartAsync throw; wait for ApplicationStarted (runs at once when started).
    private static void StopWhenStarted(IHostApplicationLifetime lifetime) =>
        lifetime.ApplicationStarted.Register(static state => ((IHostApplicationLifetime)state!).StopApplication(), lifetime);
}

/// <summary>
/// The host lifetime of one run under a service manager: no Ctrl+C or SIGTERM handling like the console lifetime, and
/// stopping the run (a restart) does not stop the service.
/// </summary>
public sealed class ServiceRunLifetime(ServiceSession session, IHostApplicationLifetime lifetime) : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        session.Attach(lifetime);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        session.Detach(lifetime);
        return Task.CompletedTask;
    }
}

public static class ServiceHostingExtensions
{
    /// <summary>Runs this host under <paramref name="session"/> (the Windows service); nothing changes without one.</summary>
    public static WebApplicationBuilder UseStreamZhService(this WebApplicationBuilder builder, ServiceSession? session)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (session is not null)
        {
            builder.Services.AddSingleton(session);
            builder.Services.AddSingleton<IHostLifetime>(sp => new ServiceRunLifetime(session, sp.GetRequiredService<IHostApplicationLifetime>()));
        }

        return builder;
    }
}
