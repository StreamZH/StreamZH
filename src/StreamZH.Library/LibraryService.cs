// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library;

public sealed record LibraryChanged(MediaType MediaType, int? Id) : IAppEvent;

public sealed class LibraryException(string message, LibraryError error) : Exception(message)
{
    public LibraryError Error { get; } = error;
}

public enum LibraryError
{
    NotFound,
    AlreadyExists,
    Invalid,

    /// <summary>Still referenced by other items (e.g. a profile used by movies).</summary>
    InUse,

    /// <summary>The change conflicts with the current state (e.g. deleting the last profile).</summary>
    Conflict,
}

/// <summary>Adds, refreshes and removes movies and series. Never deletes files on disk.</summary>
public sealed class LibraryService(
    ITmdbClient tmdb,
    RootFolderRepository rootFolders,
    MovieRepository movies,
    SeriesRepository series,
    QualityProfileService profiles,
    IEventBus events,
    ILogger<LibraryService> logger)
{
    public async Task<Movie> AddMovieAsync(
        int tmdbId, int rootFolderId, bool monitored, int? qualityProfileId = null, string? existingFolder = null, CancellationToken cancellationToken = default)
    {
        if (movies.FindByTmdbId(tmdbId) is not null)
        {
            throw new LibraryException("This movie is already in the library.", LibraryError.AlreadyExists);
        }

        var root = RootFolderFor(rootFolderId, MediaType.Movie);
        var profileId = profiles.Resolve(qualityProfileId);
        var details = await tmdb.GetMovieAsync(tmdbId, cancellationToken);

        var movie = new Movie
        {
            TmdbId = details.TmdbId,
            Monitored = monitored,
            QualityProfileId = profileId,
            RootFolderId = root.Id,
            FolderPath = existingFolder ?? Path.Combine(root.Path, FolderNames.For(details.Title, details.Year)),
            Added = DateTime.UtcNow,
        };
        Apply(movie, details);
        movie.File = DiskScanner.ScanMovie(movie.FolderPath);

        movies.Add(movie);
        logger.LogInformation("Added movie '{Title} ({Year})' {File}", movie.Title, movie.Year, movie.File is null ? "without file" : "with file");
        await events.PublishAsync(new LibraryChanged(MediaType.Movie, movie.Id), cancellationToken);
        return movie;
    }

    public async Task<Series> AddSeriesAsync(
        int tmdbId, int rootFolderId, bool monitored, int? qualityProfileId = null, string? existingFolder = null, CancellationToken cancellationToken = default)
    {
        if (series.TmdbIdToId().ContainsKey(tmdbId))
        {
            throw new LibraryException("This series is already in the library.", LibraryError.AlreadyExists);
        }

        var root = RootFolderFor(rootFolderId, MediaType.Series);
        var profileId = profiles.Resolve(qualityProfileId);
        var details = await tmdb.GetSeriesAsync(tmdbId, cancellationToken);

        var item = new Series
        {
            TmdbId = details.TmdbId,
            Monitored = monitored,
            QualityProfileId = profileId,
            RootFolderId = root.Id,
            FolderPath = existingFolder ?? Path.Combine(root.Path, FolderNames.For(details.Title, details.Year)),
            Added = DateTime.UtcNow,
        };
        Apply(item, details);

        series.Add(item);
        logger.LogInformation("Added series '{Title} ({Year})'", item.Title, item.Year);
        await events.PublishAsync(new LibraryChanged(MediaType.Series, item.Id), cancellationToken);
        return item;
    }

    /// <summary>Fetches fresh metadata and rescans the folder.</summary>
    public async Task RefreshMovieAsync(int id, CancellationToken cancellationToken = default)
    {
        var movie = movies.Find(id) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound);
        Apply(movie, await tmdb.GetMovieAsync(movie.TmdbId, cancellationToken));
        movie.File = DiskScanner.ScanMovie(movie.FolderPath);
        movie.LastRefreshed = DateTime.UtcNow;
        movies.Update(movie);
        await events.PublishAsync(new LibraryChanged(MediaType.Movie, id), cancellationToken);
    }

    /// <summary>Fetches show details and every season from TMDB, then rescans the folder.</summary>
    public async Task RefreshSeriesAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = series.Find(id) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
        var details = await tmdb.GetSeriesAsync(item.TmdbId, cancellationToken);
        Apply(item, details);

        var episodes = new List<Episode>();
        foreach (var seasonNumber in details.SeasonNumbers)
        {
            foreach (var e in await tmdb.GetSeasonAsync(item.TmdbId, seasonNumber, cancellationToken))
            {
                episodes.Add(new Episode
                {
                    SeasonNumber = e.SeasonNumber,
                    EpisodeNumber = e.EpisodeNumber,
                    Title = e.Title,
                    AirDate = e.AirDate,
                    Overview = e.Overview,
                });
            }
        }

        series.UpsertEpisodes(id, episodes, monitorNew: item.Monitored);
        series.SetEpisodeFiles(id, DiskScanner.ScanSeries(item.FolderPath));
        item.LastRefreshed = DateTime.UtcNow;
        series.Update(item);

        logger.LogInformation("Refreshed series '{Title}': {Count} episodes", item.Title, episodes.Count);
        await events.PublishAsync(new LibraryChanged(MediaType.Series, id), cancellationToken);
    }

    /// <summary>Rescans files only, no metadata requests.</summary>
    public async Task RescanAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var movie in movies.All())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = DiskScanner.ScanMovie(movie.FolderPath);
            if (file != movie.File)
            {
                movie.File = file;
                movies.Update(movie);
            }
        }

        foreach (var item in series.All())
        {
            cancellationToken.ThrowIfCancellationRequested();
            series.SetEpisodeFiles(item.Id, DiskScanner.ScanSeries(item.FolderPath));
        }

        await events.PublishAsync(new LibraryChanged(MediaType.Movie, null), cancellationToken);
        await events.PublishAsync(new LibraryChanged(MediaType.Series, null), cancellationToken);
    }

    public async Task SetMovieMonitoredAsync(int id, bool monitored)
    {
        var movie = movies.Find(id) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound);
        movie.Monitored = monitored;
        movies.Update(movie);
        await events.PublishAsync(new LibraryChanged(MediaType.Movie, id));
    }

    public async Task SetSeriesMonitoredAsync(int id, bool monitored)
    {
        var item = series.Find(id) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
        item.Monitored = monitored;
        series.Update(item);
        await events.PublishAsync(new LibraryChanged(MediaType.Series, id));
    }

    public async Task SetMovieQualityProfileAsync(int id, int qualityProfileId)
    {
        var movie = movies.Find(id) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound);
        movie.QualityProfileId = profiles.Resolve(qualityProfileId);
        movies.Update(movie);
        await events.PublishAsync(new LibraryChanged(MediaType.Movie, id));
    }

    public async Task SetSeriesQualityProfileAsync(int id, int qualityProfileId)
    {
        var item = series.Find(id) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
        item.QualityProfileId = profiles.Resolve(qualityProfileId);
        series.Update(item);
        await events.PublishAsync(new LibraryChanged(MediaType.Series, id));
    }

    public async Task SetSeasonMonitoredAsync(int id, int seasonNumber, bool monitored)
    {
        _ = series.Find(id) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
        series.SetSeasonMonitored(id, seasonNumber, monitored);
        await events.PublishAsync(new LibraryChanged(MediaType.Series, id));
    }

    public async Task DeleteMovieAsync(int id)
    {
        var movie = movies.Find(id) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound);
        movies.Delete(id);
        logger.LogInformation("Removed movie '{Title}' from the library (files kept)", movie.Title);
        await events.PublishAsync(new LibraryChanged(MediaType.Movie, id));
    }

    public async Task DeleteSeriesAsync(int id)
    {
        var item = series.Find(id) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
        series.Delete(id);
        logger.LogInformation("Removed series '{Title}' from the library (files kept)", item.Title);
        await events.PublishAsync(new LibraryChanged(MediaType.Series, id));
    }

    private RootFolder RootFolderFor(int id, MediaType mediaType)
    {
        var root = rootFolders.Find(id) ?? throw new LibraryException("Root folder not found.", LibraryError.Invalid);
        if (root.MediaType != mediaType)
        {
            throw new LibraryException($"This root folder is for {root.MediaType}, not {mediaType}.", LibraryError.Invalid);
        }

        return root;
    }

    private static void Apply(Movie movie, TmdbMovie d)
    {
        movie.ImdbId = d.ImdbId;
        movie.Title = d.Title;
        movie.OriginalTitle = d.OriginalTitle;
        movie.SortTitle = SortTitles.For(d.Title);
        movie.Year = d.Year;
        movie.Overview = d.Overview;
        movie.Runtime = d.Runtime;
        movie.Status = d.Status;
        movie.Genres = d.Genres;
        movie.PosterPath = d.PosterPath;
        movie.BackdropPath = d.BackdropPath;
        movie.OriginalLanguageId = StreamZH.Core.Quality.Language.FromIsoCode(d.OriginalLanguage).Id;
    }

    private static void Apply(Series item, TmdbSeries d)
    {
        item.TvdbId = d.TvdbId;
        item.ImdbId = d.ImdbId;
        item.Title = d.Title;
        item.OriginalTitle = d.OriginalTitle;
        item.SortTitle = SortTitles.For(d.Title);
        item.Year = d.Year;
        item.Overview = d.Overview;
        item.Status = d.Status;
        item.Network = d.Network;
        item.Genres = d.Genres;
        item.PosterPath = d.PosterPath;
        item.BackdropPath = d.BackdropPath;
        item.OriginalLanguageId = StreamZH.Core.Quality.Language.FromIsoCode(d.OriginalLanguage).Id;
    }
}