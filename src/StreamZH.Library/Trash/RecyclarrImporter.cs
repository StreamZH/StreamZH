// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace StreamZH.Library.Trash;

/// <summary>One StreamZH profile a recyclarr.yml creates or updates.</summary>
/// <param name="Template">Name of the guide profile it follows (movie template first), null for a profile without template.</param>
/// <param name="Formats">Custom formats the profile scores.</param>
/// <param name="Overrides">Scores set explicitly in the YAML (assign_scores_to with a score).</param>
/// <param name="Exists">A StreamZH profile with this name exists and is updated.</param>
public sealed record RecyclarrProfilePlan(
    string Name, string? Template, string? MovieTemplate, string? SeriesTemplate, int Formats, int Overrides, bool Exists);

public sealed record RecyclarrResult(
    IReadOnlyList<RecyclarrProfilePlan> Profiles, IReadOnlyList<string> Warnings, bool Applied, int Created, int Updated);

/// <summary>
/// Reads a Recyclarr configuration (recyclarr.yml) and does in StreamZH what Recyclarr would do in Radarr and Sonarr:
/// guide-backed profiles (<c>trash_id</c> or an included TRaSH template) become profiles that follow the template,
/// <c>custom_formats</c> with <c>assign_scores_to</c> add formats and scores, <c>custom_format_groups</c> skip or add groups,
/// <c>quality_definition</c> sets quality sizes and <c>media_naming</c> the naming. A Radarr and a Sonarr profile with the
/// same name become one StreamZH profile (movie and series template). Connection settings (base_url, api_key) are ignored.
/// </summary>
public sealed class RecyclarrImporter(
    TrashApplyService apply, QualityProfileRepository profiles, QualityProfileService profileService, CustomFormatRepository formats,
    ILogger<RecyclarrImporter> logger)
{
    // ---- YAML model ----

    private sealed record YamlQuality(string Name, bool Enabled, IReadOnlyList<string> Qualities);

    private sealed class YamlProfile
    {
        public required MediaType MediaType { get; init; }
        public string? TrashId { get; set; }
        public string? Name { get; set; }
        public string? ScoreSet { get; set; }
        public bool ResetUnmatched { get; set; }
        public List<string> ResetExcept { get; } = [];
        public bool? UpgradeAllowed { get; set; }
        public string? UntilQuality { get; set; }
        public int? UntilScore { get; set; }
        public int? MinFormatScore { get; set; }
        public int? MinUpgradeFormatScore { get; set; }
        public List<YamlQuality>? Qualities { get; set; }
        public TrashQualityProfile? Template { get; set; }
        public List<TrashExtraFormat> Formats { get; } = [];
        public List<TrashGroupSelection> Groups { get; } = [];
        public List<string> SkipGroups { get; } = [];

        public string DisplayName => Name ?? Template?.Name ?? TrashId ?? "?";
    }

    private sealed class YamlInstance
    {
        public required MediaType MediaType { get; init; }
        public required string Name { get; init; }
        public string? SizeType { get; set; }
        public double? PreferredRatio { get; set; }
        public List<YamlProfile> Profiles { get; } = [];
        public List<(string TrashId, int Line)> InstallOnly { get; } = [];
        public string? NamingFolder { get; set; }
        public string? NamingFile { get; set; }
        public bool? Rename { get; set; }
    }

    // ---- Entry point ----

    /// <summary>Shows what the configuration would do (<paramref name="applyChanges"/> false) or does it.</summary>
    /// <exception cref="ValidationFailedException">The YAML cannot be read (key "yaml").</exception>
    public RecyclarrResult Run(string yaml, bool applyChanges, TrashGuides guides)
    {
        ArgumentNullException.ThrowIfNull(guides);
        var warnings = new List<string>();
        var instances = Parse(yaml ?? string.Empty, guides, warnings);
        var plans = Plan(instances, warnings);

        var created = 0;
        var updated = 0;
        var results = new List<RecyclarrProfilePlan>();
        foreach (var plan in plans)
        {
            var existing = profiles.FindByName(plan.Name);
            var wanted = plan.Movie?.Template is not null || plan.Series?.Template is not null
                ? TrashApplyService.WantedFormats(plan.Movie?.Template, plan.Series?.Template, plan.Options, guides, warnings).Count
                : plan.Options.ExtraFormats.Count;
            results.Add(new RecyclarrProfilePlan(
                plan.Name,
                plan.Movie?.Template?.Name ?? plan.Series?.Template?.Name,
                plan.Movie?.Template?.TrashId,
                plan.Series?.Template?.TrashId,
                wanted,
                plan.Options.ExtraFormats.Count(f => f.Score is not null),
                existing is not null));
        }

        if (!applyChanges)
        {
            return new RecyclarrResult(results, Distinct(warnings), false, 0, 0);
        }

        foreach (var plan in plans)
        {
            try
            {
                var (c, u) = plan.Movie?.Template is not null || plan.Series?.Template is not null
                    ? ApplyTemplate(plan, guides, warnings)
                    : ApplyManual(plan, guides, warnings);
                created += c;
                updated += u;
            }
            catch (ValidationFailedException ex)
            {
                warnings.Add($"{plan.Name}: {string.Join(" ", ex.Errors.SelectMany(e => e.Value))}");
            }
        }

        var installOnly = instances.SelectMany(i => i.InstallOnly.Select(f => (i.MediaType, f.TrashId))).ToList();
        if (installOnly.Count > 0)
        {
            var install = apply.InstallFormats(installOnly, guides, warnings);
            created += install.Created;
            updated += install.Updated;
        }

        var sizes = instances.Where(i => i.SizeType is not null).Select(i => new TrashSizeChoice(i.MediaType, i.SizeType!, i.PreferredRatio)).ToList();
        if (sizes.Count > 0)
        {
            try
            {
                apply.ApplyQualitySizes(sizes, guides, warnings);
            }
            catch (ValidationFailedException ex)
            {
                warnings.Add($"Quality sizes: {string.Join(" ", ex.Errors.SelectMany(e => e.Value))}");
            }
        }

        ApplyNaming(instances, guides, warnings);
        logger.LogInformation(
            "Recyclarr configuration applied: {Profiles} profiles, {Created} custom formats installed, {Updated} updated, {Warnings} warnings",
            plans.Count, created, updated, warnings.Count);
        return new RecyclarrResult(results, Distinct(warnings), true, created, updated);
    }

    // ---- Planning ----

    private sealed class ProfilePlan
    {
        public required string Name { get; init; }
        public YamlProfile? Movie { get; set; }
        public YamlProfile? Series { get; set; }
        public TrashProfileOptions Options { get; set; } = new();
    }

    private static List<ProfilePlan> Plan(List<YamlInstance> instances, List<string> warnings)
    {
        var plans = new List<ProfilePlan>();
        foreach (var instance in instances)
        {
            foreach (var profile in instance.Profiles)
            {
                var name = profile.DisplayName;
                var plan = plans.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (plan is null)
                {
                    plans.Add(plan = new ProfilePlan { Name = name });
                }

                if (profile.MediaType == MediaType.Movie)
                {
                    if (plan.Movie is not null)
                    {
                        warnings.Add($"The profile '{name}' appears in two Radarr instances; the first one is used.");
                        continue;
                    }

                    plan.Movie = profile;
                }
                else
                {
                    if (plan.Series is not null)
                    {
                        warnings.Add($"The profile '{name}' appears in two Sonarr instances; the first one is used.");
                        continue;
                    }

                    plan.Series = profile;
                }
            }
        }

        foreach (var plan in plans)
        {
            var primary = plan.Movie ?? plan.Series!;
            var scoreSets = new[] { plan.Movie?.ScoreSet, plan.Series?.ScoreSet }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (scoreSets.Count > 1)
            {
                warnings.Add($"{plan.Name}: Radarr and Sonarr use different score sets; '{scoreSets[0]}' is used for both.");
            }

            var template = plan.Movie?.Template is not null ? plan.Movie : plan.Series?.Template is not null ? plan.Series : primary;
            IReadOnlyList<QualityProfileItem>? items = null;
            int? cutoff = null;
            var qualityWarnings = new List<string>();
            if (template.Qualities is { Count: > 0 } qualities)
            {
                var pseudo = new TrashQualityProfile
                {
                    TrashId = string.Empty,
                    Name = plan.Name,
                    Slug = string.Empty,
                    Cutoff = template.UntilQuality,
                    Items = [.. qualities.Select(q => new TrashQualityItem(q.Name, q.Enabled, q.Qualities))],
                };
                items = TrashApplyService.BuildItems(pseudo, template.MediaType, qualityWarnings);
                cutoff = TrashApplyService.CutoffOf(pseudo, template.MediaType, items);
            }
            else if (template.UntilQuality is { } until && template.Template is { } guide)
            {
                var guideItems = TrashApplyService.BuildItems(guide, template.MediaType, qualityWarnings);
                cutoff = TrashApplyService.CutoffOf(guide with { Cutoff = until }, template.MediaType, guideItems);
            }

            warnings.AddRange(qualityWarnings);
            var both = new[] { plan.Movie, plan.Series }.OfType<YamlProfile>().ToList();
            plan.Options = new TrashProfileOptions
            {
                ScoreSet = scoreSets.FirstOrDefault(),
                Groups = [.. both.SelectMany(p => p.Groups).DistinctBy(g => g.TrashId, StringComparer.OrdinalIgnoreCase)],
                SkipGroups = [.. both.SelectMany(p => p.SkipGroups).Distinct(StringComparer.OrdinalIgnoreCase)],
                ExtraFormats = [.. both.SelectMany(p => p.Formats)],
                ResetUnmatched = both.Any(p => p.ResetUnmatched),
                ResetExcept = [.. both.SelectMany(p => p.ResetExcept).Distinct(StringComparer.Ordinal)],
                Items = items,
                CutoffQualityId = cutoff,
                UpgradeAllowed = template.UpgradeAllowed,
                MinFormatScore = template.MinFormatScore,
                CutoffFormatScore = template.UntilScore,
                MinUpgradeFormatScore = template.MinUpgradeFormatScore,
            };
        }

        return plans;
    }

    private (int Created, int Updated) ApplyTemplate(ProfilePlan plan, TrashGuides guides, List<string> warnings)
    {
        var existing = profiles.FindByName(plan.Name);
        var result = apply.Apply(
            new TrashApplyRequest
            {
                ProfileId = existing?.Id,
                Name = plan.Name,
                MovieTemplate = plan.Movie?.Template?.TrashId,
                SeriesTemplate = plan.Series?.Template?.TrashId,
                Options = plan.Options,
            },
            guides);
        warnings.AddRange(result.Warnings);
        return (result.Created, result.Updated);
    }

    /// <summary>A profile without template (Recyclarr "manual" profile): qualities and scores as given, not followed by the sync.</summary>
    private (int Created, int Updated) ApplyManual(ProfilePlan plan, TrashGuides guides, List<string> warnings)
    {
        var existing = profiles.FindByName(plan.Name);
        var options = plan.Options;
        if (existing is null && options.Items is null)
        {
            warnings.Add($"{plan.Name}: a new profile without trash_id needs 'qualities'; skipped.");
            return (0, 0);
        }

        var wanted = options.ExtraFormats.Select(f => (f.MediaType, f.TrashId)).ToList();
        var install = apply.InstallFormats(wanted, guides, warnings);
        var scores = options.ResetUnmatched || existing is null
            ? new Dictionary<int, int>()
            : existing.FormatItems.ToDictionary(i => i.CustomFormatId, i => i.Score);
        if (options.ResetUnmatched && existing is not null)
        {
            var names = formats.All().ToDictionary(f => f.Id, f => f.Name);
            foreach (var item in existing.FormatItems.Where(i => names.TryGetValue(i.CustomFormatId, out var n) && options.ResetExcept.Contains(n, StringComparer.Ordinal)))
            {
                scores[item.CustomFormatId] = item.Score;
            }
        }

        foreach (var format in options.ExtraFormats)
        {
            if (install.Ids.TryGetValue((format.MediaType, format.TrashId), out var id))
            {
                var guide = guides.For(format.MediaType).Format(format.TrashId);
                scores[id] = format.Score ?? guide?.ScoreFor(options.ScoreSet) ?? 0;
            }
        }

        var items = options.Items ?? existing!.Items;
        var profile = new QualityProfile
        {
            Id = existing?.Id ?? 0,
            Name = plan.Name,
            Items = items,
            CutoffQualityId = options.CutoffQualityId ?? existing?.CutoffQualityId ?? items.LastOrDefault(i => i.Allowed)?.QualityId ?? Quality.Unknown.Id,
            UpgradeAllowed = options.UpgradeAllowed ?? existing?.UpgradeAllowed ?? true,
            LanguageId = existing?.LanguageId ?? Language.Any.Id,
            PreferDualLanguage = existing?.PreferDualLanguage ?? false,
            FormatItems = [.. scores.Where(s => s.Value != 0).OrderBy(s => s.Key).Select(s => new ProfileFormatItem(s.Key, s.Value))],
            MinFormatScore = options.MinFormatScore ?? existing?.MinFormatScore ?? 0,
            CutoffFormatScore = options.CutoffFormatScore ?? existing?.CutoffFormatScore ?? 0,
            MinUpgradeFormatScore = options.MinUpgradeFormatScore ?? existing?.MinUpgradeFormatScore ?? 1,
            TrashMovieTemplate = existing?.TrashMovieTemplate,
            TrashSeriesTemplate = existing?.TrashSeriesTemplate,
        };
        profileService.Validate(profile, existing?.Id);
        if (existing is null)
        {
            profiles.Add(profile);
        }
        else
        {
            profiles.Update(profile);
        }

        return (install.Created, install.Updated);
    }

    private void ApplyNaming(List<YamlInstance> instances, TrashGuides guides, List<string> warnings)
    {
        var movie = instances.FirstOrDefault(i => i.MediaType == MediaType.Movie && (i.NamingFolder ?? i.NamingFile) is not null || i.MediaType == MediaType.Movie && i.Rename is not null);
        var series = instances.FirstOrDefault(i => i.MediaType == MediaType.Series && (i.NamingFolder ?? i.NamingFile) is not null || i.MediaType == MediaType.Series && i.Rename is not null);
        if (movie is null && series is null)
        {
            return;
        }

        var errors = new Dictionary<string, string[]>();
        var naming = new TrashNaming(movie?.NamingFolder, movie?.NamingFile, series?.NamingFolder, series?.NamingFile);
        var values = apply.NamingValues(naming, guides, errors);
        if (errors.Count > 0)
        {
            warnings.AddRange(errors.Select(e => $"Naming ({e.Key[7..]}): {string.Join(" ", e.Value)}"));
            naming = new TrashNaming(
                errors.ContainsKey("naming.movieFolder") ? null : naming.MovieFolder,
                errors.ContainsKey("naming.movieFile") ? null : naming.MovieFile,
                errors.ContainsKey("naming.seriesFolder") ? null : naming.SeriesFolder,
                errors.ContainsKey("naming.seriesFile") ? null : naming.SeriesFile);
            errors.Clear();
            values = apply.NamingValues(naming, guides, errors);

            // An anime variant StreamZH cannot use leaves only the anime format unchanged (warned above).
            if (errors.Keys.Any(k => k != TrashApplyService.AnimeKey))
            {
                return;
            }
        }

        values = values with
        {
            RenameMovies = movie?.Rename ?? values.RenameMovies,
            RenameEpisodes = series?.Rename ?? values.RenameEpisodes,
        };
        apply.SaveNaming(values);
    }

    // ---- Parsing ----

    private static List<YamlInstance> Parse(string yaml, TrashGuides guides, List<string> warnings)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            root = stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode
                   ?? throw new ValidationFailedException(new Dictionary<string, string[]> { ["yaml"] = ["The configuration is empty."] });
        }
        catch (YamlException ex)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]>
            {
                ["yaml"] = [$"Line {ex.Start.Line.ToString(CultureInfo.InvariantCulture)}: {(ex.InnerException ?? ex).Message}"],
            });
        }

        var instances = new List<YamlInstance>();
        foreach (var (service, mediaType) in new[] { ("radarr", MediaType.Movie), ("sonarr", MediaType.Series) })
        {
            if (Child(root, service) is not YamlMappingNode section)
            {
                continue;
            }

            foreach (var (key, value) in section.Children)
            {
                if (value is YamlMappingNode node)
                {
                    instances.Add(ParseInstance(Scalar(key) ?? service, mediaType, node, guides.For(mediaType), warnings));
                }
            }
        }

        if (instances.Count == 0)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["yaml"] = ["No radarr or sonarr instance found."] });
        }

        if (guides.Movie.Profiles.Count + guides.Series.Profiles.Count == 0)
        {
            warnings.Add("The TRaSH Guides are not downloaded yet; trash ids and templates cannot be resolved.");
        }

        return instances;
    }

    private static YamlInstance ParseInstance(string name, MediaType mediaType, YamlMappingNode node, TrashGuide guide, List<string> warnings)
    {
        var app = mediaType == MediaType.Movie ? "radarr" : "sonarr";
        var instance = new YamlInstance { MediaType = mediaType, Name = name };

        if (Child(node, "quality_definition") is YamlMappingNode definition)
        {
            instance.SizeType = Scalar(Child(definition, "type"));
            instance.PreferredRatio = Double(Child(definition, "preferred_ratio"));
        }

        foreach (var profileNode in Mappings(Child(node, "quality_profiles")))
        {
            var profile = ParseProfile(profileNode, mediaType);
            if (profile.TrashId is { } trashId)
            {
                profile.Template = guide.Profile(trashId);
                if (profile.Template is null)
                {
                    warnings.Add($"{name}: unknown {app} quality profile trash_id {trashId} (line {Line(profileNode)}).");
                    if (profile.Name is null)
                    {
                        continue;
                    }
                }
            }
            else if (profile.Name is null)
            {
                warnings.Add($"{name}: a quality profile without name or trash_id (line {Line(profileNode)}); skipped.");
                continue;
            }

            instance.Profiles.Add(profile);
        }

        foreach (var include in Mappings(Child(node, "include")))
        {
            if (Scalar(Child(include, "template")) is { } template)
            {
                IncludeTemplate(instance, template, guide, warnings);
            }
            else if (Scalar(Child(include, "config")) is { } file)
            {
                warnings.Add($"{name}: included file '{file}' is not read; paste its content into the configuration.");
            }
        }

        foreach (var entry in Mappings(Child(node, "custom_formats")))
        {
            var ids = Strings(Child(entry, "trash_ids"));
            var targets = Mappings(Child(entry, "assign_scores_to")).Concat(Mappings(Child(entry, "quality_profiles"))).ToList();
            foreach (var id in ids)
            {
                if (guide.Format(id) is null)
                {
                    warnings.Add($"{name}: unknown {app} custom format trash_id {id} (line {Line(entry)}); skipped.");
                    continue;
                }

                if (targets.Count == 0)
                {
                    instance.InstallOnly.Add((id, Line(entry)));
                    continue;
                }

                foreach (var target in targets)
                {
                    var profile = FindProfile(instance, Scalar(Child(target, "name")), Scalar(Child(target, "trash_id")));
                    if (profile is null)
                    {
                        warnings.Add($"{name}: assign_scores_to names a profile that is not in quality_profiles (line {Line(target)}).");
                        continue;
                    }

                    profile.Formats.Add(new TrashExtraFormat(id, mediaType, Int(Child(target, "score"))));
                }
            }
        }

        if (Child(node, "custom_format_groups") is YamlMappingNode groups)
        {
            var guided = instance.Profiles.Where(p => p.Template is not null).ToList();
            foreach (var skip in Strings(Child(groups, "skip")))
            {
                guided.ForEach(p => p.SkipGroups.Add(skip));
            }

            foreach (var add in Mappings(Child(groups, "add")))
            {
                if (Scalar(Child(add, "trash_id")) is not { } groupId)
                {
                    continue;
                }

                if (guide.Group(groupId) is null)
                {
                    warnings.Add($"{name}: unknown {app} custom format group {groupId} (line {Line(add)}).");
                    continue;
                }

                var selection = new TrashGroupSelection
                {
                    TrashId = groupId,
                    Select = Strings(Child(add, "select")),
                    Exclude = Strings(Child(add, "exclude")),
                    SelectAll = Bool(Child(add, "select_all")) ?? false,
                };
                var targets = Mappings(Child(add, "assign_scores_to")).ToList();
                var chosen = targets.Count == 0
                    ? instance.Profiles
                    : [.. targets.Select(t => FindProfile(instance, Scalar(Child(t, "name")), Scalar(Child(t, "trash_id")))).OfType<YamlProfile>()];
                chosen.ForEach(p => p.Groups.Add(selection));
            }
        }

        if (Child(node, "media_naming") is YamlMappingNode naming)
        {
            if (mediaType == MediaType.Movie)
            {
                instance.NamingFolder = Scalar(Child(naming, "folder"));
                if (Child(naming, "movie") is YamlMappingNode movie)
                {
                    instance.NamingFile = Scalar(Child(movie, "standard"));
                    instance.Rename = Bool(Child(movie, "rename"));
                }
            }
            else
            {
                instance.NamingFolder = Scalar(Child(naming, "series"));
                if (Child(naming, "episodes") is YamlMappingNode episodes)
                {
                    instance.NamingFile = Scalar(Child(episodes, "standard"));
                    instance.Rename = Bool(Child(episodes, "rename"));
                }

                if (Child(naming, "season") is not null)
                {
                    warnings.Add($"{name}: media_naming.season is not applied; set the season folder in Settings → Media Management.");
                }
            }
        }

        return instance;
    }

    private static YamlProfile ParseProfile(YamlMappingNode node, MediaType mediaType)
    {
        var profile = new YamlProfile
        {
            MediaType = mediaType,
            TrashId = Scalar(Child(node, "trash_id")),
            Name = Scalar(Child(node, "name")),
            ScoreSet = Scalar(Child(node, "score_set")),
            MinFormatScore = Int(Child(node, "min_format_score")),
            MinUpgradeFormatScore = Int(Child(node, "min_upgrade_format_score")),
        };

        if (Child(node, "reset_unmatched_scores") is YamlMappingNode reset)
        {
            profile.ResetUnmatched = Bool(Child(reset, "enabled")) ?? false;
            profile.ResetExcept.AddRange(Strings(Child(reset, "except")));
        }
        else if (Bool(Child(node, "reset_unmatched_scores")) is { } resetFlag)
        {
            profile.ResetUnmatched = resetFlag; // Recyclarr before 5.0
        }

        if (Child(node, "upgrade") is YamlMappingNode upgrade)
        {
            profile.UpgradeAllowed = Bool(Child(upgrade, "allowed"));
            profile.UntilQuality = Scalar(Child(upgrade, "until_quality"));
            profile.UntilScore = Int(Child(upgrade, "until_score"));
        }

        if (Child(node, "qualities") is YamlSequenceNode qualities)
        {
            profile.Qualities = [];
            foreach (var item in qualities.Children)
            {
                if (item is YamlScalarNode scalar && scalar.Value is { Length: > 0 } single)
                {
                    profile.Qualities.Add(new YamlQuality(single, true, []));
                }
                else if (item is YamlMappingNode map && Scalar(Child(map, "name")) is { } qualityName)
                {
                    profile.Qualities.Add(new YamlQuality(qualityName, Bool(Child(map, "enabled")) ?? true, Strings(Child(map, "qualities"))));
                }
            }

            // quality_sort: bottom lists the qualities worst first.
            if (string.Equals(Scalar(Child(node, "quality_sort")), "bottom", StringComparison.OrdinalIgnoreCase))
            {
                profile.Qualities.Reverse();
            }
        }

        return profile;
    }

    /// <summary>
    /// Recyclarr's config templates by name: <c>radarr-quality-definition-movie</c> sets the quality sizes,
    /// <c>radarr-quality-profile-hd-bluray-web-german</c> / <c>radarr-custom-formats-…</c> add the guide profile whose slug
    /// has the same words (here german-hd-bluray-web). Best effort: unknown templates give a warning.
    /// </summary>
    private static void IncludeTemplate(YamlInstance instance, string template, TrashGuide guide, List<string> warnings)
    {
        var name = template.Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "radarr-", "sonarr-v4-", "sonarr-v5-", "sonarr-" })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        if (name.StartsWith("quality-definition-", StringComparison.Ordinal))
        {
            instance.SizeType ??= name["quality-definition-".Length..];
            return;
        }

        foreach (var prefix in new[] { "quality-profile-", "custom-formats-" })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        var profile = FindBySlug(guide, name);
        if (profile is null)
        {
            warnings.Add($"{instance.Name}: template '{template}' is not a TRaSH profile StreamZH knows; skipped.");
            return;
        }

        if (instance.Profiles.Any(p => string.Equals(p.Template?.TrashId, profile.TrashId, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var match = instance.Profiles.First(p => string.Equals(p.Template?.TrashId, profile.TrashId, StringComparison.OrdinalIgnoreCase)
                                                     || string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            match.Template ??= profile;
            match.TrashId ??= profile.TrashId;
            return;
        }

        instance.Profiles.Add(new YamlProfile { MediaType = instance.MediaType, TrashId = profile.TrashId, Template = profile });
    }

    /// <summary>The guide profile with this slug, or the only one whose slug has the same words in another order.</summary>
    public static TrashQualityProfile? FindBySlug(TrashGuide guide, string slug)
    {
        ArgumentNullException.ThrowIfNull(guide);
        if (guide.Profiles.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return exact;
        }

        static string Words(string s) => string.Join('-', s.ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        var words = Words(slug);
        var matches = guide.Profiles.Where(p => Words(p.Slug) == words).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static YamlProfile? FindProfile(YamlInstance instance, string? name, string? trashId) =>
        instance.Profiles.FirstOrDefault(p =>
            (name is not null && string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase))
            || (trashId is not null && string.Equals(p.TrashId, trashId, StringComparison.OrdinalIgnoreCase)));

    // ---- YAML helpers (merge keys "<<" are honoured) ----

    private static YamlNode? Child(YamlMappingNode map, string key)
    {
        foreach (var (k, v) in map.Children)
        {
            if (k is YamlScalarNode { Value: { } name } && name == key)
            {
                return v;
            }
        }

        foreach (var (k, v) in map.Children)
        {
            if (k is YamlScalarNode { Value: "<<" })
            {
                var merged = v is YamlMappingNode m ? [m] : v is YamlSequenceNode s ? s.Children.OfType<YamlMappingNode>() : [];
                foreach (var source in merged)
                {
                    if (Child(source, key) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    private static string? Scalar(YamlNode? node) => node is YamlScalarNode { Value: { Length: > 0 } value } ? value.Trim() : null;

    private static int? Int(YamlNode? node) =>
        int.TryParse(Scalar(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? Double(YamlNode? node) =>
        double.TryParse(Scalar(node), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? Bool(YamlNode? node) => Scalar(node)?.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => null,
    };

    private static List<string> Strings(YamlNode? node) => node switch
    {
        YamlSequenceNode sequence => [.. sequence.Children.Select(Scalar).OfType<string>()],
        YamlScalarNode => Scalar(node) is { } single ? [single] : [],
        _ => [],
    };

    private static IEnumerable<YamlMappingNode> Mappings(YamlNode? node) =>
        node is YamlSequenceNode sequence ? sequence.Children.OfType<YamlMappingNode>() : [];

    private static int Line(YamlNode node) => (int)node.Start.Line;

    private static List<string> Distinct(List<string> warnings) => [.. warnings.Distinct(StringComparer.Ordinal)];
}
