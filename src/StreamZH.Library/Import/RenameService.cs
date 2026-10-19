// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.CustomFormats;
using StreamZH.Data;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Import;

/// <summary>One file whose name differs from the naming scheme (paths relative to the movie or series folder).</summary>
public sealed record RenamePreview(IReadOnlyList<int> EpisodeIds, int? SeasonNumber, string ExistingPath, string NewPath);

/// <summary>
/// "Rename files" on a movie or series (Radarr's Organize, Sonarr's Rename): shows which files do not follow the naming
/// scheme and renames them, with their subtitles. Uses the same naming as the import; quality and release group come from
/// what the import stored, otherwise from the current file name.
/// </summary>
public sealed class RenameService(
    MediaManagementSettings settings,
    MovieRepository movies,
    SeriesRepository series,
    EpisodeFileRepository episodeFiles,
    MediaFileDetailsRepository fileDetails,
    HistoryRepository history,
    ImportFileSystem files,
    IEventBus events,
    ILogger<RenameService> logger,
    TimeProvider? timeProvider = null,
    CustomFormatRepository? customFormats = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public IReadOnlyList<RenamePreview> PreviewMovie(int movieId)
    {
        var movie = movies.Find(movieId) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound);
        var options = settings.Current;
        if (movie.File is not { } file || !options.RenameMovies || !IsVideo(file.RelativePath))
        {
            return [];
        }

        var full = Path.Combine(movie.FolderPath, file.RelativePath);
        var info = Info(full, movie.FolderPath).WithCustomFormats(
            Formats(MediaType.Movie), MediaType.Movie, Language.FindById(movie.OriginalLanguageId), movie.Year, Path.GetFileName(file.RelativePath), file.Size);
        var name = new FileNameBuilder(options).MovieFileName(movie, info);
        var target = name + Path.GetExtension(file.RelativePath).ToLowerInvariant();
        return name.Length == 0 || SamePath(file.RelativePath, target) ? [] : [new RenamePreview([], null, file.RelativePath, target)];
    }

    public IReadOnlyList<RenamePreview> PreviewSeries(int seriesId, int? seasonNumber = null)
    {
        var show = series.Find(seriesId) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
        var options = settings.Current;
        if (!options.RenameEpisodes)
        {
            return [];
        }

        var builder = new FileNameBuilder(options);
        var formats = Formats(MediaType.Series);
        var original = Language.FindById(show.OriginalLanguageId);
        var result = new List<RenamePreview>();
        var episodes = series.Episodes(seriesId).Where(e => e.File is not null && (seasonNumber is null || e.SeasonNumber == seasonNumber));
        foreach (var group in episodes.GroupBy(e => e.File!.RelativePath).OrderBy(g => g.Min(e => e.SeasonNumber)).ThenBy(g => g.Min(e => e.EpisodeNumber)))
        {
            var relative = group.Key;
            if (!IsVideo(relative))
            {
                continue;
            }

            var list = group.OrderBy(e => e.EpisodeNumber).ToList();
            var full = Path.Combine(show.FolderPath, relative);
            var daily = ReleaseParser.ParseEpisode(Path.GetFileName(relative))?.AirDate is not null;
            var info = Info(full, show.FolderPath).WithCustomFormats(
                formats, MediaType.Series, original, show.Year, Path.GetFileName(relative), list[0].File!.Size, CustomFormatNaming.EpisodeFileType(list.Count));
            var name = builder.EpisodeFileName(show, list, info, FileNameBuilder.FormatFor(show, daily));
            if (name.Length == 0)
            {
                continue;
            }

            var season = builder.SeasonFolderName(show, list[0].SeasonNumber);
            var target = Path.Combine(season ?? string.Empty, name + Path.GetExtension(relative).ToLowerInvariant());
            if (!SamePath(relative, target))
            {
                result.Add(new RenamePreview([.. list.Select(e => e.Id)], list[0].SeasonNumber, relative, target));
            }
        }

        return result;
    }

    /// <summary>Renames the movie file; returns the number of renamed files.</summary>
    public async Task<int> RenameMovieAsync(int movieId, CancellationToken cancellationToken = default)
    {
        var movie = movies.Find(movieId) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound);
        var preview = PreviewMovie(movieId);
        if (preview.Count == 0)
        {
            return 0;
        }

        var item = preview[0];
        var target = Move(movie.FolderPath, item);
        movie.File = new MediaFile(target, new FileInfo(Path.Combine(movie.FolderPath, target)).Length, File.GetLastWriteTimeUtc(Path.Combine(movie.FolderPath, target)));
        movies.Update(movie);
        AddHistory(MediaType.Movie, movie.Id, null, [], item);
        logger.LogInformation("Renamed {Old} to {New} in {Folder}", item.ExistingPath, target, movie.FolderPath);
        await PublishAsync(MediaType.Movie, movie.Id, null, movie.FolderPath, cancellationToken);
        return 1;
    }

    /// <summary>Renames the episode files of a series (one season, or all); returns the number of renamed files.</summary>
    public async Task<int> RenameSeriesAsync(int seriesId, int? seasonNumber = null, IReadOnlyCollection<string>? only = null, CancellationToken cancellationToken = default)
    {
        var show = series.Find(seriesId) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
        var preview = PreviewSeries(seriesId, seasonNumber).Where(p => only is null || only.Contains(p.ExistingPath)).ToList();
        var renamed = 0;
        foreach (var item in preview)
        {
            var target = Move(show.FolderPath, item);
            var full = Path.Combine(show.FolderPath, target);
            episodeFiles.SetFile(item.EpisodeIds, new MediaFile(target, new FileInfo(full).Length, File.GetLastWriteTimeUtc(full)));
            AddHistory(MediaType.Series, null, show.Id, item.EpisodeIds, item);
            renamed++;
        }

        if (renamed > 0)
        {
            if (settings.Current.DeleteEmptyFolders)
            {
                files.DeleteEmptyFolders(show.FolderPath, includeSelf: false);
            }

            logger.LogInformation("Renamed {Count} file(s) of '{Title}'", renamed, show.Title);
            await PublishAsync(MediaType.Series, null, show.Id, show.FolderPath, cancellationToken);
        }

        return renamed;
    }

    /// <summary>Moves the file (and its subtitles) within the item folder; returns the new relative path.</summary>
    private string Move(string itemFolder, RenamePreview item)
    {
        var options = settings.Current;
        var from = Path.Combine(itemFolder, item.ExistingPath);
        var to = Path.Combine(itemFolder, item.NewPath);
        if (!File.Exists(from))
        {
            throw new LibraryException($"{item.ExistingPath} no longer exists. Refresh the item first.", LibraryError.Conflict);
        }

        if (files.Problem(to) is { } dataProblem)
        {
            throw new LibraryException(dataProblem, LibraryError.Invalid);
        }

        files.CreateFolder(Path.GetDirectoryName(to)!, options);
        if (File.Exists(to) && !string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
        {
            throw new LibraryException($"{item.NewPath} already exists. Remove or rename it first.", LibraryError.Conflict);
        }

        var oldBase = Path.GetFileNameWithoutExtension(from);
        var newBase = Path.GetFileNameWithoutExtension(to);
        var siblings = Directory.EnumerateFiles(Path.GetDirectoryName(from)!)
            .Where(f => !string.Equals(f, from, StringComparison.Ordinal) && Path.GetFileName(f).StartsWith(oldBase + ".", StringComparison.OrdinalIgnoreCase))
            .Where(f => !DiskScanner.VideoExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        try
        {
            File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LibraryException(ImportFileSystem.Describe(ex, Path.GetDirectoryName(to)!), LibraryError.Conflict);
        }

        fileDetails.Move(from, to);
        foreach (var sibling in siblings)
        {
            var target = Path.Combine(Path.GetDirectoryName(to)!, newBase + Path.GetFileName(sibling)[oldBase.Length..]);
            try
            {
                if (!File.Exists(target))
                {
                    File.Move(sibling, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not rename {File}: {Message}", sibling, ex.Message);
            }
        }

        if (options.DeleteEmptyFolders && !string.Equals(Path.GetDirectoryName(from), itemFolder, StringComparison.Ordinal))
        {
            files.DeleteEmptyFolders(Path.GetDirectoryName(from)!, includeSelf: true);
        }

        return item.NewPath;
    }

    private IReadOnlyList<Core.CustomFormats.CustomFormat> Formats(MediaType mediaType) => customFormats?.ForMediaType(mediaType) ?? [];

    private NamingFileInfo Info(string fullPath, string itemFolder)
    {
        var originalName = Path.GetFileNameWithoutExtension(fullPath);
        if (fileDetails.Find(fullPath) is { } stored)
        {
            return NamingFileInfo.From(stored, originalName);
        }

        // Not imported by StreamZH: read what the current name (and the folder name) tells.
        var parsed = ReleaseParser.Parse(Path.GetFileName(fullPath));
        var (quality, languages) = Search.DecisionTargetFactory.Existing(new MediaFile(Path.GetRelativePath(itemFolder, fullPath), 0, DateTime.MinValue), itemFolder);
        return NamingFileInfo.From(parsed, null, originalName) with
        {
            Quality = quality ?? parsed?.Quality ?? Core.Quality.QualityModel.Unknown,
            Languages = languages.Count > 0 ? languages : parsed?.Languages ?? [],
        };
    }

    private void AddHistory(MediaType type, int? movieId, int? seriesId, IReadOnlyList<int> episodeIds, RenamePreview item) =>
        history.Add(new HistoryItem
        {
            Date = _time.GetUtcNow().UtcDateTime,
            EventType = HistoryEventType.Renamed,
            MediaType = type,
            MovieId = movieId,
            SeriesId = seriesId,
            EpisodeIds = episodeIds,
            SourceTitle = Path.GetFileName(item.ExistingPath),
            Data = new Dictionary<string, string?> { ["from"] = item.ExistingPath, ["to"] = item.NewPath },
        });

    private async Task PublishAsync(MediaType type, int? movieId, int? seriesId, string folder, CancellationToken cancellationToken)
    {
        await events.PublishAsync(new MediaRenamed(type, movieId, seriesId, folder), cancellationToken);
        await events.PublishAsync(new LibraryChanged(type, movieId ?? seriesId), cancellationToken);
    }

    private static bool IsVideo(string relativePath) => DiskScanner.VideoExtensions.Contains(Path.GetExtension(relativePath));

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.Ordinal);
}
