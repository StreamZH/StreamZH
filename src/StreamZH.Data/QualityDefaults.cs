// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;

namespace StreamZH.Data;

/// <summary>Profiles and size limits a new installation starts with.</summary>
public static class QualityDefaults
{
    /// <summary>Id of "Deutsch 1080p", the default profile after the first start.</summary>
    public const int DefaultProfileId = 4;

    /// <summary>Qualities that are never allowed in a seeded profile (pre-releases and screeners).</summary>
    private static readonly IReadOnlySet<int> NeverAllowed = new HashSet<int>
    {
        Quality.Unknown.Id, Quality.Workprint.Id, Quality.Cam.Id, Quality.Telesync.Id,
        Quality.Telecine.Id, Quality.Regional.Id, Quality.DvdScr.Id,
    };

    public static IReadOnlyList<QualityProfile> Profiles { get; } =
    [
        Create(1, "Any", Quality.All.Where(q => !NeverAllowed.Contains(q.Id)), Quality.Bluray1080p, Language.Any, preferDual: false),
        Create(2, "HD-1080p",
            [Quality.Hdtv1080p, Quality.WebDl1080p, Quality.WebRip1080p, Quality.Bluray1080p, Quality.Remux1080p],
            Quality.Bluray1080p, Language.Original, preferDual: false),
        Create(3, "Ultra-HD",
            [Quality.WebDl2160p, Quality.WebRip2160p, Quality.Bluray2160p, Quality.Remux2160p],
            Quality.Remux2160p, Language.Original, preferDual: false),
        Create(DefaultProfileId, "Deutsch 1080p",
            [Quality.WebDl720p, Quality.WebRip720p, Quality.Bluray720p, Quality.WebDl1080p, Quality.WebRip1080p, Quality.Bluray1080p, Quality.Remux1080p],
            Quality.Bluray1080p, Language.German, preferDual: true),
        Create(5, "Deutsch 2160p",
            [Quality.WebDl1080p, Quality.WebRip1080p, Quality.Bluray1080p, Quality.Remux1080p,
             Quality.WebDl2160p, Quality.WebRip2160p, Quality.Bluray2160p, Quality.Remux2160p],
            Quality.Remux2160p, Language.German, preferDual: true),
    ];

    /// <summary>
    /// Radarr's default size limits in MB per minute: up to WEB-1080p 0 / 95 / 100, from Bluray-1080p on no upper limit.
    /// </summary>
    public static IReadOnlyList<QualityDefinition> Definitions { get; } =
    [
        .. Quality.All.Select(q => q.Weight < Quality.Bluray1080p.Weight
            ? new QualityDefinition(q.Id, q.Name, 0, 100, 95)
            : new QualityDefinition(q.Id, q.Name, 0, null, null)),
    ];

    /// <summary>All qualities from worst to best, only the given ones allowed.</summary>
    public static IReadOnlyList<QualityProfileItem> Items(IEnumerable<Quality> allowed)
    {
        var set = allowed.Select(q => q.Id).ToHashSet();
        return [.. Quality.All.Select(q => new QualityProfileItem(q.Id, set.Contains(q.Id)))];
    }

    private static QualityProfile Create(int id, string name, IEnumerable<Quality> allowed, Quality cutoff, Language language, bool preferDual) => new()
    {
        Id = id,
        Name = name,
        Items = Items(allowed),
        CutoffQualityId = cutoff.Id,
        UpgradeAllowed = true,
        LanguageId = language.Id,
        PreferDualLanguage = preferDual,
    };
}
