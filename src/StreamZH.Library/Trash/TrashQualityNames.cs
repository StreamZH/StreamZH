// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Quality;

namespace StreamZH.Library.Trash;

/// <summary>
/// Quality names of Radarr and Sonarr (as the guides and Recyclarr use them) → StreamZH qualities. StreamZH uses Radarr's
/// qualities, so Radarr names are identical; Sonarr calls the remuxes "Bluray-1080p Remux" and "Bluray-2160p Remux".
/// </summary>
public static class TrashQualityNames
{
    private static readonly Dictionary<string, Quality> ByName =
        Quality.All.ToDictionary(q => q.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Quality> SonarrNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bluray-1080p Remux"] = Quality.Remux1080p,
        ["Bluray-2160p Remux"] = Quality.Remux2160p,
    };

    /// <summary>The StreamZH quality for a Radarr (movie) or Sonarr (series) quality name; null when unknown.</summary>
    public static Quality? Find(string? name, MediaType mediaType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var text = name.Trim();
        if (mediaType == MediaType.Series && SonarrNames.TryGetValue(text, out var sonarr))
        {
            return sonarr;
        }

        // Both spellings are accepted for both apps: Recyclarr configs are sometimes copied between instances.
        return ByName.TryGetValue(text, out var quality) ? quality : SonarrNames.GetValueOrDefault(text);
    }
}
