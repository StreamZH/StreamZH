// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Quality;

namespace StreamZH.Core.Profiles;

/// <summary>One quality inside a profile and whether releases in it may be grabbed.</summary>
public sealed record QualityProfileItem(int QualityId, bool Allowed);

/// <summary>
/// One rule set used for every media type: which qualities are allowed, until which quality StreamZH
/// keeps upgrading, and which audio language is wanted.
/// </summary>
public sealed record QualityProfile
{
    public int Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Qualities from worst to best; the order defines preference.</summary>
    public required IReadOnlyList<QualityProfileItem> Items { get; init; }

    /// <summary>Once a file reaches this quality, no further upgrades are searched.</summary>
    public required int CutoffQualityId { get; init; }

    public bool UpgradeAllowed { get; init; } = true;

    /// <summary>Required audio language: a real language, <see cref="Language.Any"/> or <see cref="Language.Original"/>.</summary>
    public required int LanguageId { get; init; }

    /// <summary>Prefer releases that also carry the original language (German.DL style dual-language).</summary>
    public bool PreferDualLanguage { get; init; }

    /// <summary>Position of a quality in this profile (higher = better), or -1 if it is not part of the profile.</summary>
    public int RankOf(int qualityId)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].QualityId == qualityId)
            {
                return i;
            }
        }

        return -1;
    }

    public bool IsAllowed(int qualityId) => Items.Any(i => i.QualityId == qualityId && i.Allowed);
}

/// <summary>Size limits per quality in MB per minute of runtime (null = no limit).</summary>
public sealed record QualityDefinition(int QualityId, string Title, double MinSize, double? MaxSize, double? PreferredSize);