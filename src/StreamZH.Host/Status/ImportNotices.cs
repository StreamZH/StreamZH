// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Data;

namespace StreamZH.Host.Status;

/// <summary>
/// Status-window notices of the import: one for every import that failed in the last day (with the reason, e.g. access
/// denied) until it is retried, imported by hand or dismissed. The running import shows as its job ("Import …").
/// </summary>
public static class ImportNotices
{
    public static readonly TimeSpan FailedNoticeLifetime = TimeSpan.FromHours(24);

    private const int MaxNotices = 20;

    public static void Add(List<OngoingNotice> notices, DownloadImportRepository? imports, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(time);
        if (imports is null)
        {
            return;
        }

        var all = imports.WithState(ImportState.Failed);
        var cutoff = time.GetUtcNow().UtcDateTime - FailedNoticeLifetime;
        foreach (var failed in all.Where(i => !i.Dismissed && i.Date >= cutoff).OrderByDescending(i => i.Date).Take(MaxNotices))
        {
            notices.Add(new OngoingNotice(
                $"import-failed-{failed.DownloadId}-{failed.Date.Ticks.ToString(CultureInfo.InvariantCulture)}",
                NoticeLevels.Error,
                $"Import failed: {failed.Title}",
                failed.Message ?? "See Activity for details.",
                "/activity",
                true));
        }
    }
}
