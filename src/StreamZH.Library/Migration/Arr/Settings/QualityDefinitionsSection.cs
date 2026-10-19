// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Profiles;
using StreamZH.Data;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Quality sizes (/qualitydefinition): minimum, preferred and maximum size per quality. Radarr, Sonarr and StreamZH all
/// count MB per minute of runtime (a movie's or an episode's), so the numbers carry over unchanged. StreamZH has one table
/// for movies and episodes: a quality StreamZH still has at its default is replaced, one that differs already (taken
/// over from the other app or set by hand) is widened to cover both (lower minimum, higher maximum).
/// </summary>
public sealed class QualityDefinitionsSection(string source) : ArrSettingsSection(source)
{
    public override string Key => "qualityDefinitions";

    public override string Label => "Quality sizes";

    public override string Description =>
        "Minimum, preferred and maximum size per quality in MB per minute. StreamZH has one size table for movies and episodes; " +
        "sizes it already has from the other app are widened so both fit.";

    public override int Order => 150;

    public override bool DefaultSelected => false;

    public override bool Advanced => true;

    protected override async Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken)
    {
        var plan = new SettingsPlan();
        var current = reader.Service<QualityDefinitionRepository>().All().ToDictionary(d => d.QualityId);
        var defaults = QualityDefaults.Definitions.ToDictionary(d => d.QualityId);
        var other = IsRadarr ? "Sonarr" : "Radarr";
        var draft = new Dictionary<int, QualityDefinition>();
        var seen = new HashSet<int>();

        foreach (var definition in await reader.GetListAsync("/qualitydefinition", cancellationToken))
        {
            var oldQuality = ArrJson.Get(definition, "quality");
            var oldName = oldQuality is { } q ? ArrJson.Str(q, "name") : null;
            var key = ItemKey(oldQuality is { } qi ? ArrJson.Int(qi, "id") ?? 0 : ArrJson.Int(definition, "id") ?? 0);
            var quality = oldQuality is { } qm ? ArrQualities.Map(Source, qm) : null;
            if (quality is null)
            {
                plan.Skip(key, oldName ?? ArrJson.Str(definition, "title") ?? key, "StreamZH does not know this quality.");
                continue;
            }

            if (!seen.Add(quality.Id))
            {
                continue;
            }

            var min = Math.Max(0, ArrJson.Double(definition, "minSize") ?? 0);
            var max = ArrJson.Double(definition, "maxSize");
            var preferred = ArrJson.Double(definition, "preferredSize");
            var old = Valid(new QualityDefinition(quality.Id, quality.Name, min, max, preferred));
            var fallback = defaults.GetValueOrDefault(quality.Id) ?? new QualityDefinition(quality.Id, quality.Name, 0, null, null);
            var now = current.GetValueOrDefault(quality.Id) ?? fallback;
            var untouched = Same(now, fallback);
            var target = untouched ? old with { Title = now.Title } : Widen(now, old);
            if (Same(target, now))
            {
                plan.Skip(key, quality.Name, "Already the same.", Describe(now));
                continue;
            }

            var warning = !untouched && !Same(target, old)
                ? $"StreamZH had other sizes for {quality.Name} ({Describe(now)}, from {other} or set by hand); it keeps the wider range {Describe(target)}."
                : null;
            plan.Add(
                new MigrationItem(key, quality.Name, MigrationAction.Update, $"{Describe(now)} → {Describe(target)}", Warning: warning),
                _ => draft[target.QualityId] = target);
        }

        plan.Commit = _ =>
        {
            if (draft.Count > 0)
            {
                reader.Service<QualityDefinitionService>().Save([.. draft.Values]);
            }
        };
        return plan;
    }

    /// <summary>The lower minimum, the higher maximum (no limit wins) and the higher preferred size of both.</summary>
    public static QualityDefinition Widen(QualityDefinition current, QualityDefinition incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        var min = Math.Min(current.MinSize, incoming.MinSize);
        double? max = current.MaxSize is { } a && incoming.MaxSize is { } b ? Math.Max(a, b) : null;
        double? preferred = current.PreferredSize is { } p && incoming.PreferredSize is { } r ? Math.Max(p, r) : null;
        return Valid(current with { MinSize = min, MaxSize = max, PreferredSize = preferred });
    }

    /// <summary>Puts the preferred size between minimum and maximum (what StreamZH accepts).</summary>
    private static QualityDefinition Valid(QualityDefinition d)
    {
        var max = d.MaxSize is { } m && m < d.MinSize ? d.MinSize : d.MaxSize;
        var preferred = d.PreferredSize is { } p ? Math.Clamp(p, d.MinSize, max ?? double.MaxValue) : (double?)null;
        return d with { MaxSize = max, PreferredSize = preferred };
    }

    private static bool Same(QualityDefinition a, QualityDefinition b) =>
        Near(a.MinSize, b.MinSize) && Near(a.MaxSize, b.MaxSize) && Near(a.PreferredSize, b.PreferredSize);

    private static bool Near(double? a, double? b) => (a, b) switch
    {
        (null, null) => true,
        ({ } x, { } y) => Math.Abs(x - y) < 0.001,
        _ => false,
    };

    /// <summary>"2 / 95 / 130 MB/min" (minimum / preferred / maximum, "no limit" for none).</summary>
    public static string Describe(QualityDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);
        static string N(double? v) => v is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "no limit";
        return $"{N(d.MinSize)} / {N(d.PreferredSize)} / {N(d.MaxSize)} MB/min";
    }
}
