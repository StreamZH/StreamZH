// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.Trash;

namespace StreamZH.Host.Status;

/// <summary>
/// Status-window notice of the TRaSH Guides sync: a warning while the last sync failed (download or a template profile
/// that could not be updated). It disappears with the next successful sync or when the sync is switched off.
/// </summary>
public static class TrashNotices
{
    public const string Key = "trash-sync-failed";

    private const string Link = "/settings/profiles";

    public static void Add(List<OngoingNotice> notices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<TrashGuideStore>() is not { } store || services.GetService<TrashSettings>() is not { } settings
            || !settings.Values.Enabled || store.LastError is not { } error)
        {
            return;
        }

        notices.Add(new OngoingNotice(
            Key,
            NoticeLevels.Warn,
            "TRaSH Guides sync failed",
            store.HasCopy ? $"{error} The last downloaded copy stays in use." : error,
            Link,
            false));
    }
}
