// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Host.Status;
using StreamZH.Library.Organize;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Host;

public sealed class OrganizeNoticesTests
{
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_running_row_shows_progress_in_place_of_the_job_row()
    {
        var tracker = new LibraryRunTracker(new InProcessEventBus(), _time);
        var services = new ServiceCollection().AddSingleton(tracker).AddSingleton<TimeProvider>(_time).BuildServiceProvider();
        tracker.Start(LibraryRunKind.Organize, MediaType.Movie, "Organize 4 movies", 4);
        tracker.Report(1, "Dune", 0.5);
        List<OngoingRunning> running = [new("job", OngoingKinds.Job, "Organize 4 movies", "Running", null, null, null, null)];
        var notices = new List<OngoingNotice>();

        OrganizeNotices.Add(running, notices, services);

        var row = Assert.Single(running);
        Assert.Equal("library-run", row.Key);
        Assert.Equal("1 of 4 · Dune", row.Sub);
        Assert.Equal(0.375, row.Progress);
        Assert.Equal("/library/movies", row.Link);
        Assert.Empty(notices);
    }

    [Fact]
    public void Failed_items_leave_a_notice_for_a_day()
    {
        var tracker = new LibraryRunTracker(new InProcessEventBus(), _time);
        var services = new ServiceCollection().AddSingleton(tracker).AddSingleton<TimeProvider>(_time).BuildServiceProvider();
        tracker.Finish(new LibraryRunResult(
            LibraryRunKind.Move, MediaType.Series, null, _time.Now.UtcDateTime, 2, 0, 0, 1, [new OrganizeError(3, "Breaking Bad", "The disk is full.")]));
        var notices = new List<OngoingNotice>();

        OrganizeNotices.Add([], notices, services);
        var notice = Assert.Single(notices);
        Assert.Equal("Move: 1 of 3 not done", notice.Title);
        Assert.Equal("Breaking Bad: The disk is full.", notice.Sub);
        Assert.Equal(NoticeLevels.Error, notice.Level);

        _time.Now += OrganizeNotices.NoticeLifetime;
        notices.Clear();
        OrganizeNotices.Add([], notices, services);
        Assert.Empty(notices);
    }
}
