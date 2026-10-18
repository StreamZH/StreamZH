// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.CustomFormats;

/// <summary>
/// Creates, validates, imports, exports and removes custom formats. Deleting a format also drops its scores from every
/// quality profile. Import takes Radarr/Sonarr exports (also the API shape with a field list) and the TRaSH Guides JSON,
/// a single format or an array; a format with the same TRaSH id, else the same name, is updated instead of added.
/// </summary>
public sealed class CustomFormatService(CustomFormatRepository formats, QualityProfileRepository profiles, ILogger<CustomFormatService> logger)
{
    /// <summary>Largest import accepted (a full TRaSH set is well below 2 MB).</summary>
    public const int MaxImportLength = 5 * 1024 * 1024;

    public IReadOnlyList<CustomFormat> All() => formats.All();

    public IReadOnlyList<CustomFormat> ForMediaType(MediaType mediaType) => formats.ForMediaType(mediaType);

    public CustomFormat? Find(int id) => formats.Find(id);

    /// <summary>How many quality profiles give each format a non-zero score.</summary>
    public IReadOnlyDictionary<int, int> UsageCounts()
    {
        var counts = new Dictionary<int, int>();
        foreach (var profile in profiles.All())
        {
            foreach (var id in profile.FormatItems.Where(i => i.Score != 0).Select(i => i.CustomFormatId).Distinct())
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return counts;
    }

    public CustomFormat Create(CustomFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var normalized = CustomFormatSchema.Normalize(format) with { Id = 0 };
        Validate(normalized, null, string.Empty);
        var id = formats.Add(normalized);
        logger.LogInformation("Custom format '{Name}' ({Type}) created", normalized.Name, normalized.MediaType);
        return formats.Find(id)!;
    }

    public CustomFormat Update(int id, CustomFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var existing = formats.Find(id) ?? throw new LibraryException("Custom format not found.", LibraryError.NotFound);
        var normalized = CustomFormatSchema.Normalize(format) with { Id = id };
        if (normalized.MediaType != existing.MediaType)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["mediaType"] = ["The media type of a format cannot be changed."] });
        }

        Validate(normalized, id, string.Empty);
        formats.Update(normalized);
        logger.LogInformation("Custom format '{Name}' ({Type}) saved", normalized.Name, normalized.MediaType);
        return formats.Find(id)!;
    }

    /// <summary>Deletes the format and removes its score from every quality profile.</summary>
    public void Delete(int id)
    {
        var format = formats.Find(id) ?? throw new LibraryException("Custom format not found.", LibraryError.NotFound);
        formats.Delete(id);
        foreach (var profile in profiles.All().Where(p => p.FormatItems.Any(i => i.CustomFormatId == id)))
        {
            profiles.Update(profile with { FormatItems = [.. profile.FormatItems.Where(i => i.CustomFormatId != id)] });
        }

        logger.LogInformation("Custom format '{Name}' ({Type}) deleted", format.Name, format.MediaType);
    }

    /// <summary>
    /// Imports one format or an array (Radarr/Sonarr/TRaSH JSON) as formats of <paramref name="mediaType"/>; returns the
    /// created or updated formats in input order. Nothing is saved when one of them is invalid; errors are keyed like the
    /// API (<c>name</c>, <c>specifications[1].fields.value</c>), prefixed with <c>[i].</c> for arrays.
    /// </summary>
    public IReadOnlyList<CustomFormat> Import(MediaType mediaType, string? json)
    {
        if (mediaType is not (MediaType.Movie or MediaType.Series))
        {
            throw Invalid("mediaType", "Use movie or series.");
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            throw Invalid("json", "Paste the JSON of one or more custom formats.");
        }

        if (json.Length > MaxImportLength)
        {
            throw Invalid("json", "The JSON is too large.");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw Invalid("json", $"This is not valid JSON: {ex.Message}");
        }

        var isArray = root is JsonArray;
        var items = root switch
        {
            JsonArray array => [.. array],
            JsonObject obj => new List<JsonNode?> { obj },
            _ => [],
        };
        if (items.Count == 0)
        {
            throw Invalid("json", "Expected a custom format object or an array of them.");
        }

        var parsed = new List<CustomFormat>();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var prefix = isArray ? $"[{i}]." : string.Empty;
            if (items[i] is not JsonObject obj)
            {
                errors[$"{prefix}json"] = ["Expected a custom format object."];
                continue;
            }

            var format = CustomFormatSchema.Normalize(FromJson(obj, mediaType));
            foreach (var (key, messages) in CustomFormatSchema.Validate(format))
            {
                errors[prefix + key] = messages;
            }

            parsed.Add(format);
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var result = new List<CustomFormat>(parsed.Count);
        foreach (var format in parsed)
        {
            var existing = (format.TrashId is { } trashId ? formats.FindByTrashId(trashId, mediaType) : null)
                ?? formats.FindByName(format.Name, mediaType);
            if (existing is null)
            {
                result.Add(formats.Find(formats.Add(format))!);
            }
            else
            {
                formats.Update(format with { Id = existing.Id, TrashId = format.TrashId ?? existing.TrashId });
                result.Add(formats.Find(existing.Id)!);
            }
        }

        logger.LogInformation("Imported {Count} custom formats ({Type})", result.Count, mediaType);
        return result;
    }

    /// <summary>Radarr/Sonarr-compatible JSON of a format ("Export" in their UI), with the TRaSH id when it has one.</summary>
    public static JsonObject Export(CustomFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var json = new JsonObject();
        if (format.TrashId is { } trashId)
        {
            json["trash_id"] = trashId;
        }

        json["name"] = format.Name;
        json["includeCustomFormatWhenRenaming"] = format.IncludeInRename;
        var specifications = new JsonArray();
        foreach (var s in format.Specifications)
        {
            var fields = new JsonObject();
            foreach (var (key, value) in s.Fields)
            {
                fields[key] = JsonNode.Parse(value.GetRawText());
            }

            specifications.Add(new JsonObject
            {
                ["name"] = s.Name,
                ["implementation"] = s.Implementation,
                ["negate"] = s.Negate,
                ["required"] = s.Required,
                ["fields"] = fields,
            });
        }

        json["specifications"] = specifications;
        return json;
    }

    /// <summary>
    /// Reads one format from Radarr/Sonarr/TRaSH JSON. Fields may be an object (<c>{"value": 4}</c>, the export and TRaSH
    /// shape) or a list (<c>[{"name": "value", "value": 4}]</c>, the API shape).
    /// </summary>
    public static CustomFormat FromJson(JsonObject obj, MediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var specifications = new List<CustomFormatSpecification>();
        if (Get(obj, "specifications") is JsonArray list)
        {
            foreach (var node in list.OfType<JsonObject>())
            {
                specifications.Add(new CustomFormatSpecification
                {
                    Name = Text(Get(node, "name")) ?? string.Empty,
                    Implementation = Text(Get(node, "implementation")) ?? string.Empty,
                    Negate = Flag(Get(node, "negate")),
                    Required = Flag(Get(node, "required")),
                    Fields = Fields(Get(node, "fields")),
                });
            }
        }

        return new CustomFormat
        {
            Name = Text(Get(obj, "name")) ?? string.Empty,
            MediaType = mediaType,
            IncludeInRename = Flag(Get(obj, "includeCustomFormatWhenRenaming") ?? Get(obj, "includeInRename")),
            Specifications = specifications,
            TrashId = Text(Get(obj, "trash_id") ?? Get(obj, "trashId")),
        };
    }

    private static Dictionary<string, JsonElement> Fields(JsonNode? node)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (value is not null)
                    {
                        fields[key] = JsonSerializer.SerializeToElement(value);
                    }
                }

                break;
            case JsonArray array:
                foreach (var field in array.OfType<JsonObject>())
                {
                    if (Text(Get(field, "name")) is { } name && Get(field, "value") is { } value)
                    {
                        fields[name] = JsonSerializer.SerializeToElement(value);
                    }
                }

                break;
        }

        return fields;
    }

    /// <summary>Property lookup that ignores case (Radarr writes camelCase, hand-made JSON sometimes does not).</summary>
    private static JsonNode? Get(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var exact) ? exact : obj.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Text(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<string>(out var s) ? s : value.ToJsonString()
        : null;

    private static bool Flag(JsonNode? node) => node is JsonValue value && (
        (value.TryGetValue<bool>(out var b) && b)
        || (value.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed) && parsed)
        || (value.TryGetValue<double>(out var d) && d != 0));

    private void Validate(CustomFormat format, int? existingId, string prefix)
    {
        var errors = CustomFormatSchema.Validate(format);
        if (!errors.ContainsKey("name") && formats.FindByName(format.Name, format.MediaType) is { } other && other.Id != existingId)
        {
            errors["name"] = [$"A {(format.MediaType == MediaType.Series ? "series" : "movie")} format with this name already exists."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors.ToDictionary(e => prefix + e.Key, e => e.Value));
        }
    }

    private static ValidationFailedException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}

public static class CustomFormatServiceCollectionExtensions
{
    /// <summary>Custom format management (the repository is registered with the other repositories).</summary>
    public static IServiceCollection AddCustomFormats(this IServiceCollection services)
    {
        services.AddSingleton<CustomFormatService>();
        return services;
    }
}
