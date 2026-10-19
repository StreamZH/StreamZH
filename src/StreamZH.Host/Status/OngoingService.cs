// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Library.Indexers;
using StreamZH.Library.PostProcessing;
using StreamZH.Library.Usenet;

namespace StreamZH.Host.Status;

/// <summary>
/// What the status window at the bottom right shows (GET /api/v1/status/ongoing): the running job, active downloads, tool
/// installations and notices about things that stop the pipeline (no Usenet server, no indexer, back-off, VIP, folders,
/// disk space, failures, par2 or 7-Zip that could not be installed).
/// Also tells the browsers to re-read it ("ongoing") when the running job changes or indexer health changes.
/// </summary>
/// <remarks>
/// The download engine is optional here: <see cref="IDownloadQueue"/> is resolved when it is registered, so the status
/// window works without it (and before it is configured). The same goes for <see cref="ToolInstaller"/>.
/// </remarks>
public sealed class OngoingService(
    JobQueue jobs,
    IndexerService indexers,
    RootFolderRepository rootFolders,
    ConfigRepository config,
    Database database,
    IStatusProbe probe,
    IEventBus events,
    TimeProvider time,
    IServiceProvider services) : IHostedService
{
    /// <summary>Config keys of the download settings (0.5): "builtin" (default) or "blackhole", and the work folders.</summary>
    public const string DownloadClientKey = "DownloadClient";
    public const string IncompleteFolderKey = "IncompleteFolder";
    public const string CompleteFolderKey = "CompleteFolder";
    public const string BlackholeClient = "blackhole";

    /// <summary>Below this a download folder gets a warning.</summary>
    public const long LowDiskSpaceBytes = 10L * 1024 * 1024 * 1024;

    public const int VipWarningDays = 7;

    public static readonly TimeSpan FailedNoticeLifetime = TimeSpan.FromHours(24);

    private const int MaxFailedNotices = 20;

    private readonly ConcurrentDictionary<int, FailedDownload> _failed = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Lock _jobLock = new();
    private (string? Running, bool Queued) _lastJob;

    private sealed record FailedDownload(int Id, string Title, string? Error, DateTimeOffset At);

    public OngoingResource Get()
    {
        var running = new List<OngoingRunning>();
        var notices = new List<OngoingNotice>();
        var queue = services.GetService<IDownloadQueue>();
        var active = queue?.Active() ?? [];

        AddToolInstalls(running);
        AddDownloads(running, active.Where(d => d.Status is not (DownloadStatus.Queued or DownloadStatus.Paused)));
        AddJob(running);
        RenamerStatus.Add(running, services);
        AddDownloads(running, active.Where(d => d.Status is DownloadStatus.Queued or DownloadStatus.Paused));

        AddToolNotices(notices);
        AddDownloadClientNotices(notices);
        DownloadNotices.Add(notices, services);
        TorrentClientNotices.Add(notices, services);
        TorrentEngineNotices.Add(notices, services);
        AddIndexerNotices(notices);
        AddFolderNotices(notices);
        AddFailedNotices(notices, active);
        ConnectNotices.Add(notices, services.GetService<ConnectionRepository>(), time);
        ImportNotices.Add(notices, services.GetService<DownloadImportRepository>(), time);
        GeneralNotices.Add(notices, services);
        SecurityNotices.Add(notices, services);
        OrganizeNotices.Add(running, notices, services);
        ImportListNotices.Add(notices, services.GetService<ImportListRepository>(), time);
        TrashNotices.Add(notices, services);
        AutomationNotices.Add(notices, services);
        DiscordNotices.Add(notices, services);
        SecretNotices.Add(notices, services);

        return new OngoingResource(running, notices, AutomationNotices.IsOn(services));
    }

    // ---- Running ----

    private void AddJob(List<OngoingRunning> running)
    {
        var status = jobs.Status;
        if (status.Running is { } name)
        {
            running.Add(new OngoingRunning(
                "job", OngoingKinds.Job, name, status.Queued > 0 ? $"{status.Queued} more queued" : "Running", null, null, null, null));
        }
        else if (status.Queued > 0)
        {
            running.Add(new OngoingRunning(
                "job", OngoingKinds.Job, "Background jobs", $"{status.Queued} queued", null, null, null, null));
        }
    }

    /// <summary>"Installing par2 1.5.0" with the download progress.</summary>
    private void AddToolInstalls(List<OngoingRunning> running)
    {
        if (services.GetService<ToolInstaller>() is not { } installer)
        {
            return;
        }

        foreach (var tool in ToolInstaller.AllTools)
        {
            var status = installer.Status(tool);
            if (!status.Installing)
            {
                continue;
            }

            var version = installer.Package(tool)?.Version;
            running.Add(new OngoingRunning(
                $"install-{ToolKey(tool)}",
                OngoingKinds.Install,
                $"Installing {PostProcessTools.DisplayName(tool)}{(version is null ? string.Empty : " " + version)}",
                status.Progress is { } p and > 0 ? $"{status.Step ?? "Installing"} · {Percent(p)}" : status.Step ?? "Starting",
                status.Progress,
                null,
                null,
                ToolsLink));
        }
    }

    private static void AddDownloads(List<OngoingRunning> running, IEnumerable<DownloadItem> items)
    {
        var ordered = items
            .OrderBy(d => d.Status switch
            {
                DownloadStatus.Extracting or DownloadStatus.Repairing or DownloadStatus.Verifying => 0,
                DownloadStatus.Downloading => 1,
                DownloadStatus.Queued => 2,
                _ => 3,
            })
            .ThenByDescending(d => d.Priority)
            .ThenBy(d => d.Added);
        running.AddRange(ordered.Select(Row));
    }

    /// <summary>The running row of one download: percent, speed and time left, or the post-processing step.</summary>
    public static OngoingRunning Row(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var post = item.Status is DownloadStatus.Verifying or DownloadStatus.Repairing or DownloadStatus.Extracting;
        var eta = item.Eta?.TotalSeconds;
        string sub;
        double? progress;
        if (post)
        {
            var step = item.StatusMessage ?? item.Status.ToString();
            sub = item.StepProgress is { } p ? $"{step} · {Percent(p)}" : step;
            progress = item.StepProgress;
        }
        else
        {
            progress = item.Progress;
            sub = item.Status switch
            {
                DownloadStatus.Queued => item.TotalBytes > 0 ? $"Queued · {FormatBytes(item.TotalBytes)}" : "Queued",
                DownloadStatus.Paused => $"Paused · {Percent(item.Progress)}",
                _ => Join(
                    Percent(item.Progress),
                    item.SpeedBps > 0 ? $"{FormatBytes(item.SpeedBps)}/s" : "waiting for connections",
                    item.SpeedBps > 0 && eta is { } seconds ? TimeLeft(seconds) : null,
                    item.MissingArticles > 0 ? $"{item.MissingArticles} missing articles" : null),
            };
        }

        return new OngoingRunning(
            $"download-{item.Id}",
            post ? OngoingKinds.PostProcess : OngoingKinds.Download,
            item.Title,
            sub,
            progress,
            item.Status == DownloadStatus.Downloading ? item.SpeedBps : null,
            item.Status == DownloadStatus.Downloading ? eta : null,
            "/activity");
    }

    // ---- Notices ----

    /// <summary>A failed installation, until the tool is found (installed with the button, configured or installed by hand).</summary>
    private void AddToolNotices(List<OngoingNotice> notices)
    {
        if (services.GetService<ToolInstaller>() is not { } installer || services.GetService<PostProcessTools>() is not { } tools)
        {
            return;
        }

        foreach (var tool in ToolInstaller.AllTools)
        {
            var status = installer.Status(tool);
            if (status.Installing || status.Error is not { } error || tools.Find(tool).Found)
            {
                continue;
            }

            notices.Add(new OngoingNotice(
                $"install-failed-{ToolKey(tool)}",
                NoticeLevels.Warn,
                $"{PostProcessTools.DisplayName(tool)} could not be installed",
                $"{error.TrimEnd('.')}. Settings → Tools",
                ToolsLink,
                false));
        }
    }

    private const string ToolsLink = "/settings/downloads#tools";

    private static string ToolKey(PostProcessTool tool) => PostProcessTools.Key(tool);

    private void AddDownloadClientNotices(List<OngoingNotice> notices)
    {
        var client = config.Get(DownloadClientKey);
        if (string.Equals(client, BlackholeClient, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The article source knows the connections of all enabled servers; without it, look at the table.
        // Unknown (no engine installed yet): no notice.
        var servers = services.GetService<IArticleSource>() is { } source ? source.MaxConnections : EnabledUsenetServers();
        if (servers == 0)
        {
            notices.Add(new OngoingNotice(
                "no-usenet-server",
                NoticeLevels.Warn,
                "No Usenet server configured",
                "Downloads cannot start until you add your Usenet provider.",
                "/settings/usenet",
                false));
        }

        UsenetNotices.Add(notices, services.GetService<UsenetServerService>(), time);
    }

    /// <summary>Enabled Usenet servers, or null when the table does not exist (download engine not installed).</summary>
    private int? EnabledUsenetServers()
    {
        try
        {
            using var connection = database.Open();
            var table = connection.QueryFirstOrDefault<string>(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'Usenet%Server%' ORDER BY length(name) LIMIT 1");
            if (table is null)
            {
                return null;
            }

            var quoted = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            var hasEnabled = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = 'Enabled'", new { table }) > 0;
            return connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM {quoted}{(hasEnabled ? " WHERE Enabled = 1" : string.Empty)}");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private void AddIndexerNotices(List<OngoingNotice> notices)
    {
        var all = indexers.All();
        var enabled = all.Where(i => i.Enable).ToList();
        if (enabled.Count == 0)
        {
            notices.Add(new OngoingNotice(
                "no-indexer",
                NoticeLevels.Warn,
                all.Count == 0 ? "No indexer configured" : "No indexer enabled",
                "StreamZH cannot search for releases without an indexer.",
                "/indexers",
                false));
            return;
        }

        var now = time.GetUtcNow();
        var health = indexers.Health();
        foreach (var indexer in enabled)
        {
            if (health.GetValueOrDefault(indexer.Id) is { DisabledTill: { } till } status && till > now)
            {
                var wait = till - now;
                notices.Add(new OngoingNotice(
                    $"indexer-paused-{indexer.Id}",
                    NoticeLevels.Warn,
                    $"{indexer.Name} paused until {LocalTime(till, now)}",
                    Join(status.LastError, $"retry in {Duration(wait)}"),
                    "/indexers",
                    false));
            }

            if (indexers.LimitStatus(indexer, null).VipExpiresInDays is { } days && days <= VipWarningDays)
            {
                notices.Add(new OngoingNotice(
                    $"indexer-vip-{indexer.Id}-{indexer.VipExpiration}",
                    days < 0 ? NoticeLevels.Error : NoticeLevels.Warn,
                    days switch
                    {
                        < 0 => $"{indexer.Name}: VIP expired",
                        0 => $"{indexer.Name}: VIP expires today",
                        1 => $"{indexer.Name}: VIP expires tomorrow",
                        _ => $"{indexer.Name}: VIP expires in {days} days",
                    },
                    days < 0
                        ? $"Expired on {indexer.VipExpiration}. Renew it or update the date in the indexer settings."
                        : "Renew the membership, then update the date in the indexer settings.",
                    "/indexers",
                    days >= 0));
            }
        }
    }

    private void AddFolderNotices(List<OngoingNotice> notices)
    {
        foreach (var root in rootFolders.All())
        {
            var access = probe.Access(root.Path);
            if (!access.Exists)
            {
                notices.Add(new OngoingNotice(
                    $"root-missing-{root.Id}", NoticeLevels.Error, "Root folder not found", $"{root.Path} · is the drive mounted?", "/settings/media-management", false));
            }
            else if (!access.Writable)
            {
                notices.Add(new OngoingNotice(
                    $"root-readonly-{root.Id}",
                    NoticeLevels.Warn,
                    "Root folder is read-only",
                    $"{root.Path} · finished downloads cannot be moved in",
                    "/settings/media-management",
                    false));
            }
        }

        if (string.Equals(config.Get(DownloadClientKey), BlackholeClient, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var drives = new HashSet<string>(PathComparer.Instance);
        foreach (var (key, label) in new[] { (IncompleteFolderKey, "incomplete"), (CompleteFolderKey, "complete") })
        {
            if (config.Get(key) is not { Length: > 0 } folder || probe.FreeSpace(folder) is not { } space || !drives.Add(space.Drive))
            {
                continue;
            }

            if (space.FreeBytes < LowDiskSpaceBytes)
            {
                notices.Add(new OngoingNotice(
                    $"low-disk-{label}",
                    space.FreeBytes < 1024L * 1024 * 1024 ? NoticeLevels.Error : NoticeLevels.Warn,
                    $"Low disk space: {FormatBytes(space.FreeBytes)} free",
                    $"Download folder ({label}): {folder}",
                    "/activity",
                    false));
            }
        }
    }

    private void AddFailedNotices(List<OngoingNotice> notices, IReadOnlyList<DownloadItem> active)
    {
        var cutoff = time.GetUtcNow() - FailedNoticeLifetime;
        foreach (var failed in _failed.Values)
        {
            if (failed.At < cutoff || active.Any(a => a.Id == failed.Id))
            {
                _failed.TryRemove(failed.Id, out _);
            }
        }

        foreach (var failed in _failed.Values.OrderByDescending(f => f.At).Take(MaxFailedNotices))
        {
            notices.Add(new OngoingNotice(
                $"download-failed-{failed.Id}",
                NoticeLevels.Error,
                $"Download failed: {failed.Title}",
                failed.Error ?? "See the download history for details.",
                "/activity?tab=history",
                true));
        }
    }

    // ---- Events ----

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lastJob = (jobs.Status.Running, jobs.Status.Queued > 0);
        lock (_subscriptions)
        {
            _subscriptions.Add(events.Subscribe<JobsChanged>(OnJobsChanged));
            _subscriptions.Add(events.Subscribe<IndexersChanged>((_, ct) => events.PublishAsync(new OngoingChanged(), ct)));
            _subscriptions.Add(events.Subscribe<DownloadChanged>(OnDownloadChanged));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        IDisposable[] subscriptions;
        lock (_subscriptions)
        {
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>Only a change of the running job (or the queue becoming empty or busy) is news; plain enqueues are not.</summary>
    private ValueTask OnJobsChanged(JobsChanged e, CancellationToken cancellationToken)
    {
        var next = (e.Status.Running, e.Status.Queued > 0);
        lock (_jobLock)
        {
            if (next == _lastJob)
            {
                return ValueTask.CompletedTask;
            }

            _lastJob = next;
        }

        return events.PublishAsync(new OngoingChanged(), cancellationToken);
    }

    /// <summary>Remembers failed downloads for a day, for the "Download failed" notices; a retry removes them.</summary>
    private ValueTask OnDownloadChanged(DownloadChanged e, CancellationToken cancellationToken)
    {
        var item = services.GetService<IDownloadQueue>()?.Find(e.DownloadId);
        if (item?.Status == DownloadStatus.Failed)
        {
            _failed[item.Id] = new FailedDownload(item.Id, item.Title, item.Error, item.Completed ?? time.GetUtcNow());
            return events.PublishAsync(new OngoingChanged(), cancellationToken);
        }

        if (_failed.TryRemove(e.DownloadId, out _))
        {
            return events.PublishAsync(new OngoingChanged(), cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    // ---- Formatting ----

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Percent(double value) => $"{Math.Floor(Math.Clamp(value, 0, 1) * 100).ToString(CultureInfo.InvariantCulture)} %";

    /// <summary>Same units as the web UI (1024 based, one decimal below 100).</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        if (bytes <= 0)
        {
            return "0 B";
        }

        var exponent = Math.Min((int)Math.Floor(Math.Log(bytes) / Math.Log(1024)), units.Length - 1);
        var value = bytes / Math.Pow(1024, exponent);
        var text = value >= 100 || exponent == 0
            ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{text} {units[exponent]}";
    }

    public static string TimeLeft(double seconds) => seconds < 60 ? "under a minute left" : $"{Duration(TimeSpan.FromSeconds(seconds))} left";

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
        var minutes = span.Minutes;
        return minutes == 0 ? $"{hours} h" : $"{hours} h {minutes} min";
    }

    /// <summary>"21:30", or "Mon 21:30" when it is not today (server time zone).</summary>
    private string LocalTime(DateTimeOffset at, DateTimeOffset now)
    {
        var zone = time.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        return local.Date == today.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }
}
