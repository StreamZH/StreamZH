// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace StreamZH.Metadata.Tmdb;

/// <summary>Lists, collections, people, discover and find for the import lists.</summary>
public sealed partial class TmdbClient : ITmdbListClient
{
    public async Task<TmdbListPage> GetListAsync(string listId, int page, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listId);
        var dto = await GetAsync<ListPageDto>($"list/{Uri.EscapeDataString(listId.Trim())}?page={Math.Max(1, page)}", null, cancellationToken);
        return Page(dto, dto.Items ?? dto.Results, null);
    }

    public async Task<TmdbListPage> GetCollectionAsync(int collectionId, CancellationToken cancellationToken = default)
    {
        var dto = await GetAsync<ListPageDto>($"collection/{collectionId}", null, cancellationToken);
        return new TmdbListPage(Blank(dto.Name), [.. Entries(dto.Parts, TmdbMediaKind.Movie)], 1, 1);
    }

    public async Task<TmdbPersonCredits> GetPersonCreditsAsync(int personId, CancellationToken cancellationToken = default)
    {
        var dto = await GetAsync<PersonDto>($"person/{personId}?append_to_response=movie_credits,tv_credits", null, cancellationToken);
        IEnumerable<TmdbCredit> Credits(List<ListEntryDto>? items, TmdbMediaKind kind, bool crew) =>
            (items ?? []).Where(i => i.Id > 0).Select(i => new TmdbCredit(Entry(i, kind), crew ? Blank(i.Department) : null, Blank(i.Job)));
        return new TmdbPersonCredits(
            dto.Id,
            dto.Name ?? string.Empty,
            [.. Credits(dto.MovieCredits?.Cast, TmdbMediaKind.Movie, false), .. Credits(dto.TvCredits?.Cast, TmdbMediaKind.Series, false)],
            [.. Credits(dto.MovieCredits?.Crew, TmdbMediaKind.Movie, true), .. Credits(dto.TvCredits?.Crew, TmdbMediaKind.Series, true)]);
    }

    public async Task<TmdbListPage> DiscoverAsync(
        TmdbMediaKind kind, IReadOnlyDictionary<string, string> parameters, int page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var query = string.Join(
            '&',
            parameters.Where(p => !string.IsNullOrEmpty(p.Value))
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")
                .Append($"page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}")
                .Append("include_adult=false"));
        var dto = await GetAsync<ListPageDto>($"discover/{(kind == TmdbMediaKind.Movie ? "movie" : "tv")}?{query}", null, cancellationToken);
        return Page(dto, dto.Results, kind);
    }

    public async Task<TmdbFindResult> FindAsync(string externalId, TmdbExternalSource source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        var name = source == TmdbExternalSource.Imdb ? "imdb_id" : "tvdb_id";
        var dto = await GetAsync<FindDto>($"find/{Uri.EscapeDataString(externalId.Trim())}?external_source={name}", null, cancellationToken);
        return new TmdbFindResult([.. Entries(dto.MovieResults, TmdbMediaKind.Movie)], [.. Entries(dto.TvResults, TmdbMediaKind.Series)]);
    }

    public async Task<string> GetCompanyNameAsync(int companyId, CancellationToken cancellationToken = default) =>
        (await GetAsync<IdNameDto>($"company/{companyId}", null, cancellationToken)).Name ?? string.Empty;

    public async Task<string> GetKeywordNameAsync(int keywordId, CancellationToken cancellationToken = default) =>
        (await GetAsync<IdNameDto>($"keyword/{keywordId}", null, cancellationToken)).Name ?? string.Empty;

    public async Task<(int Id, string Name)?> GetMovieCollectionAsync(int movieId, CancellationToken cancellationToken = default)
    {
        var dto = await GetAsync<MovieCollectionDto>($"movie/{movieId}", null, cancellationToken);
        return dto.BelongsToCollection is { Id: > 0 } c ? (c.Id, c.Name ?? string.Empty) : null;
    }

    private static TmdbListPage Page(ListPageDto dto, List<ListEntryDto>? items, TmdbMediaKind? kind) =>
        new(Blank(dto.Name), [.. Entries(items, kind)], Math.Max(1, dto.Page), Math.Max(1, dto.TotalPages));

    /// <summary>Entries with a known kind: from <paramref name="kind"/>, else from media_type (people and others are dropped).</summary>
    private static IEnumerable<TmdbListEntry> Entries(List<ListEntryDto>? items, TmdbMediaKind? kind) =>
        (items ?? [])
            .Where(i => i.Id > 0)
            .Select(i => (Item: i, Kind: kind ?? i.MediaType switch
            {
                "movie" => TmdbMediaKind.Movie,
                "tv" => TmdbMediaKind.Series,
                _ => (TmdbMediaKind?)null,
            }))
            .Where(x => x.Kind is not null)
            .Select(x => Entry(x.Item, x.Kind!.Value));

    private static TmdbListEntry Entry(ListEntryDto i, TmdbMediaKind kind)
    {
        var date = Blank(kind == TmdbMediaKind.Movie ? i.ReleaseDate : i.FirstAirDate);
        return new TmdbListEntry(
            kind,
            i.Id,
            (kind == TmdbMediaKind.Movie ? i.Title ?? i.Name : i.Name ?? i.Title) ?? string.Empty,
            YearOf(date),
            date,
            i.PosterPath,
            i.VoteCount > 0 ? i.VoteAverage : null,
            i.VoteCount ?? 0,
            Blank(i.OriginalLanguage));
    }
}
