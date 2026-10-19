// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using StreamZH.Core.CustomFormats;
using StreamZH.Data;
using StreamZH.Library.CustomFormats;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Custom formats (/customformat). StreamZH's custom formats have the same shape as Radarr's and Sonarr's (Radarr's enum
/// values for movie formats, Sonarr's for series formats), so they carry over 1:1 and are matched by name and media type.
/// A StreamZH format that follows the TRaSH Guides keeps its conditions (the TRaSH sync owns them); only its scores come
/// over with the quality profiles. Sonarr v3 has no custom formats.
/// </summary>
public sealed class CustomFormatsSection(string source) : ArrSettingsSection(source)
{
    public const string IdKind = "customFormat";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override string Key => "customFormats";

    public override string Label => "Custom formats";

    public override string Description => IsRadarr
        ? "Custom formats for movies, matched by name. Formats StreamZH keeps in sync with the TRaSH Guides keep their conditions."
        : "Custom formats for series, matched by name. Formats StreamZH keeps in sync with the TRaSH Guides keep their conditions.";

    public override int Order => 200;

    protected override async Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken)
    {
        var plan = new SettingsPlan();
        if (reader.IsSonarrV3)
        {
            plan.Warnings.Add("Sonarr v3 has no custom formats. Its preferred words come over with the release profiles.");
            return plan;
        }

        var repository = reader.Service<CustomFormatRepository>();
        foreach (var format in await reader.GetListAsync("/customformat", cancellationToken))
        {
            var id = ArrJson.Int(format, "id") ?? 0;
            var key = ItemKey(id);
            var name = ArrJson.Str(format, "name")?.Trim() ?? string.Empty;
            var label = name.Length > 0 ? name : $"Custom format {id}";
            if (JsonNode.Parse(format.GetRawText()) is not JsonObject node)
            {
                continue;
            }

            var parsed = CustomFormatSchema.Normalize(CustomFormatService.FromJson(node, reader.MediaType)) with { TrashId = null };
            var errors = CustomFormatSchema.Validate(parsed);
            var detail = Detail(parsed);
            if (errors.Count > 0)
            {
                plan.Skip(key, label, "StreamZH cannot use this format: " + string.Join(" ", errors.Values.SelectMany(v => v)), detail);
                continue;
            }

            var existing = repository.FindByName(parsed.Name, reader.MediaType);
            if (existing is null)
            {
                plan.Add(new MigrationItem(key, label, MigrationAction.Create, detail), _ =>
                {
                    var created = reader.Service<CustomFormatService>().Create(parsed);
                    reader.Context.Ids.Set(IdKind, id, created.Id);
                });
                continue;
            }

            var existingId = existing.Id;
            var same = SameConditions(existing, parsed);
            if (existing.TrashId is not null)
            {
                plan.Add(
                    new MigrationItem(
                        key,
                        label,
                        MigrationAction.Link,
                        detail,
                        "Follows the TRaSH Guides in StreamZH.",
                        same ? null : $"'{label}' follows the TRaSH Guides in StreamZH: its conditions stay as they are (the TRaSH sync keeps them up to date); only its scores in the quality profiles come over."),
                    _ => reader.Context.Ids.Set(IdKind, id, existingId));
                continue;
            }

            if (same)
            {
                plan.Add(new MigrationItem(key, label, MigrationAction.Link, detail, "Already the same."), _ => reader.Context.Ids.Set(IdKind, id, existingId));
                continue;
            }

            plan.Add(new MigrationItem(key, label, MigrationAction.Update, detail + " · conditions differ"), _ =>
            {
                reader.Service<CustomFormatService>().Update(existingId, parsed);
                reader.Context.Ids.Set(IdKind, id, existingId);
            });
        }

        return plan;
    }

    /// <summary>Same conditions and the same "include in renaming" switch.</summary>
    public static bool SameConditions(CustomFormat a, CustomFormat b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.IncludeInRename == b.IncludeInRename
            && JsonSerializer.Serialize(CustomFormatSchema.Normalize(a).Specifications, Json) == JsonSerializer.Serialize(CustomFormatSchema.Normalize(b).Specifications, Json);
    }

    private static string Detail(CustomFormat format)
    {
        var count = format.Specifications.Count;
        var text = count == 1 ? "1 condition" : $"{count} conditions";
        return format.IncludeInRename ? text + " · in file names" : text;
    }
}
