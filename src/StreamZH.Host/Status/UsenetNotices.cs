// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Downloads;
using StreamZH.Library.Usenet;
using StreamZH.Usenet.Pool;

namespace StreamZH.Host.Status;

/// <summary>
/// Status-window notices about enabled Usenet servers: a server skipped after errors (not for optional servers), a
/// required server that pauses the downloads, a used-up quota, and an account that expires within
/// <see cref="ExpiryNoticeDays"/> days (or expired that many days ago). Each disappears once the cause is gone.
/// </summary>
public static class UsenetNotices
{
    public const int ExpiryNoticeDays = 5;

    private const string Link = "/settings/usenet";

    public static void Add(List<OngoingNotice> notices, UsenetServerService? service, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(time);
        if (service is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var status = service.Status().Servers.ToDictionary(s => s.Id);
        foreach (var server in service.All().Where(s => s.Enabled))
        {
            var live = status.GetValueOrDefault(server.Id);
            AddHealth(notices, server, live, now, time);
            AddQuota(notices, server, live);
            AddExpiry(notices, server, today);
        }
    }

    private static void AddHealth(List<OngoingNotice> notices, UsenetServer server, UsenetServerStatus? live, DateTimeOffset now, TimeProvider time)
    {
        if (live is null || live.Healthy || server.OnFailure == ServerFailureAction.IgnoreLonger)
        {
            return;
        }

        var retry = live.RetryAt is { } at ? $"retry in {Duration(at - now)}" : null;
        var required = server.OnFailure == ServerFailureAction.PauseDownloads;
        notices.Add(new OngoingNotice(
            $"usenet-server-{server.Id}",
            required ? NoticeLevels.Error : NoticeLevels.Warn,
            required
                ? $"{server.Name} unavailable, downloads paused"
                : live.RetryAt is { } until ? $"{server.Name} skipped until {LocalTime(until, now, time)}" : $"{server.Name} skipped",
            Join(live.LastError, retry),
            Link,
            false));
    }

    private static void AddQuota(List<OngoingNotice> notices, UsenetServer server, UsenetServerStatus? live)
    {
        if (server.QuotaBytes is not { } quota || live is not { QuotaExhausted: true })
        {
            return;
        }

        notices.Add(new OngoingNotice(
            $"usenet-quota-{server.Id}",
            server.OnFailure == ServerFailureAction.PauseDownloads ? NoticeLevels.Error : NoticeLevels.Warn,
            $"{server.Name}: quota of {Gb(quota)} used up",
            server.OnFailure == ServerFailureAction.PauseDownloads
                ? "Downloads are paused. Reset the counter or raise the quota in the server settings."
                : "The server is skipped until you reset the counter or raise the quota.",
            Link,
            false));
    }

    private static void AddExpiry(List<OngoingNotice> notices, UsenetServer server, DateOnly today)
    {
        if (server.ExpiryDate is not { } expiry)
        {
            return;
        }

        var days = expiry.DayNumber - today.DayNumber;
        if (days is > ExpiryNoticeDays or < -ExpiryNoticeDays)
        {
            return;
        }

        var date = expiry.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var expired = days < 0;
        notices.Add(new OngoingNotice(
            $"usenet-expiry-{server.Id}-{server.ExpiresOn}",
            expired ? NoticeLevels.Error : NoticeLevels.Warn,
            expired ? $"{server.Name} account expired on {date}" : $"{server.Name} account expires on {date}",
            expired
                ? "Downloads from this server will fail. Renew the account, then update the date in the server settings."
                : days switch
                {
                    0 => "Today. Renew the account, then update the date in the server settings.",
                    1 => "Tomorrow. Renew the account, then update the date in the server settings.",
                    _ => $"In {days} days. Renew the account, then update the date in the server settings.",
                },
            Link,
            !expired));
    }

    private static string Gb(long bytes) =>
        $"{(bytes / (double)UsenetServer.BytesPerGb).ToString("0.##", CultureInfo.InvariantCulture)} GB";

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Duration(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
        {
            return "under a minute";
        }

        if (span.TotalHours < 1)
        {
            return $"{Math.Ceiling(span.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min";
        }

        var hours = (int)span.TotalHours;
        return span.Minutes == 0 ? $"{hours} h" : $"{hours} h {span.Minutes} min";
    }

    /// <summary>"21:30", or "Mon 21:30" when it is not today (server time zone).</summary>
    private static string LocalTime(DateTimeOffset at, DateTimeOffset now, TimeProvider time)
    {
        var zone = time.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        return local.Date == today.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }
}
