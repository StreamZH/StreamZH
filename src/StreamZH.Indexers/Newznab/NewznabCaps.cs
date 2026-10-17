// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Xml.Linq;
using StreamZH.Core.Indexers;

namespace StreamZH.Indexers.Newznab;

/// <summary>
/// Everything the request builder needs from t=caps. Richer than the public <see cref="IndexerCapabilities"/>
/// (aggregated id search, text search engines). A null parameter list means that search mode is not available.
/// </summary>
public sealed record NewznabCaps
{
    /// <summary>Radarr's and Sonarr's defaults, used when an indexer's caps cannot be read during a search.</summary>
    public static NewznabCaps Default { get; } = new();

    public int DefaultPageSize { get; init; } = 100;
    public int MaxPageSize { get; init; } = 100;

    public IReadOnlyList<string>? SearchParams { get; init; } = ["q"];

    // "rid" must stay for older Newznab installs (Sonarr); "imdbtitle"/"imdbyear" likewise (Radarr).
    public IReadOnlyList<string>? MovieSearchParams { get; init; } = ["q", "imdbid", "imdbtitle", "imdbyear"];
    public IReadOnlyList<string>? TvSearchParams { get; init; } = ["q", "rid", "season", "ep"];

    /// <summary>True when movie-search lists its supportedParams: several ids can go into one request.</summary>
    public bool MovieAggregateIdSearch { get; init; }

    /// <summary>True when tv-search lists its supportedParams: several ids can go into one request.</summary>
    public bool TvAggregateIdSearch { get; init; }

    // "sphinx" stays the default for older Newznab installs; "raw" means titles are sent unchanged.
    public string TextSearchEngine { get; init; } = "sphinx";
    public string MovieTextSearchEngine { get; init; } = "sphinx";
    public string TvTextSearchEngine { get; init; } = "sphinx";

    public IReadOnlyList<IndexerCategory> Categories { get; init; } = [];

    public static bool Supports(IReadOnlyList<string>? parameters, string name) =>
        parameters is not null && parameters.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Results per page: the indexer's maximum, but never more than 100 (and 100 when the indexer reports nonsense).</summary>
    public int PageSize => MaxPageSize > 0 ? Math.Min(100, MaxPageSize) : 100;

    public IndexerCapabilities ToCapabilities() => new(
        DefaultPageSize,
        MaxPageSize,
        SearchParams is not null,
        MovieSearchParams ?? [],
        TvSearchParams ?? [],
        Categories);

    /// <summary>Parses a t=caps document. Unknown or broken parts keep their defaults instead of failing the whole document.</summary>
    /// <exception cref="FormatException">The document is not a Newznab caps document.</exception>
    public static NewznabCaps Parse(XDocument document)
    {
        var root = document.Root;
        if (root is null || root.Name.LocalName != "caps")
        {
            throw new FormatException($"Expected a <caps> document but got <{root?.Name.LocalName ?? "nothing"}>.");
        }

        var caps = new NewznabCaps();

        if (root.Element("limits") is { } limits)
        {
            caps = caps with
            {
                DefaultPageSize = ParseInt(limits.Attribute("default")?.Value) ?? caps.DefaultPageSize,
                MaxPageSize = ParseInt(limits.Attribute("max")?.Value) ?? caps.MaxPageSize,
            };
        }

        if (root.Element("searching") is { } searching)
        {
            var search = ReadSearch(searching.Element("search"), caps.SearchParams);
            var movie = ReadSearch(searching.Element("movie-search"), caps.MovieSearchParams);
            var tv = ReadSearch(searching.Element("tv-search"), caps.TvSearchParams);

            caps = caps with
            {
                SearchParams = search.Params,
                TextSearchEngine = search.Engine ?? caps.TextSearchEngine,
                MovieSearchParams = movie.Params,
                MovieAggregateIdSearch = movie.Listed,
                MovieTextSearchEngine = movie.Engine ?? caps.MovieTextSearchEngine,
                TvSearchParams = tv.Params,
                TvAggregateIdSearch = tv.Listed,
                TvTextSearchEngine = tv.Engine ?? caps.TvTextSearchEngine,
            };
        }

        if (root.Element("categories") is { } categories)
        {
            caps = caps with { Categories = [.. categories.Elements("category").Select(ReadCategory).OfType<IndexerCategory>()] };
        }

        return caps;
    }

    private static (IReadOnlyList<string>? Params, bool Listed, string? Engine) ReadSearch(XElement? element, IReadOnlyList<string>? defaults)
    {
        if (element is null || !string.Equals(element.Attribute("available")?.Value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return (null, false, null);
        }

        var engine = element.Attribute("searchEngine")?.Value;
        if (element.Attribute("supportedParams")?.Value is not { } listed)
        {
            return (defaults, false, engine);
        }

        IReadOnlyList<string> parameters = [.. listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        return (parameters, true, engine);
    }

    private static IndexerCategory? ReadCategory(XElement element)
    {
        if (ParseInt(element.Attribute("id")?.Value) is not { } id)
        {
            return null;
        }

        IReadOnlyList<IndexerCategory> subcats =
        [
            .. element.Elements("subcat")
                .Select(s => ParseInt(s.Attribute("id")?.Value) is { } subId
                    ? new IndexerCategory(subId, s.Attribute("name")?.Value ?? string.Empty, [])
                    : null)
                .OfType<IndexerCategory>(),
        ];

        return new IndexerCategory(id, element.Attribute("name")?.Value ?? string.Empty, subcats);
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
}
