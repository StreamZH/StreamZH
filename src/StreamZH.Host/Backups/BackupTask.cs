// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Jobs;

namespace StreamZH.Host.Backups;

/// <summary>Scheduled backup (System → Tasks "Backup"): every backup interval, then deletes the expired ones.</summary>
public sealed class BackupTask(BackupSettings settings) : IScheduledTask
{
    public const string TaskKey = "backup";

    public string Key => TaskKey;

    public string Name => "Backup";

    public TimeSpan? Interval => settings.IntervalDays > 0 ? TimeSpan.FromDays(settings.IntervalDays) : null;

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var backups = services.GetRequiredService<BackupService>();
        backups.Create(BackupType.Scheduled);
        backups.Cleanup();
        return Task.CompletedTask;
    }
}
