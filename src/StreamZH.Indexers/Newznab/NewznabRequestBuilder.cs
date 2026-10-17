// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StreamZH.Core.Indexers;

namespace StreamZH.Indexers.Newznab;

/// <summary>One paged API query: function (t=), categories and the extra parameters (escaped, each starting with '&amp;').</summary>
public sealed record NewznabQuery(string Function, IReadOnlyList<int> Categories, string Parameters);

/// <summary>
/// The queries for one search, in tiers like Radarr's and Sonarr's request chains: all queries of a tier run,
/// and the next tier (the title fallback) only runs when the tier before found nothing.
/// </summary>
public sealed record NewznabSearchPlan(IReadOnlyList<IReadOnlyList<NewznabQuery>> Tiers);

/// <summary>Turns search criteria into Newznab queries, based on what the indexer's caps support.</summary>
public static partial class NewznabRequestBuilder
{
    /// <summary>
    /// Radarr's movie search: tier 1 searches by id (aggregated tmdbid+imdbid when the indexer lists its movie-search
    /// parameters, otherwise tmdbid before imdbid); tier 2 searches by "title year" for every title.
    /// </summary>
    public static IReadOnlyList<NewznabSearchPlan> ForMovie(MovieSearchCriteria criteria, IReadOnlyList<int> categories, NewznabCaps caps)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (categories.Count == 0)
        {
            return [];
        }

        var movieParams = caps.MovieSearchParams;
        var idTier = new List<NewznabQuery>();
        var imdbDigits = ImdbDigits(criteria.ImdbId);
        var includeTmdb = NewznabCaps.Supports(movieParams, "tmdbid") && criteria.TmdbId > 0;
        var includeImdb = NewznabCaps.Supports(movieParams, "imdbid") && imdbDigits is not null;
        var tmdb = $"&tmdbid={criteria.TmdbId.ToString(CultureInfo.InvariantCulture)}";

        // Radarr sends imdbid without the "tt" prefix.
        var imdb = $"&imdbid={imdbDigits}";

        if (caps.MovieAggregateIdSearch && (includeTmdb || includeImdb))
        {
            idTier.Add(new NewznabQuery("movie", categories, (includeTmdb ? tmdb : string.Empty) + (includeImdb ? imdb : string.Empty)));
        }
        else if (includeTmdb)
        {
            idTier.Add(new NewznabQuery("movie", categories, tmdb));
        }
        else if (includeImdb)
        {
            idTier.Add(new NewznabQuery("movie", categories, imdb));
        }

        var textTier = new List<NewznabQuery>();

        // Like Radarr, a text search needs the year: a bare title matches far too much.
        if (criteria.Year > 0)
        {
            var (function, engine) = NewznabCaps.Supports(movieParams, "q") ? ("movie", caps.MovieTextSearchEngine)
                : NewznabCaps.Supports(caps.SearchParams, "q") ? ("search", caps.TextSearchEngine)
                : (null, string.Empty);

            if (function is not null)
            {
                foreach (var title in QueryTitles(criteria.Titles, engine))
                {
                    textTier.Add(new NewznabQuery(function, categories, $"&q={Newznabify($"{title} {criteria.Year.ToString(CultureInfo.InvariantCulture)}")}"));
                }
            }
        }

        return Plans([Tiers(idTier, textTier)]);
    }

    /// <summary>
    /// Sonarr's series search: one plan per episode (several episode numbers mean several plans), a season pack
    /// searches with season only, a daily episode with season=yyyy and ep=MM/dd. Tier 1 searches by id
    /// (tvdbid, imdbid, tmdbid, aggregated when the indexer lists its tv-search parameters), tier 2 by title.
    /// </summary>
    public static IReadOnlyList<NewznabSearchPlan> ForEpisodes(EpisodeSearchCriteria criteria, IReadOnlyList<int> categories, NewznabCaps caps)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var tv = caps.TvSearchParams;
        if (categories.Count == 0 || tv is null)
        {
            return [];
        }

        var supportsText = NewznabCaps.Supports(tv, "q") || NewznabCaps.Supports(tv, "title");
        var supportsIds = new[] { "tvdbid", "imdbid", "rid", "tvmazeid", "tmdbid" }.Any(p => NewznabCaps.Supports(tv, p));
        var supportsSeason = NewznabCaps.Supports(tv, "season");
        var supportsEpisode = supportsSeason && NewznabCaps.Supports(tv, "ep");
        if (!supportsText && !supportsIds)
        {
            return [];
        }

        List<string> suffixes;
        if (criteria.AirDate is { } airDate)
        {
            if (!supportsEpisode
                || !DateOnly.TryParseExact(airDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return [];
            }

            suffixes = [$"&season={date.ToString("yyyy", CultureInfo.InvariantCulture)}&ep={date.ToString("MM", CultureInfo.InvariantCulture)}/{date.ToString("dd", CultureInfo.InvariantCulture)}"];
        }
        else if (criteria.EpisodeNumbers.Count == 0)
        {
            if (!supportsSeason)
            {
                return [];
            }

            suffixes = [$"&season={SeasonNumber(criteria.SeasonNumber)}"];
        }
        else
        {
            if (!supportsEpisode)
            {
                return [];
            }

            suffixes =
            [
                .. criteria.EpisodeNumbers.Distinct()
                    .Select(e => $"&season={SeasonNumber(criteria.SeasonNumber)}&ep={e.ToString(CultureInfo.InvariantCulture)}"),
            ];
        }

        return Plans(suffixes.Select(suffix => Tiers(TvIdQueries(criteria, categories, caps, suffix), TvTitleQueries(criteria, categories, caps, suffix))));
    }

    /// <summary>
    /// Free text search (the Search page): t=search, t=movie or t=tvsearch with q= as typed (no title cleaning).
    /// Movie and TV fall back to t=search when the indexer has no text search for them. One plan with one query.
    /// </summary>
    /// <param name="defaultCategories">Used when the criteria name no categories; empty means no category filter.</param>
    public static IReadOnlyList<NewznabSearchPlan> ForText(TextSearchCriteria criteria, IReadOnlyList<int> defaultCategories, NewznabCaps caps)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var query = criteria.Query.Trim();
        var hasQuery = query.Length > 0;

        bool Usable(IReadOnlyList<string>? parameters) => parameters is not null && (!hasQuery || NewznabCaps.Supports(parameters, "q"));

        var function = criteria.Type switch
        {
            TextSearchType.Movie when Usable(caps.MovieSearchParams) => "movie",
            TextSearchType.Tv when Usable(caps.TvSearchParams) => "tvsearch",
            _ when Usable(caps.SearchParams) => "search",
            _ => null,
        };

        if (function is null)
        {
            return [];
        }

        var categories = criteria.Categories.Count > 0 ? criteria.Categories : defaultCategories;
        var parameters = hasQuery ? $"&q={Uri.EscapeDataString(query)}" : string.Empty;
        return [new NewznabSearchPlan([[new NewznabQuery(function, [.. categories.Distinct()], parameters)]])];
    }

    /// <summary>The newest releases (RSS): t=search without a query.</summary>
    public static NewznabQuery Recent(IReadOnlyList<int> categories) => new("search", categories, string.Empty);

    /// <summary>Radarr's and Sonarr's GetCleanSceneTitle: for search engines other than "raw".</summary>
    public static string CleanTitle(string title)
    {
        var clean = BeginningTheRegex().Replace(title.Trim(), string.Empty);
        clean = clean.Replace("&", "and", StringComparison.Ordinal);
        clean = SpecialCharacterRegex().Replace(clean, string.Empty);
        clean = NonWordRegex().Replace(clean, "+");
        clean = RepeatedPlusRegex().Replace(clean, "+");
        clean = RemoveAccents(clean);
        return clean.Trim('+', ' ');
    }

    /// <summary>"+" counts as a space, the rest is URL-escaped (Radarr's NewsnabifyTitle).</summary>
    public static string Newznabify(string title) => Uri.EscapeDataString(title.Replace('+', ' '));

    /// <summary>"tt0076759", "0076759" or "76759" → "0076759"; null when there is no usable id.</summary>
    public static string? ImdbDigits(string? imdbId)
    {
        var digits = imdbId?.Trim();
        if (digits is null)
        {
            return null;
        }

        if (digits.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number.ToString("D7", CultureInfo.InvariantCulture)
            : null;
    }

    private static List<NewznabQuery> TvIdQueries(EpisodeSearchCriteria criteria, IReadOnlyList<int> categories, NewznabCaps caps, string suffix)
    {
        var tv = caps.TvSearchParams;
        var imdbDigits = ImdbDigits(criteria.ImdbId);
        var includeTvdb = NewznabCaps.Supports(tv, "tvdbid") && criteria.TvdbId > 0;
        var includeImdb = NewznabCaps.Supports(tv, "imdbid") && imdbDigits is not null;
        var includeTmdb = NewznabCaps.Supports(tv, "tmdbid") && criteria.TmdbId > 0;

        var tvdb = $"&tvdbid={criteria.TvdbId.ToString(CultureInfo.InvariantCulture)}";

        // Sonarr sends the series' imdbid with its "tt" prefix.
        var imdb = $"&imdbid=tt{imdbDigits}";
        var tmdb = $"&tmdbid={criteria.TmdbId.ToString(CultureInfo.InvariantCulture)}";

        if (caps.TvAggregateIdSearch && (includeTvdb || includeImdb || includeTmdb))
        {
            var ids = (includeTvdb ? tvdb : string.Empty) + (includeImdb ? imdb : string.Empty) + (includeTmdb ? tmdb : string.Empty);
            return [new NewznabQuery("tvsearch", categories, ids + suffix)];
        }

        var single = includeTvdb ? tvdb : includeImdb ? imdb : includeTmdb ? tmdb : null;
        return single is null ? [] : [new NewznabQuery("tvsearch", categories, single + suffix)];
    }

    private static List<NewznabQuery> TvTitleQueries(EpisodeSearchCriteria criteria, IReadOnlyList<int> categories, NewznabCaps caps, string suffix)
    {
        var tv = caps.TvSearchParams;
        if (NewznabCaps.Supports(tv, "title"))
        {
            return [.. DistinctTitles(criteria.Titles).Select(t => new NewznabQuery("tvsearch", categories, $"&title={Uri.EscapeDataString(t)}{suffix}"))];
        }

        if (NewznabCaps.Supports(tv, "q"))
        {
            return [.. QueryTitles(criteria.Titles, caps.TvTextSearchEngine).Select(t => new NewznabQuery("tvsearch", categories, $"&q={Newznabify(t)}{suffix}"))];
        }

        return [];
    }

    private static IEnumerable<string> QueryTitles(IEnumerable<string> titles, string engine) =>
        string.Equals(engine, "raw", StringComparison.OrdinalIgnoreCase)
            ? DistinctTitles(titles)
            : DistinctTitles(titles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(CleanTitle));

    private static IEnumerable<string> DistinctTitles(IEnumerable<string> titles) =>
        titles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);

    // Temporary workaround from Sonarr: NNTMux reads season=0 as "no season"; "00" works on every Newznab indexer.
    private static string SeasonNumber(int season) => season == 0 ? "00" : season.ToString(CultureInfo.InvariantCulture);

    private static List<IReadOnlyList<NewznabQuery>> Tiers(params List<NewznabQuery>[] tiers) =>
        [.. tiers.Where(t => t.Count > 0)];

    private static List<NewznabSearchPlan> Plans(IEnumerable<List<IReadOnlyList<NewznabQuery>>> plans) =>
        [.. plans.Where(p => p.Count > 0).Select(p => new NewznabSearchPlan(p))];

    private static string RemoveAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"^the\s", RegexOptions.IgnoreCase)]
    private static partial Regex BeginningTheRegex();

    [GeneratedRegex(@"['.`´‘’]")]
    private static partial Regex SpecialCharacterRegex();

    [GeneratedRegex(@"\W")]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\+{2,}")]
    private static partial Regex RepeatedPlusRegex();
}
