// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) Sonarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Quality;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Qualities and languages of Radarr and Sonarr in StreamZH terms. StreamZH uses Radarr's quality and language ids, so
/// Radarr maps 1:1. Sonarr numbers some qualities differently (Bluray-480p is 13, Bluray-576p 22, the Remuxes 20 and 21)
/// and its languages differently from Arabic (26) on, so Sonarr maps by name, then by its id table, then by
/// source and resolution.
/// </summary>
public static class ArrQualities
{
    /// <summary>Sonarr's quality ids (NzbDrone.Core.Qualities.Quality in Sonarr v3/v4) → StreamZH's (Radarr's).</summary>
    public static readonly IReadOnlyDictionary<int, int> SonarrIds = new Dictionary<int, int>
    {
        [0] = Quality.Unknown.Id,
        [1] = Quality.Sdtv.Id,
        [2] = Quality.Dvd.Id,
        [3] = Quality.WebDl1080p.Id,
        [4] = Quality.Hdtv720p.Id,
        [5] = Quality.WebDl720p.Id,
        [6] = Quality.Bluray720p.Id,
        [7] = Quality.Bluray1080p.Id,
        [8] = Quality.WebDl480p.Id,
        [9] = Quality.Hdtv1080p.Id,
        [10] = Quality.RawHd.Id,
        [12] = Quality.WebRip480p.Id,
        [13] = Quality.Bluray480p.Id,
        [14] = Quality.WebRip720p.Id,
        [15] = Quality.WebRip1080p.Id,
        [16] = Quality.Hdtv2160p.Id,
        [17] = Quality.WebRip2160p.Id,
        [18] = Quality.WebDl2160p.Id,
        [19] = Quality.Bluray2160p.Id,
        [20] = Quality.Remux1080p.Id,
        [21] = Quality.Remux2160p.Id,
        [22] = Quality.Bluray576p.Id,
    };

    private static readonly Dictionary<string, Quality> ByName = BuildNames();

    private static readonly Dictionary<string, Language> LanguagesByName = new[] { Language.Any, Language.Original }
        .Concat(Language.All)
        .ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The StreamZH quality of a quality object as Radarr or Sonarr send it ({"id", "name", "source", "resolution"}), or
    /// null when StreamZH does not know it.
    /// </summary>
    public static Quality? Map(string source, JsonElement quality)
    {
        var id = ArrJson.Int(quality, "id");
        var name = ArrJson.Str(quality, "name");
        if (source == MigrationSources.Radarr)
        {
            if (id is { } radarrId && Quality.All.FirstOrDefault(q => q.Id == radarrId) is { } byId)
            {
                return byId;
            }

            return name is not null && ByName.TryGetValue(name, out var byRadarrName) ? byRadarrName : null;
        }

        if (name is not null && ByName.TryGetValue(name, out var byName))
        {
            return byName;
        }

        if (id is { } sonarrId && SonarrIds.TryGetValue(sonarrId, out var mapped))
        {
            return Quality.FindById(mapped);
        }

        return FromSonarrSource(ArrJson.Str(quality, "source"), ArrJson.Int(quality, "resolution") ?? 0);
    }

    /// <summary>A quality from Sonarr's source ("web", "blurayRaw" …) and resolution; null when nothing fits.</summary>
    public static Quality? FromSonarrSource(string? source, int resolution)
    {
        var (qualitySource, modifier) = (source ?? string.Empty).ToUpperInvariant() switch
        {
            "TELEVISION" => (QualitySource.Tv, QualityModifier.None),
            "TELEVISIONRAW" => (QualitySource.Tv, QualityModifier.RawHd),
            "WEB" => (QualitySource.WebDl, QualityModifier.None),
            "WEBRIP" => (QualitySource.WebRip, QualityModifier.None),
            "DVD" => (QualitySource.Dvd, QualityModifier.None),
            "BLURAY" => (QualitySource.Bluray, QualityModifier.None),
            "BLURAYRAW" => (QualitySource.Bluray, QualityModifier.Remux),
            "UNKNOWN" => (QualitySource.Unknown, QualityModifier.None),
            _ => ((QualitySource?)null, QualityModifier.None),
        };

        if (qualitySource is null)
        {
            return null;
        }

        // Sonarr's DVD is 480p, StreamZH's (Radarr's) DVD has no resolution; Raw-HD is 1080p in both.
        if (qualitySource == QualitySource.Dvd)
        {
            return Quality.Dvd;
        }

        if (qualitySource == QualitySource.Unknown)
        {
            return Quality.Unknown;
        }

        var found = Quality.Find(qualitySource.Value, resolution, modifier);
        return found == Quality.Unknown ? null : found;
    }

    /// <summary>
    /// A language object ({"id", "name"}) as StreamZH language: Radarr by id (same ids), Sonarr by name. Null when unknown.
    /// </summary>
    public static Language? MapLanguage(string source, JsonElement language)
    {
        var id = ArrJson.Int(language, "id");
        var name = ArrJson.Str(language, "name");
        if (source == MigrationSources.Radarr && id is { } radarrId)
        {
            var byId = Language.FindById(radarrId);
            if (byId.Id == radarrId && radarrId != Language.Unknown.Id)
            {
                return byId;
            }
        }

        if (name is not null && LanguagesByName.TryGetValue(name.Trim(), out var byName))
        {
            return byName;
        }

        // Sonarr: Original is -2 like in Radarr.
        return id == Language.Original.Id ? Language.Original : null;
    }

    private static Dictionary<string, Quality> BuildNames()
    {
        var names = Quality.All.ToDictionary(q => q.Name, StringComparer.OrdinalIgnoreCase);

        // Sonarr's names for the Remuxes and Radarr v3's old spelling.
        names["Bluray-1080p Remux"] = Quality.Remux1080p;
        names["Bluray-2160p Remux"] = Quality.Remux2160p;
        names["Remux-1080p"] = Quality.Remux1080p;
        names["Remux-2160p"] = Quality.Remux2160p;
        names["RAWHD"] = Quality.RawHd;
        return names;
    }
}
