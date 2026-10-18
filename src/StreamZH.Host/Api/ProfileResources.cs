// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;

namespace StreamZH.Host.Api;

public sealed record QualityResource(int Id, string Name, QualitySource Source, int Resolution, QualityModifier Modifier)
{
    public static QualityResource From(Quality q) => new(q.Id, q.Name, q.Source, q.Resolution, q.Modifier);
}

public sealed record LanguageResource(int Id, string Name)
{
    public static LanguageResource From(Language l) => new(l.Id, l.Name);
}

/// <summary>One quality of a profile; qualities with the same <see cref="Group"/> rank equally.</summary>
public sealed record QualityProfileItemResource(int QualityId, string Name, bool Allowed, string? Group = null);

/// <summary>The score of one custom format in a profile; <see cref="MediaType"/> is "movie" or "series".</summary>
public sealed record ProfileFormatItemResource(int CustomFormatId, string Name, string MediaType, int Score);

/// <summary>
/// A quality profile. <see cref="FormatItems"/> lists every custom format (movie formats, then series formats, by name)
/// with its score, 0 when the profile does not score it.
/// </summary>
public sealed record QualityProfileResource(
    int Id, string Name, IReadOnlyList<QualityProfileItemResource> Items, int CutoffQualityId, bool UpgradeAllowed, int LanguageId,
    bool PreferDualLanguage)
{
    public IReadOnlyList<ProfileFormatItemResource> FormatItems { get; init; } = [];

    public int MinFormatScore { get; init; }

    public int CutoffFormatScore { get; init; }

    public int MinUpgradeFormatScore { get; init; } = 1;

    public string? TrashMovieTemplate { get; init; }

    public string? TrashSeriesTemplate { get; init; }

    /// <summary>Without the list of formats: only the profile's own scores, without names (prefer the other overload).</summary>
    public static QualityProfileResource From(QualityProfile p) => From(p, null);

    public static QualityProfileResource From(QualityProfile p, IReadOnlyList<CustomFormat>? formats)
    {
        ArgumentNullException.ThrowIfNull(p);
        var scores = p.FormatItems.GroupBy(i => i.CustomFormatId).ToDictionary(g => g.Key, g => g.Sum(i => i.Score));
        IReadOnlyList<ProfileFormatItemResource> formatItems = formats is null
            ? [.. p.FormatItems.Select(i => new ProfileFormatItemResource(i.CustomFormatId, string.Empty, string.Empty, i.Score))]
            : [.. formats
                .OrderBy(f => f.MediaType)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new ProfileFormatItemResource(f.Id, f.Name, MediaTypeName(f.MediaType), scores.GetValueOrDefault(f.Id)))];

        return new QualityProfileResource(
            p.Id,
            p.Name,
            [.. p.Items.Select(i => new QualityProfileItemResource(i.QualityId, Quality.FindById(i.QualityId).Name, i.Allowed, i.Group))],
            p.CutoffQualityId,
            p.UpgradeAllowed,
            p.LanguageId,
            p.PreferDualLanguage)
        {
            FormatItems = formatItems,
            MinFormatScore = p.MinFormatScore,
            CutoffFormatScore = p.CutoffFormatScore,
            MinUpgradeFormatScore = p.MinUpgradeFormatScore,
            TrashMovieTemplate = p.TrashMovieTemplate,
            TrashSeriesTemplate = p.TrashSeriesTemplate,
        };
    }

    /// <summary>"movie" or "series", as the custom format API writes media types.</summary>
    public static string MediaTypeName(MediaType mediaType) => mediaType == MediaType.Series ? "series" : "movie";
}

/// <summary>
/// Body for creating or updating a profile. Item names, format names and the id in the body are ignored. Custom format
/// fields that are left out keep their stored value on an update (defaults on create), so older clients do not wipe
/// scores; the TRaSH template fields are taken as sent (null = own profile). Scores of 0 are not stored.
/// </summary>
public sealed record QualityProfileRequest(
    string? Name,
    IReadOnlyList<QualityProfileItemRequest>? Items,
    int? CutoffQualityId,
    int? LanguageId,
    bool UpgradeAllowed = true,
    bool PreferDualLanguage = false)
{
    public IReadOnlyList<ProfileFormatItemRequest>? FormatItems { get; init; }

    public int? MinFormatScore { get; init; }

    public int? CutoffFormatScore { get; init; }

    public int? MinUpgradeFormatScore { get; init; }

    public string? TrashMovieTemplate { get; init; }

    public string? TrashSeriesTemplate { get; init; }

    /// <param name="existing">The stored profile on an update (null on create).</param>
    /// <param name="existingFormatItems">Its scores without those of deleted formats.</param>
    public QualityProfile ToProfile(QualityProfile? existing = null, IReadOnlyList<ProfileFormatItem>? existingFormatItems = null) => new()
    {
        Name = Name ?? string.Empty,
        Items = [.. (Items ?? []).Select(i => new QualityProfileItem(i.QualityId, i.Allowed, i.Group))],
        CutoffQualityId = CutoffQualityId ?? int.MinValue,
        UpgradeAllowed = UpgradeAllowed,
        LanguageId = LanguageId ?? int.MinValue,
        PreferDualLanguage = PreferDualLanguage,
        FormatItems = FormatItems is { } items
            ? [.. items.Select(i => new ProfileFormatItem(i.CustomFormatId, i.Score))]
            : existingFormatItems ?? existing?.FormatItems ?? [],
        MinFormatScore = MinFormatScore ?? existing?.MinFormatScore ?? 0,
        CutoffFormatScore = CutoffFormatScore ?? existing?.CutoffFormatScore ?? 0,
        MinUpgradeFormatScore = MinUpgradeFormatScore ?? existing?.MinUpgradeFormatScore ?? 1,
        TrashMovieTemplate = TrashMovieTemplate,
        TrashSeriesTemplate = TrashSeriesTemplate,
    };
}

public sealed record QualityProfileItemRequest(int QualityId, bool Allowed, string? Group = null);

/// <summary>A score in a profile request; name and media type of the resource may be sent along and are ignored.</summary>
public sealed record ProfileFormatItemRequest(int CustomFormatId, int Score);

public sealed record QualityDefinitionRequest(int QualityId, string? Title, double MinSize, double? MaxSize, double? PreferredSize)
{
    public QualityDefinition ToDefinition() => new(QualityId, Title ?? string.Empty, MinSize, MaxSize, PreferredSize);
}

public sealed record ProfileSettings(int DefaultQualityProfileId);
