// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.Downloads;
using StreamZH.Library.Search;

namespace StreamZH.Host.Status;

/// <summary>
/// Status-window notices from Settings → Downloads: the queue paused by the scheduler or at startup, categories paused
/// by the scheduler, and a watched folder that does not exist. Each disappears once the cause is gone.
/// </summary>
public static class DownloadNotices
{
    private const string SchedulingLink = "/settings/downloads#scheduling";

    public static void Add(List<OngoingNotice> notices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<DownloadEngine>() is not { } engine)
        {
            return;
        }

        if (engine.PausedBySchedule)
        {
            notices.Add(new OngoingNotice(
                "downloads-paused-schedule",
                NoticeLevels.Info,
                "Downloads paused by the scheduler",
                "They continue with the next resume rule, or resume them in Activity.",
                SchedulingLink,
                false));
        }
        else if (engine.PausedOnStart)
        {
            notices.Add(new OngoingNotice(
                "downloads-paused-start",
                NoticeLevels.Info,
                "Downloads paused at startup",
                "\"Pause downloading on start\" is on. Resume them in Activity.",
                "/settings/downloads#queue",
                false));
        }

        foreach (var category in engine.PausedCategories)
        {
            notices.Add(new OngoingNotice(
                $"download-category-paused-{category.ToLowerInvariant()}",
                NoticeLevels.Info,
                $"Category {category} paused by the scheduler",
                "Its downloads wait until a rule resumes the category.",
                SchedulingLink,
                false));
        }

        if (services.GetService<DownloadSettings>()?.WatchFolder is { } watch && !Directory.Exists(watch))
        {
            notices.Add(new OngoingNotice(
                "download-watch-folder",
                NoticeLevels.Warn,
                "Watched folder not found",
                $"{watch} does not exist; NZB files dropped there are not picked up.",
                "/settings/downloads#folders",
                false));
        }
    }
}
