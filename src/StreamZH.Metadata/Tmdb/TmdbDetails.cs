// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Metadata.Tmdb;

/// <summary>
/// The extra TMDB data the NFO writer and the notifications need (ratings, certification, credits, season posters,
/// episode stills). Separate from <see cref="ITmdbClient"/> so the library refresh stays one small request per item.
/// </summary>
public interface ITmdbDetailsClient
{
    /// <param name="certificationCountry">ISO 3166-1 code (e.g. "US", "DE") whose age rating goes into <see cref="TmdbMovieDetails.Certification"/>.</param>
    Task<TmdbMovieDetails> GetMovieDetailsAsync(int tmdbId, string certificationCountry, CancellationToken cancellationToken = default);

    Task<TmdbSeriesDetails> GetSeriesDetailsAsync(int tmdbId, string certificationCountry, CancellationToken cancellationToken = default);

    Task<TmdbSeasonDetails> GetSeasonDetailsAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default);
}

/// <summary>A person in the cast; <see cref="ProfilePath"/> is a TMDB image path.</summary>
public sealed record TmdbCastMember(string Name, string? Character, int Order, string? ProfilePath);

/// <param name="Certification">Age rating in the chosen country (e.g. "PG-13", "12"), null when TMDB has none.</param>
/// <param name="ReleaseDate">ISO date of the first release.</param>
public sealed record TmdbMovieDetails(
    TmdbMovie Movie,
    string? Tagline,
    double? Rating,
    int Votes,
    string? ReleaseDate,
    string? Certification,
    IReadOnlyList<string> Studios,
    IReadOnlyList<string> Countries,
    string? Collection,
    IReadOnlyList<TmdbCastMember> Cast,
    IReadOnlyList<string> Directors,
    IReadOnlyList<string> Writers);

public sealed record TmdbSeasonSummary(int SeasonNumber, string? Name, string? PosterPath);

public sealed record TmdbSeriesDetails(
    TmdbSeries Series,
    double? Rating,
    int Votes,
    string? FirstAirDate,
    string? Certification,
    IReadOnlyList<string> Studios,
    IReadOnlyList<TmdbCastMember> Cast,
    IReadOnlyList<TmdbSeasonSummary> Seasons);

/// <param name="StillPath">Episode thumbnail (TMDB image path).</param>
public sealed record TmdbEpisodeDetails(
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    string? AirDate,
    string? Overview,
    string? StillPath,
    double? Rating,
    int Votes,
    int? Runtime,
    IReadOnlyList<string> Directors,
    IReadOnlyList<string> Writers);

public sealed record TmdbSeasonDetails(int SeasonNumber, string? Name, string? PosterPath, IReadOnlyList<TmdbEpisodeDetails> Episodes);

/// <summary>URLs of the TMDB image CDN. Paths look like "/d5NXSklXo0qyIYkgV94XAgMIckC.jpg".</summary>
public static class TmdbImages
{
    public static readonly Uri BaseAddress = new("https://image.tmdb.org/t/p/");

    /// <summary>Sizes the CDN serves (posters, backdrops, stills and profiles together).</summary>
    public static readonly IReadOnlySet<string> Sizes = new HashSet<string>(StringComparer.Ordinal)
    {
        "w45", "w92", "w154", "w185", "w300", "w342", "w500", "w632", "w780", "w1280", "h632", "original",
    };

    /// <summary>Public URL of an image, e.g. for Discord or Kodi; null without a path.</summary>
    public static string? Url(string? path, string size = "original") =>
        string.IsNullOrWhiteSpace(path) ? null : $"{BaseAddress}{size}/{path.TrimStart('/')}";

    /// <summary>True for a plain TMDB file name such as "/abc_DEF-123.jpg" (no folders, no tricks).</summary>
    public static bool IsValidPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Length > 80)
        {
            return false;
        }

        var name = path.AsSpan(1);
        var dot = name.LastIndexOf('.');
        if (dot <= 0)
        {
            return false;
        }

        foreach (var c in name[..dot])
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                return false;
            }
        }

        var extension = name[(dot + 1)..];
        return extension.Equals("jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals("png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals("svg", StringComparison.OrdinalIgnoreCase);
    }
}
