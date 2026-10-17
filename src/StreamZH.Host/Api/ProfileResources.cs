// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

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

public sealed record QualityProfileItemResource(int QualityId, string Name, bool Allowed);

public sealed record QualityProfileResource(
    int Id, string Name, IReadOnlyList<QualityProfileItemResource> Items, int CutoffQualityId, bool UpgradeAllowed, int LanguageId,
    bool PreferDualLanguage)
{
    public static QualityProfileResource From(QualityProfile p) => new(
        p.Id,
        p.Name,
        [.. p.Items.Select(i => new QualityProfileItemResource(i.QualityId, Quality.FindById(i.QualityId).Name, i.Allowed))],
        p.CutoffQualityId,
        p.UpgradeAllowed,
        p.LanguageId,
        p.PreferDualLanguage);
}

/// <summary>Body for creating or updating a profile. Item names and the id in the body are ignored.</summary>
public sealed record QualityProfileRequest(
    string? Name,
    IReadOnlyList<QualityProfileItemRequest>? Items,
    int? CutoffQualityId,
    int? LanguageId,
    bool UpgradeAllowed = true,
    bool PreferDualLanguage = false)
{
    public QualityProfile ToProfile() => new()
    {
        Name = Name ?? string.Empty,
        Items = [.. (Items ?? []).Select(i => new QualityProfileItem(i.QualityId, i.Allowed))],
        CutoffQualityId = CutoffQualityId ?? int.MinValue,
        UpgradeAllowed = UpgradeAllowed,
        LanguageId = LanguageId ?? int.MinValue,
        PreferDualLanguage = PreferDualLanguage,
    };
}

public sealed record QualityProfileItemRequest(int QualityId, bool Allowed);

public sealed record QualityDefinitionRequest(int QualityId, string? Title, double MinSize, double? MaxSize, double? PreferredSize)
{
    public QualityDefinition ToDefinition() => new(QualityId, Title ?? string.Empty, MinSize, MaxSize, PreferredSize);
}

public sealed record ProfileSettings(int DefaultQualityProfileId);
