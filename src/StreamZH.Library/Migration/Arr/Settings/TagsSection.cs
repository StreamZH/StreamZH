// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Tags (/tag/detail, else /tag). StreamZH has no tags: Radarr and Sonarr use them to limit release profiles, indexers,
/// download clients and connections to some movies or series, and in StreamZH those apply to everything. The section lists
/// each tag with what uses it, so the user sees what becomes broader, and takes nothing over.
/// </summary>
public sealed class TagsSection(string source) : ArrSettingsSection(source)
{
    private static readonly (string Property, string Singular, string Plural)[] Uses =
    [
        ("movieIds", "movie", "movies"),
        ("seriesIds", "series", "series"),
        ("restrictionIds", "release profile", "release profiles"),
        ("releaseProfileIds", "release profile", "release profiles"),
        ("indexerIds", "indexer", "indexers"),
        ("downloadClientIds", "download client", "download clients"),
        ("notificationIds", "connection", "connections"),
        ("importListIds", "import list", "import lists"),
        ("delayProfileIds", "delay profile", "delay profiles"),
        ("autoTagIds", "auto tag", "auto tags"),
    ];

    public override string Key => "tags";

    public override string Label => "Tags";

    public override string Description =>
        "StreamZH has no tags. Release profiles, indexers and connections that a tag limits to some items apply to everything in StreamZH.";

    public override int Order => 180;

    public override bool DefaultSelected => false;

    protected override async Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken)
    {
        var plan = new SettingsPlan();
        IReadOnlyList<JsonElement> tags;
        try
        {
            tags = await reader.GetListAsync("/tag/detail", cancellationToken);
        }
        catch (ArrEndpointMissingException)
        {
            tags = await reader.GetListAsync("/tag", cancellationToken);
        }

        foreach (var tag in tags)
        {
            var id = ArrJson.Int(tag, "id") ?? 0;
            var label = ArrJson.Str(tag, "label") ?? $"Tag {id}";
            var used = Uses
                .Select(u => (u, Count: ArrJson.Array(tag, u.Property).Count))
                .Where(x => x.Count > 0)
                .Select(x => $"{x.Count} {(x.Count == 1 ? x.u.Singular : x.u.Plural)}")
                .ToList();
            plan.Skip(
                ItemKey(id),
                label,
                "StreamZH has no tags; what this tag limits applies to everything.",
                used.Count == 0 ? "Not used" : "Used by " + string.Join(", ", used));
        }

        return plan;
    }
}
