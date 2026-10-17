// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Downloads;

/// <summary>A clock that only moves when the test says so, in a fixed time zone (UTC+2, no daylight saving).</summary>
public sealed class ZonedClock(DateTimeOffset start) : TimeProvider
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    public DateTimeOffset Now { get; private set; } = start;

    public override TimeZoneInfo LocalTimeZone => Zone;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public void Advance(TimeSpan by) => Now += by;

    /// <summary>A local time of the test zone.</summary>
    public static DateTimeOffset At(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.FromHours(2));
}

public sealed class DownloadSchedulerTests : IDisposable
{
    private static readonly IReadOnlyList<DayOfWeek> Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private readonly DownloadTestBed _bed = new();

    // Monday 2026-10-12, 21:59 local.
    private readonly ZonedClock _clock = new(ZonedClock.At(2026, 10, 12, 21, 59));
    private readonly DownloadScheduleService _schedules;

    public DownloadSchedulerTests()
    {
        _bed.Options.Time = _clock;
        _schedules = new DownloadScheduleService(
            new DownloadScheduleRepository(_bed.Database), _bed.Categories, _bed.Settings, new ServiceCollection().BuildServiceProvider(), NullLogger<DownloadScheduleService>.Instance);
    }

    [Fact]
    public async Task Rules_run_at_their_time_on_their_days_only()
    {
        var engine = await _bed.StartAsync();
        var scheduler = Scheduler(engine);
        Add("22:00", Weekdays, ScheduleAction.Pause);
        Add("06:00", DownloadScheduleRule.Week, ScheduleAction.Resume);

        Assert.Equal(0, await scheduler.RunDueAsync(_clock.GetUtcNow()));
        Assert.False(engine.Status().Paused);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await scheduler.RunDueAsync(_clock.GetUtcNow()));
        Assert.True(engine.PausedBySchedule);
        Assert.Equal("Paused by the scheduler (Settings → Downloads → Scheduling).", engine.Status().StatusMessage);
        Assert.Equal("true", _bed.Config.Get(ConfigKeys.DownloadPausedBySchedule));

        // The loop may wake up late: everything due in between runs, in order.
        _clock.Advance(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(5));
        Assert.Equal(1, await scheduler.RunDueAsync(_clock.GetUtcNow()));
        Assert.False(engine.Status().Paused);

        // Saturday 22:00: the pause rule is for weekdays.
        _clock.Advance(ZonedClock.At(2026, 10, 17, 22, 0) - _clock.Now);
        await scheduler.RunDueAsync(_clock.GetUtcNow().AddMinutes(-1));
        Assert.Equal(0, await scheduler.RunDueAsync(_clock.GetUtcNow()));
        Assert.False(engine.Status().Paused);
    }

    [Fact]
    public async Task Disabled_rules_do_not_run()
    {
        var engine = await _bed.StartAsync();
        var scheduler = Scheduler(engine);
        var rule = Add("22:00", DownloadScheduleRule.Week, ScheduleAction.Pause);
        _schedules.Update(rule.Id, rule with { Enabled = false });

        await scheduler.RunDueAsync(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, await scheduler.RunDueAsync(_clock.GetUtcNow()));
        Assert.False(engine.Status().Paused);
    }

    [Fact]
    public async Task Speed_limit_rules_in_kb_and_percent_of_the_line_speed()
    {
        var engine = await _bed.StartAsync();
        var scheduler = Scheduler(engine);
        _bed.Settings.Update(new DownloadSettingsChange { MaxLineSpeedKBps = 10_000 });
        Add("22:00", DownloadScheduleRule.Week, ScheduleAction.SpeedLimit, "50%");
        Add("23:00", DownloadScheduleRule.Week, ScheduleAction.SpeedLimit, "800");
        Add("23:30", DownloadScheduleRule.Week, ScheduleAction.SpeedLimit, "0");
        await scheduler.RunDueAsync(_clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.RunDueAsync(_clock.GetUtcNow());
        Assert.Equal(5000, _bed.Settings.SpeedLimitKBps);
        Assert.Equal(5000, engine.Status().SpeedLimitKBps);

        _clock.Advance(TimeSpan.FromHours(1));
        await scheduler.RunDueAsync(_clock.GetUtcNow());
        Assert.Equal(800, _bed.Settings.SpeedLimitKBps);

        _clock.Advance(TimeSpan.FromMinutes(30));
        await scheduler.RunDueAsync(_clock.GetUtcNow());
        Assert.Equal(0, _bed.Settings.SpeedLimitKBps);
    }

    [Fact]
    public async Task Category_rules_pause_and_resume_a_category()
    {
        var engine = await _bed.StartAsync();
        var scheduler = Scheduler(engine);
        Add("22:00", DownloadScheduleRule.Week, ScheduleAction.PauseCategory, "SERIES");
        Add("22:30", DownloadScheduleRule.Week, ScheduleAction.ResumeCategory, "series");
        await scheduler.RunDueAsync(_clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.RunDueAsync(_clock.GetUtcNow());
        Assert.Equal(["series"], engine.PausedCategories);

        _clock.Advance(TimeSpan.FromMinutes(30));
        await scheduler.RunDueAsync(_clock.GetUtcNow());
        Assert.Empty(engine.PausedCategories);
    }

    [Fact]
    public async Task At_startup_the_latest_rule_of_each_kind_is_applied()
    {
        Add("08:00", DownloadScheduleRule.Week, ScheduleAction.Pause);
        Add("18:00", DownloadScheduleRule.Week, ScheduleAction.Resume);
        Add("09:00", DownloadScheduleRule.Week, ScheduleAction.SpeedLimit, "1000");
        Add("07:00", DownloadScheduleRule.Week, ScheduleAction.SpeedLimit, "2000");

        // Tuesday noon: inside the pause window, after the 09:00 speed rule.
        var engine = await _bed.StartAsync();
        await Scheduler(engine).ApplyLatestAsync(ZonedClock.At(2026, 10, 13, 12, 0));
        Assert.True(engine.PausedBySchedule);
        Assert.Equal(1000, _bed.Settings.SpeedLimitKBps);

        // 19:00: the resume rule lifts the scheduler's pause …
        await Scheduler(engine).ApplyLatestAsync(ZonedClock.At(2026, 10, 13, 19, 0));
        Assert.False(engine.Status().Paused);

        // … but not a pause the user made.
        await engine.PauseAllAsync();
        await Scheduler(engine).ApplyLatestAsync(ZonedClock.At(2026, 10, 13, 19, 0));
        Assert.True(engine.Status().Paused);
        Assert.False(engine.PausedBySchedule);
    }

    [Fact]
    public void Rules_are_validated_and_stored_with_days_and_time()
    {
        var rule = Add("7:05", [DayOfWeek.Sunday, DayOfWeek.Monday], ScheduleAction.PauseCategory, "Movies");
        Assert.Equal(new TimeOnly(7, 5), rule.Time);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Sunday], rule.Days);
        Assert.Equal("movies", rule.Argument);
        Assert.Equal("07:05 Mon, Sun: pauseCategory movies", DownloadScheduleService.Describe(rule));

        Assert.Contains("days", Invalid(new DownloadScheduleRule(0, true, new TimeOnly(1, 0), [], ScheduleAction.Pause, string.Empty)));
        Assert.Contains("argument", Invalid(new DownloadScheduleRule(0, true, new TimeOnly(1, 0), Weekdays, ScheduleAction.SpeedLimit, "50%")));
        Assert.Contains("argument", Invalid(new DownloadScheduleRule(0, true, new TimeOnly(1, 0), Weekdays, ScheduleAction.SpeedLimit, "fast")));
        Assert.Contains("argument", Invalid(new DownloadScheduleRule(0, true, new TimeOnly(1, 0), Weekdays, ScheduleAction.PauseCategory, "nope")));
        Assert.Contains("argument", Invalid(new DownloadScheduleRule(0, true, new TimeOnly(1, 0), Weekdays, ScheduleAction.PauseServer, "1")));
        Assert.Throws<LibraryException>(() => _schedules.Update(999, rule));
        Assert.Throws<LibraryException>(() => _schedules.Delete(999));

        _schedules.Delete(rule.Id);
        Assert.Empty(_schedules.All());
    }

    [Fact]
    public void Occurrences_follow_the_local_time_zone()
    {
        var rule = new DownloadScheduleRule(1, true, new TimeOnly(0, 30), [DayOfWeek.Tuesday], ScheduleAction.Pause, string.Empty);

        var at = Assert.Single(rule.Occurrences(ZonedClock.At(2026, 10, 12, 23, 0), ZonedClock.At(2026, 10, 13, 1, 0), ZonedClock.Zone));

        Assert.Equal(new DateTimeOffset(2026, 10, 12, 22, 30, 0, TimeSpan.Zero), at.ToUniversalTime());
        Assert.Empty(rule.Occurrences(ZonedClock.At(2026, 10, 13, 0, 30), ZonedClock.At(2026, 10, 13, 1, 0), ZonedClock.Zone));
    }

    private DownloadScheduler Scheduler(DownloadEngine engine) => new(
        _schedules, engine, _bed.Settings, new ServiceCollection().BuildServiceProvider(), _bed.Options, NullLogger<DownloadScheduler>.Instance);

    private DownloadScheduleRule Add(string time, IReadOnlyList<DayOfWeek> days, ScheduleAction action, string argument = "") =>
        _schedules.Create(new DownloadScheduleRule(0, true, TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture), days, action, argument));

    private IEnumerable<string> Invalid(DownloadScheduleRule rule) => Assert.Throws<ValidationFailedException>(() => _schedules.Create(rule)).Errors.Keys;

    public void Dispose() => _bed.Dispose();
}
