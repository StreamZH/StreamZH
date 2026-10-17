// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.Backups;
using StreamZH.Host.General;

namespace StreamZH.Host.Status;

/// <summary>Status-window notices of Settings → General and System: restart needed, host problems, backups, restore.</summary>
public static class GeneralNotices
{
    public static void Add(List<OngoingNotice> notices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(services);

        if (services.GetService<RunningHost>() is { } running && services.GetService<HostSettings>() is { } host)
        {
            var stored = host.Current;
            var changed = running.Override is null ? stored != running.Values : stored.UrlBase != running.Values.UrlBase;
            if (changed && !running.Fallback)
            {
                notices.Add(new OngoingNotice(
                    "restart-required", NoticeLevels.Info, "Restart required", "Changed host settings apply after a restart.", "/settings/general#host", false));
            }

            if (running.Problem is { } problem)
            {
                notices.Add(new OngoingNotice("host-problem", NoticeLevels.Error, "Host settings not fully applied", problem, "/settings/general#host", false));
            }
        }

        if (services.GetService<SecuritySettings>()?.Method == AuthenticationMethod.External)
        {
            notices.Add(new OngoingNotice(
                "auth-external",
                NoticeLevels.Info,
                "Sign-in is left to a reverse proxy",
                "Authentication is External: anyone who reaches StreamZH directly has full access.",
                "/settings/general#security",
                true));
        }

        if (services.GetService<BackupService>() is { } backups)
        {
            if (backups.LastScheduledError is { } error)
            {
                notices.Add(new OngoingNotice("backup-failed", NoticeLevels.Warn, "Scheduled backup failed", error, "/system/backup", false));
            }

            if (backups.Pending() is { } pending)
            {
                notices.Add(new OngoingNotice(
                    "restore-pending", NoticeLevels.Info, "Restore waiting for a restart", $"{pending.Source} is restored at the next start.", "/system/backup", false));
            }
        }

        if (services.GetService<StartupRestore>()?.Outcome is { } outcome)
        {
            notices.Add(new OngoingNotice(
                outcome.Success ? "restore-done" : "restore-failed",
                outcome.Success ? NoticeLevels.Info : NoticeLevels.Error,
                outcome.Success ? "Backup restored" : "Restore failed",
                outcome.Message,
                "/system/backup",
                outcome.Success));
        }
    }
}
