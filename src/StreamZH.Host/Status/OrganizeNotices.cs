// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Media;
using StreamZH.Library.Organize;

namespace StreamZH.Host.Status;

/// <summary>
/// The status window during organize, undo and move: the running row with "5 of 12 · Dune" and the progress (it takes
/// the place of the plain job row), and afterwards a notice when items failed or were skipped, for a day.
/// </summary>
public static class OrganizeNotices
{
    public static readonly TimeSpan NoticeLifetime = TimeSpan.FromHours(24);

    public static void Add(List<OngoingRunning> running, List<OngoingNotice> notices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<LibraryRunTracker>() is not { } tracker)
        {
            return;
        }

        if (tracker.Current is { } current)
        {
            var row = new OngoingRunning(
                "library-run",
                OngoingKinds.Job,
                current.Title,
                current.Started
                    ? string.Join(" · ", new[] { $"{Count(current.Done)} of {Count(current.Total)}", current.Current }.Where(p => !string.IsNullOrEmpty(p)))
                    : "Waiting for other jobs",
                current.Started ? current.Progress : null,
                null,
                null,
                Link(current.MediaType));
            var job = running.FindIndex(r => r.Key == "job" && r.Title == current.Title);
            if (job >= 0)
            {
                running[job] = row;
            }
            else
            {
                running.Add(row);
            }
        }

        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        if (tracker.Last is { } last && last.Failed + last.Skipped > 0 && time.GetUtcNow().UtcDateTime - last.Finished < NoticeLifetime)
        {
            var verb = last.Kind switch
            {
                LibraryRunKind.Move => "Move",
                LibraryRunKind.Undo => "Undo organize",
                _ => "Organize",
            };
            var total = last.Done + last.Failed + last.Skipped;
            notices.Add(new OngoingNotice(
                $"library-run-{last.Finished.Ticks.ToString(CultureInfo.InvariantCulture)}",
                last.Failed > 0 ? NoticeLevels.Error : NoticeLevels.Warn,
                $"{verb}: {Count(last.Failed + last.Skipped)} of {Count(total)} not done",
                last.Errors.FirstOrDefault() is { } first ? $"{first.Title}: {first.Message}" : null,
                Link(last.MediaType),
                true));
        }
    }

    private static string Link(MediaType mediaType) => mediaType == MediaType.Series ? "/library/series" : "/library/movies";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
