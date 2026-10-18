// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Media;
using StreamZH.Library.Notifications;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.ImportLists;

/// <summary>
/// The TMDB list types (list, collection, person, company, keyword, popular and top rated) through the TMDB key of
/// Settings → Metadata. Like Radarr's TMDb lists; popular and top rated use discover so the filters always apply.
/// </summary>
public sealed class TmdbListSource(ITmdbListClient tmdb) : IImportListSource, IImportListValidator
{
    /// <summary>TMDB returns 20 items per page; never more than this many pages per list.</summary>
    public const int MaxPages = 50;

    /// <summary>Like TMDB's own top rated page: titles with fewer votes are left out.</summary>
    public const int TopRatedMinVotes = 200;

    public IReadOnlyList<string> Implementations { get; } =
    [
        ImportListCatalog.TmdbList, ImportListCatalog.TmdbCollection, ImportListCatalog.TmdbPerson,
        ImportListCatalog.TmdbCompany, ImportListCatalog.TmdbKeyword, ImportListCatalog.TmdbPopular,
    ];

    public void Validate(string implementation, MediaType mediaType, ConnectionSettings settings, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(errors);
        var idField = IdField(implementation);
        if (idField is not null && settings.Text(idField) is { } text && ImportListHttp.TmdbNumber(text) is null)
        {
            errors.TryAdd(idField, ["Enter the number from the address on themoviedb.org."]);
        }

        if (implementation == ImportListCatalog.TmdbPopular)
        {
            if (settings.Text("minRating") is { } rating
                && (!double.TryParse(rating.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) || r is < 0 or > 10))
            {
                errors.TryAdd("minRating", ["Use a number from 0 to 10."]);
            }

            if (settings.Text("language") is { } language && (language.Length != 2 || !language.All(char.IsAsciiLetter)))
            {
                errors.TryAdd("language", ["Use a two-letter code like en."]);
            }

            if (settings.Int("yearFrom") is { } from && settings.Int("yearTo") is { } to && from > to)
            {
                errors.TryAdd("yearTo", ["Must not be before the first year."]);
            }
        }

        if (implementation == ImportListCatalog.TmdbPerson && !settings.Bool("cast") && settings.List("departments").Count == 0)
        {
            errors.TryAdd("departments", ["Turn on Cast or choose a crew department."]);
        }
    }

    public async Task<ImportListFetch> FetchAsync(ImportListContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var kind = context.MediaType == MediaType.Series ? TmdbMediaKind.Series : TmdbMediaKind.Movie;
        var s = context.Settings;
        var implementation = context.Definition.Implementation;
        var id = IdField(implementation) is { } field ? ImportListHttp.TmdbNumber(s.Text(field)) ?? throw new ImportListException("TMDB: the number is missing") : 0;

        return implementation switch
        {
            ImportListCatalog.TmdbList => await PagedAsync(page => tmdb.GetListAsync(id.ToString(CultureInfo.InvariantCulture), page, cancellationToken), kind, context.Limit(500)),
            ImportListCatalog.TmdbCollection => await CollectionAsync(id, cancellationToken),
            ImportListCatalog.TmdbPerson => await PersonAsync(id, kind, s, context.Limit(500), cancellationToken),
            ImportListCatalog.TmdbCompany => await DiscoverAsync(kind, new Dictionary<string, string> { ["with_companies"] = Id(id), ["sort_by"] = "popularity.desc" }, context.Limit(100), cancellationToken),
            ImportListCatalog.TmdbKeyword => await DiscoverAsync(kind, new Dictionary<string, string> { ["with_keywords"] = Id(id), ["sort_by"] = "popularity.desc" }, context.Limit(100), cancellationToken),
            ImportListCatalog.TmdbPopular => await DiscoverAsync(kind, PopularParameters(kind, s, context.Now), context.Limit(50), cancellationToken),
            _ => throw new ImportListException("this type is not supported"),
        };
    }

    /// <summary>The discover parameters of "popular and top rated" (dates relative to <paramref name="now"/>).</summary>
    public static Dictionary<string, string> PopularParameters(TmdbMediaKind kind, ConnectionSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        var movie = kind == TmdbMediaKind.Movie;
        var list = settings.Text(movie ? "movieList" : "seriesList") ?? "popular";
        var minVotes = settings.Int("minVotes") ?? 0;

        switch (list)
        {
            case "topRated":
                p["sort_by"] = "vote_average.desc";
                minVotes = Math.Max(minVotes, TopRatedMinVotes);
                break;
            case "upcoming" when movie:
                p["sort_by"] = "popularity.desc";
                p["with_release_type"] = "2|3";
                p["release_date.gte"] = Date(today.AddDays(1));
                p["release_date.lte"] = Date(today.AddMonths(3));
                break;
            case "nowPlaying" when movie:
                p["sort_by"] = "popularity.desc";
                p["with_release_type"] = "2|3";
                p["release_date.gte"] = Date(today.AddDays(-42));
                p["release_date.lte"] = Date(today);
                break;
            case "onTheAir" when !movie:
                p["sort_by"] = "popularity.desc";
                p["air_date.gte"] = Date(today);
                p["air_date.lte"] = Date(today.AddDays(7));
                break;
            default:
                p["sort_by"] = "popularity.desc";
                break;
        }

        if (minVotes > 0)
        {
            p["vote_count.gte"] = Id(minVotes);
        }

        if (settings.Text("minRating") is { } rating
            && double.TryParse(rating.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
        {
            p["vote_average.gte"] = r.ToString("0.#", CultureInfo.InvariantCulture);
        }

        if (settings.List(movie ? "movieGenres" : "seriesGenres") is { Count: > 0 } genres)
        {
            p["with_genres"] = string.Join('|', genres);
        }

        if (settings.Text("language") is { } language)
        {
            p["with_original_language"] = language.ToLowerInvariant();
        }

        var dateField = movie ? "primary_release_date" : "first_air_date";
        if (settings.Int("yearFrom") is { } from)
        {
            p[$"{dateField}.gte"] = $"{from}-01-01";
        }

        if (settings.Int("yearTo") is { } to)
        {
            p[$"{dateField}.lte"] = $"{to}-12-31";
        }

        if (movie && settings.List("certification") is { Count: > 0 } certifications)
        {
            p["certification_country"] = (settings.Text("certificationCountry") ?? "US").ToUpperInvariant();
            p["certification"] = string.Join('|', certifications);
        }

        return p;
    }

    private static string Id(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? IdField(string implementation) => implementation switch
    {
        ImportListCatalog.TmdbList => "listId",
        ImportListCatalog.TmdbCollection => "collectionId",
        ImportListCatalog.TmdbPerson => "personId",
        ImportListCatalog.TmdbCompany => "companyId",
        ImportListCatalog.TmdbKeyword => "keywordId",
        _ => null,
    };

    private async Task<ImportListFetch> CollectionAsync(int id, CancellationToken cancellationToken)
    {
        var page = await TmdbItems.Call(() => tmdb.GetCollectionAsync(id, cancellationToken));
        return new ImportListFetch([.. page.Results.Select(TmdbItems.From)], page.Name);
    }

    private async Task<ImportListFetch> PersonAsync(int id, TmdbMediaKind kind, ConnectionSettings s, int limit, CancellationToken cancellationToken)
    {
        var person = await TmdbItems.Call(() => tmdb.GetPersonCreditsAsync(id, cancellationToken));
        var departments = s.List("departments").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var credits = (s.Bool("cast") ? person.Cast : [])
            .Concat(person.Crew.Where(c => c.Department is { } d && departments.Contains(d)))
            .Where(c => c.Entry.Kind == kind)
            .Select(c => c.Entry)
            .DistinctBy(e => e.TmdbId)
            .OrderByDescending(e => e.ReleaseDate ?? string.Empty, StringComparer.Ordinal)
            .Take(limit);
        return new ImportListFetch([.. credits.Select(TmdbItems.From)], person.Name);
    }

    private Task<ImportListFetch> DiscoverAsync(TmdbMediaKind kind, Dictionary<string, string> parameters, int limit, CancellationToken cancellationToken) =>
        PagedAsync(page => tmdb.DiscoverAsync(kind, parameters, page, cancellationToken), kind, limit);

    /// <summary>Reads pages until the limit, the last page or <see cref="MaxPages"/>.</summary>
    private static async Task<ImportListFetch> PagedAsync(Func<int, Task<TmdbListPage>> load, TmdbMediaKind kind, int limit)
    {
        var items = new List<ImportListItem>();
        string? name = null;
        for (var page = 1; page <= MaxPages; page++)
        {
            var result = await TmdbItems.Call(() => load(page));
            name ??= result.Name;
            items.AddRange(result.Results.Where(e => e.Kind == kind).Select(TmdbItems.From));
            if (items.Count >= limit || page >= result.TotalPages || result.Results.Count == 0)
            {
                break;
            }
        }

        return new ImportListFetch([.. items.DistinctBy(i => i.TmdbId).Take(limit)], name);
    }
}
