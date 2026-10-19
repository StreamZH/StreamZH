// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Torrent;

namespace StreamZH.Host.Status;

/// <summary>
/// Status-window notices of the built-in torrent engine (0.11), only while a "StreamZH" download client is enabled:
/// the engine could not start, the listen port is taken by another program, the disk is full, torrents stopped with an
/// error, and trackers that do not answer (summarised, host names only: announce URLs carry passkeys). Each disappears
/// once the cause is gone.
/// </summary>
public static class TorrentEngineNotices
{
    public const string StartKey = "torrent-engine-start";
    public const string PortKey = "torrent-engine-port";
    public const string DiskFullKey = "torrent-engine-disk-full";
    public const string ErrorsKey = "torrent-engine-errors";
    public const string TrackersKey = "torrent-engine-trackers";

    private const string Link = "/settings/downloadclients";
    private const string ActivityLink = "/activity";

    public static void Add(List<OngoingNotice> notices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<TorrentEngine>() is not { } engine)
        {
            return;
        }

        Add(notices, engine.Status());
    }

    public static void Add(List<OngoingNotice> notices, TorrentEngineStatus status)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(status);
        if (!status.Wanted && !status.Running)
        {
            return;
        }

        if (status.StartError is { Length: > 0 } startError)
        {
            notices.Add(new OngoingNotice(StartKey, NoticeLevels.Error, "Torrent engine could not start", startError, Link, false));
        }

        if (status.PortState == "notFree")
        {
            notices.Add(new OngoingNotice(
                PortKey,
                NoticeLevels.Error,
                $"Torrent port {status.ListenPort.ToString(CultureInfo.InvariantCulture)} is used by another program",
                "Other peers cannot connect to StreamZH. Choose another port in the torrent engine settings.",
                Link,
                false));
        }

        var diskFull = status.Problems.Where(p => p.Kind == "diskFull").ToList();
        if (diskFull.Count > 0)
        {
            notices.Add(new OngoingNotice(
                DiskFullKey,
                NoticeLevels.Error,
                "Torrent downloads stopped: disk full",
                $"{Count(diskFull.Count)} could not write. Free up space, then resume them in Activity.",
                ActivityLink,
                false));
        }

        var errors = status.Problems.Where(p => p.Kind == "error").ToList();
        if (errors.Count > 0)
        {
            notices.Add(new OngoingNotice(
                ErrorsKey,
                NoticeLevels.Warn,
                errors.Count == 1 ? "A torrent stopped with an error" : $"{Count(errors.Count)} stopped with an error",
                errors.Count == 1 ? $"{errors[0].Name}: {errors[0].Message}" : $"{errors[0].Name}: {errors[0].Message} · and {Count(errors.Count - 1)} more",
                ActivityLink,
                false));
        }

        var trackers = status.Problems.Where(p => p.Kind == "tracker").ToList();
        if (trackers.Count > 0)
        {
            var hosts = trackers.Select(p => p.Message).Distinct(StringComparer.OrdinalIgnoreCase).Take(3);
            notices.Add(new OngoingNotice(
                TrackersKey,
                NoticeLevels.Warn,
                trackers.Count == 1 ? "A tracker does not answer" : $"Trackers do not answer for {Count(trackers.Count)}",
                string.Join(" · ", hosts),
                ActivityLink,
                true));
        }
    }

    private static string Count(int torrents) =>
        torrents == 1 ? "1 torrent" : $"{torrents.ToString(CultureInfo.InvariantCulture)} torrents";
}
