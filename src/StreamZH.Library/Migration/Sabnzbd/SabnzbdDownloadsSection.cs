// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Data;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;
using StreamZH.Library.Usenet;

namespace StreamZH.Library.Migration.Sabnzbd;

/// <summary>
/// SABnzbd's download settings → Settings → Downloads: speed limit and line speed, the incomplete/complete/NZB backup
/// folders (through the path mappings), the scheduler (pause, resume, speed limit, servers, categories) and the switches
/// StreamZH has too (abort hopeless jobs, ignore samples, unwanted extensions, duplicates, …). Settings StreamZH does not
/// have are listed as "not taken over". Every setting is one item, so each can be left out.
/// </summary>
public sealed class SabnzbdDownloadsSection : IMigrationSection
{
    public const string SectionKey = "downloads";

    public string Source => MigrationSources.Sabnzbd;

    public string Key => SectionKey;

    public string Label => "Download settings";

    public string Description => "Speed limit, download folders, the scheduler and switches like \"abort jobs that cannot be completed\" or unwanted extensions.";

    public int Order => 365;

    public bool DefaultSelected => true;

    public bool Advanced => false;

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var config = await context.Services.GetRequiredService<SabnzbdApiClient>().ConfigAsync(context.Connection, cancellationToken);
        var plan = Plan(config, context, preview: true);
        var warnings = new List<string>();
        if (plan.Any(p => p.Folder is not null && p.Item.Action != MigrationAction.Skip))
        {
            warnings.Add("While both run, StreamZH and SABnzbd use the same download folders. That is fine: each one only touches its own downloads.");
        }

        return new MigrationSectionPlan([.. plan.Select(p => p.Item)], warnings);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        var config = await context.Services.GetRequiredService<SabnzbdApiClient>().ConfigAsync(context.Connection, cancellationToken);
        var settings = context.Services.GetRequiredService<DownloadSettings>();
        var plan = Plan(config, context, preview: false);
        var done = 0;
        foreach (var step in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(done++, plan.Count, step.Item.Label);
            var item = step.Item;
            if (item.Action == MigrationAction.Skip)
            {
                run.Skipped(item.Label, item.Reason ?? "Not taken over.");
                continue;
            }

            if (context.IsExcluded(item.Key))
            {
                run.Skipped(item.Label, "Left out in the wizard.");
                continue;
            }

            if (item.Action == MigrationAction.Link)
            {
                run.Linked(item.Label);
                continue;
            }

            try
            {
                if (step.Change is { } change)
                {
                    settings.Update(change);
                    run.Updated(item.Label);
                }
                else if (step.Rule is { } rule)
                {
                    context.Services.GetRequiredService<DownloadScheduleService>().Create(rule);
                    run.Created(item.Label);
                }
                else
                {
                    run.Skipped(item.Label, "Not taken over.");
                }
            }
            catch (ValidationFailedException ex)
            {
                run.Failed(item.Label, string.Join(" ", ex.Errors.Values.SelectMany(v => v)));
            }
        }

        run.Progress(plan.Count, plan.Count);
    }

    /// <summary>One setting: the wizard item and what the run changes (a settings change or a new scheduler rule).</summary>
    private sealed record Step(MigrationItem Item, DownloadSettingsChange? Change = null, DownloadScheduleRule? Rule = null, string? Folder = null);

    private static List<Step> Plan(SabnzbdConfig config, MigrationContext context, bool preview)
    {
        var settings = context.Services.GetRequiredService<DownloadSettings>();
        var steps = new List<Step>();
        Speed(config, settings, steps);
        Folders(config, settings, context, steps);
        Switches(config, settings, steps);
        Schedules(config, settings, context, preview, steps);
        NotTakenOver(config, steps);
        return steps;
    }

    private static string ItemKey(string name) => $"{SectionKey}:{name}";

    // ---- Speed ----

    private static void Speed(SabnzbdConfig config, DownloadSettings settings, List<Step> steps)
    {
        // bandwidth_max: the line speed ("50M" bytes/s); bandwidth_perc: the limit in percent of it (100 or 0 = none).
        var line = SabnzbdConfig.FromUnits(config.Misc("bandwidth_max")) is { } bytes and > 0 ? (long)Math.Round(bytes / 1024) : 0;
        var percent = config.MiscInt("bandwidth_perc") ?? 100;
        var limit = line > 0 && percent is > 0 and < 100 ? line * percent / 100 : 0;
        var detail = line == 0
            ? "no maximum line speed set in SABnzbd: no speed limit"
            : limit > 0
                ? $"line speed {Speed(line)}, limit {percent.ToString(CultureInfo.InvariantCulture)} % = {Speed(limit)}"
                : $"line speed {Speed(line)}, no limit";
        var same = settings.MaxLineSpeedKBps == line && settings.SpeedLimitKBps == limit;
        steps.Add(new Step(
            new MigrationItem(ItemKey("speed"), "Speed limit", same ? MigrationAction.Link : MigrationAction.Update, same ? detail + " · same as in StreamZH" : detail),
            new DownloadSettingsChange { MaxLineSpeedKBps = line, SpeedLimitKBps = limit }));
    }

    private static string Speed(long kbps) => kbps >= 1024
        ? $"{(kbps / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} MB/s"
        : $"{kbps.ToString(CultureInfo.InvariantCulture)} KB/s";

    // ---- Folders ----

    private static void Folders(SabnzbdConfig config, DownloadSettings settings, MigrationContext context, List<Step> steps)
    {
        Folder("download_dir", "Incomplete folder", settings.IncompleteFolder, path => new DownloadSettingsChange { IncompleteFolder = path });
        Folder("complete_dir", "Completed folder", settings.CompleteFolder, path => new DownloadSettingsChange { CompleteFolder = path });
        Folder("nzb_backup_dir", "NZB backup folder", settings.NzbBackupFolder, path => new DownloadSettingsChange { NzbBackupFolder = path });

        void Folder(string key, string label, string? current, Func<string, DownloadSettingsChange> change)
        {
            var remote = config.Misc(key).Trim();
            if (remote.Length == 0)
            {
                return;
            }

            if (!SabnzbdConfig.IsAbsolute(remote))
            {
                steps.Add(new Step(new MigrationItem(
                    ItemKey(key), label, MigrationAction.Skip, remote,
                    $"\"{remote}\" is relative to SABnzbd's home folder; set the folder in Settings → Downloads.")));
                return;
            }

            context.Paths.Use(remote);
            var local = context.Paths.Map(remote);
            if (current is not null && PathComparer.Instance.Equals(Path.TrimEndingDirectorySeparator(local), Path.TrimEndingDirectorySeparator(current)))
            {
                steps.Add(new Step(new MigrationItem(ItemKey(key), label, MigrationAction.Link, $"{local} · same as in StreamZH"), Folder: local));
                return;
            }

            if (!context.Paths.FolderExists(local))
            {
                steps.Add(new Step(new MigrationItem(
                    ItemKey(key), label, MigrationAction.Skip, remote == local ? remote : $"{remote} → {local}",
                    $"StreamZH cannot see {local}. Add a path mapping for {remote} and preview again."), Folder: local));
                return;
            }

            steps.Add(new Step(
                new MigrationItem(ItemKey(key), label, MigrationAction.Update, remote == local ? local : $"{remote} → {local}"),
                change(local),
                Folder: local));
        }
    }

    // ---- Switches ----

    private static void Switches(SabnzbdConfig config, DownloadSettings settings, List<Step> steps)
    {
        Flag("fail_hopeless_jobs", "Abort jobs that cannot be completed", settings.AbortHopeless, on => new() { AbortHopeless = on });
        Flag("ignore_samples", "Ignore samples", settings.PostProcess.IgnoreSamples, on => new() { IgnoreSamples = on });
        Flag("pre_check", "Check before download", settings.PreCheck, on => new() { PreCheck = on });
        Flag("enable_recursive", "Unpack archives inside archives", settings.PostProcess.RecursiveUnpack, on => new() { RecursiveUnpack = on });
        Flag("enable_all_par", "Download all par2 files", settings.DownloadAllPar2, on => new() { DownloadAllPar2 = on });
        Flag("safe_postproc", "Only unpack verified downloads", settings.OnlyVerified, on => new() { OnlyVerified = on });
        Flag("deobfuscate_final_filenames", "Deobfuscate final file names", settings.PostProcess.Deobfuscate, on => new() { Deobfuscate = on });
        Flag("folder_rename", "Temporary folder name while unpacking", settings.TemporaryFolderName, on => new() { TemporaryFolderName = on });
        Flag("start_paused", "Pause downloading on start", settings.PauseOnStart, on => new() { PauseOnStart = on });

        Number("propagation_delay", "Propagation delay", (int)settings.PropagationDelay.TotalMinutes, 0, DownloadSettings.MaxPropagationDelayMinutes,
            n => new() { PropagationDelayMinutes = n }, n => n == 0 ? "off" : $"{n.ToString(CultureInfo.InvariantCulture)} minutes");
        Number("max_art_tries", "Article tries per server", settings.ArticleTries, 1, DownloadSettings.MaxArticleTries,
            n => new() { ArticleTries = n }, n => $"{n.ToString(CultureInfo.InvariantCulture)} tries");

        // no_dupes: 0 off, 1 discard, 2 pause, 3 fail to the history, 4 tag.
        if (config.MiscInt("no_dupes") is { } dupes)
        {
            var action = dupes switch { 1 or 3 => DuplicateAction.Fail, 2 => DuplicateAction.Pause, 4 => DuplicateAction.Tag, _ => DuplicateAction.Off };
            Choice("no_dupes", "Detect duplicate downloads", action, settings.DuplicateAction, v => new() { DuplicateAction = DownloadOptionValues.Of(v) });
        }

        // pause_on_pwrar: 0 off, 1 pause, 2 abort.
        if (config.MiscInt("pause_on_pwrar") is { } encrypted)
        {
            var action = encrypted switch { 1 => EncryptedAction.Pause, 2 => EncryptedAction.Fail, _ => EncryptedAction.Off };
            Choice("pause_on_pwrar", "Encrypted RAR downloads", action, settings.EncryptedAction, v => new() { EncryptedAction = DownloadOptionValues.Of(v) });
        }

        if (AutoSort(config.Misc("auto_sort")) is { } sort)
        {
            Choice("auto_sort", "Sort the queue automatically", sort, settings.AutoSort, v => new() { AutoSort = DownloadOptionValues.Of(v) });
        }

        Unwanted(config, settings, steps);

        if (config.HasMisc("cleanup_list"))
        {
            var list = config.MiscList("cleanup_list");
            var parsed = ExtensionList.Parse(string.Join(", ", list), out var invalid);
            if (parsed is null)
            {
                steps.Add(new Step(new MigrationItem(ItemKey("cleanup_list"), "Cleanup list", MigrationAction.Skip, string.Join(", ", list), $"\"{invalid}\" is not a file extension StreamZH can use.")));
            }
            else
            {
                var text = ExtensionList.Format(parsed);
                var same = text == settings.CleanupListText;
                steps.Add(new Step(
                    new MigrationItem(ItemKey("cleanup_list"), "Cleanup list", same ? MigrationAction.Link : MigrationAction.Update, text.Length == 0 ? "empty" : text),
                    new DownloadSettingsChange { CleanupList = text }));
            }
        }

        if (SabnzbdConfig.FromUnits(config.Misc("download_free")) is { } free)
        {
            var gb = Math.Round(free / (1024d * 1024 * 1024), 2);
            var same = Math.Abs(gb - settings.MinFreeSpaceGb) < 0.005;
            steps.Add(new Step(
                new MigrationItem(ItemKey("download_free"), "Pause when free space is below", same ? MigrationAction.Link : MigrationAction.Update,
                    $"{gb.ToString("0.##", CultureInfo.InvariantCulture)} GB"),
                new DownloadSettingsChange { MinFreeSpaceGb = gb }));
        }

        void Flag(string key, string label, bool current, Func<bool, DownloadSettingsChange> change)
        {
            if (config.MiscBool(key) is not { } on)
            {
                return;
            }

            var same = on == current;
            steps.Add(new Step(new MigrationItem(ItemKey(key), label, same ? MigrationAction.Link : MigrationAction.Update, (on ? "on" : "off") + (same ? " · same as in StreamZH" : string.Empty)), change(on)));
        }

        void Number(string key, string label, int current, int min, int max, Func<int, DownloadSettingsChange> change, Func<int, string> show)
        {
            if (config.MiscInt(key) is not { } value)
            {
                return;
            }

            var n = Math.Clamp(value, min, max);
            var same = n == current;
            steps.Add(new Step(new MigrationItem(ItemKey(key), label, same ? MigrationAction.Link : MigrationAction.Update, show(n) + (same ? " · same as in StreamZH" : string.Empty)), change(n)));
        }

        void Choice<T>(string key, string label, T value, T current, Func<T, DownloadSettingsChange> change)
            where T : struct, Enum
        {
            var same = EqualityComparer<T>.Default.Equals(value, current);
            steps.Add(new Step(
                new MigrationItem(ItemKey(key), label, same ? MigrationAction.Link : MigrationAction.Update, DownloadOptionValues.Of(value) + (same ? " · same as in StreamZH" : string.Empty)),
                change(value)));
        }
    }

    /// <summary>auto_sort of SABnzbd 4 ("avg_age asc", "remaining desc", "name asc"), or the old 0/1 switch.</summary>
    private static QueueSort? AutoSort(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        return text switch
        {
            "" or "0" => QueueSort.Off,
            "1" => QueueSort.Oldest,
            _ when text.StartsWith("avg_age", StringComparison.Ordinal) => text.EndsWith("desc", StringComparison.Ordinal) ? QueueSort.Oldest : QueueSort.Newest,
            _ when text.StartsWith("remaining", StringComparison.Ordinal) || text.StartsWith("size", StringComparison.Ordinal) =>
                text.EndsWith("desc", StringComparison.Ordinal) ? QueueSort.Largest : QueueSort.Smallest,
            _ when text.StartsWith("name", StringComparison.Ordinal) => QueueSort.Name,
            _ => null,
        };
    }

    private static void Unwanted(SabnzbdConfig config, DownloadSettings settings, List<Step> steps)
    {
        if (!config.HasMisc("unwanted_extensions") && !config.HasMisc("action_on_unwanted_extensions"))
        {
            return;
        }

        var key = ItemKey("unwanted_extensions");
        var list = config.MiscList("unwanted_extensions");
        if (config.MiscInt("unwanted_extensions_mode") == 1)
        {
            steps.Add(new Step(new MigrationItem(key, "Unwanted extensions", MigrationAction.Skip, string.Join(", ", list),
                "SABnzbd uses the list as a whitelist (only these extensions are wanted); StreamZH only has a blacklist.")));
            return;
        }

        // action_on_unwanted_extensions: 0 off, 1 pause, 2 abort.
        var action = config.MiscInt("action_on_unwanted_extensions") switch { 1 => UnwantedAction.Pause, 2 => UnwantedAction.Fail, _ => UnwantedAction.Off };
        var parsed = list.Count == 0 ? settings.UnwantedExtensions : ExtensionList.Parse(string.Join(", ", list), out _);
        if (parsed is null)
        {
            steps.Add(new Step(new MigrationItem(key, "Unwanted extensions", MigrationAction.Skip, string.Join(", ", list), "The list has entries that are not file extensions.")));
            return;
        }

        var text = ExtensionList.Format(parsed);
        var same = action == settings.UnwantedAction && text == settings.UnwantedExtensionsText;
        var detail = action == UnwantedAction.Off ? $"off ({(text.Length == 0 ? "no extensions" : text)})" : $"{DownloadOptionValues.Of(action)}: {text}";
        steps.Add(new Step(
            new MigrationItem(key, "Unwanted extensions", same ? MigrationAction.Link : MigrationAction.Update, detail + (same ? " · same as in StreamZH" : string.Empty)),
            new DownloadSettingsChange { UnwantedExtensions = text, UnwantedAction = DownloadOptionValues.Of(action) }));
    }

    // ---- Scheduler ----

    private static void Schedules(SabnzbdConfig config, DownloadSettings settings, MigrationContext context, bool preview, List<Step> steps)
    {
        var lines = config.MiscList("schedlines");
        if (lines.Count == 0)
        {
            return;
        }

        var schedules = context.Services.GetRequiredService<DownloadScheduleService>();
        var categories = context.Services.GetRequiredService<DownloadCategoryService>();
        var servers = context.Services.GetService<UsenetServerService>()?.All() ?? [];
        var existing = schedules.All();

        // The speed rules in percent need the line speed this section sets first.
        var line = SabnzbdConfig.FromUnits(config.Misc("bandwidth_max")) is { } b and > 0 ? (long)Math.Round(b / 1024) : settings.MaxLineSpeedKBps;
        for (var i = 0; i < lines.Count; i++)
        {
            var key = ItemKey($"schedule:{i.ToString(CultureInfo.InvariantCulture)}");
            var text = lines[i].Trim();
            var parsed = ParseSchedule(text);
            if (parsed is null)
            {
                steps.Add(new Step(new MigrationItem(key, text, MigrationAction.Skip, null, "SABnzbd's scheduler line could not be read.")));
                continue;
            }

            var (enabled, time, days, action, argument) = parsed.Value;
            var label = $"{time.ToString("HH:mm", CultureInfo.InvariantCulture)} {(days.Count == 7 ? "every day" : string.Join(", ", days.Select(d => d.ToString()[..3])))}: {action}{(argument.Length > 0 ? " " + argument : string.Empty)}";
            ScheduleAction? target = null;
            string? targetArgument = null;
            string? reason = null;
            string? pending = null;
            switch (action)
            {
                case "pause":
                    target = ScheduleAction.Pause;
                    targetArgument = string.Empty;
                    break;
                case "resume":
                    target = ScheduleAction.Resume;
                    targetArgument = string.Empty;
                    break;
                case "speedlimit":
                    target = ScheduleAction.SpeedLimit;
                    targetArgument = SpeedArgument(argument, line, out reason);
                    break;
                case "enable_server" or "disable_server":
                    target = action == "enable_server" ? ScheduleAction.ResumeServer : ScheduleAction.PauseServer;
                    var id = context.Ids.Get("usenetServer", argument)
                        ?? (config.Servers.FirstOrDefault(s => string.Equals(s.Name, argument, StringComparison.OrdinalIgnoreCase)) is { } sab
                            ? SabnzbdServersSection.Match(servers, sab)?.Id
                            : null);
                    targetArgument = id?.ToString(CultureInfo.InvariantCulture);

                    // In the preview the server may still come with the "Usenet servers" part of this run.
                    if (id is null && preview && config.Servers.Any(s => string.Equals(s.Name, argument, StringComparison.OrdinalIgnoreCase)))
                    {
                        targetArgument = argument;
                        pending = "once the Usenet servers are taken over";
                    }

                    reason = targetArgument is null ? $"The server \"{argument}\" is not in StreamZH; take over the Usenet servers too." : null;
                    break;
                case "pause_cat" or "resume_cat":
                    target = action == "pause_cat" ? ScheduleAction.PauseCategory : ScheduleAction.ResumeCategory;
                    var name = SabnzbdCategoriesSection.TargetName(argument);
                    targetArgument = categories.Find(name)?.Name;
                    if (targetArgument is null && preview && config.Categories.Any(c => string.Equals(c.Name, argument, StringComparison.OrdinalIgnoreCase)))
                    {
                        targetArgument = name;
                        pending = "once the categories are taken over";
                    }

                    reason = targetArgument is null ? $"The category \"{argument}\" is not in StreamZH; take over the categories too." : null;
                    break;
                default:
                    reason = $"StreamZH's scheduler has no \"{action}\".";
                    break;
            }

            if (target is null || targetArgument is null || reason is not null)
            {
                steps.Add(new Step(new MigrationItem(key, label, MigrationAction.Skip, text, reason ?? "Not taken over.")));
                continue;
            }

            var rule = new DownloadScheduleRule(0, enabled, time, days, target.Value, targetArgument);
            if (pending is not null)
            {
                steps.Add(new Step(new MigrationItem(key, label, MigrationAction.Create, $"{label} ({pending})")));
                continue;
            }

            var same = existing.Any(r => r.Time == rule.Time && r.Action == rule.Action && r.Days.Order().SequenceEqual(rule.Days.Order())
                && string.Equals(r.Argument, rule.Argument, StringComparison.OrdinalIgnoreCase));
            var detail = DownloadScheduleService.Describe(rule) + (enabled ? string.Empty : " (switched off)");
            steps.Add(same
                ? new Step(new MigrationItem(key, label, MigrationAction.Link, detail + " · already in StreamZH"))
                : new Step(new MigrationItem(key, label, MigrationAction.Create, detail), Rule: rule));
        }
    }

    /// <summary>"1 30 7 12345 pause" = on, 07:30, Monday to Friday, pause. Days: 1 Monday … 7 Sunday; anything else = every day.</summary>
    public static (bool Enabled, TimeOnly Time, IReadOnlyList<DayOfWeek> Days, string Action, string Argument)? ParseSchedule(string line)
    {
        var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 5
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute) || minute > 59
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var hour) || hour > 23)
        {
            return null;
        }

        IReadOnlyList<DayOfWeek> days = parts[3].All(char.IsAsciiDigit)
            ? [.. parts[3].Select(c => c - '0').Where(d => d is >= 1 and <= 7).Distinct().Order().Select(d => d == 7 ? DayOfWeek.Sunday : (DayOfWeek)d)]
            : DownloadScheduleRule.Week;
        if (days.Count == 0)
        {
            days = DownloadScheduleRule.Week;
        }

        return (parts[0] == "1", new TimeOnly(hour, minute), days, parts[4].ToLowerInvariant(), parts.Length > 5 ? parts[5] : string.Empty);
    }

    /// <summary>SABnzbd: "50" or "50%" = percent of the line speed (1–100), "500K"/"5M" = absolute; "0" = no limit.</summary>
    private static string? SpeedArgument(string argument, long lineKbps, out string? reason)
    {
        reason = null;
        var text = argument.Trim();
        var percent = text.EndsWith('%');
        var number = percent ? text[..^1].Trim() : text;
        if (!percent && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) && plain is > 0 and < 101)
        {
            percent = true;
        }

        if (percent)
        {
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 100)
            {
                reason = $"\"{argument}\" is not a speed StreamZH understands.";
                return null;
            }

            if (lineKbps <= 0)
            {
                reason = "A speed limit in percent needs SABnzbd's maximum line speed, which is not set.";
                return null;
            }

            return string.Create(CultureInfo.InvariantCulture, $"{p}%");
        }

        if (SabnzbdConfig.FromUnits(text) is not { } bytes)
        {
            reason = $"\"{argument}\" is not a speed StreamZH understands.";
            return null;
        }

        return ((long)Math.Round(bytes / 1024)).ToString(CultureInfo.InvariantCulture);
    }

    // ---- Not taken over ----

    private static void NotTakenOver(SabnzbdConfig config, List<Step> steps)
    {
        Off("direct_unpack", "Direct Unpack", "StreamZH unpacks after the download.");
        Off("pause_on_post_processing", "Pause downloading during post-processing", "StreamZH keeps downloading while it unpacks.");
        Off("top_only", "Only get articles for the top of the queue", "StreamZH sets how many downloads run at once instead (Settings → Downloads → Queue).");
        Off("replace_spaces", "Replace spaces in folder names", "StreamZH names folders by itself when it imports.");
        Off("flat_unpack", "Ignore folders inside archives", "Not available in StreamZH.");

        if (config.Misc("dirscan_dir").Trim() is { Length: > 0 } watch)
        {
            steps.Add(new Step(new MigrationItem(ItemKey("dirscan_dir"), "Watched folder", MigrationAction.Skip, watch,
                "Both apps would pick up the same NZB files. Set the watched folder in Settings → Downloads once SABnzbd is stopped.")));
        }

        if (config.Misc("script_dir").Trim().Length > 0)
        {
            steps.Add(new Step(new MigrationItem(ItemKey("script_dir"), "Post-processing scripts", MigrationAction.Skip, config.Misc("script_dir"),
                "Scripts are not taken over; StreamZH imports and renames by itself (Settings → Connect runs your own scripts).")));
        }

        if (config.Misc("quota_size").Trim() is { Length: > 0 } quota)
        {
            steps.Add(new Step(new MigrationItem(ItemKey("quota_size"), "Download quota", MigrationAction.Skip, quota,
                "StreamZH keeps quotas per Usenet server; set one in Settings → Usenet.")));
        }

        if (config.RssFeeds > 0)
        {
            steps.Add(new Step(new MigrationItem(ItemKey("rss"), "RSS feeds", MigrationAction.Skip, $"{config.RssFeeds.ToString(CultureInfo.InvariantCulture)} feeds",
                "StreamZH finds releases through its indexers; take over the indexers from Sonarr, Radarr or Prowlarr.")));
        }

        if (config.ActiveSorters > 0)
        {
            steps.Add(new Step(new MigrationItem(ItemKey("sorters"), "Sorting", MigrationAction.Skip, $"{config.ActiveSorters.ToString(CultureInfo.InvariantCulture)} sorters",
                "StreamZH renames and sorts into the library by itself (Settings → Media Management).")));
        }

        void Off(string key, string label, string reason)
        {
            if (config.MiscBool(key) == true)
            {
                steps.Add(new Step(new MigrationItem(ItemKey(key), label, MigrationAction.Skip, "on in SABnzbd", reason)));
            }
        }
    }
}
