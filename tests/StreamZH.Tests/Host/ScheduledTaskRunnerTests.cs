// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Host.Jobs;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Host;

public sealed class ScheduledTaskRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"streamzh-tasks-{Guid.NewGuid():N}");
    private readonly ConfigRepository _config;
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

    public ScheduledTaskRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        var database = new Database(new AppPaths(_dir).DatabaseFile);
        database.Migrate();
        _config = new ConfigRepository(database);
    }

    private sealed class CountingTask(TimeSpan? interval) : IScheduledTask
    {
        public string Key => "count";
        public string Name => "Count";
        public TimeSpan? Interval { get; set; } = interval;
        public int Runs { get; private set; }

        public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_task_is_queued_once_its_interval_has_passed_and_never_twice()
    {
        var task = new CountingTask(TimeSpan.FromMinutes(15));
        var queue = new JobQueue(new InProcessEventBus());
        var runner = new ScheduledTaskRunner([task], queue, _config, NullLogger<ScheduledTaskRunner>.Instance, _time);

        runner.CheckDue();
        Assert.Equal(0, queue.Status.Queued);
        Assert.Equal(_time.Now.AddMinutes(15), runner.States().Single().NextRun);

        _time.Now += TimeSpan.FromMinutes(16);
        runner.CheckDue();
        runner.CheckDue();
        Assert.Equal(1, queue.Status.Queued);
        Assert.True(runner.States().Single().Queued);

        Assert.True(queue.Reader.TryRead(out var job));
        await job.Run(null!, CancellationToken.None);
        Assert.Equal(1, task.Runs);
        var state = runner.States().Single();
        Assert.True(state.LastSuccess);
        Assert.Equal(_time.Now, state.LastRun);
    }

    [Fact]
    public void A_task_without_interval_is_off_but_can_run_now()
    {
        var task = new CountingTask(null);
        var queue = new JobQueue(new InProcessEventBus());
        var runner = new ScheduledTaskRunner([task], queue, _config, NullLogger<ScheduledTaskRunner>.Instance, _time);

        _time.Now += TimeSpan.FromDays(10);
        runner.CheckDue();
        Assert.Equal(0, queue.Status.Queued);
        Assert.Null(runner.States().Single().NextRun);
        Assert.True(runner.RunNow("COUNT"));
        Assert.False(runner.RunNow("count"));
        Assert.False(runner.RunNow("unknown"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
