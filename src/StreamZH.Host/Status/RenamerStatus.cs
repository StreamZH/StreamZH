// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Library.Renamer;

namespace StreamZH.Host.Status;

/// <summary>
/// The running file rename (Tools → File renamer) in the status window: "Renaming 40 files · 12 of 40" with progress.
/// It takes the place of the plain job row, because the rename is that job.
/// </summary>
public static class RenamerStatus
{
    public static void Add(List<OngoingRunning> running, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<RenamerProgress>()?.Current is not { } activity)
        {
            return;
        }

        var row = new OngoingRunning(
            $"renamer-{activity.RunId.ToString(CultureInfo.InvariantCulture)}",
            OngoingKinds.Job,
            activity.Title,
            $"{activity.Done.ToString(CultureInfo.InvariantCulture)} of {activity.Total.ToString(CultureInfo.InvariantCulture)}",
            activity.Total > 0 ? (double)activity.Done / activity.Total : null,
            null,
            null,
            "/tools/renamer");
        var job = running.FindIndex(r => r.Key == "job");
        if (job >= 0)
        {
            running[job] = row;
        }
        else
        {
            running.Add(row);
        }
    }
}
