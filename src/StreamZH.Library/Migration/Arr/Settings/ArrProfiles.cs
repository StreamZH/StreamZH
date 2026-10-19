// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>The qualities of a Radarr/Sonarr profile in StreamZH terms.</summary>
/// <param name="Items">All StreamZH qualities from worst to best; the profile's groups kept as <see cref="QualityProfileItem.Group"/>.</param>
/// <param name="CutoffQualityId">An allowed quality (a group cutoff becomes the group's first quality).</param>
public sealed record ArrProfileQualities(IReadOnlyList<QualityProfileItem> Items, int CutoffQualityId, IReadOnlyList<string> Warnings);

/// <summary>The language a Sonarr v3 language profile stands for in a StreamZH profile.</summary>
public sealed record ArrLanguageChoice(Language Language, bool PreferDualLanguage, string? Warning);

/// <summary>
/// Turns Radarr/Sonarr quality profiles into StreamZH's: flattens quality groups, maps Sonarr's quality ids, resolves the
/// cutoff, reads Sonarr v3 language profiles, and merges a profile into an existing StreamZH profile of the same name.
/// </summary>
public static class ArrProfiles
{
    /// <summary>
    /// The profile's items ([{quality, allowed}] or groups [{id, name, items, allowed}], worst first, as Radarr and Sonarr
    /// send them) as StreamZH items. StreamZH qualities the app does not have (Sonarr has no CAM, BR-DISK …) are added as
    /// not allowed at their place by weight.
    /// </summary>
    public static ArrProfileQualities Qualities(string source, JsonElement profile)
    {
        var warnings = new List<string>();
        var items = new List<QualityProfileItem>();
        var seen = new HashSet<int>();
        var cutoff = ArrJson.Int(profile, "cutoff");
        int? cutoffQuality = null;

        void Add(JsonElement entry, bool allowed, string? group)
        {
            if (ArrJson.Get(entry, "quality") is not { ValueKind: JsonValueKind.Object } quality)
            {
                return;
            }

            var mapped = ArrQualities.Map(source, quality);
            if (mapped is null)
            {
                warnings.Add($"StreamZH does not know the quality {ArrJson.Str(quality, "name") ?? "?"}; it is left out.");
                return;
            }

            if (!seen.Add(mapped.Id))
            {
                return;
            }

            items.Add(new QualityProfileItem(mapped.Id, allowed, group));
            if (group is null && cutoff is { } c && ArrJson.Int(quality, "id") == c)
            {
                cutoffQuality = mapped.Id;
            }
        }

        foreach (var entry in ArrJson.Array(profile, "items"))
        {
            var allowed = ArrJson.Bool(entry, "allowed");
            var children = ArrJson.Array(entry, "items");
            if (children.Count == 0)
            {
                Add(entry, allowed, null);
                continue;
            }

            // A group: its qualities share its allowed flag and rank equally in StreamZH.
            var name = ArrJson.Str(entry, "name")?.Trim();
            var group = string.IsNullOrEmpty(name) ? $"Group {ArrJson.Int(entry, "id")}" : name;
            var first = items.Count;
            foreach (var child in children)
            {
                Add(child, allowed, group);
            }

            if (cutoff is { } groupCutoff && ArrJson.Int(entry, "id") == groupCutoff && items.Count > first)
            {
                cutoffQuality = items[first].QualityId;
            }
        }

        foreach (var missing in Quality.All.Where(q => !seen.Contains(q.Id)))
        {
            InsertByWeight(items, new QualityProfileItem(missing.Id, false));
        }

        if (cutoffQuality is not { } resolved || !items.Any(i => i.QualityId == resolved && i.Allowed))
        {
            var best = items.LastOrDefault(i => i.Allowed);
            if (best is not null && items.Any(i => i.Allowed))
            {
                if (cutoff is not null)
                {
                    warnings.Add($"The cutoff is not an allowed quality in StreamZH; it is {Quality.FindById(best.QualityId).Name} now.");
                }

                cutoffQuality = best.QualityId;
            }
        }

        return new ArrProfileQualities(items, cutoffQuality ?? Quality.Unknown.Id, warnings);
    }

    /// <summary>Puts an item before the first better quality, never inside a group.</summary>
    public static void InsertByWeight(List<QualityProfileItem> items, QualityProfileItem item)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(item);
        var weight = Quality.FindById(item.QualityId).Weight;
        var index = items.FindIndex(i => Quality.FindById(i.QualityId).Weight > weight);
        if (index < 0)
        {
            items.Add(item);
            return;
        }

        while (index > 0 && index < items.Count && items[index].Group is { } group && items[index - 1].Group == group)
        {
            index++;
        }

        items.Insert(index, item);
    }

    /// <summary>
    /// What a Sonarr v3 language profile ({cutoff, languages: [{language, allowed}]}) means for a StreamZH profile, which
    /// asks for one language: one allowed language is that language; several make the cutoff the wanted language (German
    /// with English allowed: German, preferring dual-language releases) because StreamZH has no fallback language.
    /// </summary>
    public static ArrLanguageChoice LanguageProfile(JsonElement languageProfile)
    {
        var allowed = ArrJson.Array(languageProfile, "languages")
            .Where(l => ArrJson.Bool(l, "allowed"))
            .Select(l => ArrJson.Get(l, "language") is { } language ? ArrQualities.MapLanguage(MigrationSources.Sonarr, language) : null)
            .OfType<Language>()
            .Where(l => l.Id > 0)
            .Distinct()
            .ToList();
        var cutoff = ArrJson.Get(languageProfile, "cutoff") is { } c ? ArrQualities.MapLanguage(MigrationSources.Sonarr, c) : null;
        var name = ArrJson.Str(languageProfile, "name") ?? "the language profile";

        if (allowed.Count == 0)
        {
            return new ArrLanguageChoice(Language.Any, false, null);
        }

        if (allowed.Count == 1)
        {
            return new ArrLanguageChoice(allowed[0], false, null);
        }

        if (cutoff is not null && allowed.Contains(cutoff))
        {
            var others = string.Join(", ", allowed.Where(l => l != cutoff).Select(l => l.Name));
            return new ArrLanguageChoice(
                cutoff,
                true,
                $"Sonarr's {name} also accepts {others} until {cutoff.Name} is found. StreamZH asks for {cutoff.Name} and prefers dual-language releases; it does not grab {others}-only releases.");
        }

        return new ArrLanguageChoice(Language.Any, false, $"Sonarr's {name} allows several languages; the StreamZH profile accepts any language.");
    }

    /// <summary>
    /// True when the StreamZH profile has scores of the other media type's formats (or follows a TRaSH template of it): then
    /// it already holds the other app's part, and the qualities are merged instead of replaced.
    /// </summary>
    public static bool HasOtherSide(QualityProfile profile, IReadOnlyDictionary<int, MediaType> formatTypes, MediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(formatTypes);
        var other = mediaType == MediaType.Movie ? MediaType.Series : MediaType.Movie;
        var template = other == MediaType.Movie ? profile.TrashMovieTemplate : profile.TrashSeriesTemplate;
        return template is not null
            || profile.FormatItems.Any(i => i.Score != 0 && formatTypes.TryGetValue(i.CustomFormatId, out var type) && type == other);
    }

    /// <summary>The scores of the other media type's formats kept, this media type's replaced by <paramref name="scores"/>.</summary>
    public static IReadOnlyList<ProfileFormatItem> ReplaceScores(
        IReadOnlyList<ProfileFormatItem> existing, IReadOnlyList<ProfileFormatItem> scores, IReadOnlyDictionary<int, MediaType> formatTypes, MediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(formatTypes);
        return
        [
            .. existing.Where(i => i.Score != 0 && formatTypes.TryGetValue(i.CustomFormatId, out var type) && type != mediaType),
            .. scores.Where(i => i.Score != 0),
        ];
    }

    /// <summary>The qualities of both: everything either profile allows is allowed (the existing order and groups stay).</summary>
    public static IReadOnlyList<QualityProfileItem> UnionAllowed(IReadOnlyList<QualityProfileItem> existing, IReadOnlyList<QualityProfileItem> incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        var wanted = incoming.Where(i => i.Allowed).Select(i => i.QualityId).ToHashSet();
        var result = existing.Select(i => wanted.Contains(i.QualityId) && !i.Allowed ? i with { Allowed = true } : i).ToList();
        foreach (var missing in incoming.Where(i => i.Allowed && result.All(r => r.QualityId != i.QualityId)))
        {
            InsertByWeight(result, missing with { Group = null });
        }

        return result;
    }

    /// <summary>Same qualities, cutoff, language, switches and scores (score order does not matter).</summary>
    public static bool Equivalent(QualityProfile a, QualityProfile b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        static IEnumerable<ProfileFormatItem> Scores(QualityProfile p) => p.FormatItems.Where(i => i.Score != 0).OrderBy(i => i.CustomFormatId);
        return a.Name == b.Name
            && a.Items.SequenceEqual(b.Items)
            && a.CutoffQualityId == b.CutoffQualityId
            && a.UpgradeAllowed == b.UpgradeAllowed
            && a.LanguageId == b.LanguageId
            && a.PreferDualLanguage == b.PreferDualLanguage
            && a.MinFormatScore == b.MinFormatScore
            && a.CutoffFormatScore == b.CutoffFormatScore
            && a.MinUpgradeFormatScore == b.MinUpgradeFormatScore
            && Scores(a).SequenceEqual(Scores(b));
    }
}
