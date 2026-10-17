// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Metadata;

/// <summary>Counters of one run, for the log and the tests.</summary>
public sealed record MetadataWriteResult(int Nfos, int Images, int Moved, int Deleted)
{
    public static MetadataWriteResult None { get; } = new(0, 0, 0, 0);

    public static MetadataWriteResult operator +(MetadataWriteResult a, MetadataWriteResult b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return new(a.Nfos + b.Nfos, a.Images + b.Images, a.Moved + b.Moved, a.Deleted + b.Deleted);
    }
}

/// <summary>
/// The "Kodi (XBMC) / Emby" metadata consumer of Radarr and Sonarr: NFO files and artwork next to the media.
/// NFO files are rewritten on every run (titles, ratings and file names change); images are only downloaded when missing.
/// After a rename, StreamZH's own NFO and thumb files that no longer match a video are moved to the new name, or deleted
/// when the video is gone. NFO files from releases are kept as ".nfo-orig", like Radarr does.
/// </summary>
public sealed class KodiMetadataWriter(
    MovieRepository movies,
    SeriesRepository seriesRepository,
    MetadataConfig settings,
    ITmdbDetailsClient tmdb,
    MetadataImageStore images,
    ILogger<KodiMetadataWriter> logger)
{
    public const string MovieNfoName = "movie.nfo";
    public const string SeriesNfoName = "tvshow.nfo";

    public async Task<MetadataWriteResult> WriteMovieAsync(int movieId, CancellationToken cancellationToken)
    {
        var options = settings.Kodi;
        if (!options.Enabled || movies.Find(movieId) is not { } movie || !Directory.Exists(movie.FolderPath))
        {
            return MetadataWriteResult.None;
        }

        var videos = MovieVideos(movie);
        if (videos.Count == 0)
        {
            return MetadataWriteResult.None;
        }

        var folder = movie.FolderPath;
        var nfoPath = options.UseMovieNfo ? Path.Combine(folder, MovieNfoName) : Path.ChangeExtension(videos[0], ".nfo");
        var result = CleanUpMovie(folder, nfoPath, options);

        if (options.MovieMetadata || options.MovieMetadataUrl)
        {
            var details = options.MovieMetadata ? await TryAsync(() => tmdb.GetMovieDetailsAsync(movie.TmdbId, settings.CertificationCountry, cancellationToken), movie.Title) : null;
            var content = options.MovieMetadata
                ? KodiNfo.Movie(movie, details, settings.NfoOriginalTitle, options.MovieCollection, options.MovieMetadataUrl)
                : KodiNfo.MovieUrlOnly(movie);
            WriteNfo(nfoPath, content);
            result += new MetadataWriteResult(1, 0, 0, 0);
        }

        if (options.MovieImages)
        {
            result += await SaveImageAsync(movie.PosterPath, folder, "poster", cancellationToken);
            result += await SaveImageAsync(movie.BackdropPath, folder, "fanart", cancellationToken);
        }

        logger.LogDebug("Metadata for {Movie}: {Result}", movie.Title, result);
        return result;
    }

    public async Task<MetadataWriteResult> WriteSeriesAsync(int seriesId, CancellationToken cancellationToken)
    {
        var options = settings.Kodi;
        if (!options.Enabled || seriesRepository.Find(seriesId) is not { } series || !Directory.Exists(series.FolderPath))
        {
            return MetadataWriteResult.None;
        }

        var folder = series.FolderPath;
        var files = seriesRepository.Episodes(seriesId)
            .Where(e => e.File is not null)
            .GroupBy(e => Path.GetFullPath(Path.Combine(folder, e.File!.RelativePath)), StringComparer.Ordinal)
            .Where(g => File.Exists(g.Key))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Episode>)[.. g], StringComparer.Ordinal);
        var result = CleanUpEpisodes(folder, files);

        TmdbSeriesDetails? details = null;
        if (options.SeriesMetadata || options.SeasonImages)
        {
            details = await TryAsync(() => tmdb.GetSeriesDetailsAsync(series.TmdbId, settings.CertificationCountry, cancellationToken), series.Title);
        }

        if (options.SeriesMetadata || options.SeriesMetadataUrl)
        {
            var content = options.SeriesMetadata
                ? KodiNfo.Series(series, details, settings.NfoOriginalTitle, options.SeriesMetadataUrl)
                : KodiNfo.UrlMarker + "\n" + KodiNfo.SeriesUrl(series.TmdbId) + "\n" + (series.TvdbId is { } tvdb ? KodiNfo.TvdbUrl(tvdb) + "\n" : string.Empty);
            WriteNfo(Path.Combine(folder, SeriesNfoName), content);
            result += new MetadataWriteResult(1, 0, 0, 0);
        }

        if (options.SeriesImages)
        {
            result += await SaveImageAsync(series.PosterPath, folder, "poster", cancellationToken);
            result += await SaveImageAsync(series.BackdropPath, folder, "fanart", cancellationToken);
        }

        if (options.SeasonImages && details is not null)
        {
            foreach (var season in details.Seasons)
            {
                result += await SaveImageAsync(season.PosterPath, folder, SeasonPosterName(season.SeasonNumber), cancellationToken);
            }
        }

        if ((options.EpisodeMetadata || options.EpisodeImages) && files.Count > 0)
        {
            var episodeDetails = new Dictionary<(int Season, int Episode), TmdbEpisodeDetails>();
            foreach (var season in files.Values.SelectMany(e => e).Select(e => e.SeasonNumber).Distinct().Order())
            {
                var seasonDetails = await TryAsync(() => tmdb.GetSeasonDetailsAsync(series.TmdbId, season, cancellationToken), $"{series.Title} season {season}");
                foreach (var episode in seasonDetails?.Episodes ?? [])
                {
                    episodeDetails[(episode.SeasonNumber, episode.EpisodeNumber)] = episode;
                }
            }

            foreach (var (video, episodes) in files)
            {
                if (options.EpisodeMetadata)
                {
                    WriteNfo(Path.ChangeExtension(video, ".nfo"), KodiNfo.Episodes(series, episodes, episodeDetails));
                    result += new MetadataWriteResult(1, 0, 0, 0);
                }

                if (options.EpisodeImages)
                {
                    var first = episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).First();
                    var still = episodeDetails.GetValueOrDefault((first.SeasonNumber, first.EpisodeNumber))?.StillPath;
                    result += await SaveImageAsync(still, Path.GetDirectoryName(video)!, Path.GetFileNameWithoutExtension(video) + "-thumb", cancellationToken);
                }
            }
        }

        logger.LogDebug("Metadata for {Series}: {Result}", series.Title, result);
        return result;
    }

    /// <summary>Every movie and series of the library (Settings → Metadata → "Write metadata now").</summary>
    public async Task<MetadataWriteResult> WriteAllAsync(CancellationToken cancellationToken)
    {
        var result = MetadataWriteResult.None;
        if (!settings.Kodi.Enabled)
        {
            return result;
        }

        foreach (var movie in movies.All())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result += await WriteMovieAsync(movie.Id, cancellationToken);
        }

        foreach (var series in seriesRepository.All())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result += await WriteSeriesAsync(series.Id, cancellationToken);
        }

        logger.LogInformation(
            "Metadata written for the library: {Nfos} NFO files, {Images} images, {Moved} moved, {Deleted} removed",
            result.Nfos,
            result.Images,
            result.Moved,
            result.Deleted);
        return result;
    }

    /// <summary>"season01-poster", "season-specials-poster" for season 0 (Sonarr's names).</summary>
    public static string SeasonPosterName(int season) =>
        season == 0 ? "season-specials-poster" : $"season{season.ToString("00", CultureInfo.InvariantCulture)}-poster";

    /// <summary>The movie's video files, the main one (from the library, else the largest) first.</summary>
    private static List<string> MovieVideos(Movie movie)
    {
        var all = Directory.EnumerateFiles(movie.FolderPath)
            .Where(f => DiskScanner.VideoExtensions.Contains(Path.GetExtension(f)) && !IsSample(f))
            .OrderByDescending(f => new FileInfo(f).Length)
            .ToList();
        if (movie.File is { } file && Path.GetFullPath(Path.Combine(movie.FolderPath, file.RelativePath)) is var main && File.Exists(main))
        {
            all.RemoveAll(f => string.Equals(f, main, StringComparison.Ordinal));
            all.Insert(0, main);
        }

        return all;
    }

    private static bool IsSample(string file) => Path.GetFileNameWithoutExtension(file).EndsWith("sample", StringComparison.OrdinalIgnoreCase);

    /// <summary>Our NFO files next to a video that no longer exists: moved to the new NFO name (first one) or deleted.</summary>
    private static MetadataWriteResult CleanUpMovie(string folder, string nfoPath, KodiMetadataOptions options)
    {
        var moved = 0;
        var deleted = 0;
        foreach (var nfo in Directory.EnumerateFiles(folder, "*.nfo"))
        {
            if (string.Equals(nfo, nfoPath, StringComparison.Ordinal) || !IsOurFile(nfo))
            {
                continue;
            }

            var isMovieNfo = string.Equals(Path.GetFileName(nfo), MovieNfoName, StringComparison.OrdinalIgnoreCase);
            if (!isMovieNfo && HasVideo(nfo) && !options.UseMovieNfo)
            {
                continue;
            }

            if (!File.Exists(nfoPath))
            {
                File.Move(nfo, nfoPath);
                moved++;
            }
            else
            {
                File.Delete(nfo);
                deleted++;
            }
        }

        return new MetadataWriteResult(0, 0, moved, deleted);
    }

    /// <summary>
    /// Our episode NFO files (and their thumbs) whose video was renamed or removed: moved next to the episode's current
    /// video when it has none yet, otherwise deleted.
    /// </summary>
    private static MetadataWriteResult CleanUpEpisodes(string folder, IReadOnlyDictionary<string, IReadOnlyList<Episode>> files)
    {
        var byEpisode = new Dictionary<(int, int), string>();
        foreach (var (video, episodes) in files)
        {
            foreach (var episode in episodes)
            {
                byEpisode.TryAdd((episode.SeasonNumber, episode.EpisodeNumber), video);
            }
        }

        var moved = 0;
        var deleted = 0;
        foreach (var nfo in Directory.EnumerateFiles(folder, "*.nfo", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(nfo), SeriesNfoName, StringComparison.OrdinalIgnoreCase) || HasVideo(nfo) || !IsOurFile(nfo, out var content))
            {
                continue;
            }

            var thumbs = Thumbs(nfo);
            var numbers = KodiNfo.ReadEpisodeNumbers(content);
            var target = numbers.Select(n => byEpisode.GetValueOrDefault(n)).FirstOrDefault(v => v is not null);
            var targetNfo = target is null ? null : Path.ChangeExtension(target, ".nfo");
            if (targetNfo is not null && !File.Exists(targetNfo))
            {
                File.Move(nfo, targetNfo);
                foreach (var thumb in thumbs)
                {
                    var newThumb = Path.Combine(Path.GetDirectoryName(target!)!, Path.GetFileNameWithoutExtension(target!) + "-thumb" + Path.GetExtension(thumb));
                    if (!File.Exists(newThumb))
                    {
                        File.Move(thumb, newThumb);
                    }
                    else
                    {
                        File.Delete(thumb);
                    }
                }

                moved++;
            }
            else
            {
                File.Delete(nfo);
                foreach (var thumb in thumbs)
                {
                    File.Delete(thumb);
                }

                deleted++;
            }
        }

        return new MetadataWriteResult(0, 0, moved, deleted);
    }

    /// <summary>A video with the same name as the NFO is on disk (in the library or not).</summary>
    private static bool HasVideo(string nfo)
    {
        var directory = Path.GetDirectoryName(nfo)!;
        var stem = Path.GetFileNameWithoutExtension(nfo);
        return Directory.EnumerateFiles(directory, stem + ".*")
            .Any(f => DiskScanner.VideoExtensions.Contains(Path.GetExtension(f)) && string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.Ordinal));
    }

    private static List<string> Thumbs(string nfo)
    {
        var directory = Path.GetDirectoryName(nfo)!;
        var stem = Path.GetFileNameWithoutExtension(nfo) + "-thumb";
        return [.. new[] { ".jpg", ".png" }.Select(ext => Path.Combine(directory, stem + ext)).Where(File.Exists)];
    }

    private static bool IsOurFile(string path) => IsOurFile(path, out _);

    private static bool IsOurFile(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return KodiNfo.IsOurs(content);
        }
        catch (IOException)
        {
            content = string.Empty;
            return false;
        }
    }

    /// <summary>Writes the NFO; an NFO that came with the release (not ours) is kept as ".nfo-orig".</summary>
    private void WriteNfo(string path, string content)
    {
        if (File.Exists(path) && !IsOurFile(path))
        {
            var original = Path.ChangeExtension(path, ".nfo-orig");
            File.Move(path, original, overwrite: true);
            logger.LogInformation("Kept the release NFO as {File}", original);
        }

        var temp = $"{path}.{Guid.NewGuid():N}.partial";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Saves "name.jpg" (extension of the TMDB file) in the folder when missing.</summary>
    private async Task<MetadataWriteResult> SaveImageAsync(string? tmdbPath, string folder, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tmdbPath) || !TmdbImages.IsValidPath(tmdbPath))
        {
            return MetadataWriteResult.None;
        }

        var target = Path.Combine(folder, name + Path.GetExtension(tmdbPath).ToLowerInvariant());
        try
        {
            return await images.SaveAsync(tmdbPath, target, cancellationToken) ? new MetadataWriteResult(0, 1, 0, 0) : MetadataWriteResult.None;
        }
        catch (TmdbException ex)
        {
            logger.LogWarning("Could not save {Image}: {Error}", target, ex.Message);
            return MetadataWriteResult.None;
        }
    }

    /// <summary>TMDB data, or null when TMDB is not set up or not reachable (the NFO then uses the library data).</summary>
    private async Task<T?> TryAsync<T>(Func<Task<T>> load, string what)
        where T : class
    {
        try
        {
            return await load();
        }
        catch (TmdbException ex)
        {
            logger.LogDebug("No TMDB details for {What}: {Error}", what, ex.Message);
            return null;
        }
    }
}
