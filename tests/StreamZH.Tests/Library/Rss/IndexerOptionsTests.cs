// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Decisions;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Jobs;
using StreamZH.Library.Rss;
using StreamZH.Library.Search;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.Rss;

public sealed class IndexerOptionsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly SearchTestBed _bed = new();

    [Fact]
    public void Defaults_keep_the_old_behaviour_and_switch_rss_on()
    {
        Assert.Equal(IndexerOptions.Defaults, _bed.Options.Values with { WhitelistedHardcodedSubs = IndexerOptions.Defaults.WhitelistedHardcodedSubs });
        Assert.Empty(_bed.Options.Values.WhitelistedHardcodedSubs);
        Assert.Equal(TimeSpan.FromMinutes(60), _bed.Options.RssSyncInterval);
        Assert.Null(_bed.Options.MissingSearchInterval);
        Assert.False(_bed.Options.SearchOnAdd);

        var rules = _bed.Options.Rules();
        Assert.Equal((0, 0, 0, false), (rules.MinimumAgeMinutes, rules.RetentionDays, rules.MaximumSizeMb, rules.AllowHardcodedSubs));
        Assert.Empty(rules.ReleaseProfiles);
    }

    [Fact]
    public void Saved_values_are_read_back_and_only_enabled_profiles_become_rules()
    {
        var values = new IndexerOptionValues(15, 3000, 20000, true, ["DKSUBS", "nlsub"], MinimumAvailability.Released, -7, 30, 12, true);
        _bed.Options.Save(values);
        _bed.ReleaseProfiles.Add(new ReleaseProfile { Name = "On", Ignored = ["CAM"] });
        _bed.ReleaseProfiles.Add(new ReleaseProfile { Name = "Off", Enabled = false, Ignored = ["TS"] });

        var read = _bed.Options.Values;
        Assert.Equal(values with { WhitelistedHardcodedSubs = read.WhitelistedHardcodedSubs }, read);
        Assert.Equal(["DKSUBS", "nlsub"], read.WhitelistedHardcodedSubs);
        Assert.Equal(TimeSpan.FromMinutes(30), _bed.Options.RssSyncInterval);
        Assert.Equal(TimeSpan.FromHours(12), _bed.Options.MissingSearchInterval);

        var rules = _bed.Options.Rules();
        Assert.Equal((15, 3000, 20000, true), (rules.MinimumAgeMinutes, rules.RetentionDays, rules.MaximumSizeMb, rules.AllowHardcodedSubs));
        Assert.Equal("On", Assert.Single(rules.ReleaseProfiles).Name);

        _bed.Options.Save(values with { RssSyncInterval = 0 });
        Assert.Null(_bed.Options.RssSyncInterval);
    }

    [Fact]
    public void Broken_config_values_fall_back_to_safe_values()
    {
        _bed.Config.Set(ConfigKeys.IndexerRssSyncInterval, "3");
        _bed.Config.Set(ConfigKeys.IndexerMinimumAge, "-5");
        _bed.Config.Set(ConfigKeys.IndexerMinimumAvailability, "soon");
        _bed.Config.Set(ConfigKeys.IndexerAllowHardcodedSubs, "maybe");

        var values = _bed.Options.Values;
        Assert.Equal((10, 0, MinimumAvailability.InCinemas, false), (values.RssSyncInterval, values.MinimumAge, values.MinimumAvailability, values.AllowHardcodedSubs));
    }

    [Theory]
    [InlineData(MinimumAvailability.Announced, "2027-05-01", 0, null)]
    [InlineData(MinimumAvailability.InCinemas, "2026-10-01", 0, null)]
    [InlineData(MinimumAvailability.InCinemas, "2026-11-01", 0, "2026-11-01")]
    [InlineData(MinimumAvailability.InCinemas, "2026-10-10", 14, "2026-10-24")]
    [InlineData(MinimumAvailability.InCinemas, "2026-10-20", -7, null)]
    [InlineData(MinimumAvailability.Released, "2026-10-01", 0, "2026-12-30")]
    [InlineData(MinimumAvailability.Released, "2026-06-01", 0, null)]
    public void Availability_follows_the_cinema_date(MinimumAvailability minimum, string inCinemas, int delay, string? expected)
    {
        var movie = new Movie { Title = "Test", Year = 2026, Status = "Post Production", InCinemas = inCinemas };
        var from = IndexerOptions.AvailableFrom(movie, minimum, delay, Now);
        Assert.Equal(expected, from?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("Released", 2026, true)]
    [InlineData("", 2026, true)]
    [InlineData("In Production", 2026, false)]
    [InlineData("In Production", 2025, true)]
    public void Without_a_date_the_tmdb_status_decides(string status, int year, bool available)
    {
        var movie = new Movie { Title = "Test", Year = year, Status = status };
        Assert.Equal(available, IndexerOptions.AvailableFrom(movie, MinimumAvailability.Released, 30, Now) is null);
        Assert.Null(IndexerOptions.AvailableFrom(movie, MinimumAvailability.Announced, 0, Now));
    }

    [Fact]
    public void The_rss_task_follows_the_interval_setting_live()
    {
        var task = new RssSyncTask(_bed.Options);
        var missing = new MissingSearchTask(_bed.Options);
        var queue = new JobQueue(new InProcessEventBus());
        var runner = new ScheduledTaskRunner([task, missing], queue, _bed.Config, NullLogger<ScheduledTaskRunner>.Instance, _bed.Time);

        runner.CheckDue();
        var rss = runner.States().Single(s => s.Key == "rss-sync");
        Assert.Equal(("RSS sync", TimeSpan.FromMinutes(60)), (rss.Name, rss.Interval));
        Assert.Equal(_bed.Time.Now.AddMinutes(60), rss.NextRun);
        Assert.Null(runner.States().Single(s => s.Key == "missing-search").Interval);

        // 15 minutes now: due after 20 minutes, not after 60.
        _bed.Options.Save(_bed.Options.Values with { RssSyncInterval = 15, MissingSearchInterval = 24 });
        _bed.Time.Now += TimeSpan.FromMinutes(20);
        runner.CheckDue();
        Assert.Equal(1, queue.Status.Queued);
        Assert.True(runner.States().Single(s => s.Key == "rss-sync").Queued);
        Assert.Equal(TimeSpan.FromHours(24), runner.States().Single(s => s.Key == "missing-search").Interval);

        // Off: never due again.
        _bed.Options.Save(_bed.Options.Values with { RssSyncInterval = 0 });
        Assert.Null(runner.States().Single(s => s.Key == "rss-sync").NextRun);
    }

    public void Dispose() => _bed.Dispose();
}
