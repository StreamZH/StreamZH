// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;

namespace StreamZH.Core.Decisions;

/// <summary>Language helpers shared by the language specification, the upgrade rules and the ranking.</summary>
public static class LanguageRules
{
    /// <summary>
    /// Languages of a release with placeholders resolved, like Radarr does after parsing:
    /// no language or <see cref="Language.Unknown"/> means the original language of the movie/series
    /// (English if that is not known), and <see cref="Language.Original"/> ("German.DL") means the original language too.
    /// </summary>
    public static IReadOnlyList<Language> EffectiveLanguages(ParsedRelease release, Language originalLanguage) =>
        Normalize(release.Languages, originalLanguage);

    public static IReadOnlyList<Language> Normalize(IReadOnlyList<Language> languages, Language originalLanguage)
    {
        var fallback = IsReal(originalLanguage) ? originalLanguage : Language.English;
        if (languages.Count == 0)
        {
            return [fallback];
        }

        return [.. languages
            .Select(l => l == Language.Unknown || l == Language.Original || l == Language.Any ? fallback : l)
            .Distinct()];
    }

    /// <summary>True for a real language (not Unknown, Any or Original).</summary>
    public static bool IsReal(Language language) => language.Id > 0;

    /// <summary>
    /// Dual language as the profile prefers it: the profile's language plus a second one, either the original
    /// language or English (e.g. "German.DL" for a German profile). Only profiles with a specific language
    /// have a dual-language preference; Any and Original never do.
    /// </summary>
    public static bool IsDualLanguage(IReadOnlyList<Language> languages, QualityProfile profile, Language originalLanguage)
    {
        var wanted = Language.FindById(profile.LanguageId);

        if (!IsReal(wanted) || !languages.Contains(wanted))
        {
            return false;
        }

        var hasOriginal = IsReal(originalLanguage) && originalLanguage != wanted && languages.Contains(originalLanguage);
        var hasEnglish = wanted != Language.English && languages.Contains(Language.English);

        return hasOriginal || hasEnglish;
    }

    public static string Describe(IReadOnlyList<Language> languages) =>
        languages.Count == 0 ? "Unknown" : string.Join(", ", languages.Select(l => l.Name));
}
