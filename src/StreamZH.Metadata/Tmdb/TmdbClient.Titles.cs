// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace StreamZH.Metadata.Tmdb;

/// <summary>
/// The other names of a movie or series on TMDB: alternative titles (per country) and translations (per language),
/// used to match release names like "Der.Teufel.traegt.Prada" and as extra search terms.
/// </summary>
public interface ITmdbTitlesClient
{
    Task<TmdbTitles> GetMovieTitlesAsync(int tmdbId, CancellationToken cancellationToken = default);

    Task<TmdbTitles> GetSeriesTitlesAsync(int tmdbId, CancellationToken cancellationToken = default);
}

/// <param name="Country">ISO 3166-1 code, e.g. "DE".</param>
/// <param name="Type">TMDB's note, e.g. "working title"; null when empty.</param>
public sealed record TmdbAlternativeTitle(string Title, string Country, string? Type);

/// <param name="Language">ISO 639-1 code, e.g. "de".</param>
/// <param name="Country">ISO 3166-1 code, e.g. "CH".</param>
public sealed record TmdbTranslation(string Language, string Country, string Title);

public sealed record TmdbTitles(IReadOnlyList<TmdbAlternativeTitle> AlternativeTitles, IReadOnlyList<TmdbTranslation> Translations)
{
    public static readonly TmdbTitles None = new([], []);
}

public sealed partial class TmdbClient : ITmdbTitlesClient
{
    public async Task<TmdbTitles> GetMovieTitlesAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        var alternative = await GetAsync<AlternativeTitlesDto>($"movie/{tmdbId}/alternative_titles", null, cancellationToken);
        var translations = await GetAsync<TranslationsDto>($"movie/{tmdbId}/translations", null, cancellationToken);
        return new TmdbTitles(Alternatives(alternative.Titles), Translations(translations, d => d.Title));
    }

    public async Task<TmdbTitles> GetSeriesTitlesAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        // TV answers with "results" instead of "titles", and translations carry "name" instead of "title".
        var alternative = await GetAsync<AlternativeTitlesDto>($"tv/{tmdbId}/alternative_titles", null, cancellationToken);
        var translations = await GetAsync<TranslationsDto>($"tv/{tmdbId}/translations", null, cancellationToken);
        return new TmdbTitles(Alternatives(alternative.Results), Translations(translations, d => d.Name));
    }

    private static List<TmdbAlternativeTitle> Alternatives(List<AlternativeTitleDto>? titles) =>
        [.. (titles ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .Select(t => new TmdbAlternativeTitle(t.Title!.Trim(), (t.Iso31661 ?? string.Empty).Trim().ToUpperInvariant(), Blank(t.Type)))];

    private static List<TmdbTranslation> Translations(TranslationsDto dto, Func<TranslationDataDto, string?> title) =>
        [.. (dto.Translations ?? [])
            .Where(t => t.Data is not null && !string.IsNullOrWhiteSpace(title(t.Data)) && !string.IsNullOrWhiteSpace(t.Iso6391))
            .Select(t => new TmdbTranslation(
                t.Iso6391!.Trim().ToLowerInvariant(), (t.Iso31661 ?? string.Empty).Trim().ToUpperInvariant(), title(t.Data!)!.Trim()))];
}

// Wire formats (snake_case JSON).
internal sealed class AlternativeTitlesDto
{
    /// <summary>Movies.</summary>
    public List<AlternativeTitleDto>? Titles { get; set; }

    /// <summary>TV.</summary>
    public List<AlternativeTitleDto>? Results { get; set; }
}

internal sealed class AlternativeTitleDto
{
    [JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; set; }

    public string? Title { get; set; }

    public string? Type { get; set; }
}

internal sealed class TranslationsDto
{
    public List<TranslationDto>? Translations { get; set; }
}

internal sealed class TranslationDto
{
    [JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; set; }

    [JsonPropertyName("iso_639_1")]
    public string? Iso6391 { get; set; }

    public TranslationDataDto? Data { get; set; }
}

internal sealed class TranslationDataDto
{
    public string? Title { get; set; }

    public string? Name { get; set; }
}
