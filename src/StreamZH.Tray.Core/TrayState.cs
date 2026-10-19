// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Tray;

namespace StreamZH.Tray.Core;

/// <summary>What Windows says about the StreamZH process: the service state, or for a console install whether StreamZH.exe runs.</summary>
public enum ProcessState
{
    Stopped,
    StartPending,
    StopPending,
    Running,
}

public enum TrayStateKind
{
    NotRunning,
    Starting,
    Stopping,
    Running,
    Paused,

    /// <summary>Running, but the status cannot be read (tray.token not readable, or refused).</summary>
    NoAccess,

    /// <summary>The process runs but has not answered for a while.</summary>
    NotResponding,
}

/// <summary>The colour of the dot on the icon.</summary>
public enum StatusDot
{
    Green,
    Orange,
    Red,
}

/// <param name="StartupError">1.0 RC: while not running, why the last start failed (e.g. the port is in use).</param>
public sealed record TrayState(TrayStateKind Kind, TrayStatus? Status = null, TokenProblem TokenProblem = TokenProblem.None, StartupError? StartupError = null)
{
    public bool IsUp => Kind is TrayStateKind.Running or TrayStateKind.Paused or TrayStateKind.NoAccess or TrayStateKind.NotResponding;

    public StatusDot Dot => Kind switch
    {
        TrayStateKind.Running when Status is { Warnings: 0, Errors: 0 } => StatusDot.Green,
        TrayStateKind.Running or TrayStateKind.Paused or TrayStateKind.Starting or TrayStateKind.Stopping or TrayStateKind.NoAccess => StatusDot.Orange,
        _ => StatusDot.Red,
    };
}

/// <summary>
/// Turns what Windows says about the process and the answer of /api/v1/tray/status into one state. A process that runs
/// but does not answer counts as starting for <see cref="StartGrace"/> (the service is "running" for Windows some seconds
/// before StreamZH listens), then as not responding.
/// </summary>
public sealed class TrayStateResolver(TimeProvider time)
{
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(90);

    private DateTimeOffset? _silentSince;

    public TrayState Resolve(ProcessState process, ProbeResult? probe, TokenProblem tokenProblem)
    {
        if (probe is { Outcome: ProbeOutcome.Ok, Status: { } status })
        {
            _silentSince = null;
            return new TrayState(status.Paused ? TrayStateKind.Paused : TrayStateKind.Running, status);
        }

        switch (process)
        {
            case ProcessState.Stopped:
                _silentSince = null;
                return new TrayState(TrayStateKind.NotRunning);
            case ProcessState.StartPending:
                return new TrayState(TrayStateKind.Starting);
            case ProcessState.StopPending:
                return new TrayState(TrayStateKind.Stopping);
        }

        if (probe is { Outcome: ProbeOutcome.Unauthorized or ProbeOutcome.Failed })
        {
            _silentSince = null;
            return new TrayState(TrayStateKind.NoAccess, TokenProblem: tokenProblem);
        }

        var now = time.GetUtcNow();
        _silentSince ??= now;
        return now - _silentSince.Value < StartGrace ? new TrayState(TrayStateKind.Starting) : new TrayState(TrayStateKind.NotResponding);
    }

    /// <summary>After Start or Restart from the menu: the next silence is a start again.</summary>
    public void Restarting() => _silentSince = time.GetUtcNow();
}
