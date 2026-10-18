// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Media;

namespace StreamZH.Core.CustomFormats;

/// <summary>
/// One condition of a custom format, in the shape Radarr and Sonarr use (and the TRaSH Guides publish):
/// <c>{ "name", "implementation": "ReleaseTitleSpecification", "negate", "required", "fields": { "value": … } }</c>.
/// Enum values in <see cref="Fields"/> (source, quality modifier, release type, language, indexer flags) follow Radarr
/// for movie formats and Sonarr for series formats, so TRaSH JSON can be used unchanged.
/// </summary>
public sealed record CustomFormatSpecification
{
    public required string Name { get; init; }

    /// <summary>Radarr/Sonarr implementation name, e.g. "ReleaseTitleSpecification" (see <see cref="CustomFormatImplementations"/>).</summary>
    public required string Implementation { get; init; }

    /// <summary>The condition matches when it does NOT apply.</summary>
    public bool Negate { get; init; }

    /// <summary>Must match for the format to apply; otherwise one matching condition per implementation type is enough.</summary>
    public bool Required { get; init; }

    /// <summary>"value" for most types; "min"/"max" for size (GB) and year.</summary>
    public IReadOnlyDictionary<string, JsonElement> Fields { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// A custom format like in Radarr/Sonarr: a named set of conditions on the release; profiles give it a score.
/// Movie formats score movie releases, series formats score episode releases.
/// </summary>
public sealed record CustomFormat
{
    public int Id { get; init; }

    public required string Name { get; init; }

    public required MediaType MediaType { get; init; }

    /// <summary>Shown in the {Custom Formats} naming token.</summary>
    public bool IncludeInRename { get; init; }

    public IReadOnlyList<CustomFormatSpecification> Specifications { get; init; } = [];

    /// <summary>TRaSH Guides id when the format comes from the guides (kept in sync); null for own formats.</summary>
    public string? TrashId { get; init; }
}

/// <summary>Score of one custom format inside a quality profile.</summary>
public sealed record ProfileFormatItem(int CustomFormatId, int Score);

/// <summary>The implementation names StreamZH understands (Radarr and Sonarr spellings).</summary>
public static class CustomFormatImplementations
{
    public const string ReleaseTitle = "ReleaseTitleSpecification";
    public const string ReleaseGroup = "ReleaseGroupSpecification";
    public const string Edition = "EditionSpecification";
    public const string Language = "LanguageSpecification";
    public const string Source = "SourceSpecification";
    public const string Resolution = "ResolutionSpecification";
    public const string QualityModifier = "QualityModifierSpecification";
    public const string Size = "SizeSpecification";
    public const string IndexerFlag = "IndexerFlagSpecification";
    public const string ReleaseType = "ReleaseTypeSpecification";
    public const string Year = "YearSpecification";

    public static IReadOnlyList<string> All { get; } =
        [ReleaseTitle, ReleaseGroup, Edition, Language, Source, Resolution, QualityModifier, Size, IndexerFlag, ReleaseType, Year];
}
