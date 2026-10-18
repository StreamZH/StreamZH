// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Titles;

/// <summary>
/// Alternative titles of movies and series: fetched from TMDB (alternative titles and translations) when an item is added
/// or refreshed, plus the titles you add yourself. Scene names (TheXEM) are written by the scene numbering, not here.
/// </summary>
public sealed class AlternativeTitleService(
    AlternativeTitleRepository repository,
    ILogger<AlternativeTitleService> logger,
    ITmdbTitlesClient? tmdb = null,
    ITmdbSettings? tmdbSettings = null)
{
    public IReadOnlyList<AlternativeTitle> For(MediaType mediaType, int itemId) => repository.For(mediaType, itemId);

    /// <summary>All alternative titles of the library, grouped for matching many names at once.</summary>
    public AlternativeTitleLookup Lookup() => new([.. repository.All(MediaType.Movie), .. repository.All(MediaType.Series)]);

    /// <summary>Fetches the movie's titles from TMDB. Never throws for TMDB problems: the old titles stay and a warning is logged.</summary>
    public Task<bool> RefreshMovieAsync(Movie movie, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(movie);
        return RefreshAsync(
            MediaType.Movie, movie.Id, movie.Title, movie.OriginalTitle, movie.OriginalLanguageId,
            c => tmdb!.GetMovieTitlesAsync(movie.TmdbId, c), cancellationToken);
    }

    /// <summary>Fetches the series' titles from TMDB. Never throws for TMDB problems: the old titles stay and a warning is logged.</summary>
    public Task<bool> RefreshSeriesAsync(Series series, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(series);
        return RefreshAsync(
            MediaType.Series, series.Id, series.Title, series.OriginalTitle, series.OriginalLanguageId,
            c => tmdb!.GetSeriesTitlesAsync(series.TmdbId, c), cancellationToken);
    }

    /// <summary>Adds your own title (for matching and searching). A refresh never removes it.</summary>
    /// <exception cref="ValidationFailedException">Empty, too long, a season for a movie, or the item already has this title.</exception>
    public AlternativeTitle AddUserTitle(MediaType mediaType, int itemId, string mainTitle, string originalTitle, string? title, int? seasonNumber)
    {
        var text = title?.Trim() ?? string.Empty;
        var clean = AlternativeTitleRules.CleanTitle(text);
        if (text.Length == 0 || clean.Length == 0)
        {
            throw Invalid("title", "Enter a title with letters or digits.");
        }

        if (text.Length > AlternativeTitleRules.MaxTitleLength)
        {
            throw Invalid("title", $"Use at most {AlternativeTitleRules.MaxTitleLength} characters.");
        }

        if (mediaType == MediaType.Movie && seasonNumber is not null)
        {
            throw Invalid("seasonNumber", "Movies have no seasons.");
        }

        if (seasonNumber is < 0 or > 10_000)
        {
            throw Invalid("seasonNumber", "Use a season number from 0 to 10000.");
        }

        var existing = repository.For(mediaType, itemId);
        var known = AlternativeTitleRules.CleanTitle(mainTitle) == clean || AlternativeTitleRules.CleanTitle(originalTitle) == clean
            || existing.Any(t => t.CleanTitle == clean && t.SeasonNumber == seasonNumber);
        if (known)
        {
            throw Invalid("title", "This title is already known.");
        }

        var added = new AlternativeTitle
        {
            MediaType = mediaType,
            ItemId = itemId,
            Title = text,
            CleanTitle = clean,
            Source = AlternativeTitleSources.User,
            SeasonNumber = seasonNumber,
        };
        var id = repository.Add(added);
        logger.LogInformation("Added the title '{Title}' to {Type} {Id}", text, mediaType, itemId);
        return added with { Id = id };
    }

    /// <summary>Removes one of your titles; titles from TMDB or TheXEM come back with the next refresh and cannot be removed.</summary>
    /// <exception cref="LibraryException">Not found for this item, or not one of yours.</exception>
    public void DeleteUserTitle(MediaType mediaType, int itemId, int titleId)
    {
        var title = repository.Find(titleId);
        if (title is null || title.MediaType != mediaType || title.ItemId != itemId)
        {
            throw new LibraryException("Title not found.", LibraryError.NotFound);
        }

        if (title.Source != AlternativeTitleSources.User)
        {
            throw new LibraryException("Only titles you added can be removed.", LibraryError.Conflict);
        }

        repository.Delete(titleId);
        logger.LogInformation("Removed the title '{Title}' from {Type} {Id}", title.Title, mediaType, itemId);
    }

    private static ValidationFailedException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>When the item leaves the library.</summary>
    public void DeleteForItem(MediaType mediaType, int itemId) => repository.DeleteForItem(mediaType, itemId);

    private async Task<bool> RefreshAsync(
        MediaType mediaType, int itemId, string title, string originalTitle, int originalLanguageId,
        Func<CancellationToken, Task<TmdbTitles>> fetch, CancellationToken cancellationToken)
    {
        if (tmdb is null || itemId <= 0)
        {
            return false;
        }

        TmdbTitles titles;
        try
        {
            titles = await fetch(cancellationToken);
        }
        catch (TmdbException ex)
        {
            logger.LogWarning("Alternative titles of '{Title}' not updated: {Message}", title, ex.Message);
            return false;
        }

        var existing = repository.For(mediaType, itemId);
        var keep = existing
            .Where(t => t.Source is not (AlternativeTitleSources.Tmdb or AlternativeTitleSources.Translation))
            .Select(t => t.Title);
        var originalLanguage = IsoCode(originalLanguageId);
        var (alternatives, translations) = AlternativeTitleRules.FromTmdb(
            mediaType, itemId, titles, title, originalTitle, originalLanguage, tmdbSettings?.Language, keep);

        Replace(AlternativeTitleSources.Translation, translations);
        Replace(AlternativeTitleSources.Tmdb, alternatives);
        logger.LogDebug(
            "Alternative titles of '{Title}': {Translations} translations, {Alternatives} TMDB titles", title, translations.Count, alternatives.Count);
        return true;

        // Unchanged titles keep their ids (an open "Also known as" panel stays valid).
        void Replace(string source, IReadOnlyList<AlternativeTitle> fresh)
        {
            static string Key(AlternativeTitle t) => $"{t.Title}\u0001{t.LanguageId}\u0001{t.SeasonNumber}\u0001{t.SceneSeasonNumber}";
            var old = existing.Where(t => t.Source == source).Select(Key).Order(StringComparer.Ordinal);
            if (!old.SequenceEqual(fresh.Select(Key).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                repository.ReplaceSource(mediaType, itemId, source, fresh);
            }
        }
    }

    /// <summary>The ISO 639-1 code of a language id ("de" for German), for picking the translation in the original language.</summary>
    private static string? IsoCode(int languageId) => languageId switch
    {
        <= 0 => null,
        _ when languageId == Core.Quality.Language.PortugueseBrazil.Id => "pt",
        _ when languageId == Core.Quality.Language.SpanishLatino.Id => "es",
        _ => IsoCodes.TryGetValue(languageId, out var code) ? code : null,
    };

    private static readonly Dictionary<int, string> IsoCodes = BuildIsoCodes();

    private static Dictionary<int, string> BuildIsoCodes()
    {
        var codes = new Dictionary<int, string>();
        foreach (var code in new[]
        {
            "en", "fr", "es", "de", "it", "da", "nl", "ja", "is", "zh", "ru", "pl", "vi", "sv", "no", "fi", "tr", "pt", "el", "ko", "hu",
            "he", "lt", "cs", "hi", "ro", "th", "bg", "ar", "uk", "fa", "bn", "sk", "lv", "ca", "hr", "sr", "bs", "et", "ta", "id", "te",
            "mk", "sl", "ml", "kn", "sq", "af", "mr", "tl", "ur", "rm", "mn", "ka",
        })
        {
            codes.TryAdd(Core.Quality.Language.FromIsoCode(code).Id, code);
        }

        return codes;
    }
}
