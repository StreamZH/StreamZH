// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;

namespace StreamZH.Library.Trash;

/// <summary>A custom format of the guides (<c>docs/json/{radarr,sonarr}/cf/*.json</c>).</summary>
public sealed record TrashCustomFormat
{
    public required string TrashId { get; init; }
    public required string Name { get; init; }

    /// <summary>File name without ".json"; the guide's anchors and categories refer to it.</summary>
    public required string Slug { get; init; }

    /// <summary>Score set → score ("default", "german", "sqp-1-1080p", …).</summary>
    public IReadOnlyDictionary<string, int> Scores { get; init; } = new Dictionary<string, int>();
    public bool IncludeInRename { get; init; }
    public IReadOnlyList<CustomFormatSpecification> Specifications { get; init; } = [];
    public string? Description { get; init; }

    /// <summary>Section of the guide's "Collection of Custom Formats" (e.g. "Audio Formats"), when known.</summary>
    public string? Category { get; init; }

    /// <summary>Score from the set, else the "default" set (like Recyclarr); null when the guide has no score.</summary>
    public int? ScoreFor(string? scoreSet) =>
        scoreSet is not null && Scores.TryGetValue(scoreSet, out var score) ? score
        : Scores.TryGetValue(TrashGuide.DefaultScoreSet, out var fallback) ? fallback
        : null;
}

/// <summary>One format of a CF group.</summary>
public sealed record TrashGroupFormat(string TrashId, string Name, bool Required, bool Default);

/// <summary>A custom format group of the guides (<c>cf-groups/*.json</c>).</summary>
public sealed record TrashCustomFormatGroup
{
    public required string TrashId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Added automatically to the profiles it applies to.</summary>
    public bool Default { get; init; }
    public IReadOnlyList<TrashGroupFormat> Formats { get; init; } = [];

    /// <summary>Profile trash ids the group is meant for (empty = all, unless <see cref="Exclude"/>).</summary>
    public IReadOnlyList<string> Include { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];

    public bool AppliesTo(string profileTrashId) =>
        Include.Count > 0 ? Include.Contains(profileTrashId, StringComparer.OrdinalIgnoreCase) : !Exclude.Contains(profileTrashId, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A quality or a group of qualities in a guide profile (best first, like the guide).</summary>
public sealed record TrashQualityItem(string Name, bool Allowed, IReadOnlyList<string> Qualities)
{
    public bool IsGroup => Qualities.Count > 0;
}

/// <summary>A quality profile template of the guides (<c>quality-profiles/*.json</c>).</summary>
public sealed record TrashQualityProfile
{
    public required string TrashId { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public string? Description { get; init; }
    public string? Url { get; init; }

    /// <summary>Name of the quality-profile group ("Standard", "German", …), when listed there.</summary>
    public string? Group { get; init; }
    public string? ScoreSet { get; init; }
    public bool UpgradeAllowed { get; init; } = true;

    /// <summary>Name of a quality or group of <see cref="Items"/>.</summary>
    public string? Cutoff { get; init; }
    public int MinFormatScore { get; init; }
    public int CutoffFormatScore { get; init; }
    public int MinUpgradeFormatScore { get; init; } = 1;

    /// <summary>Radarr only, e.g. "Any" or "Original".</summary>
    public string? Language { get; init; }

    public IReadOnlyList<TrashQualityItem> Items { get; init; } = [];

    /// <summary>Format name → trash id, in the guide's order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> FormatItems { get; init; } = [];
}

/// <summary>Size limits of one quality in MB per minute, as in the guide (max/preferred at the slider end = unlimited).</summary>
public sealed record TrashQualitySizeItem(string Quality, double Min, double? Preferred, double? Max);

/// <summary>A quality size file (<c>quality-size/*.json</c>), e.g. type "movie", "series", "anime", "sqp-streaming".</summary>
public sealed record TrashQualitySize(string TrashId, string Type, IReadOnlyList<TrashQualitySizeItem> Qualities);

/// <summary>All guide data of one app (Radarr → movies, Sonarr → series).</summary>
public sealed record TrashGuide
{
    public const string DefaultScoreSet = "default";

    public required MediaType MediaType { get; init; }
    public IReadOnlyList<TrashCustomFormat> CustomFormats { get; init; } = [];
    public IReadOnlyList<TrashCustomFormatGroup> Groups { get; init; } = [];
    public IReadOnlyList<TrashQualityProfile> Profiles { get; init; } = [];
    public IReadOnlyList<TrashQualitySize> QualitySizes { get; init; } = [];

    /// <summary>Naming section ("folder", "file", "series", "season", "episodes.standard", …) → variant → format.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Naming { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    public TrashCustomFormat? Format(string trashId) => CustomFormats.FirstOrDefault(f => string.Equals(f.TrashId, trashId, StringComparison.OrdinalIgnoreCase));

    public TrashQualityProfile? Profile(string trashId) => Profiles.FirstOrDefault(p => string.Equals(p.TrashId, trashId, StringComparison.OrdinalIgnoreCase));

    public TrashCustomFormatGroup? Group(string trashId) => Groups.FirstOrDefault(g => string.Equals(g.TrashId, trashId, StringComparison.OrdinalIgnoreCase));

    public TrashQualitySize? QualitySize(string type) => QualitySizes.FirstOrDefault(q => string.Equals(q.Type, type, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The parsed guides: Radarr data as movie guide, Sonarr data as series guide.</summary>
public sealed record TrashGuides(TrashGuide Movie, TrashGuide Series, IReadOnlyList<string> Warnings)
{
    public TrashGuide For(MediaType mediaType) => mediaType == MediaType.Series ? Series : Movie;

    public static TrashGuides Empty { get; } = new(new TrashGuide { MediaType = MediaType.Movie }, new TrashGuide { MediaType = MediaType.Series }, []);
}
