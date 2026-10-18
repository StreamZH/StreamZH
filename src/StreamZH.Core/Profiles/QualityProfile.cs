// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Quality;

namespace StreamZH.Core.Profiles;

/// <summary>
/// One quality inside a profile and whether releases in it may be grabbed. Qualities with the same
/// <paramref name="Group"/> (e.g. TRaSH's "Merged QPs") rank equally; custom format scores decide between them.
/// </summary>
public sealed record QualityProfileItem(int QualityId, bool Allowed, string? Group = null);

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

    /// <summary>Custom format scores (movie and series formats together; each scores its own media type).</summary>
    public IReadOnlyList<ProfileFormatItem> FormatItems { get; init; } = [];

    /// <summary>Releases below this custom format score are rejected.</summary>
    public int MinFormatScore { get; init; }

    /// <summary>Once a file reaches the cutoff quality and this score, no further upgrades are searched (0 = quality cutoff only).</summary>
    public int CutoffFormatScore { get; init; }

    /// <summary>An upgrade of the same quality must raise the score by at least this much.</summary>
    public int MinUpgradeFormatScore { get; init; } = 1;

    /// <summary>TRaSH Guides templates this profile follows (null = own profile); the TRaSH sync keeps it up to date.</summary>
    public string? TrashMovieTemplate { get; init; }

    public string? TrashSeriesTemplate { get; init; }

    /// <summary>
    /// Position of a quality in this profile (higher = better), or -1 if it is not part of the profile. Qualities of one
    /// group share the position of the group's first (lowest) item.
    /// </summary>
    public int RankOf(int qualityId)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].QualityId != qualityId)
            {
                continue;
            }

            var group = Items[i].Group;
            if (string.IsNullOrEmpty(group))
            {
                return i;
            }

            var first = i;
            while (first > 0 && string.Equals(Items[first - 1].Group, group, StringComparison.Ordinal))
            {
                first--;
            }

            return first;
        }

        return -1;
    }

    public bool IsAllowed(int qualityId) => Items.Any(i => i.QualityId == qualityId && i.Allowed);
}

/// <summary>Size limits per quality in MB per minute of runtime (null = no limit).</summary>
public sealed record QualityDefinition(int QualityId, string Title, double MinSize, double? MaxSize, double? PreferredSize);