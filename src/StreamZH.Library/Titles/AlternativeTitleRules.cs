// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Metadata.Tmdb;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Titles;

/// <summary>
/// Which TMDB titles become alternative titles (Radarr's alternative titles and translations), and which titles a search
/// sends to the indexers (Sonarr's scene titles in the search request).
/// </summary>
public static class AlternativeTitleRules
{
    /// <summary>Titles one search may send as text queries (per episode or movie), so indexer API limits last.</summary>
    public const int MaxSearchTitles = 3;

    /// <summary>Alternative titles kept per source and item; some films have a hundred.</summary>
    public const int MaxTitlesPerSource = 100;

    public const int MaxTitleLength = 200;

    /// <summary>German first; German, Swiss and Austrian titles are all "de".</summary>
    private static readonly string[] AlwaysTranslated = ["de", "en"];

    /// <summary>Main language of a country, for TMDB's alternative titles (they only carry the country).</summary>
    private static readonly Dictionary<string, Language> CountryLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DE"] = Language.German, ["AT"] = Language.German, ["CH"] = Language.German, ["LI"] = Language.German,
        ["US"] = Language.English, ["GB"] = Language.English, ["UK"] = Language.English, ["CA"] = Language.English,
        ["AU"] = Language.English, ["NZ"] = Language.English, ["IE"] = Language.English,
        ["FR"] = Language.French, ["BE"] = Language.French, ["LU"] = Language.French,
        ["ES"] = Language.Spanish, ["MX"] = Language.SpanishLatino, ["AR"] = Language.SpanishLatino, ["CL"] = Language.SpanishLatino,
        ["CO"] = Language.SpanishLatino, ["PE"] = Language.SpanishLatino, ["VE"] = Language.SpanishLatino,
        ["IT"] = Language.Italian, ["DK"] = Language.Danish, ["NL"] = Language.Dutch, ["JP"] = Language.Japanese,
        ["IS"] = Language.Icelandic, ["CN"] = Language.Chinese, ["TW"] = Language.Chinese, ["HK"] = Language.Chinese,
        ["RU"] = Language.Russian, ["PL"] = Language.Polish, ["VN"] = Language.Vietnamese, ["SE"] = Language.Swedish,
        ["NO"] = Language.Norwegian, ["FI"] = Language.Finnish, ["TR"] = Language.Turkish, ["PT"] = Language.Portuguese,
        ["BR"] = Language.PortugueseBrazil, ["GR"] = Language.Greek, ["KR"] = Language.Korean, ["HU"] = Language.Hungarian,
        ["IL"] = Language.Hebrew, ["LT"] = Language.Lithuanian, ["CZ"] = Language.Czech, ["IN"] = Language.Hindi,
        ["RO"] = Language.Romanian, ["TH"] = Language.Thai, ["BG"] = Language.Bulgarian, ["UA"] = Language.Ukrainian,
        ["IR"] = Language.Persian, ["SK"] = Language.Slovak, ["LV"] = Language.Latvian, ["HR"] = Language.Croatian,
        ["RS"] = Language.Serbian, ["BA"] = Language.Bosnian, ["EE"] = Language.Estonian, ["ID"] = Language.Indonesian,
        ["MK"] = Language.Macedonian, ["SI"] = Language.Slovenian, ["AL"] = Language.Albanian, ["ZA"] = Language.Afrikaans,
        ["PH"] = Language.Tagalog, ["PK"] = Language.Urdu, ["MN"] = Language.Mongolian, ["GE"] = Language.Georgian,
    };

    /// <summary>Clean title for matching: the parser's normalisation, the same the release matcher uses.</summary>
    public static string CleanTitle(string title) => TitleNormalizer.Clean(title);

    /// <summary>The language a country's alternative title is most likely in; null when unknown.</summary>
    public static int? LanguageOfCountry(string? country) =>
        country is not null && CountryLanguages.TryGetValue(country.Trim(), out var language) ? language.Id : null;

    /// <summary>The language of a TMDB translation ("pt" + "BR" is Portuguese (Brazil)); null when unknown.</summary>
    public static int? LanguageOfTranslation(string language, string country)
    {
        if (string.Equals(language, "pt", StringComparison.OrdinalIgnoreCase) && string.Equals(country, "BR", StringComparison.OrdinalIgnoreCase))
        {
            return Language.PortugueseBrazil.Id;
        }

        var found = Language.FromIsoCode(language);
        return found.Id > 0 ? found.Id : null;
    }

    /// <summary>"de-DE" → "de".</summary>
    public static string? LanguageCode(string? tmdbLanguage) =>
        string.IsNullOrWhiteSpace(tmdbLanguage) ? null : tmdbLanguage.Split('-', 2)[0].Trim().ToLowerInvariant();

    /// <summary>
    /// The TMDB titles worth keeping, as (source, titles): translations into German, English, the original language and
    /// the metadata language; all alternative titles that can appear in a release name (Latin letters or digits).
    /// Duplicates of the title, the original title, of <paramref name="keep"/> (yours, scene names) and of each other are
    /// left out; a translation wins over the same alternative title because it knows its language.
    /// </summary>
    public static (IReadOnlyList<AlternativeTitle> Tmdb, IReadOnlyList<AlternativeTitle> Translations) FromTmdb(
        MediaType mediaType,
        int itemId,
        TmdbTitles titles,
        string title,
        string originalTitle,
        string? originalLanguage,
        string? metadataLanguage,
        IEnumerable<string> keep)
    {
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(keep);
        var seen = new HashSet<string>(StringComparer.Ordinal) { CleanTitle(title), CleanTitle(originalTitle) };
        seen.UnionWith(keep.Select(CleanTitle));

        var languages = AlwaysTranslated
            .Append(LanguageCode(originalLanguage))
            .Append(LanguageCode(metadataLanguage))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var translations = new List<AlternativeTitle>();
        foreach (var t in titles.Translations
            .Where(t => languages.Contains(t.Language, StringComparer.OrdinalIgnoreCase))
            .OrderBy(t => languages.IndexOf(t.Language.ToLowerInvariant())))
        {
            if (Make(t.Title, AlternativeTitleSources.Translation, LanguageOfTranslation(t.Language, t.Country)) is { } made && translations.Count < MaxTitlesPerSource)
            {
                translations.Add(made);
            }
        }

        var alternatives = new List<AlternativeTitle>();
        foreach (var t in titles.AlternativeTitles)
        {
            if (Make(t.Title, AlternativeTitleSources.Tmdb, LanguageOfCountry(t.Country)) is { } made && alternatives.Count < MaxTitlesPerSource)
            {
                alternatives.Add(made);
            }
        }

        return (alternatives, translations);

        AlternativeTitle? Make(string value, string source, int? languageId)
        {
            var text = value.Trim();
            var clean = CleanTitle(text);
            if (text.Length is 0 or > MaxTitleLength || !Usable(clean) || !seen.Add(clean))
            {
                return null;
            }

            return new AlternativeTitle
            {
                MediaType = mediaType,
                ItemId = itemId,
                Title = text,
                CleanTitle = clean,
                Source = source,
                LanguageId = languageId,
            };
        }
    }

    /// <summary>A clean title a release name can contain: at least one Latin letter or digit ("Ла-ла Ленд" cannot).</summary>
    public static bool Usable(string clean) => clean.Any(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// The titles a search sends as text queries (the id search comes first anyway): titles in the profile's language
    /// first, then the title, scene names and yours, then the original title and the remaining titles in the profile's
    /// language or English; without duplicates and at most <see cref="MaxSearchTitles"/>. With
    /// <paramref name="searchAlternativeTitles"/> off: the title and the original title, as before 0.8.1.
    /// </summary>
    /// <param name="preferredLanguageId">The quality profile's language (Original resolved to the item's language); 0 or negative = none.</param>
    /// <param name="seasonNumber">Series: only names that apply to this season.</param>
    public static IReadOnlyList<string> SearchTitles(
        string title,
        string originalTitle,
        IReadOnlyList<AlternativeTitle> alternativeTitles,
        int preferredLanguageId,
        bool searchAlternativeTitles,
        int? seasonNumber = null)
    {
        ArgumentNullException.ThrowIfNull(alternativeTitles);
        if (!searchAlternativeTitles)
        {
            return Distinct([title, originalTitle], int.MaxValue);
        }

        var usable = alternativeTitles
            .Where(t => seasonNumber is not { } season || t.SeasonNumber is null || t.SeasonNumber == season)
            .ToList();
        bool Preferred(AlternativeTitle t) => preferredLanguageId > 0 && t.LanguageId == preferredLanguageId;

        IEnumerable<string> ordered =
        [
            .. usable.Where(Preferred).OrderBy(SourceOrder).Select(t => t.Title),
            title,
            .. usable.Where(t => t.Source is AlternativeTitleSources.Xem or AlternativeTitleSources.User && !Preferred(t)).OrderBy(SourceOrder).Select(t => t.Title),
            originalTitle,
            .. usable.Where(t => t.LanguageId == Language.English.Id && !Preferred(t) && t.Source is AlternativeTitleSources.Translation).Select(t => t.Title),
        ];
        return Distinct(ordered, MaxSearchTitles);
    }

    /// <summary>Yours first, then scene names, translations, TMDB alternative titles.</summary>
    private static int SourceOrder(AlternativeTitle title) => title.Source switch
    {
        AlternativeTitleSources.User => 0,
        AlternativeTitleSources.Xem => 1,
        AlternativeTitleSources.Translation => 2,
        _ => 3,
    };

    private static List<string> Distinct(IEnumerable<string> titles, int max)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var t in titles)
        {
            if (string.IsNullOrWhiteSpace(t) || !seen.Add(CleanTitle(t)) || result.Count >= max)
            {
                continue;
            }

            result.Add(t.Trim());
        }

        return result;
    }
}
