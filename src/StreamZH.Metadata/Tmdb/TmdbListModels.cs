// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Metadata.Tmdb;

/// <summary>Movie or TV show in a TMDB list, credit or discover result.</summary>
public enum TmdbMediaKind
{
    Movie,
    Series,
}

/// <summary>Kind of id for <see cref="ITmdbListClient.FindAsync"/>.</summary>
public enum TmdbExternalSource
{
    Imdb,
    Tvdb,
}

/// <summary>One movie or show as TMDB lists, discover and credits return it.</summary>
/// <param name="ReleaseDate">Movies: release date; series: first air date (yyyy-MM-dd); null when unknown.</param>
/// <param name="Overview">Plot summary (0.10, for Discover).</param>
/// <param name="BackdropPath">Background image (0.10, for Discover).</param>
public sealed record TmdbListEntry(
    TmdbMediaKind Kind,
    int TmdbId,
    string Title,
    int Year,
    string? ReleaseDate = null,
    string? PosterPath = null,
    double? VoteAverage = null,
    int VoteCount = 0,
    string? OriginalLanguage = null,
    string? Overview = null,
    string? BackdropPath = null);

/// <summary>One page of results.</summary>
/// <param name="Name">Name of the list, collection or person when TMDB returns one.</param>
public sealed record TmdbListPage(string? Name, IReadOnlyList<TmdbListEntry> Results, int Page, int TotalPages);

/// <summary>A credit of a person (cast or crew) with the crew department ("Directing", "Writing" …).</summary>
/// <param name="Department">Null for cast (acting) credits.</param>
public sealed record TmdbCredit(TmdbListEntry Entry, string? Department, string? Job);

public sealed record TmdbPersonCredits(int PersonId, string Name, IReadOnlyList<TmdbCredit> Cast, IReadOnlyList<TmdbCredit> Crew);

/// <summary>Result of a lookup by IMDb or TVDB id.</summary>
public sealed record TmdbFindResult(IReadOnlyList<TmdbListEntry> Movies, IReadOnlyList<TmdbListEntry> Series);

/// <summary>TMDB calls behind Settings → Import lists (lists, collections, people, companies, keywords, discover, find).</summary>
public interface ITmdbListClient
{
    /// <summary>A public TMDB list (v3 list id); movies and shows mixed.</summary>
    Task<TmdbListPage> GetListAsync(string listId, int page, CancellationToken cancellationToken = default);

    /// <summary>All movies of a collection, e.g. 10 for Star Wars.</summary>
    Task<TmdbListPage> GetCollectionAsync(int collectionId, CancellationToken cancellationToken = default);

    /// <summary>Movie and TV credits of a person (one request).</summary>
    Task<TmdbPersonCredits> GetPersonCreditsAsync(int personId, CancellationToken cancellationToken = default);

    /// <summary>
    /// discover/movie or discover/tv with the given parameters (TMDB names, e.g. with_companies, sort_by,
    /// vote_count.gte); <paramref name="page"/> starts at 1.
    /// </summary>
    Task<TmdbListPage> DiscoverAsync(TmdbMediaKind kind, IReadOnlyDictionary<string, string> parameters, int page, CancellationToken cancellationToken = default);

    /// <summary>Finds the TMDB entries of an IMDb id (tt…) or a TVDB id.</summary>
    Task<TmdbFindResult> FindAsync(string externalId, TmdbExternalSource source, CancellationToken cancellationToken = default);

    Task<string> GetCompanyNameAsync(int companyId, CancellationToken cancellationToken = default);

    Task<string> GetKeywordNameAsync(int keywordId, CancellationToken cancellationToken = default);

    /// <summary>The collection a movie belongs to (id and name), null when none.</summary>
    Task<(int Id, string Name)?> GetMovieCollectionAsync(int movieId, CancellationToken cancellationToken = default);
}

// Wire formats (snake_case JSON).
internal sealed class ListEntryDto
{
    public int Id { get; set; }
    public string? MediaType { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? ReleaseDate { get; set; }
    public string? FirstAirDate { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? Overview { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public string? OriginalLanguage { get; set; }
    public string? Department { get; set; }
    public string? Job { get; set; }
}

internal sealed class ListPageDto
{
    public string? Name { get; set; }
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public List<ListEntryDto>? Results { get; set; }

    /// <summary>v3 lists return "items" instead of "results".</summary>
    public List<ListEntryDto>? Items { get; set; }

    /// <summary>Collections return "parts".</summary>
    public List<ListEntryDto>? Parts { get; set; }
}

internal sealed class CreditListDto
{
    public List<ListEntryDto>? Cast { get; set; }
    public List<ListEntryDto>? Crew { get; set; }
}

internal sealed class PersonDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public CreditListDto? MovieCredits { get; set; }
    public CreditListDto? TvCredits { get; set; }
}

internal sealed class FindDto
{
    public List<ListEntryDto>? MovieResults { get; set; }
    public List<ListEntryDto>? TvResults { get; set; }
}

internal sealed class IdNameDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

internal sealed class MovieCollectionDto
{
    public IdNameDto? BelongsToCollection { get; set; }
}
