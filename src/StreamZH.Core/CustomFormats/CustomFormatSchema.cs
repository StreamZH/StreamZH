// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using StreamZH.Core.Media;

namespace StreamZH.Core.CustomFormats;

/// <summary>One input of a condition type. <see cref="Type"/>: text, regex, number, select, flags or checkbox.</summary>
public sealed record CustomFormatFieldSchema(
    string Name,
    string Label,
    string Type,
    string? Unit = null,
    IReadOnlyList<CustomFormatOption>? Options = null,
    string? HelpText = null);

/// <summary>A condition type with its label and fields, as the editor shows it.</summary>
public sealed record CustomFormatImplementationSchema(
    string Implementation,
    string Label,
    string Description,
    IReadOnlyList<CustomFormatFieldSchema> Fields);

/// <summary>
/// The condition types per media type (Radarr's for movies, Sonarr's for series) with their fields and choices, and the
/// validation and normalisation of formats (Radarr's specification validators).
/// </summary>
public static class CustomFormatSchema
{
    public const int MaxNameLength = 100;

    private const string RegexHelp = "Regular expression, not case-sensitive.";

    /// <summary>The condition types offered for a media type, in Radarr's/Sonarr's order.</summary>
    public static IReadOnlyList<CustomFormatImplementationSchema> For(MediaType mediaType)
    {
        var series = mediaType == MediaType.Series;
        var list = new List<CustomFormatImplementationSchema>
        {
            new(CustomFormatImplementations.ReleaseTitle, "Release Title", "The release name (or the file name) matches a regular expression.",
                [new(CustomFormatFields.Value, "Regular Expression", "regex", HelpText: RegexHelp)]),
        };

        if (!series)
        {
            list.Add(new(CustomFormatImplementations.Edition, "Edition", "The edition read from the name (Director's Cut, IMAX …) matches a regular expression.",
                [new(CustomFormatFields.Value, "Regular Expression", "regex", HelpText: RegexHelp)]));
        }

        list.Add(new(CustomFormatImplementations.Language, "Language", "The release has this audio language (Original = the movie's or series' original language).",
        [
            new(CustomFormatFields.Value, "Language", "select", Options: CustomFormatValues.Languages(mediaType)),
            new(CustomFormatFields.ExceptLanguage, "Except Language", "checkbox", HelpText: "Matches when any other language than the selected one is present."),
        ]));
        list.Add(new(CustomFormatImplementations.IndexerFlag, "Indexer Flag", "The indexer marks the release with this flag (Usenet indexers set none).",
            [new(CustomFormatFields.Value, "Flag", "flags", Options: CustomFormatValues.IndexerFlags(mediaType))]));
        list.Add(new(CustomFormatImplementations.Source, "Source", "The quality's source (WEB-DL, Bluray …).",
            [new(CustomFormatFields.Value, "Source", "select", Options: CustomFormatValues.Sources(mediaType))]));
        list.Add(new(CustomFormatImplementations.Resolution, "Resolution", "The quality's resolution.",
            [new(CustomFormatFields.Value, "Resolution", "select", Options: CustomFormatValues.Resolutions)]));

        if (!series)
        {
            list.Add(new(CustomFormatImplementations.QualityModifier, "Quality Modifier", "The quality's modifier (Remux, BR-DISK …).",
                [new(CustomFormatFields.Value, "Quality Modifier", "select", Options: CustomFormatValues.Modifiers)]));
        }

        list.Add(new(CustomFormatImplementations.Size, "Size", "The release size is above the minimum and at most the maximum.",
        [
            new(CustomFormatFields.Min, "Minimum Size", "number", "GB", HelpText: "Release must be larger than this size."),
            new(CustomFormatFields.Max, "Maximum Size", "number", "GB", HelpText: "Release must be at most this size."),
        ]));
        list.Add(new(CustomFormatImplementations.ReleaseGroup, "Release Group", "The release group matches a regular expression.",
            [new(CustomFormatFields.Value, "Regular Expression", "regex", HelpText: RegexHelp)]));

        if (series)
        {
            list.Add(new(CustomFormatImplementations.ReleaseType, "Release Type", "Single episode, multi-episode or season pack.",
                [new(CustomFormatFields.Value, "Release Type", "select", Options: CustomFormatValues.ReleaseTypes)]));
        }
        else
        {
            list.Add(new(CustomFormatImplementations.Year, "Year", "The movie year is between the minimum and the maximum.",
            [
                new(CustomFormatFields.Min, "Minimum Year", "number"),
                new(CustomFormatFields.Max, "Maximum Year", "number"),
            ]));
        }

        return list;
    }

    /// <summary>The canonical implementation name ("releasetitlespecification" → "ReleaseTitleSpecification"), null if unknown.</summary>
    public static string? Canonical(string? implementation)
    {
        if (string.IsNullOrWhiteSpace(implementation))
        {
            return null;
        }

        var trimmed = implementation.Trim();
        return CustomFormatImplementations.All.FirstOrDefault(i =>
            i.Equals(trimmed, StringComparison.OrdinalIgnoreCase) || i.Equals(trimmed + "Specification", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Trims names, writes implementation names canonically and the fields in their JSON types (numbers as numbers, regex
    /// as text), dropping fields the condition type does not use. Run before <see cref="Validate"/>.
    /// </summary>
    public static CustomFormat Normalize(CustomFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format with
        {
            Name = format.Name?.Trim() ?? string.Empty,
            TrashId = string.IsNullOrWhiteSpace(format.TrashId) ? null : format.TrashId.Trim(),
            Specifications = [.. (format.Specifications ?? []).Select(NormalizeSpecification)],
        };
    }

    private static CustomFormatSpecification NormalizeSpecification(CustomFormatSpecification s)
    {
        var implementation = Canonical(s.Implementation) ?? s.Implementation?.Trim() ?? string.Empty;
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var source = s.Fields ?? new Dictionary<string, JsonElement>();
        var spec = s with { Fields = source };

        switch (implementation)
        {
            case CustomFormatImplementations.ReleaseTitle or CustomFormatImplementations.ReleaseGroup or CustomFormatImplementations.Edition:
                if (CustomFormatFields.String(spec, CustomFormatFields.Value) is { } pattern)
                {
                    fields[CustomFormatFields.Value] = CustomFormatFields.Text(pattern);
                }

                break;
            case CustomFormatImplementations.Size:
                CopyNumber(spec, fields, CustomFormatFields.Min, integer: false);
                CopyNumber(spec, fields, CustomFormatFields.Max, integer: false);
                break;
            case CustomFormatImplementations.Year:
                CopyNumber(spec, fields, CustomFormatFields.Min, integer: true);
                CopyNumber(spec, fields, CustomFormatFields.Max, integer: true);
                break;
            case CustomFormatImplementations.Language:
                CopyNumber(spec, fields, CustomFormatFields.Value, integer: true);
                if (CustomFormatFields.Has(spec, CustomFormatFields.ExceptLanguage))
                {
                    fields[CustomFormatFields.ExceptLanguage] = CustomFormatFields.Boolean(CustomFormatFields.Bool(spec, CustomFormatFields.ExceptLanguage));
                }

                break;
            case CustomFormatImplementations.Source or CustomFormatImplementations.Resolution or CustomFormatImplementations.QualityModifier
                or CustomFormatImplementations.ReleaseType or CustomFormatImplementations.IndexerFlag:
                CopyNumber(spec, fields, CustomFormatFields.Value, integer: true);
                break;
            default:
                foreach (var (key, value) in source)
                {
                    fields[key] = value.Clone();
                }

                break;
        }

        return new CustomFormatSpecification
        {
            Name = s.Name?.Trim() ?? string.Empty,
            Implementation = implementation,
            Negate = s.Negate,
            Required = s.Required,
            Fields = fields,
        };
    }

    private static void CopyNumber(CustomFormatSpecification spec, Dictionary<string, JsonElement> fields, string name, bool integer)
    {
        if (!CustomFormatFields.Has(spec, name))
        {
            return;
        }

        if (CustomFormatFields.Double(spec, name) is { } number)
        {
            fields[name] = integer && number == Math.Floor(number) && Math.Abs(number) < int.MaxValue
                ? CustomFormatFields.Number((int)number)
                : CustomFormatFields.Number(number);
        }
        else
        {
            // Keep what was sent so validation can name it ("not a number").
            fields[name] = spec.Fields[name].Clone();
        }
    }

    /// <summary>
    /// Problems of a (normalised) format, keyed like the API: <c>name</c>, <c>specifications</c>,
    /// <c>specifications[2].fields.value</c>. Name uniqueness is checked by the caller.
    /// </summary>
    public static Dictionary<string, string[]> Validate(CustomFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(format.Name))
        {
            errors["name"] = ["Enter a name."];
        }
        else if (format.Name.Length > MaxNameLength)
        {
            errors["name"] = [$"Use at most {MaxNameLength} characters."];
        }

        if (format.MediaType is not (MediaType.Movie or MediaType.Series))
        {
            errors["mediaType"] = ["Use movie or series."];
        }

        if (format.Specifications.Count == 0)
        {
            errors["specifications"] = ["Add at least one condition."];
        }

        for (var i = 0; i < format.Specifications.Count; i++)
        {
            ValidateSpecification(format.MediaType, format.Specifications[i], $"specifications[{i}]", errors);
        }

        return errors;
    }

    private static void ValidateSpecification(MediaType mediaType, CustomFormatSpecification s, string key, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(s.Name))
        {
            errors[$"{key}.name"] = ["Enter a name for the condition."];
        }

        var value = $"{key}.fields.{CustomFormatFields.Value}";
        var min = $"{key}.fields.{CustomFormatFields.Min}";
        var max = $"{key}.fields.{CustomFormatFields.Max}";

        switch (s.Implementation)
        {
            case CustomFormatImplementations.ReleaseTitle or CustomFormatImplementations.ReleaseGroup or CustomFormatImplementations.Edition:
                var pattern = CustomFormatFields.String(s, CustomFormatFields.Value);
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    errors[value] = ["Enter a regular expression."];
                }
                else if (!CustomFormatCalculator.IsValidRegex(pattern))
                {
                    errors[value] = ["This is not a valid regular expression."];
                }

                break;
            case CustomFormatImplementations.Language:
                RequireOption(s, value, CustomFormatValues.Languages(mediaType), "Choose a language.", errors);
                break;
            case CustomFormatImplementations.Source:
                RequireOption(s, value, CustomFormatValues.Sources(mediaType), "Choose a source.", errors);
                break;
            case CustomFormatImplementations.Resolution:
                RequireOption(s, value, CustomFormatValues.Resolutions, "Choose a resolution.", errors);
                break;
            case CustomFormatImplementations.QualityModifier:
                RequireOption(s, value, CustomFormatValues.Modifiers, "Choose a quality modifier.", errors);
                break;
            case CustomFormatImplementations.ReleaseType:
                RequireOption(s, value, CustomFormatValues.ReleaseTypes, "Choose a release type.", errors);
                break;
            case CustomFormatImplementations.IndexerFlag:
                var all = CustomFormatValues.IndexerFlags(mediaType).Aggregate(0, (acc, o) => acc | o.Value);
                if (CustomFormatFields.Int(s, CustomFormatFields.Value) is not { } flags || flags <= 0 || (flags & ~all) != 0)
                {
                    errors[value] = ["Choose a flag."];
                }

                break;
            case CustomFormatImplementations.Size:
                var minSize = CustomFormatFields.Double(s, CustomFormatFields.Min);
                var maxSize = CustomFormatFields.Double(s, CustomFormatFields.Max);
                if (minSize is null || minSize < 0)
                {
                    errors[min] = ["Enter a size of 0 GB or more."];
                }

                if (maxSize is null || maxSize <= (minSize ?? 0))
                {
                    errors[max] = ["Enter a size larger than the minimum."];
                }

                break;
            case CustomFormatImplementations.Year:
                var minYear = CustomFormatFields.Int(s, CustomFormatFields.Min);
                var maxYear = CustomFormatFields.Int(s, CustomFormatFields.Max);
                if (minYear is null || minYear <= 0)
                {
                    errors[min] = ["Enter a year."];
                }

                if (maxYear is null || maxYear < (minYear ?? 1))
                {
                    errors[max] = ["Enter a year not before the minimum."];
                }

                break;
            default:
                errors[$"{key}.implementation"] =
                [
                    string.IsNullOrWhiteSpace(s.Implementation)
                        ? "Choose a condition type."
                        : string.Create(CultureInfo.InvariantCulture, $"Unknown condition type '{s.Implementation}'."),
                ];
                break;
        }
    }

    private static void RequireOption(
        CustomFormatSpecification s, string key, IReadOnlyList<CustomFormatOption> options, string message, Dictionary<string, string[]> errors)
    {
        if (CustomFormatFields.Int(s, CustomFormatFields.Value) is not { } v || options.All(o => o.Value != v))
        {
            errors[key] = [message];
        }
    }
}
