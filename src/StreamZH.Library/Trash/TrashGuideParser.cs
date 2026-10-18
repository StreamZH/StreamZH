// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;

namespace StreamZH.Library.Trash;

/// <summary>
/// Reads a copy of the TRaSH Guides repository: <c>metadata.json</c> names the JSON folders of Radarr and Sonarr
/// (<c>json_paths</c>, never hard-coded), plus the "Collection of Custom Formats" pages for the format categories.
/// Broken files are skipped with a warning, like Recyclarr does.
/// </summary>
public static partial class TrashGuideParser
{
    public const string MetadataFile = "metadata.json";

    /// <summary>The guide pages whose index tables give each custom format its category (Recyclarr reads the same pages).</summary>
    public static IReadOnlyDictionary<MediaType, string> CategoryFiles { get; } = new Dictionary<MediaType, string>
    {
        [MediaType.Movie] = "docs/Radarr/Radarr-collection-of-custom-formats.md",
        [MediaType.Series] = "docs/Sonarr/sonarr-collection-of-custom-formats.md",
    };

    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The <c>json_paths</c> of metadata.json: app ("radarr"/"sonarr") → kind ("custom_formats", …) → relative paths.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadMetadata(string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson, DocumentOptions);
        var result = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("json_paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("metadata.json has no json_paths.");
        }

        foreach (var app in paths.EnumerateObject().Where(a => a.Value.ValueKind == JsonValueKind.Object))
        {
            var kinds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kind in app.Value.EnumerateObject())
            {
                kinds[kind.Name] = kind.Value.ValueKind switch
                {
                    JsonValueKind.Array => [.. kind.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)],
                    JsonValueKind.String => [kind.Value.GetString()!],
                    _ => [],
                };
            }

            result[app.Name] = kinds;
        }

        return result;
    }

    /// <summary>Every path the metadata names (folders and files), relative to the repository root with '/' separators.</summary>
    public static IReadOnlyList<string> MetadataPaths(string metadataJson) =>
        [.. ReadMetadata(metadataJson).Values.SelectMany(k => k.Values).SelectMany(p => p).Select(p => p.Replace('\\', '/').Trim('/')).Distinct()];

    /// <summary>Reads the guides below <paramref name="root"/> (the folder that contains metadata.json).</summary>
    /// <exception cref="FormatException">metadata.json is missing or broken.</exception>
    public static TrashGuides Parse(string root)
    {
        var metadataPath = Path.Combine(root, MetadataFile);
        if (!File.Exists(metadataPath))
        {
            throw new FormatException("metadata.json is missing.");
        }

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> metadata;
        try
        {
            metadata = ReadMetadata(File.ReadAllText(metadataPath));
        }
        catch (JsonException ex)
        {
            throw new FormatException($"metadata.json could not be read: {ex.Message}", ex);
        }

        var warnings = new List<string>();
        var movie = ParseApp(root, metadata.GetValueOrDefault("radarr"), MediaType.Movie, warnings);
        var series = ParseApp(root, metadata.GetValueOrDefault("sonarr"), MediaType.Series, warnings);
        return new TrashGuides(movie, series, warnings);
    }

    private static TrashGuide ParseApp(
        string root, IReadOnlyDictionary<string, IReadOnlyList<string>>? paths, MediaType mediaType, List<string> warnings)
    {
        if (paths is null)
        {
            return new TrashGuide { MediaType = mediaType };
        }

        IEnumerable<(string File, JsonElement Root)> Files(string kind)
        {
            foreach (var relative in paths.GetValueOrDefault(kind) ?? [])
            {
                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                IEnumerable<string> files = Directory.Exists(full)
                    ? Directory.EnumerateFiles(full, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)
                    : File.Exists(full) ? [full] : [];
                foreach (var file in files)
                {
                    JsonElement element;
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(file), DocumentOptions);
                        element = document.RootElement.Clone();
                    }
                    catch (JsonException ex)
                    {
                        warnings.Add($"{Relative(root, file)}: {ex.Message}");
                        continue;
                    }

                    yield return (file, element);
                }
            }
        }

        T? Read<T>(string file, Func<T> read)
            where T : class
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException or JsonException)
            {
                warnings.Add($"{Relative(root, file)}: {ex.Message}");
                return null;
            }
        }

        var categories = ReadCategories(Path.Combine(root, CategoryFiles[mediaType].Replace('/', Path.DirectorySeparatorChar)));
        var formats = Files("custom_formats")
            .Select(f => Read(f.File, () => ParseFormat(f.Root, Path.GetFileNameWithoutExtension(f.File), categories)))
            .OfType<TrashCustomFormat>()
            .DistinctBy(f => f.TrashId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = Files("custom_format_groups")
            .Select(f => Read(f.File, () => ParseGroup(f.Root)))
            .OfType<TrashCustomFormatGroup>()
            .DistinctBy(g => g.TrashId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var profileGroups = new Dictionary<string, (string Group, string Slug)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, element) in Files("quality_profile_groups"))
        {
            foreach (var group in element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                var name = String(group, "name") ?? string.Empty;
                if (group.TryGetProperty("profiles", out var list) && list.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in list.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                    {
                        profileGroups.TryAdd(p.Value.GetString()!, (name, p.Name));
                    }
                }
            }

            _ = file;
        }

        var profiles = Files("quality_profiles")
            .Select(f => Read(f.File, () => ParseProfile(f.Root, Path.GetFileNameWithoutExtension(f.File), profileGroups)))
            .OfType<TrashQualityProfile>()
            .DistinctBy(p => p.TrashId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sizes = Files("qualities")
            .Select(f => Read(f.File, () => ParseQualitySize(f.Root)))
            .OfType<TrashQualitySize>()
            .ToList();

        var naming = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, element) in Files("naming"))
        {
            AddNaming(naming, string.Empty, element);
        }

        return new TrashGuide
        {
            MediaType = mediaType,
            CustomFormats = formats,
            Groups = groups,
            Profiles = profiles,
            QualitySizes = sizes,
            Naming = naming,
        };
    }

    public static TrashCustomFormat ParseFormat(JsonElement element, string slug, IReadOnlyDictionary<string, string>? categories = null)
    {
        var trashId = String(element, "trash_id") ?? throw new FormatException("trash_id is missing.");
        var name = String(element, "name") ?? throw new FormatException("name is missing.");
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("trash_scores", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var score in s.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Number && x.Value.TryGetInt32(out _)))
            {
                scores[score.Name] = score.Value.GetInt32();
            }
        }

        var specifications = new List<CustomFormatSpecification>();
        if (element.TryGetProperty("specifications", out var specs) && specs.ValueKind == JsonValueKind.Array)
        {
            foreach (var spec in specs.EnumerateArray())
            {
                specifications.Add(ParseSpecification(spec));
            }
        }

        var category = categories is null
            ? null
            : categories.GetValueOrDefault(AnchorKey(slug)) ?? categories.GetValueOrDefault(AnchorKey(name));
        return new TrashCustomFormat
        {
            TrashId = trashId,
            Name = name,
            Slug = slug,
            Scores = scores,
            IncludeInRename = Bool(element, "includeCustomFormatWhenRenaming"),
            Specifications = specifications,
            Description = String(element, "trash_description"),
            Category = category,
        };
    }

    /// <summary>One condition in the Radarr/Sonarr shape; "fields" may be an object (guides) or a list of {name, value} (app export).</summary>
    public static CustomFormatSpecification ParseSpecification(JsonElement spec)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (spec.TryGetProperty("fields", out var f))
        {
            if (f.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in f.EnumerateObject())
                {
                    fields[field.Name] = field.Value.Clone();
                }
            }
            else if (f.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in f.EnumerateArray())
                {
                    if (String(field, "name") is { } fieldName && field.TryGetProperty("value", out var value))
                    {
                        fields[fieldName] = value.Clone();
                    }
                }
            }
        }

        return new CustomFormatSpecification
        {
            Name = String(spec, "name") ?? string.Empty,
            Implementation = String(spec, "implementation") ?? throw new FormatException("A specification has no implementation."),
            Negate = Bool(spec, "negate"),
            Required = Bool(spec, "required"),
            Fields = fields,
        };
    }

    public static TrashCustomFormatGroup ParseGroup(JsonElement element)
    {
        var formats = new List<TrashGroupFormat>();
        if (element.TryGetProperty("custom_formats", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var cf in list.EnumerateArray())
            {
                if (String(cf, "trash_id") is { } id)
                {
                    formats.Add(new TrashGroupFormat(id, String(cf, "name") ?? id, Bool(cf, "required"), Bool(cf, "default")));
                }
            }
        }

        IReadOnlyList<string> Profiles(string kind) =>
            element.TryGetProperty("quality_profiles", out var qp) && qp.ValueKind == JsonValueKind.Object
            && qp.TryGetProperty(kind, out var map) && map.ValueKind == JsonValueKind.Object
                ? [.. map.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()!)]
                : [];

        return new TrashCustomFormatGroup
        {
            TrashId = String(element, "trash_id") ?? throw new FormatException("trash_id is missing."),
            Name = String(element, "name") ?? throw new FormatException("name is missing."),
            Description = String(element, "trash_description"),
            Default = Bool(element, "default"),
            Formats = formats,
            Include = Profiles("include"),
            Exclude = Profiles("exclude"),
        };
    }

    public static TrashQualityProfile ParseProfile(JsonElement element, string fileSlug, IReadOnlyDictionary<string, (string Group, string Slug)>? groups = null)
    {
        var trashId = String(element, "trash_id") ?? throw new FormatException("trash_id is missing.");
        var items = new List<TrashQualityItem>();
        if (element.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var qualities = item.TryGetProperty("items", out var inner) && inner.ValueKind == JsonValueKind.Array
                    ? inner.EnumerateArray().Where(q => q.ValueKind == JsonValueKind.String).Select(q => q.GetString()!).ToList()
                    : [];
                items.Add(new TrashQualityItem(String(item, "name") ?? throw new FormatException("A quality has no name."), Bool(item, "allowed"), qualities));
            }
        }

        var formatItems = new List<KeyValuePair<string, string>>();
        if (element.TryGetProperty("formatItems", out var fi) && fi.ValueKind == JsonValueKind.Object)
        {
            formatItems.AddRange(fi.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString()!)));
        }

        var group = groups?.GetValueOrDefault(trashId);
        return new TrashQualityProfile
        {
            TrashId = trashId,
            Name = String(element, "name") ?? throw new FormatException("name is missing."),
            Slug = group?.Slug is { Length: > 0 } slug ? slug : fileSlug,
            Description = String(element, "trash_description"),
            Url = String(element, "trash_url"),
            Group = group?.Group,
            ScoreSet = String(element, "trash_score_set"),
            UpgradeAllowed = !element.TryGetProperty("upgradeAllowed", out _) || Bool(element, "upgradeAllowed"),
            Cutoff = String(element, "cutoff"),
            MinFormatScore = Int(element, "minFormatScore") ?? 0,
            CutoffFormatScore = Int(element, "cutoffFormatScore") ?? 0,
            MinUpgradeFormatScore = Int(element, "minUpgradeFormatScore") ?? 1,
            Language = String(element, "language"),
            Items = items,
            FormatItems = formatItems,
        };
    }

    public static TrashQualitySize ParseQualitySize(JsonElement element)
    {
        var qualities = new List<TrashQualitySizeItem>();
        if (element.TryGetProperty("qualities", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in list.EnumerateArray())
            {
                qualities.Add(new TrashQualitySizeItem(
                    String(q, "quality") ?? throw new FormatException("A quality size has no quality."),
                    Double(q, "min") ?? 0,
                    Double(q, "preferred"),
                    Double(q, "max")));
            }
        }

        return new TrashQualitySize(
            String(element, "trash_id") ?? string.Empty,
            String(element, "type") ?? throw new FormatException("type is missing."),
            qualities);
    }

    /// <summary>
    /// Category of each format from the index tables of a "Collection of Custom Formats" page: the column header is the
    /// category, each cell links to a format (<c>[Name](#anchor)</c>). Keys are anchors and names (lower case, letters and digits).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadCategories(string markdownFile)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(markdownFile))
        {
            return result;
        }

        var lines = File.ReadAllLines(markdownFile);
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith('|') || !SeparatorRow().IsMatch(lines[i + 1].Trim()))
            {
                continue;
            }

            var headers = Cells(lines[i]);
            var row = i + 2;
            for (; row < lines.Length && lines[row].TrimStart().StartsWith('|'); row++)
            {
                var cells = Cells(lines[row]);
                for (var c = 0; c < cells.Count && c < headers.Count; c++)
                {
                    var match = LinkCell().Match(cells[c]);
                    if (match.Success && headers[c].Length > 0)
                    {
                        result.TryAdd(AnchorKey(match.Groups["anchor"].Value), headers[c]);
                        result.TryAdd(AnchorKey(match.Groups["name"].Value), headers[c]);
                    }
                }
            }

            i = row - 1;
        }

        return result;
    }

    /// <summary>Lower case letters and digits only: "repack-proper", "repackproper" and "Repack/Proper" give the same key.</summary>
    public static string AnchorKey(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static List<string> Cells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|'))
        {
            text = text[1..];
        }

        if (text.EndsWith('|'))
        {
            text = text[..^1];
        }

        return [.. text.Split('|').Select(c => c.Trim())];
    }

    private static void AddNaming(Dictionary<string, IReadOnlyDictionary<string, string>> naming, string prefix, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                variants[property.Name] = property.Value.GetString()!;
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                AddNaming(naming, prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}", property.Value);
            }
        }

        if (variants.Count > 0 && prefix.Length > 0)
        {
            naming[prefix] = variants;
        }
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    internal static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>true, "true" (the guides use both) → true.</summary>
    internal static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? Double(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        } : null;

    [GeneratedRegex(@"^\|?[\s:|-]*-[\s:|-]*\|?$")]
    private static partial Regex SeparatorRow();

    [GeneratedRegex(@"\[(?<name>[^\]]+)\]\(#(?<anchor>[^)\s]+)\)")]
    private static partial Regex LinkCell();
}
