// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Downloads;
using StreamZH.Host.Auth;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;

namespace StreamZH.Host.Api;

/// <summary>
/// Settings → Downloads: the download client and engine settings (folders, queue and post-processing switches, history,
/// special), download categories and the scheduler.
/// </summary>
public static class DownloadSettingsEndpoints
{
    public static readonly IReadOnlyList<string> DayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    public static IEndpointRouteBuilder MapDownloadSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings/downloads").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapGet("/", (DownloadSettings downloads) => DownloadSettingsResource.From(downloads));

        group.MapPut("/", (DownloadSettingsChange request, DownloadSettings downloads, ILogger<DownloadSettings> logger) =>
        {
            downloads.Update(request);
            logger.LogInformation(
                "Download settings saved: {Client}, incomplete {Incomplete}, complete {Complete}, NZB folder {NzbFolder}, limit {Limit} KB/s, "
                + "min. free {MinFree} GB, keep files {KeepFiles}, abort hopeless {AbortHopeless}, {Tries} tries per article, watched folder {Watch}, "
                + "pre-check {PreCheck}, max. active {MaxActive}, duplicates {Duplicates}, post-processing workers {Workers}",
                DownloadSettings.ClientValue(downloads.Client),
                downloads.IncompleteFolder,
                downloads.CompleteFolder,
                downloads.NzbFolder,
                downloads.SpeedLimitKBps,
                downloads.MinFreeSpaceGb,
                downloads.KeepFiles,
                downloads.AbortHopeless,
                downloads.ArticleTries,
                downloads.WatchFolder ?? "off",
                downloads.PreCheck,
                downloads.MaxActiveDownloads,
                DownloadOptionValues.Of(downloads.DuplicateAction),
                downloads.PostProcessWorkers);
            return Results.Ok(DownloadSettingsResource.From(downloads));
        });

        group.MapGet("/categories", (DownloadCategoryService categories, DownloadEngine engine) =>
            CategoryResources(categories, engine));

        group.MapPost("/categories", (DownloadCategoryRequest request, DownloadCategoryService categories, DownloadEngine engine) =>
        {
            if (request.ToDefinition(0) is not { } definition)
            {
                return Results.ValidationProblem(request.Errors());
            }

            var created = categories.Create(definition);
            return Results.Created($"/api/v1/settings/downloads/categories/{created.Id}", DownloadCategoryResource.From(created, categories, engine));
        });

        group.MapPut("/categories/{id:int}", (int id, DownloadCategoryRequest request, DownloadCategoryService categories, DownloadEngine engine) =>
        {
            if (request.ToDefinition(id) is not { } definition)
            {
                return Results.ValidationProblem(request.Errors());
            }

            return Results.Ok(DownloadCategoryResource.From(categories.Update(id, definition), categories, engine));
        });

        group.MapDelete("/categories/{id:int}", (int id, DownloadCategoryService categories) =>
        {
            categories.Delete(id);
            return Results.NoContent();
        });

        group.MapGet("/schedules", (DownloadScheduleService schedules) => schedules.All().Select(DownloadScheduleResource.From).ToList());

        group.MapPost("/schedules", (DownloadScheduleRequest request, DownloadScheduleService schedules) =>
        {
            if (request.ToRule(0, out var errors) is not { } rule)
            {
                return Results.ValidationProblem(errors);
            }

            var created = schedules.Create(rule);
            return Results.Created($"/api/v1/settings/downloads/schedules/{created.Id}", DownloadScheduleResource.From(created));
        });

        group.MapPut("/schedules/{id:int}", (int id, DownloadScheduleRequest request, DownloadScheduleService schedules) =>
        {
            if (request.ToRule(id, out var errors) is not { } rule)
            {
                return Results.ValidationProblem(errors);
            }

            return Results.Ok(DownloadScheduleResource.From(schedules.Update(id, rule)));
        });

        group.MapDelete("/schedules/{id:int}", (int id, DownloadScheduleService schedules) =>
        {
            schedules.Delete(id);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>The categories with their resolved folders and whether the scheduler paused them.</summary>
    public static List<DownloadCategoryResource> CategoryResources(DownloadCategoryService categories, DownloadEngine engine) =>
        [.. categories.All().Select(c => DownloadCategoryResource.From(c, categories, engine))];

    public static string PriorityValue(int? priority) => priority switch
    {
        null => "default",
        DownloadCategoryService.PausedPriority => "paused",
        -1 => "low",
        0 => "normal",
        1 => "high",
        _ => "force",
    };

    public static bool TryParsePriority(string? value, out int? priority)
    {
        priority = null;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "default":
                return true;
            case "paused":
                priority = DownloadCategoryService.PausedPriority;
                return true;
            case "low":
                priority = -1;
                return true;
            case "normal":
                priority = 0;
                return true;
            case "high":
                priority = 1;
                return true;
            case "force":
                priority = 2;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Download settings. <see cref="Client"/> is "builtin" or "blackhole"; folders come with their defaults and a problem
/// text when StreamZH cannot write there. Optional folders (NZB backup, watched folder) are "" when off. Choices are
/// camelCase strings (duplicateAction: off/tag/pause/fail, autoSort: off/oldest/newest/smallest/largest/name,
/// unwantedAction: off/warn/pause/fail, encryptedAction: off/pause/fail, toolPriority: normal/belowNormal/idle).
/// </summary>
public sealed record DownloadSettingsResource(
    string NzbFolder,
    string DefaultNzbFolder,
    bool IsDefault,
    string? Problem,
    string Client,
    string IncompleteFolder,
    string DefaultIncompleteFolder,
    string? IncompleteProblem,
    string CompleteFolder,
    string DefaultCompleteFolder,
    string? CompleteProblem,
    long SpeedLimitKBps,
    double MinFreeSpaceGb,
    bool KeepFiles,
    bool AbortHopeless,
    int ArticleTries,
    string NzbBackupFolder,
    string? NzbBackupProblem,
    string WatchFolder,
    string? WatchProblem,
    int WatchIntervalSeconds,
    bool PauseOnStart,
    bool PreCheck,
    int MaxActiveDownloads,
    int PropagationDelayMinutes,
    string DuplicateAction,
    string AutoSort,
    string UnwantedExtensions,
    string UnwantedAction,
    string EncryptedAction,
    bool DownloadAllPar2,
    bool OnlyVerified,
    bool RecursiveUnpack,
    bool IgnoreSamples,
    string CleanupList,
    bool Deobfuscate,
    bool TemporaryFolderName,
    string Par2Arguments,
    string SevenZipArguments,
    string ToolPriority,
    int PostProcessWorkers,
    long MaxLineSpeedKBps,
    int HistoryKeepDays,
    int HistoryKeepItems,
    bool HistoryKeepFailed)
{
    public static DownloadSettingsResource From(DownloadSettings downloads)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        var folder = downloads.NzbFolder;
        var incomplete = downloads.IncompleteFolder;
        var complete = downloads.CompleteFolder;
        var backup = downloads.NzbBackupFolder;
        var watch = downloads.WatchFolder;
        return new DownloadSettingsResource(
            folder,
            downloads.DefaultNzbFolder,
            folder == downloads.DefaultNzbFolder,
            DownloadSettings.CheckWritable(folder),
            DownloadSettings.ClientValue(downloads.Client),
            incomplete,
            downloads.DefaultIncompleteFolder,
            DownloadSettings.CheckWritable(incomplete),
            complete,
            downloads.DefaultCompleteFolder,
            DownloadSettings.CheckWritable(complete),
            downloads.SpeedLimitKBps,
            downloads.MinFreeSpaceGb,
            downloads.KeepFiles,
            downloads.AbortHopeless,
            downloads.ArticleTries,
            backup ?? string.Empty,
            backup is null ? null : DownloadSettings.CheckWritable(backup),
            watch ?? string.Empty,
            watch is null ? null : DownloadSettings.CheckWritable(watch),
            (int)downloads.WatchInterval.TotalSeconds,
            downloads.PauseOnStart,
            downloads.PreCheck,
            downloads.MaxActiveDownloads,
            (int)downloads.PropagationDelay.TotalMinutes,
            DownloadOptionValues.Of(downloads.DuplicateAction),
            DownloadOptionValues.Of(downloads.AutoSort),
            downloads.UnwantedExtensionsText,
            DownloadOptionValues.Of(downloads.UnwantedAction),
            DownloadOptionValues.Of(downloads.EncryptedAction),
            downloads.DownloadAllPar2,
            downloads.OnlyVerified,
            downloads.PostProcess.RecursiveUnpack,
            downloads.PostProcess.IgnoreSamples,
            downloads.CleanupListText,
            downloads.PostProcess.Deobfuscate,
            downloads.TemporaryFolderName,
            downloads.Par2ArgumentsText,
            downloads.SevenZipArgumentsText,
            DownloadOptionValues.Of(downloads.ToolPriority),
            downloads.PostProcessWorkers,
            downloads.MaxLineSpeedKBps,
            downloads.HistoryKeepDays,
            downloads.HistoryKeepItems,
            downloads.HistoryKeepFailed);
    }
}

/// <param name="Folder">As entered: "" = the name below the completed folder.</param>
/// <param name="Path">Where finished downloads of the category go.</param>
/// <param name="Priority">default, paused, low, normal, high or force.</param>
/// <param name="PostProcessing">none, repair, unpack or full.</param>
/// <param name="IsDefault">movies, series and other: cannot be renamed or deleted.</param>
/// <param name="Paused">Paused by the scheduler.</param>
public sealed record DownloadCategoryResource(int Id, string Name, string Folder, string Path, string Priority, string PostProcessing, bool IsDefault, bool Paused)
{
    public static DownloadCategoryResource From(DownloadCategoryDefinition category, DownloadCategoryService categories, DownloadEngine engine)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(engine);
        return new(
            category.Id,
            category.Name,
            category.Folder,
            categories.FolderOf(category),
            DownloadSettingsEndpoints.PriorityValue(category.Priority),
            DownloadOptionValues.Of(category.PostProcessing),
            category.IsDefault,
            engine.PausedCategories.Contains(category.Name, StringComparer.OrdinalIgnoreCase));
    }
}

public sealed record DownloadCategoryRequest(string? Name, string? Folder, string? Priority, string? PostProcessing)
{
    public DownloadCategoryDefinition? ToDefinition(int id) =>
        Errors().Count > 0 || !DownloadSettingsEndpoints.TryParsePriority(Priority, out var priority)
            ? null
            : new DownloadCategoryDefinition(
                id, Name ?? string.Empty, Folder ?? string.Empty, priority, DownloadOptionValues.Parse<PostProcessLevel>(PostProcessing) ?? PostProcessLevel.Full);

    public Dictionary<string, string[]> Errors()
    {
        var errors = new Dictionary<string, string[]>();
        if (!DownloadSettingsEndpoints.TryParsePriority(Priority, out _))
        {
            errors["priority"] = ["Use default, paused, low, normal, high or force."];
        }

        if (PostProcessing is not null && DownloadOptionValues.Parse<PostProcessLevel>(PostProcessing) is null)
        {
            errors["postProcessing"] = ["Use none, repair, unpack or full."];
        }

        return errors;
    }
}

/// <param name="Time">"HH:mm", local time of the server.</param>
/// <param name="Days">mon … sun.</param>
/// <param name="Action">pause, resume, speedLimit, pauseServer, resumeServer, pauseCategory or resumeCategory.</param>
/// <param name="Argument">speedLimit: KB/s or "50%"; servers: the server id; categories: the name; else "".</param>
public sealed record DownloadScheduleResource(int Id, bool Enabled, string Time, IReadOnlyList<string> Days, string Action, string Argument, string Description)
{
    public static DownloadScheduleResource From(DownloadScheduleRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new(
            rule.Id,
            rule.Enabled,
            rule.Time.ToString("HH:mm", CultureInfo.InvariantCulture),
            [.. rule.Days.Select(d => DownloadSettingsEndpoints.DayKeys[(int)d == 0 ? 6 : (int)d - 1])],
            DownloadOptionValues.Of(rule.Action),
            rule.Argument,
            DownloadScheduleService.Describe(rule));
    }
}

public sealed record DownloadScheduleRequest(bool? Enabled, string? Time, IReadOnlyList<string>? Days, string? Action, string? Argument)
{
    public DownloadScheduleRule? ToRule(int id, out Dictionary<string, string[]> errors)
    {
        errors = [];
        if (!TimeOnly.TryParseExact(Time?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            errors["time"] = ["Use a time like 23:00."];
        }

        var days = new List<DayOfWeek>();
        foreach (var day in Days ?? [])
        {
            var index = DownloadSettingsEndpoints.DayKeys.ToList().IndexOf(day.Trim().ToLowerInvariant()[..Math.Min(3, day.Trim().Length)]);
            if (index < 0)
            {
                errors["days"] = ["Use mon, tue, wed, thu, fri, sat or sun."];
                break;
            }

            days.Add(DownloadScheduleRule.Week[index]);
        }

        var action = DownloadOptionValues.Parse<ScheduleAction>(Action);
        if (action is null)
        {
            errors["action"] = [$"Use one of: {DownloadOptionValues.Names<ScheduleAction>()}."];
        }

        return errors.Count > 0 ? null : new DownloadScheduleRule(id, Enabled ?? true, time, days, action!.Value, Argument ?? string.Empty);
    }
}
