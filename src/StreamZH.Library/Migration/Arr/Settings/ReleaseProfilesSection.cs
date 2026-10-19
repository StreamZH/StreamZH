// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Decisions;
using StreamZH.Data;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Release profiles: Sonarr's /releaseprofile (v3 with preferred words and scores, v4 must/must not contain only), Radarr
/// v5's /releaseprofile and Radarr v4's /restriction. StreamZH's release profiles have the same terms (plain text or
/// /regex/i) and Sonarr v3's preferred words with scores, so they carry over directly; matched by name. StreamZH has no
/// tags, and its release profiles apply to movies and series alike.
/// </summary>
public sealed class ReleaseProfilesSection(string source) : ArrSettingsSection(source)
{
    /// <summary>Preferred word scores StreamZH accepts (Settings → Indexers → Release profiles).</summary>
    public const int MaxScore = 10_000;

    public const int MaxNameLength = 100;

    public override string Key => "releaseProfiles";

    public override string Label => "Release profiles";

    public override string Description => IsRadarr
        ? "Must contain and must not contain terms (restrictions in Radarr v4). In StreamZH they apply to movies and series."
        : "Must contain, must not contain and Sonarr v3's preferred words with their scores. In StreamZH they apply to movies and series.";

    public override int Order => 270;

    protected override async Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken)
    {
        var plan = new SettingsPlan();
        var restrictions = IsRadarr && reader.Major < 5;
        var list = await reader.GetListAsync(restrictions ? "/restriction" : "/releaseprofile", cancellationToken);
        var tagLabels = list.Any(p => ArrJson.Ints(p, "tags").Count > 0) ? await Labels(reader, "/tag", "label", cancellationToken) : [];
        var indexerNames = list.Any(p => ArrJson.Int(p, "indexerId") > 0) ? await Labels(reader, "/indexer", "name", cancellationToken) : [];
        var existing = reader.Service<ReleaseProfileRepository>().All();
        var indexers = reader.OptionalService<IndexerRepository>();
        var otherMedia = IsRadarr ? "series" : "movies";

        foreach (var profile in list)
        {
            var id = ArrJson.Int(profile, "id") ?? 0;
            var key = ItemKey(id);
            var name = ArrJson.Str(profile, "name")?.Trim() is { Length: > 0 } n
                ? n
                : $"{reader.AppName} {(restrictions ? "restriction" : "release profile")} {id}";
            if (name.Length > MaxNameLength)
            {
                name = name[..MaxNameLength].TrimEnd();
            }

            var warnings = new List<string>();
            var required = Terms(ArrJson.Strings(ArrJson.Get(profile, "required")), warnings);
            var ignored = Terms(ArrJson.Strings(ArrJson.Get(profile, "ignored")), warnings);
            var preferred = Preferred(ArrJson.Array(profile, "preferred"), warnings);
            if (required.Count + ignored.Count + preferred.Count == 0)
            {
                plan.Skip(key, name, "It has no terms StreamZH can use.", warning: warnings.Count > 0 ? string.Join(" ", warnings) : null);
                continue;
            }

            var indexerIds = new List<int>();
            if (ArrJson.Int(profile, "indexerId") is { } oldIndexer && oldIndexer > 0)
            {
                var mapped = reader.Context.Ids.Get("indexer", oldIndexer)
                    ?? (indexerNames.GetValueOrDefault(oldIndexer) is { } indexerName ? indexers?.FindByName(indexerName)?.Id : null);
                if (mapped is { } indexerId)
                {
                    indexerIds.Add(indexerId);
                }
                else
                {
                    warnings.Add($"{reader.AppName} used it for the indexer {indexerNames.GetValueOrDefault(oldIndexer) ?? "#" + oldIndexer}; StreamZH does not have it (yet), so the profile applies to all indexers.");
                }
            }

            var tags = ArrJson.Ints(profile, "tags");
            if (tags.Count > 0)
            {
                var labels = string.Join(", ", tags.Select(t => tagLabels.GetValueOrDefault(t) ?? "#" + t));
                warnings.Add($"{reader.AppName} used it only for items tagged {labels}; StreamZH has no tags, so it applies to everything.");
            }

            if (required.Count > 0)
            {
                warnings.Add($"The must contain terms now also apply to {otherMedia}.");
            }

            if (ArrJson.Bool(profile, "includePreferredWhenRenaming"))
            {
                warnings.Add("StreamZH does not put preferred words into file names; use {Custom Formats} in the naming instead.");
            }

            var target = new ReleaseProfile
            {
                Name = name,
                Enabled = ArrJson.Bool(profile, "enabled", true),
                Required = required,
                Ignored = ignored,
                Preferred = preferred,
                IndexerIds = indexerIds,
            };
            var detail = Detail(target);
            var warning = warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct());
            var match = existing.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                plan.Add(new MigrationItem(key, name, MigrationAction.Create, detail, Warning: warning), _ => reader.Service<ReleaseProfileRepository>().Add(target));
                continue;
            }

            var updated = target with { Id = match.Id, QualityProfileIds = match.QualityProfileIds };
            if (Same(updated, match))
            {
                plan.Add(new MigrationItem(key, name, MigrationAction.Link, detail, "Already the same.", warning));
                continue;
            }

            plan.Add(new MigrationItem(key, name, MigrationAction.Update, detail, Warning: warning), _ => reader.Service<ReleaseProfileRepository>().Update(updated));
        }

        return plan;
    }

    private static List<string> Terms(IReadOnlyList<string> terms, List<string> warnings)
    {
        var result = new List<string>();
        foreach (var term in terms.Distinct(StringComparer.Ordinal))
        {
            if (ReleaseTerms.Validate(term) is { } problem)
            {
                warnings.Add($"Left out: {problem}");
                continue;
            }

            result.Add(term);
        }

        return result;
    }

    /// <summary>Sonarr v3's preferred words: [{"key": "term", "value": score}].</summary>
    private static List<PreferredTerm> Preferred(IReadOnlyList<JsonElement> entries, List<string> warnings)
    {
        var result = new List<PreferredTerm>();
        foreach (var entry in entries)
        {
            var term = ArrJson.Str(entry, "key")?.Trim() ?? string.Empty;
            var score = ArrJson.Int(entry, "value") ?? 0;
            if (ReleaseTerms.Validate(term) is { } problem)
            {
                warnings.Add($"Left out: {problem}");
                continue;
            }

            if (score == 0 || result.Any(r => r.Term == term))
            {
                continue;
            }

            if (Math.Abs(score) > MaxScore)
            {
                warnings.Add($"The score of '{term}' is limited to {(score > 0 ? MaxScore : -MaxScore)} (StreamZH allows -{MaxScore} to {MaxScore}).");
                score = Math.Clamp(score, -MaxScore, MaxScore);
            }

            result.Add(new PreferredTerm(term, score));
        }

        return result;
    }

    private static bool Same(ReleaseProfile a, ReleaseProfile b) =>
        a.Name == b.Name && a.Enabled == b.Enabled && a.Required.SequenceEqual(b.Required) && a.Ignored.SequenceEqual(b.Ignored)
        && a.Preferred.SequenceEqual(b.Preferred) && a.IndexerIds.SequenceEqual(b.IndexerIds) && a.QualityProfileIds.SequenceEqual(b.QualityProfileIds);

    private static string Detail(ReleaseProfile p)
    {
        var parts = new List<string>();
        if (p.Required.Count > 0)
        {
            parts.Add($"must contain {p.Required.Count}");
        }

        if (p.Ignored.Count > 0)
        {
            parts.Add($"must not contain {p.Ignored.Count}");
        }

        if (p.Preferred.Count > 0)
        {
            parts.Add($"{p.Preferred.Count} preferred");
        }

        if (!p.Enabled)
        {
            parts.Add("off");
        }

        return string.Join(" · ", parts);
    }

    private static async Task<Dictionary<int, string>> Labels(ArrReader reader, string path, string property, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        IReadOnlyList<JsonElement> entries;
        try
        {
            entries = await reader.GetListAsync(path, cancellationToken);
        }
        catch (ArrEndpointMissingException)
        {
            return result;
        }

        foreach (var entry in entries)
        {
            if (ArrJson.Int(entry, "id") is { } id && ArrJson.Str(entry, property) is { } text)
            {
                result[id] = text;
            }
        }

        return result;
    }
}
