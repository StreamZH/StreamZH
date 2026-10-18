// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Metadata.Tmdb;

/// <summary>Ratings, certification, credits and artwork paths for NFO files (one request per movie, series or season).</summary>
public sealed partial class TmdbClient : ITmdbDetailsClient
{
    /// <summary>Actors written to an NFO file; Kodi shows the first ones only anyway.</summary>
    public const int MaxCast = 20;

    public async Task<TmdbMovieDetails> GetMovieDetailsAsync(int tmdbId, string certificationCountry, CancellationToken cancellationToken = default)
    {
        var m = await GetAsync<MovieDetailsDto>($"movie/{tmdbId}?append_to_response=credits,release_dates", null, cancellationToken);
        var movie = new TmdbMovie(
            m.Id,
            Blank(m.ImdbId),
            m.Title ?? string.Empty,
            m.OriginalTitle ?? m.Title ?? string.Empty,
            YearOf(m.ReleaseDate),
            Blank(m.Overview),
            m.Runtime ?? 0,
            m.Status ?? string.Empty,
            [.. (m.Genres ?? []).Select(g => g.Name).OfType<string>()],
            m.PosterPath,
            m.BackdropPath,
            m.OriginalLanguage);
        return new TmdbMovieDetails(
            movie,
            Blank(m.Tagline),
            m.VoteCount > 0 ? m.VoteAverage : null,
            m.VoteCount ?? 0,
            Blank(m.ReleaseDate),
            MovieCertification(m.ReleaseDates, certificationCountry),
            [.. (m.ProductionCompanies ?? []).Select(c => c.Name).OfType<string>()],
            [.. (m.ProductionCountries ?? []).Select(c => c.Name).OfType<string>()],
            Blank(m.BelongsToCollection?.Name),
            Cast(m.Credits),
            Crew(m.Credits, "Director"),
            Crew(m.Credits, "Screenplay", "Writer", "Story", "Novel"));
    }

    public async Task<TmdbSeriesDetails> GetSeriesDetailsAsync(int tmdbId, string certificationCountry, CancellationToken cancellationToken = default)
    {
        var t = await GetAsync<TvDetailsDto>($"tv/{tmdbId}?append_to_response=external_ids,credits,content_ratings", null, cancellationToken);
        var series = new TmdbSeries(
            t.Id,
            t.ExternalIds?.TvdbId,
            Blank(t.ExternalIds?.ImdbId),
            t.Name ?? string.Empty,
            t.OriginalName ?? t.Name ?? string.Empty,
            YearOf(t.FirstAirDate),
            Blank(t.Overview),
            t.Status ?? string.Empty,
            t.Networks?.FirstOrDefault()?.Name,
            [.. (t.Genres ?? []).Select(g => g.Name).OfType<string>()],
            t.PosterPath,
            t.BackdropPath,
            [.. (t.Seasons ?? []).Select(s => s.SeasonNumber).Order()],
            t.OriginalLanguage);
        var rating = t.ContentRatings?.Results?
            .FirstOrDefault(r => string.Equals(r.Iso31661, certificationCountry, StringComparison.OrdinalIgnoreCase))?.Rating;
        return new TmdbSeriesDetails(
            series,
            t.VoteCount > 0 ? t.VoteAverage : null,
            t.VoteCount ?? 0,
            Blank(t.FirstAirDate),
            Blank(rating),
            [.. (t.Networks ?? []).Select(n => n.Name).OfType<string>()],
            Cast(t.Credits),
            [.. (t.Seasons ?? []).OrderBy(s => s.SeasonNumber).Select(s => new TmdbSeasonSummary(s.SeasonNumber, Blank(s.Name), s.PosterPath))]);
    }

    public async Task<TmdbSeasonDetails> GetSeasonDetailsAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default)
    {
        var s = await GetAsync<SeasonDetailsDto>($"tv/{tmdbId}/season/{seasonNumber}", null, cancellationToken);
        return new TmdbSeasonDetails(
            seasonNumber,
            Blank(s.Name),
            s.PosterPath,
            [.. (s.Episodes ?? []).Select(e => new TmdbEpisodeDetails(
                e.SeasonNumber,
                e.EpisodeNumber,
                string.IsNullOrWhiteSpace(e.Name) ? $"Episode {e.EpisodeNumber}" : e.Name,
                Blank(e.AirDate),
                Blank(e.Overview),
                e.StillPath,
                e.VoteCount > 0 ? e.VoteAverage : null,
                e.VoteCount ?? 0,
                e.Runtime,
                [.. (e.Crew ?? []).Where(c => c.Job == "Director").Select(c => c.Name).OfType<string>().Distinct()],
                [.. (e.Crew ?? []).Where(c => c.Job is "Writer" or "Screenplay" or "Teleplay").Select(c => c.Name).OfType<string>().Distinct()]))]);
    }

    /// <summary>TMDB release types (release_dates): 4 = digital, 5 = physical (DVD/Blu-ray).</summary>
    private const int DigitalReleaseType = 4;
    private const int PhysicalReleaseType = 5;

    /// <summary>
    /// The earliest release of a type in any country as yyyy-MM-dd (like Radarr's digital and physical release); null when none.
    /// </summary>
    internal static string? EarliestRelease(ReleaseDatesDto? releaseDates, int type) =>
        (releaseDates?.Results ?? [])
            .SelectMany(r => r.ReleaseDates ?? [])
            .Where(d => d.Type == type && d.ReleaseDate is { Length: >= 10 })
            .Select(d => d.ReleaseDate![..10])
            .Where(d => DateOnly.TryParseExact(d, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// The certification of the country: theatrical release first (type 3), then any release with one.
    /// </summary>
    private static string? MovieCertification(ReleaseDatesDto? releaseDates, string country)
    {
        var dates = releaseDates?.Results?
            .FirstOrDefault(r => string.Equals(r.Iso31661, country, StringComparison.OrdinalIgnoreCase))?.ReleaseDates ?? [];
        return dates.Where(d => d.Type == 3).Select(d => Blank(d.Certification)).FirstOrDefault(c => c is not null)
            ?? dates.Select(d => Blank(d.Certification)).FirstOrDefault(c => c is not null);
    }

    private static List<TmdbCastMember> Cast(CreditsDto? credits) =>
        [.. (credits?.Cast ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.Order)
            .Take(MaxCast)
            .Select(c => new TmdbCastMember(c.Name!, Blank(c.Character), c.Order, c.ProfilePath))];

    private static List<string> Crew(CreditsDto? credits, params string[] jobs) =>
        [.. (credits?.Crew ?? []).Where(c => jobs.Contains(c.Job)).Select(c => c.Name).OfType<string>().Distinct()];
}

// Wire formats of the detail requests (snake_case JSON).
internal sealed class MovieDetailsDto
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    public int? Runtime { get; set; }
    public string? Status { get; set; }
    public string? ImdbId { get; set; }
    public string? OriginalLanguage { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public List<NamedDto>? Genres { get; set; }
    public List<NamedDto>? ProductionCompanies { get; set; }
    public List<NamedDto>? ProductionCountries { get; set; }
    public NamedDto? BelongsToCollection { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public CreditsDto? Credits { get; set; }
    public ReleaseDatesDto? ReleaseDates { get; set; }
}

internal sealed class TvDetailsDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? OriginalName { get; set; }
    public string? OriginalLanguage { get; set; }
    public string? FirstAirDate { get; set; }
    public string? Overview { get; set; }
    public string? Status { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public List<NamedDto>? Genres { get; set; }
    public List<NamedDto>? Networks { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public List<SeasonInfoDto>? Seasons { get; set; }
    public ExternalIdsDto? ExternalIds { get; set; }
    public CreditsDto? Credits { get; set; }
    public ContentRatingsDto? ContentRatings { get; set; }
}

internal sealed class SeasonInfoDto
{
    public int SeasonNumber { get; set; }
    public string? Name { get; set; }
    public string? PosterPath { get; set; }
}

internal sealed class SeasonDetailsDto
{
    public string? Name { get; set; }
    public string? PosterPath { get; set; }
    public List<EpisodeDetailsDto>? Episodes { get; set; }
}

internal sealed class EpisodeDetailsDto
{
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string? Name { get; set; }
    public string? AirDate { get; set; }
    public string? Overview { get; set; }
    public string? StillPath { get; set; }
    public double? VoteAverage { get; set; }
    public int? VoteCount { get; set; }
    public int? Runtime { get; set; }
    public List<CrewDto>? Crew { get; set; }
}

internal sealed class CreditsDto
{
    public List<CastDto>? Cast { get; set; }
    public List<CrewDto>? Crew { get; set; }
}

internal sealed class CastDto
{
    public string? Name { get; set; }
    public string? Character { get; set; }
    public int Order { get; set; }
    public string? ProfilePath { get; set; }
}

internal sealed class CrewDto
{
    public string? Name { get; set; }
    public string? Job { get; set; }
}

internal sealed class ReleaseDatesDto
{
    public List<CountryReleaseDatesDto>? Results { get; set; }
}

internal sealed class CountryReleaseDatesDto
{
    [System.Text.Json.Serialization.JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; set; }

    public List<ReleaseDateDto>? ReleaseDates { get; set; }
}

internal sealed class ReleaseDateDto
{
    public string? Certification { get; set; }
    public int Type { get; set; }

    /// <summary>ISO date and time, e.g. "2021-10-22T00:00:00.000Z".</summary>
    public string? ReleaseDate { get; set; }
}

internal sealed class ContentRatingsDto
{
    public List<ContentRatingDto>? Results { get; set; }
}

internal sealed class ContentRatingDto
{
    [System.Text.Json.Serialization.JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; set; }

    public string? Rating { get; set; }
}
