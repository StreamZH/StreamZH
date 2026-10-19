// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Import list exclusions: Radarr's /exclusions (TMDB ids) become StreamZH exclusions directly. Sonarr's /importlistexclusion
/// has TVDB ids only; StreamZH needs the TMDB id, so an exclusion is taken over when the series is already in StreamZH
/// (its TVDB id is known there) and otherwise skipped: it follows with the series lookup.
/// </summary>
public sealed class ImportListExclusionsSection(string source) : ArrSettingsSection(source)
{
    public override string Key => "importListExclusions";

    public override string Label => "List exclusions";

    public override string Description => IsRadarr
        ? "Movies your import lists must never add."
        : "Series your import lists must never add. Sonarr knows them by TVDB id; StreamZH needs the TMDB id.";

    public override int Order => 450;

    protected override async Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken)
    {
        var plan = new SettingsPlan();
        var repository = reader.Service<ImportListRepository>();
        var excluded = repository.Exclusions().Where(e => e.MediaType == reader.MediaType).Select(e => e.TmdbId).ToHashSet();
        var planned = new HashSet<int>();

        if (IsRadarr)
        {
            foreach (var exclusion in await reader.GetListAsync("/exclusions", cancellationToken))
            {
                var tmdbId = ArrJson.Int(exclusion, "tmdbId") ?? 0;
                var title = ArrJson.Str(exclusion, "movieTitle")?.Trim() ?? string.Empty;
                var year = ArrJson.Int(exclusion, "movieYear") ?? 0;
                var label = ItemLabel(title, year, $"TMDB {tmdbId}");
                Add(plan, reader, ItemKey(tmdbId), label, tmdbId, title, year, excluded, planned);
            }

            return plan;
        }

        var series = reader.Service<SeriesRepository>().All().Where(s => s.TvdbId is > 0).GroupBy(s => s.TvdbId!.Value).ToDictionary(g => g.Key, g => g.First());
        foreach (var exclusion in await reader.GetListAsync("/importlistexclusion", cancellationToken))
        {
            var tvdbId = ArrJson.Int(exclusion, "tvdbId") ?? 0;
            var title = ArrJson.Str(exclusion, "title")?.Trim() ?? string.Empty;
            var key = ItemKey(tvdbId);
            if (series.TryGetValue(tvdbId, out var known) && known.TmdbId > 0)
            {
                Add(plan, reader, key, ItemLabel(title, known.Year, $"TVDB {tvdbId}"), known.TmdbId, title.Length > 0 ? title : known.Title, known.Year, excluded, planned);
                continue;
            }

            plan.Skip(key, ItemLabel(title, 0, $"TVDB {tvdbId}"), "Needs the TMDB id; follows with the series (TVDB → TMDB lookup).", $"TVDB {tvdbId}");
        }

        return plan;
    }

    private static void Add(
        SettingsPlan plan, ArrReader reader, string key, string label, int tmdbId, string title, int year, HashSet<int> excluded, HashSet<int> planned)
    {
        if (tmdbId <= 0)
        {
            plan.Skip(key, label, "It has no TMDB id.");
            return;
        }

        if (excluded.Contains(tmdbId) || !planned.Add(tmdbId))
        {
            plan.Add(new MigrationItem(key, label, MigrationAction.Link, $"TMDB {tmdbId}", "StreamZH excludes it already."));
            return;
        }

        var mediaType = reader.MediaType;
        plan.Add(new MigrationItem(key, label, MigrationAction.Create, $"TMDB {tmdbId}"), _ =>
            reader.Service<ImportListRepository>().AddExclusion(
                mediaType, tmdbId, title.Length == 0 ? $"TMDB {tmdbId}" : title.Length > 300 ? title[..300] : title, Math.Max(0, year), DateTime.UtcNow));
    }

    private static string ItemLabel(string title, int year, string fallback) =>
        title.Length == 0 ? fallback : year > 0 ? $"{title} ({year})" : title;
}
