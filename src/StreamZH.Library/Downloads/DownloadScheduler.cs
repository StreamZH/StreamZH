// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Library.Search;
using StreamZH.Library.Usenet;

namespace StreamZH.Library.Downloads;

/// <summary>What a scheduler rule does (SABnzbd's Config → Scheduling).</summary>
public enum ScheduleAction
{
    /// <summary>Pause the whole queue.</summary>
    Pause,
    Resume,

    /// <summary>Set the speed limit: KB/s ("500"), percent of the maximum line speed ("50%"), or "0" for unlimited.</summary>
    SpeedLimit,

    /// <summary>Disable a Usenet server (argument: its id).</summary>
    PauseServer,
    ResumeServer,

    /// <summary>Stop downloads of a category (argument: its name).</summary>
    PauseCategory,
    ResumeCategory,
}

/// <summary>"At HH:MM on these days do this". <see cref="Days"/>: Monday … Sunday.</summary>
public sealed record DownloadScheduleRule(int Id, bool Enabled, TimeOnly Time, IReadOnlyList<DayOfWeek> Days, ScheduleAction Action, string Argument)
{
    public static readonly IReadOnlyList<DayOfWeek> Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>Rules that set the same thing (the queue, the speed, one server, one category) share a key; the latest one wins.</summary>
    public string StateKey => Action switch
    {
        ScheduleAction.Pause or ScheduleAction.Resume => "queue",
        ScheduleAction.SpeedLimit => "speed",
        ScheduleAction.PauseServer or ScheduleAction.ResumeServer => $"server:{Argument}",
        _ => $"category:{Argument.ToLowerInvariant()}",
    };

    /// <summary>When the rule fires within (<paramref name="from"/>, <paramref name="to"/>], in local time of <paramref name="zone"/>.</summary>
    public IEnumerable<DateTimeOffset> Occurrences(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var first = TimeZoneInfo.ConvertTime(from, zone).Date.AddDays(-1);
        var last = TimeZoneInfo.ConvertTime(to, zone).Date;
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (!Days.Contains(day.DayOfWeek))
            {
                continue;
            }

            var local = day.Add(Time.ToTimeSpan());
            if (zone.IsInvalidTime(local))
            {
                local = local.AddHours(1); // skipped by the clock change: run right after it
            }

            var at = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (at > from && at <= to)
            {
                yield return at;
            }
        }
    }
}

/// <summary>Stores and validates the scheduler rules.</summary>
public sealed class DownloadScheduleService(
    DownloadScheduleRepository repository,
    DownloadCategoryService categories,
    DownloadSettings settings,
    IServiceProvider services,
    ILogger<DownloadScheduleService> logger)
{
    public const int MaxRules = 100;

    public IReadOnlyList<DownloadScheduleRule> All() => [.. repository.All().Select(ToRule)];

    public DownloadScheduleRule? Find(int id) => repository.Find(id) is { } record ? ToRule(record) : null;

    /// <exception cref="ValidationFailedException">Invalid fields.</exception>
    public DownloadScheduleRule Create(DownloadScheduleRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (repository.All().Count >= MaxRules)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["action"] = [$"At most {MaxRules} rules."] });
        }

        var normalized = Validate(rule with { Id = 0 });
        var id = repository.Add(ToRecord(normalized));
        logger.LogInformation("Scheduler rule added: {Rule}", Describe(normalized));
        return Find(id)!;
    }

    /// <exception cref="LibraryException">Not found.</exception>
    /// <exception cref="ValidationFailedException">Invalid fields.</exception>
    public DownloadScheduleRule Update(int id, DownloadScheduleRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _ = repository.Find(id) ?? throw new LibraryException("Scheduler rule not found.", LibraryError.NotFound);
        var normalized = Validate(rule with { Id = id });
        repository.Update(ToRecord(normalized));
        logger.LogInformation("Scheduler rule saved: {Rule}", Describe(normalized));
        return Find(id)!;
    }

    /// <exception cref="LibraryException">Not found.</exception>
    public void Delete(int id)
    {
        if (!repository.Delete(id))
        {
            throw new LibraryException("Scheduler rule not found.", LibraryError.NotFound);
        }
    }

    /// <summary>The speed limit in KB/s a speed rule sets right now (percent rules need the maximum line speed), or null.</summary>
    public long? SpeedOf(string argument)
    {
        var text = argument.Trim();
        if (text.EndsWith('%'))
        {
            return int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent is >= 1 and <= 100 && settings.MaxLineSpeedKBps > 0
                ? settings.MaxLineSpeedKBps * percent / 100
                : null;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var kbps) && kbps <= DownloadSettings.MaxSpeedLimitKBps ? kbps : null;
    }

    public static string Describe(DownloadScheduleRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var days = rule.Days.Count == 7 ? "every day" : string.Join(", ", rule.Days.Select(d => d.ToString()[..3]));
        var argument = rule.Argument.Length > 0 ? $" {rule.Argument}" : string.Empty;
        return $"{rule.Time.ToString("HH:mm", CultureInfo.InvariantCulture)} {days}: {DownloadOptionValues.Of(rule.Action)}{argument}";
    }

    private DownloadScheduleRule Validate(DownloadScheduleRule rule)
    {
        var errors = new Dictionary<string, string[]>();
        var days = DownloadScheduleRule.Week.Where(rule.Days.Contains).ToList();
        if (days.Count == 0)
        {
            errors["days"] = ["Choose at least one day."];
        }

        if (!Enum.IsDefined(rule.Action))
        {
            errors["action"] = [$"Use one of: {DownloadOptionValues.Names<ScheduleAction>()}."];
        }

        var argument = rule.Argument?.Trim() ?? string.Empty;
        switch (rule.Action)
        {
            case ScheduleAction.Pause or ScheduleAction.Resume:
                argument = string.Empty;
                break;
            case ScheduleAction.SpeedLimit:
                if (argument.EndsWith('%'))
                {
                    if (!int.TryParse(argument[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var percent) || percent is < 1 or > 100)
                    {
                        errors["argument"] = ["Use 1% to 100%, or KB/s (0 = unlimited)."];
                    }
                    else if (settings.MaxLineSpeedKBps <= 0)
                    {
                        errors["argument"] = ["Set the maximum line speed (Special) first, or use KB/s."];
                    }
                }
                else if (SpeedOf(argument) is null)
                {
                    errors["argument"] = [$"Use KB/s (0 = unlimited, at most {DownloadSettings.MaxSpeedLimitKBps.ToString("N0", CultureInfo.InvariantCulture)}) or a percentage like 50%."];
                }

                break;
            case ScheduleAction.PauseServer or ScheduleAction.ResumeServer:
                var server = int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var serverId)
                    ? services.GetService<UsenetServerService>()?.Find(serverId)
                    : null;
                if (server is null)
                {
                    errors["argument"] = ["Choose a Usenet server."];
                }

                break;
            case ScheduleAction.PauseCategory or ScheduleAction.ResumeCategory:
                if (categories.Find(argument) is { } category)
                {
                    argument = category.Name;
                }
                else
                {
                    errors["argument"] = ["Choose a download category."];
                }

                break;
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return rule with { Days = days, Argument = argument, Time = new TimeOnly(rule.Time.Hour, rule.Time.Minute) };
    }

    private static DownloadScheduleRule ToRule(DownloadScheduleRecord r) => new(
        r.Id,
        r.Enabled,
        new TimeOnly(Math.Clamp(r.Minute, 0, 1439) / 60, Math.Clamp(r.Minute, 0, 1439) % 60),
        [.. DownloadScheduleRule.Week.Where((_, i) => (r.Days & (1 << i)) != 0)],
        DownloadOptionValues.Parse<ScheduleAction>(r.Action) ?? ScheduleAction.Pause,
        r.Argument);

    private static DownloadScheduleRecord ToRecord(DownloadScheduleRule rule)
    {
        var mask = 0;
        for (var i = 0; i < DownloadScheduleRule.Week.Count; i++)
        {
            if (rule.Days.Contains(DownloadScheduleRule.Week[i]))
            {
                mask |= 1 << i;
            }
        }

        return new DownloadScheduleRecord(rule.Id, rule.Enabled, (rule.Time.Hour * 60) + rule.Time.Minute, mask, DownloadOptionValues.Of(rule.Action), rule.Argument);
    }
}

/// <summary>
/// Runs the scheduler rules (minute resolution). At startup the latest rule of each kind within the last week is applied
/// once (so "pause at 8:00, resume at 18:00" is paused after a start at noon); after that each rule runs when its time
/// comes. A resume at startup only lifts a pause the scheduler made.
/// </summary>
public sealed class DownloadScheduler(
    DownloadScheduleService schedules,
    DownloadEngine engine,
    DownloadSettings settings,
    IServiceProvider services,
    DownloadEngineOptions options,
    ILogger<DownloadScheduler> logger) : BackgroundService
{
    private DateTimeOffset? _last;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ApplyLatestAsync(options.Time.GetUtcNow());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The scheduler could not apply its rules at startup");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = options.Time.GetUtcNow();
            var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(1).AddSeconds(1);
            try
            {
                await Task.Delay(next - now, options.Time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunDueAsync(options.Time.GetUtcNow());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A scheduler rule failed");
            }
        }
    }

    /// <summary>Runs the enabled rules whose time lies since the last call (first call: since a minute ago). Returns how many ran.</summary>
    public async Task<int> RunDueAsync(DateTimeOffset now)
    {
        var from = _last ?? now.AddMinutes(-1);
        _last = now;
        if (now <= from)
        {
            return 0;
        }

        var zone = options.Time.LocalTimeZone;
        var due = schedules.All().Where(r => r.Enabled)
            .SelectMany(r => r.Occurrences(from, now, zone).Select(at => (Rule: r, At: at)))
            .OrderBy(x => x.At).ThenBy(x => x.Rule.Id)
            .ToList();
        foreach (var (rule, _) in due)
        {
            await ApplyAsync(rule, startup: false);
        }

        return due.Count;
    }

    /// <summary>Applies the latest enabled rule of each kind from the last 7 days (startup).</summary>
    public async Task ApplyLatestAsync(DateTimeOffset now)
    {
        _last = now;
        var zone = options.Time.LocalTimeZone;
        var latest = schedules.All().Where(r => r.Enabled)
            .SelectMany(r => r.Occurrences(now.AddDays(-7), now, zone).Select(at => (Rule: r, At: at)))
            .GroupBy(x => x.Rule.StateKey)
            .Select(g => g.OrderBy(x => x.At).ThenBy(x => x.Rule.Id).Last().Rule)
            .ToList();
        foreach (var rule in latest)
        {
            await ApplyAsync(rule, startup: true);
        }
    }

    private async Task ApplyAsync(DownloadScheduleRule rule, bool startup)
    {
        logger.LogInformation("Scheduler{Startup}: {Rule}", startup ? " (startup)" : string.Empty, DownloadScheduleService.Describe(rule));
        switch (rule.Action)
        {
            case ScheduleAction.Pause:
                await engine.PauseAllAsync(bySchedule: true);
                break;
            case ScheduleAction.Resume:
                if (!startup || engine.PausedBySchedule)
                {
                    await engine.ResumeAllAsync();
                }

                break;
            case ScheduleAction.SpeedLimit:
                if (schedules.SpeedOf(rule.Argument) is { } kbps)
                {
                    settings.SetSpeedLimit(kbps);
                }
                else
                {
                    logger.LogWarning("Scheduler: speed limit {Argument} cannot be set (is the maximum line speed set?)", rule.Argument);
                }

                break;
            case ScheduleAction.PauseServer or ScheduleAction.ResumeServer:
                SetServerEnabled(rule.Argument, rule.Action == ScheduleAction.ResumeServer);
                break;
            case ScheduleAction.PauseCategory or ScheduleAction.ResumeCategory:
                await engine.SetCategoryPausedAsync(rule.Argument, rule.Action == ScheduleAction.PauseCategory);
                break;
        }
    }

    private void SetServerEnabled(string argument, bool enabled)
    {
        if (services.GetService<UsenetServerService>() is not { } servers
            || !int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || servers.Find(id) is not { } server)
        {
            logger.LogWarning("Scheduler: Usenet server {Id} does not exist any more", argument);
            return;
        }

        if (server.Enabled != enabled)
        {
            servers.Update(id, server with { Enabled = enabled });
        }
    }
}

/// <summary>"Keep history" of Settings → Downloads: removes old downloads from the history every hour (off while both limits are 0).</summary>
public sealed class DownloadHistoryCleanupTask(DownloadSettings settings) : IScheduledTask
{
    public string Key => "download-history-cleanup";

    public string Name => "Download history cleanup";

    public TimeSpan? Interval => settings.HistoryKeepDays > 0 || settings.HistoryKeepItems > 0 ? TimeSpan.FromHours(1) : null;

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<DownloadEngine>().CleanupHistoryAsync();
}
