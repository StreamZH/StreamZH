// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.CustomFormats;
using StreamZH.Parsing.Releases;

namespace StreamZH.Tests.CustomFormats;

/// <summary>
/// Custom formats for tests: real TRaSH Guides JSON (Files/radarr, Files/sonarr; MIT, github.com/TRaSH-Guides/Guides)
/// and hand-built ones, plus inputs read with the real release parser.
/// </summary>
internal static class CustomFormatTestData
{
    public static string FixturePath(string kind, string file) => Path.Combine(AppContext.BaseDirectory, "CustomFormats", "Files", kind, file);

    /// <summary>A TRaSH format: kind "radarr" gives a movie format, "sonarr" a series format.</summary>
    public static CustomFormat Trash(string kind, string file, int id)
    {
        var json = (JsonObject)JsonNode.Parse(File.ReadAllText(FixturePath(kind, file)))!;
        var format = CustomFormatSchema.Normalize(CustomFormatService.FromJson(json, kind == "sonarr" ? MediaType.Series : MediaType.Movie)) with { Id = id };
        Assert.Empty(CustomFormatSchema.Validate(format));
        return format;
    }

    public static CustomFormat Format(MediaType mediaType, string name, int id, params CustomFormatSpecification[] specifications) => new()
    {
        Id = id,
        Name = name,
        MediaType = mediaType,
        IncludeInRename = true,
        Specifications = specifications,
    };

    public static CustomFormat Movie(string name, int id, params CustomFormatSpecification[] specifications) =>
        Format(MediaType.Movie, name, id, specifications);

    public static CustomFormatSpecification Spec(string implementation, object value, bool negate = false, bool required = false, string? name = null) => new()
    {
        Name = name ?? implementation,
        Implementation = implementation,
        Negate = negate,
        Required = required,
        Fields = new Dictionary<string, JsonElement> { [CustomFormatFields.Value] = JsonSerializer.SerializeToElement(value) },
    };

    public static CustomFormatSpecification Title(string regex, bool negate = false, bool required = false) =>
        Spec(CustomFormatImplementations.ReleaseTitle, regex, negate, required, regex);

    public static CustomFormatSpecification Range(string implementation, double min, double max, bool negate = false, bool required = false) => new()
    {
        Name = implementation,
        Implementation = implementation,
        Negate = negate,
        Required = required,
        Fields = new Dictionary<string, JsonElement>
        {
            [CustomFormatFields.Min] = JsonSerializer.SerializeToElement(min),
            [CustomFormatFields.Max] = JsonSerializer.SerializeToElement(max),
        },
    };

    /// <summary>A movie release read with the real parser; the original language is English unless given.</summary>
    public static CustomFormatInput MovieRelease(string title, Language? original = null, long sizeBytes = 0, int targetYear = 0, int indexerFlags = 0)
    {
        var parsed = ReleaseParser.ParseMovie(title) ?? throw new InvalidOperationException($"Cannot parse {title}");
        return CustomFormatInput.ForRelease(parsed, sizeBytes, MediaType.Movie, original ?? Language.English, targetYear, indexerFlags);
    }

    public static CustomFormatInput EpisodeRelease(string title, Language? original = null, long sizeBytes = 0)
    {
        var parsed = ReleaseParser.ParseEpisode(title) ?? throw new InvalidOperationException($"Cannot parse {title}");
        return CustomFormatInput.ForRelease(parsed, sizeBytes, MediaType.Series, original ?? Language.English);
    }

    public static bool Matches(CustomFormat format, CustomFormatInput input) => CustomFormatCalculator.Evaluate(format, input).Matched;
}
