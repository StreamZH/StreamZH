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
using StreamZH.Library.Import;

namespace StreamZH.Library.Trash;

/// <summary>Naming variants of the guides (keys of <c>radarr-naming.json</c> / <c>sonarr-naming.json</c>), null = unchanged.</summary>
public sealed record TrashNaming(string? MovieFolder = null, string? MovieFile = null, string? SeriesFolder = null, string? SeriesFile = null);

/// <summary>Size limits of one guide quality-size file (e.g. Radarr "movie", Sonarr "series"); see <see cref="TrashApplyService.ApplyQualitySizes"/>.</summary>
public sealed record TrashSizeChoice(MediaType MediaType, string Type, double? PreferredRatio = null);

/// <summary>What <see cref="TrashApplyService.Apply"/> does to one profile.</summary>
public sealed record TrashApplyRequest
{
    /// <summary>Profile to update; null creates one.</summary>
    public int? ProfileId { get; init; }

    /// <summary>Name of a new profile (default: the template's name) or a new name for the profile.</summary>
    public string? Name { get; init; }

    public string? MovieTemplate { get; init; }

    public string? SeriesTemplate { get; init; }

    public TrashProfileOptions Options { get; init; } = new();
}

/// <param name="Created">Custom formats installed.</param>
/// <param name="Updated">Custom formats whose name, conditions or rename flag changed.</param>
public sealed record TrashApplyResult(QualityProfile Profile, bool ProfileCreated, int Created, int Updated, IReadOnlyList<string> Warnings);

/// <summary>One format a profile gets from its templates, groups and extra formats.</summary>
public sealed record TrashWantedFormat(MediaType MediaType, string TrashId, string Name, int Score, bool ExplicitScore);

/// <summary>
/// Turns TRaSH templates into StreamZH data: installs the custom formats (matched by trash id, else by name), builds the
/// quality profile from a movie template (qualities, cutoff, scores) and/or a series template (series format scores;
/// its qualities only without a movie template), adds the default CF groups of the template and the chosen extra groups,
/// keeps the scores the user changed, and applies quality sizes and naming variants.
/// </summary>
public sealed class TrashApplyService(
    CustomFormatRepository formats,
    QualityProfileRepository profiles,
    QualityProfileService profileService,
    QualityDefinitionService definitions,
    MediaManagementSettings mediaManagement,
    TrashRepository trash,
    TrashSettings settings,
    ConfigRepository config,
    ILogger<TrashApplyService> logger)
{
    /// <summary>Radarr's and Sonarr's slider ends: a guide value at or above them means "no limit" (like Recyclarr).</summary>
    public static (double Max, double Preferred) UnlimitedThreshold(MediaType mediaType) =>
        mediaType == MediaType.Series ? (1000, 995) : (2000, 1999);

    // ---- Profiles ----

    /// <summary>Creates or updates a profile that follows the given templates. Writes nothing when the request is invalid.</summary>
    /// <exception cref="ValidationFailedException">Unknown profile or template, name taken, no template.</exception>
    public TrashApplyResult Apply(TrashApplyRequest request, TrashGuides guides)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(guides);
        var warnings = new List<string>();
        var errors = new Dictionary<string, string[]>();

        QualityProfile? existing = null;
        if (request.ProfileId is { } id && (existing = profiles.Find(id)) is null)
        {
            errors["profileId"] = ["This quality profile does not exist."];
        }

        var movie = Template(request.MovieTemplate, guides.Movie, "movieTemplate", errors);
        var series = Template(request.SeriesTemplate, guides.Series, "seriesTemplate", errors);
        if (string.IsNullOrWhiteSpace(request.MovieTemplate) && string.IsNullOrWhiteSpace(request.SeriesTemplate))
        {
            errors["movieTemplate"] = ["Choose a movie or a series template."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var options = request.Options;
        var draft = BuildProfile(existing, request.Name, movie, series, options, [], []);
        profileService.Validate(draft, existing?.Id);

        var wanted = WantedFormats(movie, series, options, guides, warnings);
        var install = InstallFormats(wanted.Select(w => (w.MediaType, w.TrashId)), guides, warnings);

        var previous = existing is null ? null : trash.Options(existing.Id);
        var overrides = existing is null || !settings.Values.KeepScoreOverrides ? new Dictionary<int, int>() : trash.Overrides(existing.Id);
        var wantedScores = new Dictionary<int, int>();
        foreach (var w in wanted)
        {
            if (install.Ids.TryGetValue((w.MediaType, w.TrashId), out var formatId))
            {
                wantedScores[formatId] = w.Score;
            }
        }

        var formatItems = Scores(existing, previous, options, wantedScores, overrides);
        var profile = BuildProfile(existing, request.Name, movie, series, options, formatItems, warnings);
        profileService.Validate(profile, existing?.Id);

        int profileId;
        if (existing is null)
        {
            profileId = profiles.Add(profile);
        }
        else
        {
            profileId = existing.Id;
            profiles.Update(profile);
        }

        trash.SaveOptions(profileId, options with { AppliedFormatIds = [.. wantedScores.Keys.Order()] });
        if (!settings.Values.KeepScoreOverrides)
        {
            trash.ClearOverrides(profileId);
        }

        var saved = profiles.Find(profileId)!;
        logger.LogInformation(
            "Quality profile '{Name}' {Action} from TRaSH {Templates}: {Formats} scored formats, {Created} formats installed, {Updated} updated",
            saved.Name, existing is null ? "created" : "updated", string.Join(" + ", new[] { movie?.Name, series?.Name }.OfType<string>()),
            saved.FormatItems.Count, install.Created, install.Updated);
        return new TrashApplyResult(saved, existing is null, install.Created, install.Updated, warnings);
    }

    /// <summary>
    /// POST /trash/apply: the profile, then (optionally) the quality sizes of its templates and naming variants. Naming
    /// variants are checked first, so an invalid request changes nothing.
    /// </summary>
    public TrashApplyResult ApplyWithExtras(TrashApplyRequest request, bool applyQualitySizes, TrashNaming? naming, TrashGuides guides)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(guides);
        MediaManagementValues? namingValues = null;
        if (naming is not null && new[] { naming.MovieFolder, naming.MovieFile, naming.SeriesFolder, naming.SeriesFile }.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            var errors = new Dictionary<string, string[]>();
            namingValues = NamingValues(naming, guides, errors);
            if (errors.Count > 0)
            {
                throw new ValidationFailedException(errors);
            }
        }

        var result = Apply(request, guides);
        var warnings = result.Warnings.ToList();
        if (applyQualitySizes)
        {
            var choices = new List<TrashSizeChoice>();
            if (request.MovieTemplate is { } movie && guides.Movie.Profile(movie) is { } m)
            {
                choices.Add(SizeChoiceFor(m, MediaType.Movie));
            }

            if (request.SeriesTemplate is { } series && guides.Series.Profile(series) is { } s)
            {
                choices.Add(SizeChoiceFor(s, MediaType.Series));
            }

            ApplyQualitySizes(choices, guides, warnings);
        }

        if (namingValues is not null)
        {
            SaveNaming(namingValues);
        }

        return result with { Warnings = warnings };
    }

    /// <summary>The formats (with scores) a profile gets: template formats, default groups, extra groups, extra formats.</summary>
    public static IReadOnlyList<TrashWantedFormat> WantedFormats(
        TrashQualityProfile? movie, TrashQualityProfile? series, TrashProfileOptions options, TrashGuides guides, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(guides);
        ArgumentNullException.ThrowIfNull(warnings);
        var result = new Dictionary<(MediaType, string), TrashWantedFormat>();
        var order = new List<(MediaType, string)>();

        string ScoreSet(MediaType mediaType) =>
            options.ScoreSet ?? (mediaType == MediaType.Series ? series : movie)?.ScoreSet ?? TrashGuide.DefaultScoreSet;

        void Add(MediaType mediaType, string trashId, int? score)
        {
            var format = guides.For(mediaType).Format(trashId);
            if (format is null)
            {
                warnings.Add($"Unknown {Label(mediaType)} custom format {trashId}; skipped.");
                return;
            }

            var key = (mediaType, format.TrashId);
            if (result.ContainsKey(key) && score is null)
            {
                return;
            }

            if (!result.ContainsKey(key))
            {
                order.Add(key);
            }

            result[key] = new TrashWantedFormat(mediaType, format.TrashId, format.Name, score ?? format.ScoreFor(ScoreSet(mediaType)) ?? 0, score is not null);
        }

        foreach (var (template, mediaType) in new[] { (movie, MediaType.Movie), (series, MediaType.Series) })
        {
            if (template is null)
            {
                continue;
            }

            foreach (var item in template.FormatItems)
            {
                Add(mediaType, item.Value, null);
            }

            foreach (var group in guides.For(mediaType).Groups.Where(g => g.Default && g.AppliesTo(template.TrashId)
                                                                          && !options.SkipGroups.Contains(g.TrashId, StringComparer.OrdinalIgnoreCase)))
            {
                foreach (var format in group.Formats.Where(f => f.Required || f.Default))
                {
                    Add(mediaType, format.TrashId, null);
                }
            }
        }

        foreach (var selection in options.Groups)
        {
            var found = false;
            foreach (var mediaType in new[] { MediaType.Movie, MediaType.Series })
            {
                if (guides.For(mediaType).Group(selection.TrashId) is not { } group)
                {
                    continue;
                }

                found = true;
                var picked = group.Formats.Where(f => f.Required
                                                      || (f.Default && !selection.Exclude.Contains(f.TrashId, StringComparer.OrdinalIgnoreCase))
                                                      || selection.SelectAll
                                                      || selection.Select.Contains(f.TrashId, StringComparer.OrdinalIgnoreCase)).ToList();

                // A group picked without a selection that has no required or default formats: all of them (else nothing would happen).
                if (picked.Count == 0 && selection.Select.Count == 0 && selection.Exclude.Count == 0)
                {
                    picked = [.. group.Formats];
                }

                foreach (var format in picked)
                {
                    Add(mediaType, format.TrashId, null);
                }
            }

            if (!found)
            {
                warnings.Add($"Unknown custom format group {selection.TrashId}; skipped.");
            }
        }

        foreach (var extra in options.ExtraFormats)
        {
            Add(extra.MediaType, extra.TrashId, extra.Score);
        }

        return [.. order.Select(k => result[k])];
    }

    /// <summary>
    /// The new scores: formats the last sync scored but no longer wants are removed, the wanted ones set, the user's
    /// overrides applied on top; with ResetUnmatched every other score goes too. Scores of 0 are dropped (0 = not scored).
    /// </summary>
    private IReadOnlyList<ProfileFormatItem> Scores(
        QualityProfile? existing, TrashProfileOptions? previous, TrashProfileOptions options, Dictionary<int, int> wanted, IReadOnlyDictionary<int, int> overrides)
    {
        var known = formats.All().ToDictionary(f => f.Id);
        var scores = (existing?.FormatItems ?? []).Where(i => known.ContainsKey(i.CustomFormatId)).ToDictionary(i => i.CustomFormatId, i => i.Score);
        foreach (var id in previous?.AppliedFormatIds ?? [])
        {
            if (!wanted.ContainsKey(id))
            {
                scores.Remove(id);
            }
        }

        if (options.ResetUnmatched)
        {
            foreach (var id in scores.Keys.Where(id => !wanted.ContainsKey(id) && !overrides.ContainsKey(id)
                                                       && !options.ResetExcept.Contains(known[id].Name, StringComparer.Ordinal)).ToList())
            {
                scores.Remove(id);
            }
        }

        foreach (var (id, score) in wanted)
        {
            scores[id] = score;
        }

        foreach (var (id, score) in overrides.Where(o => known.ContainsKey(o.Key)))
        {
            scores[id] = score;
        }

        return [.. scores.Where(s => s.Value != 0).OrderBy(s => s.Key).Select(s => new ProfileFormatItem(s.Key, s.Value))];
    }

    private static QualityProfile BuildProfile(
        QualityProfile? existing, string? name, TrashQualityProfile? movie, TrashQualityProfile? series, TrashProfileOptions options,
        IReadOnlyList<ProfileFormatItem> formatItems, List<string> warnings)
    {
        var primary = movie ?? series!;
        var primaryType = movie is not null ? MediaType.Movie : MediaType.Series;
        var items = options.Items ?? BuildItems(primary, primaryType, warnings);
        var cutoff = options.CutoffQualityId ?? CutoffOf(primary, primaryType, items);
        var language = options.LanguageId ?? LanguageOf(movie?.Language, warnings) ?? existing?.LanguageId ?? Language.Any.Id;

        var trimmed = name?.Trim();
        return new QualityProfile
        {
            Id = existing?.Id ?? 0,
            Name = trimmed is { Length: > 0 } ? trimmed : existing?.Name ?? primary.Name,
            Items = items,
            CutoffQualityId = cutoff,
            UpgradeAllowed = options.UpgradeAllowed ?? primary.UpgradeAllowed,
            LanguageId = language,
            PreferDualLanguage = existing?.PreferDualLanguage ?? false,
            FormatItems = formatItems,
            MinFormatScore = options.MinFormatScore ?? primary.MinFormatScore,
            CutoffFormatScore = options.CutoffFormatScore ?? primary.CutoffFormatScore,
            MinUpgradeFormatScore = options.MinUpgradeFormatScore ?? Math.Max(1, primary.MinUpgradeFormatScore),
            TrashMovieTemplate = movie?.TrashId,
            TrashSeriesTemplate = series?.TrashId,
        };
    }

    /// <summary>
    /// StreamZH items (worst first) from guide items (best first): groups become <see cref="QualityProfileItem.Group"/>,
    /// qualities the guide does not list are added as not allowed, unknown names are skipped with a warning.
    /// </summary>
    public static IReadOnlyList<QualityProfileItem> BuildItems(TrashQualityProfile template, MediaType mediaType, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(warnings);
        var listed = new List<QualityProfileItem>();
        var seen = new HashSet<int>();
        foreach (var item in template.Items.Reverse())
        {
            var names = item.IsGroup ? item.Qualities : [item.Name];
            var members = new List<Quality>();
            foreach (var name in names)
            {
                var quality = TrashQualityNames.Find(name, mediaType);
                if (quality is null)
                {
                    warnings.Add($"Unknown quality '{name}' in {template.Name}; skipped.");
                }
                else if (seen.Add(quality.Id))
                {
                    members.Add(quality);
                }
            }

            var index = Quality.All.ToList();
            foreach (var quality in members.OrderBy(index.IndexOf))
            {
                listed.Add(new QualityProfileItem(quality.Id, item.Allowed, item.IsGroup ? item.Name : null));
            }
        }

        var missing = Quality.All.Where(q => !seen.Contains(q.Id)).Select(q => new QualityProfileItem(q.Id, false));
        return [.. missing, .. listed];
    }

    /// <summary>The cutoff quality: the named quality, or for a group its best member; the best allowed quality as fallback.</summary>
    public static int CutoffOf(TrashQualityProfile template, MediaType mediaType, IReadOnlyList<QualityProfileItem> items)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(items);
        var cutoff = template.Cutoff;
        var item = template.Items.FirstOrDefault(i => string.Equals(i.Name, cutoff, StringComparison.OrdinalIgnoreCase));
        var candidates = item is { IsGroup: true } ? item.Qualities : cutoff is null ? [] : [cutoff];
        foreach (var name in candidates)
        {
            if (TrashQualityNames.Find(name, mediaType) is { } quality && items.Any(i => i.QualityId == quality.Id && i.Allowed))
            {
                return quality.Id;
            }
        }

        return items.LastOrDefault(i => i.Allowed)?.QualityId ?? Quality.Unknown.Id;
    }

    private static int? LanguageOf(string? name, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (string.Equals(name, "Any", StringComparison.OrdinalIgnoreCase))
        {
            return Language.Any.Id;
        }

        if (string.Equals(name, "Original", StringComparison.OrdinalIgnoreCase))
        {
            return Language.Original.Id;
        }

        if (Language.All.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) is { } language)
        {
            return language.Id;
        }

        warnings.Add($"Unknown profile language '{name}'; Any is used.");
        return Language.Any.Id;
    }

    private static TrashQualityProfile? Template(string? trashId, TrashGuide guide, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(trashId))
        {
            return null;
        }

        var template = guide.Profile(trashId.Trim());
        if (template is null)
        {
            errors[field] = [guide.Profiles.Count == 0
                ? "The TRaSH Guides are not downloaded yet; sync them first."
                : $"No {Label(guide.MediaType)} quality profile with the trash id {trashId} in the guides."];
        }

        return template;
    }

    // ---- Custom formats ----

    /// <summary>Result of installing formats: (media type, trash id) → format id.</summary>
    public sealed record InstallResult(IReadOnlyDictionary<(MediaType, string), int> Ids, int Created, int Updated);

    /// <summary>
    /// Installs or updates the guide formats: an existing format with the trash id (else with the name) is updated and
    /// linked to the guide; otherwise a new format is added.
    /// </summary>
    public InstallResult InstallFormats(IEnumerable<(MediaType MediaType, string TrashId)> wanted, TrashGuides guides, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(guides);
        ArgumentNullException.ThrowIfNull(warnings);
        var ids = new Dictionary<(MediaType, string), int>();
        int created = 0, updated = 0;
        foreach (var (mediaType, trashId) in wanted.Distinct())
        {
            if (guides.For(mediaType).Format(trashId) is not { } guide)
            {
                continue;
            }

            var existing = formats.FindByTrashId(guide.TrashId, mediaType) ?? formats.FindByName(guide.Name, mediaType);
            if (existing is null)
            {
                ids[(mediaType, trashId)] = formats.Add(ToFormat(guide, mediaType, guide.Name));
                created++;
                continue;
            }

            ids[(mediaType, trashId)] = existing.Id;
            if (Update(existing, guide, warnings))
            {
                updated++;
            }
        }

        return new InstallResult(ids, created, updated);
    }

    /// <summary>Brings every format linked to the guides (trash id set) up to date; returns how many changed.</summary>
    public int UpdateManagedFormats(TrashGuides guides, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(guides);
        var changed = 0;
        foreach (var format in formats.All().Where(f => f.TrashId is not null))
        {
            if (guides.For(format.MediaType).Format(format.TrashId!) is { } guide && Update(format, guide, warnings))
            {
                changed++;
            }
        }

        return changed;
    }

    private bool Update(CustomFormat existing, TrashCustomFormat guide, List<string> warnings)
    {
        var name = guide.Name;
        if (!string.Equals(existing.Name, name, StringComparison.Ordinal) && formats.FindByName(name, existing.MediaType) is { } other && other.Id != existing.Id)
        {
            warnings.Add($"Custom format '{existing.Name}' keeps its name: another format is already called '{name}'.");
            name = existing.Name;
        }

        var target = ToFormat(guide, existing.MediaType, name) with { Id = existing.Id };
        if (SameFormat(existing, target))
        {
            return false;
        }

        formats.Update(target);
        return true;
    }

    private static CustomFormat ToFormat(TrashCustomFormat guide, MediaType mediaType, string name) => new()
    {
        Name = name,
        MediaType = mediaType,
        IncludeInRename = guide.IncludeInRename,
        Specifications = guide.Specifications,
        TrashId = guide.TrashId,
    };

    private static bool SameFormat(CustomFormat a, CustomFormat b) =>
        a.Name == b.Name && a.IncludeInRename == b.IncludeInRename && string.Equals(a.TrashId, b.TrashId, StringComparison.OrdinalIgnoreCase)
        && System.Text.Json.JsonSerializer.Serialize(a.Specifications, CustomFormatJson) == System.Text.Json.JsonSerializer.Serialize(b.Specifications, CustomFormatJson);

    private static readonly System.Text.Json.JsonSerializerOptions CustomFormatJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    // ---- Quality sizes ----

    /// <summary>
    /// Applies guide size limits (MB per minute, like Radarr/Sonarr and StreamZH). StreamZH has one size table for
    /// movies and episodes, so with a movie and a series choice the limits are combined: the lower minimum and the higher
    /// maximum/preferred size, so neither movies nor episodes are rejected. Values at the slider end mean no limit.
    /// </summary>
    public int ApplyQualitySizes(IReadOnlyList<TrashSizeChoice> choices, TrashGuides guides, List<string> warnings, bool remember = true)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(guides);
        ArgumentNullException.ThrowIfNull(warnings);
        var limits = new Dictionary<int, List<(double Min, double? Preferred, double? Max)>>();
        foreach (var choice in choices)
        {
            var file = guides.For(choice.MediaType).QualitySize(choice.Type);
            if (file is null)
            {
                warnings.Add($"No {Label(choice.MediaType)} quality sizes of type '{choice.Type}' in the guides.");
                continue;
            }

            var (maxEnd, preferredEnd) = UnlimitedThreshold(choice.MediaType);
            foreach (var item in file.Qualities)
            {
                if (TrashQualityNames.Find(item.Quality, choice.MediaType) is not { } quality)
                {
                    warnings.Add($"Unknown quality '{item.Quality}' in the {choice.Type} quality sizes; skipped.");
                    continue;
                }

                var max = item.Max is { } m && m < maxEnd ? m : (double?)null;
                double? preferred = choice.PreferredRatio is { } ratio
                    ? Math.Round(item.Min + (((max ?? maxEnd) - item.Min) * Math.Clamp(ratio, 0, 1)), 1)
                    : item.Preferred;
                if (preferred is { } p && (p >= preferredEnd || (max is { } mx && p > mx)))
                {
                    preferred = p >= preferredEnd ? null : max;
                }

                if (!limits.TryGetValue(quality.Id, out var list))
                {
                    limits[quality.Id] = list = [];
                }

                list.Add((item.Min, preferred, max));
            }
        }

        if (limits.Count == 0)
        {
            return 0;
        }

        var combined = limits.Select(l => new QualityDefinition(
            l.Key,
            string.Empty,
            l.Value.Min(v => v.Min),
            l.Value.Any(v => v.Max is null) ? null : l.Value.Max(v => v.Max),
            l.Value.Any(v => v.Preferred is null) ? null : l.Value.Max(v => v.Preferred))).ToList();
        definitions.Save(combined);
        if (remember)
        {
            config.Set(ConfigKeys.TrashQualitySizes, FormatChoices(choices));
        }

        return combined.Count;
    }

    /// <summary>The quality size choice the sync re-applies (empty when none was applied).</summary>
    public IReadOnlyList<TrashSizeChoice> RememberedSizeChoices() => ParseChoices(config.Get(ConfigKeys.TrashQualitySizes));

    /// <summary>"anime" sizes for anime templates, else "movie" (Radarr) or "series" (Sonarr).</summary>
    public static TrashSizeChoice SizeChoiceFor(TrashQualityProfile template, MediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(template);
        var anime = template.Slug.Contains("anime", StringComparison.OrdinalIgnoreCase) || template.Name.Contains("Anime", StringComparison.OrdinalIgnoreCase);
        return new TrashSizeChoice(mediaType, anime ? "anime" : mediaType == MediaType.Series ? "series" : "movie");
    }

    private static string FormatChoices(IReadOnlyList<TrashSizeChoice> choices) => string.Join(
        ',',
        choices.Select(c => $"{(c.MediaType == MediaType.Series ? "series" : "movie")}:{c.Type}"
                            + (c.PreferredRatio is { } r ? ":" + r.ToString(CultureInfo.InvariantCulture) : string.Empty)));

    private static List<TrashSizeChoice> ParseChoices(string? text) =>
    [
        .. (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Split(':'))
            .Where(p => p.Length >= 2)
            .Select(p => new TrashSizeChoice(
                p[0] == "series" ? MediaType.Series : MediaType.Movie,
                p[1],
                p.Length > 2 && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null)),
    ];

    // ---- Naming ----

    /// <summary>The Media Management values with the chosen variants; errors keyed "naming.movieFolder" etc.</summary>
    public MediaManagementValues NamingValues(TrashNaming naming, TrashGuides guides, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(naming);
        ArgumentNullException.ThrowIfNull(guides);
        ArgumentNullException.ThrowIfNull(errors);
        var values = mediaManagement.Current;

        string? Variant(string? key, TrashGuide guide, string section, string field)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (guide.Naming.GetValueOrDefault(section)?.GetValueOrDefault(key.Trim()) is { } format)
            {
                return format;
            }

            var known = guide.Naming.GetValueOrDefault(section)?.Keys ?? [];
            errors[$"naming.{field}"] = [$"Unknown variant '{key}'. Use one of: {string.Join(", ", known)}."];
            return null;
        }

        var movieFolder = Variant(naming.MovieFolder, guides.Movie, "folder", "movieFolder");
        var movieFile = Variant(naming.MovieFile, guides.Movie, "file", "movieFile");
        var seriesFolder = Variant(naming.SeriesFolder, guides.Series, "series", "seriesFolder");
        var seriesFile = Variant(naming.SeriesFile, guides.Series, "episodes.standard", "seriesFile");
        var key = naming.SeriesFile?.Trim() ?? string.Empty;
        var daily = seriesFile is null ? null : guides.Series.Naming.GetValueOrDefault("episodes.daily")?.GetValueOrDefault(key);
        var anime = seriesFile is null ? null : guides.Series.Naming.GetValueOrDefault("episodes.anime")?.GetValueOrDefault(key);

        var updated = values with
        {
            MovieFolderFormat = movieFolder ?? values.MovieFolderFormat,
            StandardMovieFormat = movieFile ?? values.StandardMovieFormat,
            SeriesFolderFormat = seriesFolder ?? values.SeriesFolderFormat,
            StandardEpisodeFormat = seriesFile ?? values.StandardEpisodeFormat,
            DailyEpisodeFormat = daily ?? values.DailyEpisodeFormat,
            AnimeEpisodeFormat = anime ?? values.AnimeEpisodeFormat,
        };

        var fields = new Dictionary<string, string>
        {
            ["movieFolderFormat"] = "movieFolder",
            ["standardMovieFormat"] = "movieFile",
            ["seriesFolderFormat"] = "seriesFolder",
            ["standardEpisodeFormat"] = "seriesFile",
            ["dailyEpisodeFormat"] = "seriesFile",
            ["animeEpisodeFormat"] = "seriesFile",
        };
        foreach (var (field, messages) in MediaManagementSettings.Validate(updated))
        {
            if (fields.TryGetValue(field, out var target))
            {
                errors.TryAdd($"naming.{target}", [.. messages.Select(m => $"StreamZH cannot use this variant: {m}")]);
            }
        }

        return updated;
    }

    public void SaveNaming(MediaManagementValues values) => mediaManagement.Update(values);

    private static string Label(MediaType mediaType) => mediaType == MediaType.Series ? "series" : "movie";
}
