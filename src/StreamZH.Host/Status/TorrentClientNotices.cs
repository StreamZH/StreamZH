// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.Downloads.Torrent;

namespace StreamZH.Host.Status;

/// <summary>
/// 0.11 status-window notices: an enabled download client StreamZH cannot reach or log in to (from the torrent tracker's
/// last poll or grab). The notice disappears with the next successful contact.
/// </summary>
public static class TorrentClientNotices
{
    private const string Link = "/settings/downloadclients";

    public static void Add(List<OngoingNotice> notices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<TorrentTracker>() is not { } tracker || services.GetService<DownloadClientService>() is not { } clients)
        {
            return;
        }

        var enabled = clients.All().Where(c => c.Enable).ToDictionary(c => c.Id);
        foreach (var health in tracker.Health().Where(h => !h.Ok && enabled.ContainsKey(h.ClientId)))
        {
            notices.Add(new OngoingNotice(
                $"download-client-{health.ClientId}",
                NoticeLevels.Error,
                $"{enabled[health.ClientId].Name} unavailable",
                health.Error,
                Link,
                false));
        }
    }
}
