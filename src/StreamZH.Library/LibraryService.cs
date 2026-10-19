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

    /// <summary>0.12: the folder is outside the owner's storage locations (the web app offers the owner to add it).</summary>
    OutsideStorage,
}

/// <summary>Adds, refreshes and removes movies and series. Never deletes files on disk.</summary>
public sealed class LibraryService(
    ITmdbClient tmdb,
    RootFolderRepository rootFolders,
    MovieRepository movies,
    SeriesRepository series,
    QualityProfileService profiles,
    IEventBus events,
    ILogger<LibraryService> logger,
    Import.MediaManagementSettings? mediaManagement = null,
    SceneNumbering.XemService? xem = null,
    Titles.AlternativeTitleService? alternativeTitles = null,
    StreamZH.Core.AppPaths? paths = null)
{
    /// <summary>
    /// 0.12 (SV3 W-01): an existing folder given from outside (the migration reads it from the old app) must be inside the
    /// storage locations like every other folder, so a later "delete with files" can never reach anything else.
    /// </summary>
    private void CheckExistingFolder(string? folder)
    {
        if (folder is null || paths is null)
        {
            return;
        }

        if (paths.StorageProblem(folder) is { } outside)
        {
            throw new LibraryException(outside, LibraryError.OutsideStorage);
        }

        if (paths.FolderProblem(folder) is { } problem)
        {
            throw new LibraryException(problem, LibraryError.Invalid);
        }
    }

    private Import.MediaManagementValues Media => mediaManagement?.Current ?? new Import.MediaManagementValues();

    public async Task<Movie> AddMovieAsync(
        int tmdbId, int rootFolderId, bool monitored, int? qualityProfileId = null, string? existingFolder = null, CancellationToken cancellationToken = default)
    {
        if (movies.FindByTmdbId(tmdbId) is not null)
        {
            throw new LibraryException("This movie is already in the library.", LibraryError.AlreadyExists);
        }

        CheckExistingFolder(existingFolder);
        var root = RootFolderFor(rootFolderId, MediaType.Movie);
        var profileId = profiles.Resolve(qualityProfileId);
        var details = await tmdb.GetMovieAsync(tmdbId, cancellationToken);

        var movie = new Movie
        {
            TmdbId = details.TmdbId,
            Monitored = monitored,
            QualityProfileId = profileId,
            RootFolderId = root.Id,
            Added = DateTime.UtcNow,
        };
        Apply(movie, details);
        movie.FolderPath = existingFolder ?? Path.Combine(root.Path, FolderName(new Import.FileNameBuilder(Media).MovieFolderName(movie), details.Title, details.Year));
        movie.File = DiskScanner.ScanMovie(movie.FolderPath);
        CreateFolderIfWanted(movie.FolderPath);

        movies.Add(movie);
        logger.LogInformation("Added movie '{Title} ({Year})' {File}", movie.Title, movie.Year, movie.File is null ? "without file" : "with file");
        if (alternativeTitles is not null)
        {
            // Ids can be reused after a removal: titles left from an earlier item must not stick to this one.
            alternativeTitles.DeleteForItem(MediaType.Movie, movie.Id);
            await alternativeTitles.RefreshMovieAsync(movie, cancellationToken);
        }

        await events.PublishAsync(new LibraryChanged(MediaType.Movie, movie.Id), cancellationToken);
        return movie;
    }

    /// <param name="seriesType">Standard, Daily or Anime; null = guessed from TMDB (Japanese animation, talk shows and news).</param>
    public async Task<Series> AddSeriesAsync(
        int tmdbId,
        int rootFolderId,
        bool monitored,
        int? qualityProfileId = null,
        string? existingFolder = null,
        SeriesType? seriesType = null,
        CancellationToken cancellationToken = default)
    {
        if (series.TmdbIdToId().ContainsKey(tmdbId))
        {
            throw new LibraryException("This series is already in the library.", LibraryError.AlreadyExists);
        }

        CheckExistingFolder(existingFolder);
        var root = RootFolderFor(rootFolderId, MediaType.Series);
        var profileId = profiles.Resolve(qualityProfileId);
        var details = await tmdb.GetSeriesAsync(tmdbId, cancellationToken);

        var item = new Series
        {
            TmdbId = details.TmdbId,
            Monitored = monitored,
            QualityProfileId = profileId,
            RootFolderId = root.Id,
            Added = DateTime.UtcNow,
            SeriesType = seriesType ?? SeriesTypes.Suggest(details.GenreIds, details.Genres, details.OriginCountries, details.OriginalLanguage),
        };
        Apply(item, details);
        item.FolderPath = existingFolder ?? Path.Combine(root.Path, FolderName(new Import.FileNameBuilder(Media).SeriesFolderName(item), details.Title, details.Year));
        CreateFolderIfWanted(item.FolderPath);

        series.Add(item);
        logger.LogInformation("Added series '{Title} ({Year})'", item.Title, item.Year);
        if (alternativeTitles is not null)
        {
            alternativeTitles.DeleteForItem(MediaType.Series, item.Id);
            await alternativeTitles.RefreshSeriesAsync(item, cancellationToken);
        }

        await events.PublishAsync(new LibraryChanged(MediaType.Series, item.Id), cancellationToken);
        return item;
    }

    /// <summary>Fetches fresh metadata and rescans the folder (a refresh the user asked for).</summary>
    public Task RefreshMovieAsync(int id, CancellationToken cancellationToken = default) => RefreshMovieAsync(id, manual: true, cancellationToken);

    /// <summary>
    /// Fetches fresh metadata; the folder is rescanned as "Rescan folder after refresh" says (always, only after a manual
    /// refresh, never).
    /// </summary>
    public async Task RefreshMovieAsync(int id, bool manual, CancellationToken cancellationToken)
    {
        var movie = movies.Find(id) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound);
        Apply(movie, await tmdb.GetMovieAsync(movie.TmdbId, cancellationToken));
        if (ShouldRescan(manual))
        {
            ApplyScan(movie, DiskScanner.ScanMovie(movie.FolderPath));
        }

        movie.LastRefreshed = DateTime.UtcNow;
        movies.Update(movie);
        if (alternativeTitles is not null)
        {
            await alternativeTitles.RefreshMovieAsync(movie, cancellationToken);
        }

        await events.PublishAsync(new LibraryChanged(MediaType.Movie, id), cancellationToken);
    }

    /// <summary>Fetches show details and every season from TMDB, then rescans the folder (a refresh the user asked for).</summary>
    public Task RefreshSeriesAsync(int id, CancellationToken cancellationToken = default) => RefreshSeriesAsync(id, manual: true, cancellationToken);

    /// <summary>Fetches show details and every season; the folder is rescanned as "Rescan folder after refresh" says.</summary>
    public async Task RefreshSeriesAsync(int id, bool manual, CancellationToken cancellationToken)
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

        EpisodeNumbering.AssignAbsoluteNumbers(episodes);
        series.UpsertEpisodes(id, episodes, monitorNew: item.Monitored);

        // TheXEM scene numbering and scene names (series with a TVDB id from TMDB's external ids); never fails the refresh.
        if (xem is not null)
        {
            await xem.UpdateSeriesAsync(item, cancellationToken);
        }

        if (ShouldRescan(manual))
        {
            ApplyScan(item);
        }

        item.LastRefreshed = DateTime.UtcNow;
        series.Update(item);
        if (alternativeTitles is not null)
        {
            await alternativeTitles.RefreshSeriesAsync(item, cancellationToken);
        }


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
                ApplyScan(movie, file);
                movies.Update(movie);
            }
            else if (file is null)
            {
                DeleteFolderIfEmpty(movie.FolderPath);
            }
        }

        foreach (var item in series.All())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyScan(item);
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

    public async Task SetSeriesTypeAsync(int id, SeriesType seriesType)
    {
        _ = series.Find(id) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
        series.SetSeriesType(id, seriesType);
        await events.PublishAsync(new LibraryChanged(MediaType.Series, id));
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
        alternativeTitles?.DeleteForItem(MediaType.Movie, id);
        logger.LogInformation("Removed movie '{Title}' from the library (files kept)", movie.Title);
        await events.PublishAsync(new LibraryChanged(MediaType.Movie, id));
    }

    public async Task DeleteSeriesAsync(int id)
    {
        var item = series.Find(id) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
        series.Delete(id);
        alternativeTitles?.DeleteForItem(MediaType.Series, id);
        logger.LogInformation("Removed series '{Title}' from the library (files kept)", item.Title);
        await events.PublishAsync(new LibraryChanged(MediaType.Series, id));
    }

    private bool ShouldRescan(bool manual) => Media.RescanAfterRefresh switch
    {
        Import.RescanAfterRefresh.Never => false,
        Import.RescanAfterRefresh.AfterManualRefresh => manual,
        _ => true,
    };

    /// <summary>Takes the scan result; a file that disappeared unmonitors the movie when "Unmonitor deleted files" is on.</summary>
    private void ApplyScan(Movie movie, MediaFile? file)
    {
        if (movie.File is not null && file is null && Media.UnmonitorDeletedFiles && movie.Monitored)
        {
            movie.Monitored = false;
            logger.LogInformation("The file of '{Title}' is gone; the movie is no longer monitored", movie.Title);
        }

        movie.File = file;
        if (file is null)
        {
            DeleteFolderIfEmpty(movie.FolderPath);
        }
    }

    /// <summary>Scans the series folder; episodes whose file disappeared are unmonitored when "Unmonitor deleted files" is on.</summary>
    private void ApplyScan(Series item)
    {
        var found = DiskScanner.ScanSeries(item.FolderPath, item, series.Episodes(item.Id));
        if (Media.UnmonitorDeletedFiles)
        {
            foreach (var gone in series.Episodes(item.Id).Where(e => e.File is not null && e.Monitored && !found.ContainsKey((e.SeasonNumber, e.EpisodeNumber))))
            {
                series.SetEpisodeMonitored(gone.Id, false);
                logger.LogInformation("The file of {Title} S{Season:00}E{Episode:00} is gone; the episode is no longer monitored", item.Title, gone.SeasonNumber, gone.EpisodeNumber);
            }
        }

        series.SetEpisodeFiles(item.Id, found);
        if (found.Count == 0)
        {
            DeleteFolderIfEmpty(item.FolderPath);
        }
    }

    /// <summary>"Create empty movie/series folders": the folder is created when the item is added.</summary>
    private void CreateFolderIfWanted(string folder)
    {
        if (!Media.CreateEmptyFolders)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not create the folder {Folder}: {Message}", folder, ex.Message);
        }
    }

    /// <summary>"Delete empty folders": a movie or series folder without any file is removed during the disk scan.</summary>
    private void DeleteFolderIfEmpty(string folder)
    {
        if (!Media.DeleteEmptyFolders || Media.CreateEmptyFolders || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
            {
                Directory.Delete(folder, recursive: true);
                logger.LogInformation("Deleted the empty folder {Folder}", folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("Could not delete the empty folder {Folder}: {Message}", folder, ex.Message);
        }
    }

    /// <summary>The name from the folder format, or "Title (Year)" when the format gives nothing.</summary>
    private static string FolderName(string fromFormat, string title, int year) =>
        fromFormat.Length > 0 ? fromFormat : FolderNames.For(title, year);

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
        movie.InCinemas = d.ReleaseDate ?? movie.InCinemas;
        movie.DigitalRelease = d.DigitalRelease ?? movie.DigitalRelease;
        movie.PhysicalRelease = d.PhysicalRelease ?? movie.PhysicalRelease;
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