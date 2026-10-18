// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace StreamZH.Host.Service;

/// <summary>
/// StreamZH as a Windows service (installed with tools\Install-Service.ps1). Connects to the service control manager
/// once per process, before the first run, on a background thread. Unlike Microsoft's WindowsServiceLifetime (one host,
/// one service run) the connection outlives the host, so the in-process restart keeps the service running.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceSession : ServiceSession
{
    public const string ServiceName = "StreamZH";

    /// <summary>The event log source; Install-Service.ps1 registers it, LocalSystem can also create it.</summary>
    public const string EventSource = "StreamZH";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly ControlHandler _handler;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dispatcherEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WindowsServiceSession() => _handler = new ControlHandler(this);

    /// <summary>
    /// When this process was started by the service control manager: connects to it and returns the session; else null
    /// (console, Docker, systemd). Startup errors from here on go to the Windows event log as well.
    /// </summary>
    public static ServiceSession? StartIfWindowsService()
    {
        if (!RunningAs.IsWindowsService)
        {
            return null;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteEventLog($"StreamZH stopped because of an error. Windows restarts the service (recovery settings of the service).{Environment.NewLine}{Environment.NewLine}{e.ExceptionObject}");

        var session = new WindowsServiceSession();
        var dispatcher = new Thread(session.Dispatch) { IsBackground = true, Name = "Service control" };
        dispatcher.Start();

        // ServiceBase.Run blocks until the service stops; OnStart says Windows has accepted the start.
        var first = Task.WaitAny([session._started.Task, session._dispatcherEnded.Task], StartTimeout);
        if (first != 0)
        {
            throw new InvalidOperationException("The Windows service control manager did not start the service.", session._dispatcherEnded.Task.Exception);
        }

        return session;
    }

    /// <summary>The last run is over. A stop from inside StreamZH (Shutdown) also stops the service, as "stopped", not "failed".</summary>
    public override void Finish()
    {
        base.Finish();
        if (!StopRequested)
        {
            _handler.Stop();
        }

        _dispatcherEnded.Task.Wait(StopTimeout);
    }

    /// <summary>Writes an error to the Application event log; never throws (this runs while StreamZH is failing).</summary>
    public static void WriteEventLog(string message)
    {
        try
        {
            const int MaxLength = 31_000;
            EventLog.WriteEntry(EventSource, message.Length > MaxLength ? message[..MaxLength] : message, EventLogEntryType.Error, 1000);
        }
        catch (Exception)
        {
            // No event log (source missing and no right to create it): the log file in the data folder still has it.
        }
    }

    private void Dispatch()
    {
        try
        {
            ServiceBase.Run(_handler);
            _started.TrySetException(new InvalidOperationException("The service stopped before it started."));
            _dispatcherEnded.TrySetResult();
        }
        catch (Exception e)
        {
            _started.TrySetException(e);
            _dispatcherEnded.TrySetException(e);
        }
    }

    private sealed class ControlHandler : ServiceBase
    {
        private readonly WindowsServiceSession _session;

        public ControlHandler(WindowsServiceSession session)
        {
            _session = session;
            ServiceName = WindowsServiceSession.ServiceName;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = false;
        }

        protected override void OnStart(string[] args) => _session._started.TrySetResult();

        protected override void OnStop() => StopStreamZh();

        protected override void OnShutdown() => StopStreamZh();

        private void StopStreamZh()
        {
            try
            {
                // Downloads and the database need a moment; tell Windows not to give up after the default wait hint.
                RequestAdditionalTime((int)StopTimeout.TotalMilliseconds);
            }
            catch (InvalidOperationException)
            {
                // Not in a pending state (stop from Finish after the last run): nothing to wait for.
            }

            _session.RequestStop(StopTimeout);
        }
    }
}
