// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace StreamZH.Metadata.Tmdb;

/// <summary>Credentials and language for TMDB, read on every request so settings changes apply at once.</summary>
public interface ITmdbSettings
{
    /// <summary>API Read Access Token (v4, long) or API key (v3, 32 hex characters).</summary>
    string? Token { get; }

    /// <summary>TMDB language code, e.g. en-US or de-DE.</summary>
    string Language { get; }
}

public sealed record TmdbSearchResult(
    int TmdbId,
    string Title,
    string OriginalTitle,
    int Year,
    string? Overview,
    string? PosterPath);

public sealed record TmdbMovie(
    int TmdbId,
    string? ImdbId,
    string Title,
    string OriginalTitle,
    int Year,
    string? Overview,
    int Runtime,
    string Status,
    IReadOnlyList<string> Genres,
    string? PosterPath,
    string? BackdropPath,
    string? OriginalLanguage = null,
    string? ReleaseDate = null);

public sealed record TmdbSeries(
    int TmdbId,
    int? TvdbId,
    string? ImdbId,
    string Title,
    string OriginalTitle,
    int Year,
    string? Overview,
    string Status,
    string? Network,
    IReadOnlyList<string> Genres,
    string? PosterPath,
    string? BackdropPath,
    IReadOnlyList<int> SeasonNumbers,
    string? OriginalLanguage = null);

public sealed record TmdbEpisode(int SeasonNumber, int EpisodeNumber, string Title, string? AirDate, string? Overview);

public sealed class TmdbException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

// Wire formats (snake_case JSON).
internal sealed class SearchPage<T>
{
    public List<T> Results { get; set; } = [];
}

internal sealed class MovieDto
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Overview { get; set; }
    public int? Runtime { get; set; }
    public string? Status { get; set; }
    public string? ImdbId { get; set; }
    public string? OriginalLanguage { get; set; }
    public List<NamedDto>? Genres { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
}

internal sealed class TvDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? OriginalName { get; set; }
    public string? OriginalLanguage { get; set; }
    public string? FirstAirDate { get; set; }
    public string? Overview { get; set; }
    public string? Status { get; set; }
    public List<NamedDto>? Genres { get; set; }
    public List<NamedDto>? Networks { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public List<SeasonSummaryDto>? Seasons { get; set; }
    public ExternalIdsDto? ExternalIds { get; set; }
}

internal sealed class SeasonSummaryDto
{
    public int SeasonNumber { get; set; }
}

internal sealed class SeasonDto
{
    public List<EpisodeDto>? Episodes { get; set; }
}

internal sealed class EpisodeDto
{
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string? Name { get; set; }
    public string? AirDate { get; set; }
    public string? Overview { get; set; }
}

internal sealed class ExternalIdsDto
{
    public string? ImdbId { get; set; }
    public int? TvdbId { get; set; }
}

internal sealed class NamedDto
{
    public string? Name { get; set; }
}

