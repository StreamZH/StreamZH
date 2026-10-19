// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Tray;

/// <summary>
/// What StreamZH and the Windows tray icon (StreamZH.Tray.exe) agree on. StreamZH writes two files into the data folder
/// when it starts: <see cref="TokenFile"/> (a random token, readable by administrators, SYSTEM and the signed-in users
/// only) and <see cref="EndpointFile"/> (where it listens). The tray sends the token in <see cref="Header"/>; StreamZH
/// accepts it only from this computer and only for the endpoints under <see cref="ApiPath"/>.
/// </summary>
public static class TrayContract
{
    public const string TokenFile = "tray.token";
    public const string EndpointFile = "tray.json";
    public const string Header = "X-StreamZH-Tray";
    public const string ApiPath = "/api/v1/tray";
}

/// <summary>Contents of tray.json.</summary>
/// <param name="Url">Base address on this computer including the URL base, without a trailing slash (http://127.0.0.1:9797/streamzh).</param>
/// <param name="DashboardUrl">What "Open dashboard" opens (http://localhost:9797/streamzh/).</param>
/// <param name="RunningAs">"windowsService", "console", "systemd" or "docker".</param>
/// <param name="ProcessId">The StreamZH process, to tell "running but not answering" from "not running".</param>
public sealed record TrayEndpoint(string Url, string DashboardUrl, string Version, string RunningAs, int ProcessId, string LogDirectory, DateTimeOffset Written);

/// <summary>GET /api/v1/tray/status: one cheap summary for the tray icon.</summary>
/// <param name="Run">Changes with every start of StreamZH; finished downloads are numbered per run.</param>
/// <param name="Downloads">Items in the download queue (downloading, queued, paused, post-processing).</param>
/// <param name="Progress">Downloaded bytes of all of them over their total size, 0..1.</param>
/// <param name="EtaSeconds">Time left for the queue at the current speed; null when nothing downloads.</param>
/// <param name="Finished">Downloads finished since <c>after</c> (oldest first, at most 20).</param>
/// <param name="LastFinished">Number of the last finished download in this run (0 = none yet).</param>
/// <param name="SetupCode">0.12: the one-time setup code while no owner account exists ("Show setup code"); else null.</param>
public sealed record TrayStatus(
    string Version,
    string RunningAs,
    string InstanceName,
    string Run,
    bool Paused,
    string? PausedReason,
    int Downloads,
    long TotalBytes,
    long DownloadedBytes,
    double Progress,
    long SpeedBps,
    double? EtaSeconds,
    int Warnings,
    int Errors,
    IReadOnlyList<TrayFinishedDownload> Finished,
    long LastFinished,
    string? SetupCode = null);

/// <summary>A download that completed or failed.</summary>
public sealed record TrayFinishedDownload(long Number, int Id, string Title, bool Failed, string? Error, DateTimeOffset At);
