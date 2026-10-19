// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Library.Downloads;

namespace StreamZH.Library.Migration.Sabnzbd;

/// <summary>
/// SABnzbd's categories → StreamZH's download categories (Settings → Downloads → Categories): folder, priority and
/// post-processing. The category Radarr uses ("movies") becomes StreamZH's "movies", Sonarr's ("tv") its "series";
/// others are added as they are. "*" (SABnzbd's default) has no counterpart, but its post-processing and priority are
/// used for categories that inherit them. Scripts are not taken over.
/// </summary>
public sealed class SabnzbdCategoriesSection : IMigrationSection
{
    public const string SectionKey = "categories";

    private static readonly string[] MovieNames = ["movies", "movie", "film", "films", "radarr"];
    private static readonly string[] SeriesNames = ["tv", "series", "shows", "tvshows", "tv shows", "sonarr", "serien"];

    public string Source => MigrationSources.Sabnzbd;

    public string Key => SectionKey;

    public string Label => "Categories";

    public string Description => "Download categories with their folder, priority and post-processing. \"movies\" and \"tv\" become StreamZH's movies and series.";

    public int Order => 362;

    public bool DefaultSelected => true;

    public bool Advanced => false;

    public static string ItemKey(SabnzbdCategory category) => $"{SectionKey}:{category.Name}";

    /// <summary>The StreamZH category name for a SABnzbd category ("tv" → "series", "Movies" → "movies", "Anime" → "anime").</summary>
    public static string TargetName(string sabnzbd)
    {
        var name = sabnzbd.Trim();
        if (MovieNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return DownloadCategoryService.Movies;
        }

        if (SeriesNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return DownloadCategoryService.Series;
        }

        return name.ToLowerInvariant();
    }

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var config = await context.Services.GetRequiredService<SabnzbdApiClient>().ConfigAsync(context.Connection, cancellationToken);
        var categories = context.Services.GetRequiredService<DownloadCategoryService>();
        var items = new List<MigrationItem>();
        var defaults = config.Categories.FirstOrDefault(c => c.Name == "*");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in config.Categories)
        {
            if (category.Name == "*")
            {
                items.Add(new MigrationItem(
                    ItemKey(category), "* (default)", MigrationAction.Skip, Describe(Plan(category, defaults, context)),
                    "SABnzbd's default category has no counterpart; categories that inherit from it get its settings."));
                continue;
            }

            var target = TargetName(category.Name);
            if (!targets.Add(target))
            {
                items.Add(new MigrationItem(ItemKey(category), category.Name, MigrationAction.Skip, null, $"Another SABnzbd category already becomes \"{target}\"."));
                continue;
            }

            var plan = Plan(category, defaults, context);
            var existing = categories.Find(target);
            var detail = (string.Equals(target, category.Name, StringComparison.OrdinalIgnoreCase) ? string.Empty : $"→ {target} · ") + Describe(plan);
            items.Add(new MigrationItem(
                ItemKey(category),
                category.Name,
                existing is null ? MigrationAction.Create : MigrationAction.Update,
                detail,
                Warning: plan.Warning));
        }

        return new MigrationSectionPlan(items);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        var config = await context.Services.GetRequiredService<SabnzbdApiClient>().ConfigAsync(context.Connection, cancellationToken);
        var categories = context.Services.GetRequiredService<DownloadCategoryService>();
        var defaults = config.Categories.FirstOrDefault(c => c.Name == "*");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var category in config.Categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(done++, config.Categories.Count, category.Name);
            if (category.Name == "*")
            {
                continue;
            }

            var key = ItemKey(category);
            var target = TargetName(category.Name);
            if (context.IsExcluded(key))
            {
                run.Skipped(category.Name, "Left out in the wizard.");
                continue;
            }

            if (!targets.Add(target))
            {
                run.Skipped(category.Name, $"Another SABnzbd category already became \"{target}\".");
                continue;
            }

            var plan = Plan(category, defaults, context);
            try
            {
                var existing = categories.Find(target);
                var folder = plan.Folder ?? existing?.Folder ?? string.Empty;
                var definition = new DownloadCategoryDefinition(existing?.Id ?? 0, existing?.Name ?? target, folder, plan.Priority, plan.PostProcessing);
                if (existing is null)
                {
                    categories.Create(definition);
                    run.Created(category.Name);
                }
                else
                {
                    categories.Update(existing.Id, definition);
                    run.Updated(category.Name);
                }

                context.Ids.Set("downloadCategory", category.Name, categories.Find(target)!.Id);
                if (plan.Warning is { } warning)
                {
                    run.Warn($"{category.Name}: {warning}", category.Name);
                }

                if (plan.Script is { } script)
                {
                    run.Info($"{category.Name}: the script \"{script}\" is not taken over; StreamZH imports and renames by itself.", category.Name);
                }
            }
            catch (ValidationFailedException ex)
            {
                run.Failed(category.Name, string.Join(" ", ex.Errors.Values.SelectMany(v => v)));
            }
        }

        run.Progress(config.Categories.Count, config.Categories.Count);
    }

    /// <summary>What a SABnzbd category becomes. <see cref="Folder"/> null keeps StreamZH's folder.</summary>
    public sealed record CategoryPlan(string? Folder, int? Priority, PostProcessLevel PostProcessing, string? Warning, string? Script);

    public static CategoryPlan Plan(SabnzbdCategory category, SabnzbdCategory? defaults, MigrationContext context)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(context);

        // SABnzbd pp: 0 download only, 1 +repair, 2 +unpack, 3 +delete; empty = like "*".
        var pp = Pp(category.Pp) ?? Pp(defaults?.Pp) ?? 3;
        var level = (PostProcessLevel)Math.Clamp(pp, 0, 3);

        // Priority: -100 = like "*"; -2 paused, -1 low, 0 normal, 1 high, 2 force.
        var priority = category.Priority == SabnzbdConfig.DefaultPriority ? defaults?.Priority ?? SabnzbdConfig.DefaultPriority : category.Priority;
        int? mapped = priority switch
        {
            SabnzbdConfig.DefaultPriority => null,
            DownloadCategoryService.PausedPriority => DownloadCategoryService.PausedPriority,
            _ => Math.Clamp(priority, DownloadEngine.MinPriority, DownloadEngine.MaxPriority),
        };

        string? folder = null;
        string? warning = null;
        var dir = category.Dir.Trim();
        if (dir.Length > 0 && category.Name != "*")
        {
            if (SabnzbdConfig.IsAbsolute(dir))
            {
                context.Paths.Use(dir);
                var local = context.Paths.Map(dir);
                if (Directory.Exists(local))
                {
                    folder = local;
                }
                else
                {
                    warning = $"StreamZH cannot see the folder {local}; the category keeps StreamZH's folder. Add a path mapping for {dir}.";
                }
            }
            else
            {
                // Relative: below the completed folder in both apps.
                folder = dir.Replace('\\', '/').Trim('/');
            }
        }

        var script = category.Script.Trim() is { Length: > 0 } s && !string.Equals(s, "None", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase)
            ? s
            : null;
        return new CategoryPlan(folder, mapped, level, warning, script);
    }

    private static int? Pp(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pp) && pp is >= 0 and <= 3 ? pp : null;

    private static string Describe(CategoryPlan plan)
    {
        var parts = new List<string>
        {
            plan.PostProcessing switch
            {
                PostProcessLevel.None => "download only",
                PostProcessLevel.Repair => "repair",
                PostProcessLevel.Unpack => "repair, unpack",
                _ => "repair, unpack, delete",
            },
            plan.Priority switch
            {
                null => "default priority",
                DownloadCategoryService.PausedPriority => "added paused",
                -1 => "low priority",
                0 => "normal priority",
                1 => "high priority",
                _ => "force priority",
            },
        };
        if (plan.Folder is { } folder)
        {
            parts.Add($"folder {folder}");
        }

        if (plan.Script is { } script)
        {
            parts.Add($"script {script} not taken over");
        }

        return string.Join(" · ", parts);
    }
}
