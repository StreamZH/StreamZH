// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Data;
using StreamZH.Library.Notifications;

namespace StreamZH.Host.Status;

/// <summary>
/// Status-window notices of Settings → Connect: one warning per enabled connection whose last delivery failed
/// ("Plex: unauthorized"). It disappears with the next successful delivery or test, or when the connection is saved.
/// </summary>
public static class ConnectNotices
{
    public const string KeyPrefix = "connect-failed-";

    private const string Link = "/settings/connect";

    public static void Add(List<OngoingNotice> notices, ConnectionRepository? repository, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(time);
        if (repository is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var connection in repository.All().Where(c => c.Enable && c.Status.Failing))
        {
            var at = new DateTimeOffset(connection.Status.LastFailure!.Value, TimeSpan.Zero);
            var type = ConnectCatalog.Find(connection.Implementation)?.Name ?? connection.Implementation;
            notices.Add(new OngoingNotice(
                $"{KeyPrefix}{connection.Id.ToString(CultureInfo.InvariantCulture)}",
                NoticeLevels.Warn,
                $"{connection.Name}: {connection.Status.LastError ?? "failed"}",
                $"{type} · last try {LocalTime(at, now, time)} · check it in Settings → Connect",
                Link,
                false));
        }
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

/// <summary>Startup grace and check interval of <see cref="ConnectHealthWatcher"/>.</summary>
public sealed record ConnectHealthOptions(TimeSpan StartupDelay, TimeSpan Interval)
{
    /// <summary>Like Radarr's grace period: notices right after a start (indexers not checked yet …) are not reported.</summary>
    public static ConnectHealthOptions Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1));
}

/// <summary>
/// "On Health Issue" and "On Health Restored": compares the warnings and errors of the status window every minute and
/// tells the connections which appeared and which went away. Failed downloads ("On Download Failure") and the
/// connections' own notices are left out.
/// </summary>
public sealed class ConnectHealthWatcher(
    OngoingService ongoing,
    ConnectDispatcher dispatcher,
    ConnectHealthOptions options,
    TimeProvider time,
    ILogger<ConnectHealthWatcher> logger) : BackgroundService
{
    private readonly Lock _lock = new();
    private Dictionary<string, OngoingNotice> _known = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.StartupDelay, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Check();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Health check for the connections failed");
                }

                await Task.Delay(options.Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One comparison; public for the tests.</summary>
    public void Check()
    {
        lock (_lock)
        {
            if (!dispatcher.WantsHealth())
            {
                _known.Clear();
                return;
            }

            var current = ongoing.Get().Notices
                .Where(Relevant)
                .GroupBy(n => n.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var (key, notice) in current)
            {
                if (!_known.ContainsKey(key))
                {
                    dispatcher.HealthChanged(Health(notice), restored: false);
                }
            }

            foreach (var (key, notice) in _known)
            {
                if (!current.ContainsKey(key))
                {
                    dispatcher.HealthChanged(Health(notice), restored: true);
                }
            }

            _known = current;
        }
    }

    private static bool Relevant(OngoingNotice notice) =>
        notice.Level is NoticeLevels.Warn or NoticeLevels.Error
        && !notice.Key.StartsWith(ConnectNotices.KeyPrefix, StringComparison.Ordinal)
        && !notice.Key.StartsWith("download-failed-", StringComparison.Ordinal);

    private static ConnectHealth Health(OngoingNotice notice) => new(notice.Key, notice.Level, notice.Title, notice.Sub, notice.Link);
}
