// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Decisions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.CustomFormats;
using StreamZH.Library.Downloads;
using StreamZH.Library.MediaFiles;
using StreamZH.Library.Search;
using StreamZH.Metadata.Tmdb;
using StreamZH.Parsing;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Import;

/// <summary>A manual import: the user picked the movie or series (and the episodes per file).</summary>
/// <param name="Files">Paths relative to the download folder; empty = every video file (movie: the largest).</param>
public sealed record ManualImportRequest(MediaType MediaType, int? MovieId, int? SeriesId, IReadOnlyList<ManualImportFile> Files);

/// <param name="EpisodeIds">Series only: the episodes in this file.</param>
public sealed record ManualImportFile(string Path, IReadOnlyList<int> EpisodeIds);

/// <summary>A video file in a download folder as the manual import dialog shows it.</summary>
public sealed record ImportFileCandidate(
    string RelativePath,
    long Size,
    string Quality,
    IReadOnlyList<string> Languages,
    int? SeasonNumber,
    IReadOnlyList<int> EpisodeNumbers,
    IReadOnlyList<int> EpisodeIds,
    string? Problem);

/// <summary>One video file of an import: where it went, or why not.</summary>
public sealed record ImportedFile(string Source, string? Destination, IReadOnlyList<int> EpisodeIds, string? Rejection, bool IsUpgrade);

/// <summary>
/// Completed download handling and manual import (Radarr/Sonarr's ImportApprovedMovie/ImportApprovedEpisodes): finds the
/// video files of a finished download, matches them to the movie or to episodes, checks them (sample, upgrade, free space,
/// episode title), moves, copies or hard-links them into the library under the configured names, imports subtitles,
/// updates the library and the history, replaces older files (recycle bin) and cleans up. A failed import never touches
/// the downloaded files.
/// </summary>
public sealed class DownloadImportService(
    MediaManagementSettings settings,
    DownloadSettings downloads,
    MovieRepository movies,
    SeriesRepository series,
    EpisodeFileRepository episodeFiles,
    MediaFileDetailsRepository fileDetails,
    DownloadImportRepository imports,
    HistoryRepository history,
    QualityProfileRepository profiles,
    ImportFileSystem files,
    IEventBus events,
    ILogger<DownloadImportService> logger,
    IDownloadQueue? queue = null,
    DownloadEngine? engine = null,
    ITmdbClient? tmdb = null,
    TimeProvider? timeProvider = null,
    CustomFormatRepository? customFormats = null,
    MediaInfoService? mediaInfo = null)
{
    /// <summary>Sonarr waits this long after the air date for a missing episode title.</summary>
    public static readonly TimeSpan EpisodeTitleWait = TimeSpan.FromHours(48);

    private const long Megabyte = 1024L * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Marks the download as queued for import (shown in Activity until the job runs).</summary>
    public DownloadImport MarkQueued(DownloadItem item, ManualImportRequest? manual = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var import = new DownloadImport(
            item.Id, ImportState.Queued, null, Now, item.Title, manual?.MediaType ?? item.Link.MediaType,
            manual?.MovieId ?? item.Link.MovieId, manual?.SeriesId ?? item.Link.SeriesId, manual is null ? item.Link.EpisodeIds : [], [],
            Manual: manual is null ? imports.Find(item.Id)?.Manual : JsonSerializer.Serialize(manual, Json));
        imports.Save(import);
        return import;
    }

    /// <summary>
    /// Imports a completed download. Without <paramref name="manual"/> the download's own link (or an earlier manual
    /// choice) decides where the files go. Never throws for import problems: they end up in the returned state.
    /// </summary>
    /// <exception cref="LibraryException">The download does not exist, is not completed, or belongs to nothing.</exception>
    public async Task<DownloadImport> ImportAsync(int downloadId, ManualImportRequest? manual = null, CancellationToken cancellationToken = default)
    {
        var item = queue?.Find(downloadId) ?? throw new LibraryException($"Download {downloadId} not found.", LibraryError.NotFound);
        if (item.Status != DownloadStatus.Completed)
        {
            throw new LibraryException("Only completed downloads can be imported.", LibraryError.Conflict);
        }

        var previous = imports.Find(downloadId);
        if (manual is null && previous?.Manual is { } stored)
        {
            manual = JsonSerializer.Deserialize<ManualImportRequest>(stored, Json);
        }

        var mediaType = manual?.MediaType ?? item.Link.MediaType;
        if (mediaType is null)
        {
            throw new LibraryException("This download belongs to no movie or series. Choose one to import it.", LibraryError.Invalid);
        }

        var state = new DownloadImport(
            item.Id, ImportState.Importing, null, Now, item.Title, mediaType,
            manual?.MovieId ?? item.Link.MovieId, manual?.SeriesId ?? item.Link.SeriesId,
            manual is null ? item.Link.EpisodeIds : [.. manual.Files.SelectMany(f => f.EpisodeIds).Distinct()], [],
            Manual: manual is null ? null : JsonSerializer.Serialize(manual, Json));
        imports.Save(state);
        await events.PublishAsync(new OngoingChanged(), cancellationToken);

        Outcome outcome;
        try
        {
            outcome = mediaType == MediaType.Movie
                ? await ImportMovieAsync(item, state.MovieId, manual, cancellationToken)
                : await ImportEpisodesAsync(item, state.SeriesId, manual, cancellationToken);
        }
        catch (ImportException ex)
        {
            outcome = Outcome.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome = Outcome.Failed(ImportFileSystem.Describe(ex, item.OutputPath ?? string.Empty));
        }

        var result = state with
        {
            State = outcome.State,
            Message = outcome.Message,
            Date = Now,
            Files = [.. outcome.Files.Where(f => f.Destination is not null).Select(f => f.Destination!)],
            IsUpgrade = outcome.Files.Any(f => f.IsUpgrade),
            Dismissed = false,
        };
        imports.Save(result);

        if (result.State == ImportState.Failed)
        {
            logger.LogWarning("Import of download {Id} '{Title}' failed: {Message}", item.Id, item.Title, result.Message);
            history.Add(History(item, HistoryEventType.ImportFailed, state, null, new Dictionary<string, string?> { ["message"] = result.Message }));
        }
        else if (result.State == ImportState.Waiting)
        {
            logger.LogInformation("Import of download {Id} '{Title}' waits: {Message}", item.Id, item.Title, result.Message);
        }
        else
        {
            logger.LogInformation("Imported download {Id} '{Title}': {Count} file(s){Note}", item.Id, item.Title, result.Files.Count, result.Message is null ? string.Empty : $" ({result.Message})");
            await AfterImportAsync(item, outcome, cancellationToken);
        }

        await events.PublishAsync(new DownloadChanged(item.Id), cancellationToken);
        await events.PublishAsync(new OngoingChanged(), cancellationToken);
        return result;
    }

    /// <summary>The video files of a download for the manual import dialog, matched to episodes of <paramref name="seriesId"/> when given.</summary>
    public IReadOnlyList<ImportFileCandidate> Candidates(int downloadId, int? seriesId)
    {
        var item = queue?.Find(downloadId) ?? throw new LibraryException($"Download {downloadId} not found.", LibraryError.NotFound);
        if (item.OutputPath is not { } folder || !Directory.Exists(folder))
        {
            throw new LibraryException("The download folder no longer exists.", LibraryError.NotFound);
        }

        var library = seriesId is { } id ? series.Episodes(id) : [];
        return [.. VideoFiles(folder).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).Select(file =>
        {
            var info = Describe(item.Title, file.FullName);
            var parsed = ParseEpisodeFile(file.FullName, item.Title, single: false);
            IReadOnlyList<int> ids = [];
            string? problem = null;
            if (seriesId is not null)
            {
                if (parsed is null)
                {
                    problem = "Season and episode not found in the name";
                }
                else
                {
                    var (episodes, rejection) = ReleaseMatching.MapEpisodes(parsed, library);
                    ids = [.. episodes.Select(e => e.Id)];
                    problem = rejection?.Message;
                }
            }

            return new ImportFileCandidate(
                Path.GetRelativePath(folder, file.FullName),
                file.Length,
                info.Quality.ToString(),
                [.. info.Languages.Where(l => l.Id > 0).Select(l => l.Name)],
                parsed?.SeasonNumber,
                parsed?.EpisodeNumbers ?? [],
                ids,
                problem);
        })];
    }

    // ------------------------------------------------------------------ movies

    private async Task<Outcome> ImportMovieAsync(DownloadItem item, int? movieId, ManualImportRequest? manual, CancellationToken cancellationToken)
    {
        var options = settings.Current;
        var movie = (movieId is { } id ? movies.Find(id) : null) ?? throw new ImportException("The movie is no longer in the library.");
        var folder = DownloadFolder(item);

        var videoCount = VideoFiles(folder).Count();
        FileInfo source;
        if (manual is { Files.Count: > 0 })
        {
            source = ManualFile(folder, manual.Files[0].Path);
        }
        else
        {
            var videos = VideoFiles(folder).ToList();
            source = videos.MaxBy(f => f.Length) ?? throw new ImportException(NoVideoMessage(folder));
        }

        var info = (await WithMediaInfoAsync(Describe(item.Title, source.FullName), source.FullName, item.Title, cancellationToken)).WithCustomFormats(
            customFormats?.ForMediaType(MediaType.Movie) ?? [], MediaType.Movie, Language.FindById(movie.OriginalLanguageId), movie.Year, source.Name, source.Length);
        var existingPath = movie.File is { } current && !IsDisc(current) ? Path.Combine(movie.FolderPath, current.RelativePath) : null;
        if (existingPath is not null && !File.Exists(existingPath))
        {
            existingPath = null;
        }

        var upgrade = existingPath is not null;
        if (manual is null && existingPath is not null
            && UpgradeProblem(movie.QualityProfileId, existingPath, movie.FolderPath, info, options, MediaType.Movie, Language.FindById(movie.OriginalLanguageId), movie.Year) is { } notBetter)
        {
            return Outcome.Failed(notBetter, [new ImportedFile(source.FullName, null, [], notBetter, false)]);
        }

        CheckFreeSpace(movie.FolderPath, source, options);

        var baseName = options.RenameMovies ? new FileNameBuilder(options).MovieFileName(movie, info) : Path.GetFileNameWithoutExtension(source.Name);
        if (baseName.Length == 0)
        {
            baseName = Path.GetFileNameWithoutExtension(source.Name);
        }

        files.CreateFolder(movie.FolderPath, options);
        var destination = Path.Combine(movie.FolderPath, baseName + source.Extension.ToLowerInvariant());
        var itemFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(movie.FolderPath));
        var replaced = PlaceFile(source.FullName, destination, existingPath is null ? [] : [existingPath], itemFolderName, options);

        SetFileDate(destination, await ReleaseDateAsync(movie, options, cancellationToken), options);
        ImportExtras(folder, source, [source], destination, options);

        var modified = File.GetLastWriteTimeUtc(destination);
        movie.File = new MediaFile(Path.GetRelativePath(movie.FolderPath, destination), new FileInfo(destination).Length, modified);
        movies.Update(movie);
        SaveDetails(destination, info);
        foreach (var old in replaced)
        {
            fileDetails.Delete(old);
        }

        var imported = new ImportedFile(source.FullName, destination, [], null, upgrade);
        history.Add(History(item, upgrade ? HistoryEventType.Upgraded : HistoryEventType.Imported, null, imported, new Dictionary<string, string?>
        {
            ["importedPath"] = destination,
            ["droppedPath"] = source.FullName,
            ["replaced"] = replaced.Count == 0 ? null : string.Join("|", replaced),
            ["manual"] = manual is null ? null : "true",
        }) with { MediaType = MediaType.Movie, MovieId = movie.Id, SeriesId = null, EpisodeIds = [] });

        return new Outcome(ImportState.Imported, null, [imported], MediaType.Movie, movie.Id, null, movie.FolderPath, movie.Title, videoCount <= 1);
    }

    // ------------------------------------------------------------------ episodes

    private async Task<Outcome> ImportEpisodesAsync(DownloadItem item, int? seriesId, ManualImportRequest? manual, CancellationToken cancellationToken)
    {
        var options = settings.Current;
        var show = (seriesId is { } id ? series.Find(id) : null) ?? throw new ImportException("The series is no longer in the library.");
        var library = series.Episodes(show.Id);
        var byId = library.ToDictionary(e => e.Id);
        var folder = DownloadFolder(item);
        var videos = VideoFiles(folder).ToList();
        if (videos.Count == 0 && manual is not { Files.Count: > 0 })
        {
            throw new ImportException(NoVideoMessage(folder));
        }

        // 1. Which episodes are in which file.
        var planned = new List<(FileInfo File, IReadOnlyList<Episode> Episodes, bool Daily)>();
        var results = new List<ImportedFile>();
        if (manual is { Files.Count: > 0 })
        {
            foreach (var choice in manual.Files)
            {
                var file = ManualFile(folder, choice.Path);
                var episodes = choice.EpisodeIds.Select(e => byId.GetValueOrDefault(e)).OfType<Episode>().ToList();
                if (episodes.Count == 0)
                {
                    results.Add(new ImportedFile(file.FullName, null, [], "No episode chosen", false));
                    continue;
                }

                planned.Add((file, episodes, ParseEpisodeFile(file.FullName, item.Title, single: false)?.AirDate is not null));
            }
        }
        else
        {
            var grabbed = item.Link.EpisodeIds.ToHashSet();
            foreach (var file in videos)
            {
                var parsed = ParseEpisodeFile(file.FullName, item.Title, single: videos.Count == 1);
                IReadOnlyList<Episode> episodes;
                if (parsed is null || parsed.FullSeason)
                {
                    // Obfuscated name of a single-episode download: the grab says which episode it is.
                    if (videos.Count == 1 && grabbed.Count is > 0 and <= 3)
                    {
                        episodes = [.. grabbed.Select(e => byId.GetValueOrDefault(e)).OfType<Episode>()];
                    }
                    else
                    {
                        results.Add(new ImportedFile(file.FullName, null, [], "Season and episode not found in the file name", false));
                        continue;
                    }
                }
                else
                {
                    var (mapped, rejection) = ReleaseMatching.MapEpisodes(parsed, library);
                    if (rejection is not null)
                    {
                        results.Add(new ImportedFile(file.FullName, null, [], rejection.Message, false));
                        continue;
                    }

                    episodes = mapped;
                }

                if (grabbed.Count > 0 && episodes.Any(e => !grabbed.Contains(e.Id)))
                {
                    results.Add(new ImportedFile(
                        file.FullName, null, [.. episodes.Select(e => e.Id)], $"{Label(episodes)} was not grabbed with this release", false));
                    continue;
                }

                planned.Add((file, episodes, parsed?.AirDate is not null));
            }

            // Two files for the same episode: the larger one wins.
            foreach (var group in planned.GroupBy(p => string.Join(',', p.Episodes.Select(e => e.Id).Order())).Where(g => g.Count() > 1).ToList())
            {
                foreach (var loser in group.OrderByDescending(p => p.File.Length).Skip(1))
                {
                    planned.Remove(loser);
                    results.Add(new ImportedFile(loser.File.FullName, null, [.. loser.Episodes.Select(e => e.Id)], "A larger file has the same episode", false));
                }
            }
        }

        if (planned.Count == 0)
        {
            var reason = results.Select(r => r.Rejection).FirstOrDefault(r => r is not null) ?? "No video file could be matched to an episode";
            return Outcome.Failed(results.Count > 1 ? $"{reason} (and {results.Count - 1} more)" : reason, results);
        }

        // 2. Hold back while an episode title is missing (Sonarr's "Episode Title Required").
        var builder = new FileNameBuilder(options);
        if (manual is null && WaitForTitle(planned.SelectMany(p => p.Episodes).ToList(), show, planned.Count > 1, options) is { } wait)
        {
            return new Outcome(ImportState.Waiting, wait, results, MediaType.Series, show.Id, null, show.FolderPath, show.Title, false);
        }

        // 3. Checks per file: better than what is on disk, enough space for all of them.
        var accepted = new List<(FileInfo File, IReadOnlyList<Episode> Episodes, bool Daily, NamingFileInfo Info, IReadOnlyList<string> Existing)>();
        var seriesFormats = customFormats?.ForMediaType(MediaType.Series) ?? [];
        foreach (var (file, episodes, daily) in planned)
        {
            var info = (await WithMediaInfoAsync(Describe(item.Title, file.FullName), file.FullName, item.Title, cancellationToken, single: planned.Count == 1)).WithCustomFormats(
                seriesFormats, MediaType.Series, Language.FindById(show.OriginalLanguageId), show.Year, file.Name, file.Length,
                CustomFormatNaming.EpisodeFileType(episodes.Count));
            var existing = episodes
                .Where(e => e.File is not null)
                .Select(e => Path.Combine(show.FolderPath, e.File!.RelativePath))
                .Distinct(PathComparer.Instance)
                .Where(File.Exists)
                .ToList();

            if (manual is null
                && existing.Select(p => UpgradeProblem(show.QualityProfileId, p, show.FolderPath, info, options, MediaType.Series, Language.FindById(show.OriginalLanguageId), show.Year))
                    .FirstOrDefault(p => p is not null) is { } notBetter)
            {
                results.Add(new ImportedFile(file.FullName, null, [.. episodes.Select(e => e.Id)], $"{Label(episodes)}: {notBetter}", false));
                continue;
            }

            accepted.Add((file, episodes, daily, info, existing));
        }

        if (accepted.Count == 0)
        {
            var reason = results.Select(r => r.Rejection).First(r => r is not null)!;
            return Outcome.Failed(results.Count > 1 ? $"{reason} (and {results.Count - 1} more)" : reason, results);
        }

        CheckFreeSpace(show.FolderPath, accepted.Select(a => a.File), options);

        // 4. Move them in.
        files.CreateFolder(show.FolderPath, options);
        var seriesFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(show.FolderPath));
        var imported = new List<ImportedFile>();
        foreach (var (file, episodes, daily, info, existing) in accepted)
        {
            var season = episodes[0].SeasonNumber;
            var targetFolder = builder.SeasonFolderName(show, season) is { } seasonFolder ? Path.Combine(show.FolderPath, seasonFolder) : show.FolderPath;
            files.CreateFolder(targetFolder, options);
            var baseName = options.RenameEpisodes
                ? builder.EpisodeFileName(show, episodes, info, FileNameBuilder.FormatFor(show, daily))
                : Path.GetFileNameWithoutExtension(file.Name);
            if (baseName.Length == 0)
            {
                baseName = Path.GetFileNameWithoutExtension(file.Name);
            }

            var destination = Path.Combine(targetFolder, baseName + file.Extension.ToLowerInvariant());
            var ids = episodes.Select(e => e.Id).ToHashSet();

            // An old file is only replaced when no other episode still uses it (multi-episode files).
            var replaceable = existing
                .Where(p => episodeFiles.EpisodesWithFile(show.Id, Path.GetRelativePath(show.FolderPath, p)).All(ids.Contains))
                .ToList();

            var replaced = PlaceFile(file.FullName, destination, replaceable, seriesFolderName, options);
            SetFileDate(destination, AirDate(episodes), options);
            ImportExtras(folder, file, videos.Count == 1 ? [file] : videos, destination, options);

            var media = new MediaFile(Path.GetRelativePath(show.FolderPath, destination), new FileInfo(destination).Length, File.GetLastWriteTimeUtc(destination));
            episodeFiles.SetFile(ids, media);
            SaveDetails(destination, info);
            foreach (var old in replaced)
            {
                fileDetails.Delete(old);
            }

            var upgrade = existing.Count > 0;
            var result = new ImportedFile(file.FullName, destination, [.. ids], null, upgrade);
            imported.Add(result);
            history.Add(History(item, upgrade ? HistoryEventType.Upgraded : HistoryEventType.Imported, null, result, new Dictionary<string, string?>
            {
                ["importedPath"] = destination,
                ["droppedPath"] = file.FullName,
                ["replaced"] = replaced.Count == 0 ? null : string.Join("|", replaced),
                ["manual"] = manual is null ? null : "true",
            }) with { MediaType = MediaType.Series, MovieId = null, SeriesId = show.Id, EpisodeIds = [.. ids] });
        }

        var skipped = results.Where(r => r.Rejection is not null).ToList();
        var message = skipped.Count == 0
            ? null
            : $"{skipped.Count} file{(skipped.Count == 1 ? " was" : "s were")} not imported: {skipped[0].Rejection}{(skipped.Count > 1 ? " …" : string.Empty)}";
        var everything = videos.All(v => imported.Any(i => PathComparer.Instance.Equals(i.Source, v.FullName)));
        return new Outcome(ImportState.Imported, message, [.. imported, .. results], MediaType.Series, null, show.Id, show.FolderPath, show.Title, everything);
    }

    // ------------------------------------------------------------------ shared steps

    private string DownloadFolder(DownloadItem item)
    {
        if (string.IsNullOrEmpty(item.OutputPath))
        {
            throw new ImportException("The download has no folder with finished files.");
        }

        if (!Directory.Exists(item.OutputPath))
        {
            throw new ImportException($"The download folder {item.OutputPath} no longer exists. It may have been moved or deleted.");
        }

        return item.OutputPath;
    }

    private static FileInfo ManualFile(string folder, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(folder, relative));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ImportException($"{relative} is not in the download folder.");
        }

        var file = new FileInfo(full);
        return file.Exists ? file : throw new ImportException($"{relative} no longer exists in the download folder.");
    }

    private static string NoVideoMessage(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var disc = Directory.Exists(folder)
            && (Directory.EnumerateDirectories(folder, "BDMV", options).Any() || Directory.EnumerateDirectories(folder, "VIDEO_TS", options).Any());
        return disc
            ? $"{folder} holds a disc folder (BDMV or VIDEO_TS); disc folders are not imported automatically. Move it into the movie folder by hand."
            : $"No video file found in {folder} (samples and extras are skipped).";
    }

    /// <summary>Video files of a download: no samples, extras or other non-video files (like the disk scan).</summary>
    public static IEnumerable<FileInfo> VideoFiles(string folder) =>
        DiskScanner.VideoFiles(folder).Where(f => !f.Extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) || f.Length > 0);

    private static bool IsDisc(MediaFile file) =>
        !DiskScanner.VideoExtensions.Contains(Path.GetExtension(file.RelativePath));

    /// <summary>Quality, languages and the rest: from the release name, filled up from the file name.</summary>
    private static NamingFileInfo Describe(string releaseTitle, string path)
    {
        var release = ReleaseParser.Parse(releaseTitle);
        var fileName = Path.GetFileNameWithoutExtension(path);
        var file = ReleaseParser.Parse(Path.GetFileName(path));
        var quality = release?.Quality is { Quality.Id: > 0 } q ? q : file?.Quality ?? QualityParser.ParseQuality(Path.GetFileName(path));
        var languages = release?.Languages is { Count: > 0 } l ? l : file?.Languages ?? [];
        return new NamingFileInfo
        {
            Quality = quality,
            Languages = [.. languages.Where(x => x != Language.Unknown)],
            ReleaseGroup = release?.ReleaseGroup ?? file?.ReleaseGroup,
            Edition = release?.Edition ?? file?.Edition,
            VideoCodec = release?.VideoCodec ?? file?.VideoCodec,
            AudioCodec = release?.AudioCodec ?? file?.AudioCodec,
            Hdr = release?.Hdr is { } hdr and not HdrFormat.None ? hdr : file?.Hdr ?? HdrFormat.None,
            SceneName = releaseTitle,
            OriginalFileName = fileName,
        };
    }

    /// <summary>
    /// Like Radarr, the downloaded file is analysed before the checks: the media info goes into the naming, the quality gets
    /// the video's resolution when neither the release nor the file name states one, and a release name without a language
    /// takes the audio languages. Without ffprobe (or with analysing switched off) nothing changes.
    /// </summary>
    /// <param name="single">The release is this one file, so its name tells x264 from h264 (else the file name does).</param>
    private async Task<NamingFileInfo> WithMediaInfoAsync(NamingFileInfo info, string path, string releaseTitle, CancellationToken cancellationToken, bool single = true)
    {
        if (mediaInfo is not { Enabled: true })
        {
            return info;
        }

        try
        {
            var probed = await mediaInfo.ProbeAsync(path, single ? releaseTitle : Path.GetFileName(path), cancellationToken);
            return info with
            {
                MediaInfo = probed,
                Quality = MediaInfoQuality.Augment(info.Quality, probed, releaseTitle, Path.GetFileName(path)),
                Languages = info.Languages.Count > 0 ? info.Languages : probed.AudioLanguages,
            };
        }
        catch (MediaInfoException e)
        {
            logger.LogWarning("Media info of {Path} could not be read: {Message}", path, e.Message);
            return info;
        }
    }

    /// <summary>Season and episodes of a file: its name, then (for a single file) the release name.</summary>
    private static ParsedRelease? ParseEpisodeFile(string path, string releaseTitle, bool single)
    {
        var parsed = ReleaseParser.ParseEpisode(Path.GetFileName(path));
        if (parsed is { FullSeason: false } && (parsed.SeasonNumber is not null || parsed.AirDate is not null))
        {
            return parsed;
        }

        if (EpisodeFileParser.Parse(Path.GetFileName(path)) is { } simple)
        {
            return new ParsedRelease
            {
                ReleaseTitle = Path.GetFileName(path),
                Title = string.Empty,
                CleanTitle = string.Empty,
                SeasonNumber = simple.Season,
                EpisodeNumbers = simple.Episodes,
            };
        }

        var fromRelease = single ? ReleaseParser.ParseEpisode(releaseTitle) : null;
        return fromRelease is { FullSeason: false } ? fromRelease : parsed;
    }

    /// <summary>
    /// Null when the new file may replace the file on disk; otherwise why not (like Radarr's import UpgradeSpecification):
    /// a lower quality, an older revision, or the same quality with a lower custom format score ("Not a custom format
    /// upgrade"). A file in the wrong language is always replaced by one in the profile's language.
    /// </summary>
    private string? UpgradeProblem(
        int profileId, string existingPath, string itemFolder, NamingFileInfo candidate, MediaManagementValues options, MediaType mediaType, Language originalLanguage, int year)
    {
        if (profiles.Find(profileId) is not { } profile)
        {
            return null;
        }

        var (existing, existingLanguages) = ExistingQuality(existingPath, itemFolder);
        if (existing is null)
        {
            return null;
        }

        if (UpgradeRules.IsWrongLanguageFix(profile, existingLanguages, candidate.Languages))
        {
            return null;
        }

        var compare = profile.RankOf(candidate.Quality.Quality.Id).CompareTo(profile.RankOf(existing.Quality.Id));
        if (compare < 0)
        {
            return $"Not an upgrade: the file in the library has a better quality ({existing})";
        }

        if (compare == 0 && candidate.Quality.Quality == existing.Quality && options.PropersAndRepacks != ProperHandling.DoNotPrefer
            && candidate.Quality.Revision.CompareTo(existing.Revision) < 0)
        {
            return $"Not an upgrade: the file in the library has a newer revision ({existing})";
        }

        if (compare == 0 && customFormats is not null)
        {
            var existingFormats = new DecisionTargetFactory(profiles, null, fileDetails, null, customFormats).FileFormats(
                mediaType, ExistingMediaFile(existingPath, itemFolder), itemFolder, originalLanguage, year);
            var existingScore = Core.CustomFormats.CustomFormatCalculator.Score(profile, existingFormats);
            var candidateScore = Core.CustomFormats.CustomFormatCalculator.Score(profile, candidate.CustomFormats);
            if (candidateScore < existingScore)
            {
                return $"Not a custom format upgrade: the file in the library scores {Scored(existingScore, existingFormats)}, this one {Scored(candidateScore, candidate.CustomFormats)}";
            }
        }

        return null;
    }

    private static string Scored(int score, IReadOnlyList<Core.CustomFormats.CustomFormat> formats) =>
        formats.Count == 0
            ? score.ToString("+#;-#;0", CultureInfo.InvariantCulture)
            : $"{score.ToString("+#;-#;0", CultureInfo.InvariantCulture)} ({string.Join(", ", formats.Select(f => f.Name).Order(StringComparer.OrdinalIgnoreCase))})";

    private static MediaFile ExistingMediaFile(string path, string itemFolder)
    {
        var file = new FileInfo(path);
        return new MediaFile(Path.GetRelativePath(itemFolder, path), file.Exists ? file.Length : 0, file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue);
    }

    /// <summary>Quality of a file in the library: what the import stored, else read from its name and folder.</summary>
    public (QualityModel? Quality, IReadOnlyList<Language> Languages) ExistingQuality(string path, string itemFolder)
    {
        if (fileDetails.Find(path) is { } stored)
        {
            return (new QualityModel(Core.Quality.Quality.FindById(stored.QualityId), new Revision(stored.RevisionVersion, stored.RevisionReal, stored.IsRepack)),
                stored.Languages);
        }

        return DecisionTargetFactory.Existing(new MediaFile(Path.GetRelativePath(itemFolder, path), 0, DateTime.MinValue), itemFolder);
    }

    private void CheckFreeSpace(string target, FileInfo file, MediaManagementValues options) => CheckFreeSpace(target, [file], options);

    private void CheckFreeSpace(string target, IEnumerable<FileInfo> sources, MediaManagementValues options)
    {
        if (options.SkipFreeSpaceCheck)
        {
            return;
        }

        var list = sources.ToList();
        var needed = list
            .Where(f => options.ImportMode == ImportMode.Copy || !ImportFileSystem.SameDrive(f.FullName, target))
            .Sum(f => f.Length);
        if (files.FreeBytes(target) is not { } free)
        {
            return;
        }

        var reserve = options.MinimumFreeSpaceMb * Megabyte;
        if (free - needed < reserve)
        {
            throw new ImportException(
                $"Not enough free space for {target}: {Size(free)} free, the import needs {Size(needed)} and {options.MinimumFreeSpaceMb} MB must stay free "
                + "(Settings → Media Management → Minimum free space).");
        }
    }

    /// <summary>
    /// Puts the file at <paramref name="destination"/> and gets the old files out of the way afterwards (recycle bin or delete).
    /// When the destination is the old file itself, the old file is set aside first and put back if the transfer fails.
    /// Returns the old files that were replaced.
    /// </summary>
    private List<string> PlaceFile(string source, string destination, IReadOnlyList<string> oldFiles, string itemFolderName, MediaManagementValues options)
    {
        var replaced = new List<string>();
        string? aside = null;
        var sameAsOld = oldFiles.FirstOrDefault(o => PathComparer.Instance.Equals(o, destination));
        if (sameAsOld is not null || File.Exists(destination))
        {
            aside = destination + ".streamzh-old";
            try
            {
                File.Move(destination, aside, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ImportException(ImportFileSystem.Describe(ex, Path.GetDirectoryName(destination)!), ex);
            }
        }

        try
        {
            files.Transfer(source, destination, options.ImportMode, options.CopyUsingHardlinks);
        }
        catch
        {
            if (aside is not null)
            {
                try
                {
                    File.Move(aside, destination, overwrite: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "Could not put {File} back after a failed import", destination);
                }
            }

            throw;
        }

        files.ApplyPermissions(destination, isFolder: false, options);

        if (aside is not null)
        {
            RecycleWithExtras(aside, destination, itemFolderName, options, keepExtras: true);
            replaced.Add(destination);
        }

        foreach (var old in oldFiles.Where(o => !PathComparer.Instance.Equals(o, destination) && File.Exists(o)))
        {
            RecycleWithExtras(old, old, itemFolderName, options, keepExtras: false);
            replaced.Add(old);
        }

        if (options.DeleteEmptyFolders)
        {
            foreach (var old in oldFiles.Where(o => !PathComparer.Instance.Equals(o, destination)))
            {
                var oldFolder = Path.GetDirectoryName(old);
                if (oldFolder is not null && !PathComparer.Instance.Equals(oldFolder, Path.GetDirectoryName(destination)))
                {
                    files.DeleteEmptyFolders(oldFolder, includeSelf: true);
                }
            }
        }

        return replaced;
    }

    /// <summary>Recycles an old video file; its subtitles go too unless they now belong to the new file of the same name.</summary>
    private void RecycleWithExtras(string path, string originalPath, string itemFolderName, MediaManagementValues options, bool keepExtras)
    {
        files.Recycle(path, options.RecycleBin, itemFolderName);
        if (keepExtras || !options.ImportExtraFiles)
        {
            return;
        }

        var folder = Path.GetDirectoryName(originalPath)!;
        var baseName = Path.GetFileNameWithoutExtension(originalPath);
        foreach (var extra in ExtraFiles.Find(folder, options.ExtraExtensions)
            .Where(e => PathComparer.Instance.Equals(Path.GetDirectoryName(e), folder) && Path.GetFileName(e).StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)))
        {
            files.Recycle(extra, options.RecycleBin, itemFolderName);
        }
    }

    private void ImportExtras(string downloadFolder, FileInfo video, IReadOnlyList<FileInfo> allVideos, string destination, MediaManagementValues options)
    {
        if (!options.ImportExtraFiles)
        {
            return;
        }

        var videoBase = Path.GetFileNameWithoutExtension(video.Name);
        var newBase = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileNameWithoutExtension(destination));
        var episode = EpisodeFileParser.Parse(video.Name);
        var taken = new HashSet<string>(PathComparer.Instance);
        foreach (var extra in ExtraFiles.Find(downloadFolder, options.ExtraExtensions))
        {
            var name = Path.GetFileName(extra);
            if (allVideos.Count > 1)
            {
                // Season pack: only the extras of this episode (same name start, or the same SxxEyy).
                var sameName = name.StartsWith(videoBase + ".", StringComparison.OrdinalIgnoreCase);
                var parsed = EpisodeFileParser.Parse(name);
                var sameEpisode = parsed is not null && episode is not null && parsed.Value.Season == episode.Value.Season
                    && parsed.Value.Episodes.SequenceEqual(episode.Value.Episodes);
                if (!sameName && !sameEpisode)
                {
                    continue;
                }
            }

            var target = Path.Combine(Path.GetDirectoryName(destination)!, ExtraFiles.TargetName(extra, videoBase, Path.GetFileName(newBase)));
            if (!taken.Add(target) || File.Exists(target))
            {
                target = ImportFileSystem.Unique(target);
                taken.Add(target);
            }

            try
            {
                files.Transfer(extra, target, options.ImportMode, options.CopyUsingHardlinks);
                files.ApplyPermissions(target, isFolder: false, options);
            }
            catch (ImportException ex)
            {
                logger.LogWarning("Extra file {File} was not imported: {Message}", extra, ex.Message);
            }
        }
    }

    private void SetFileDate(string path, DateTime? releaseDate, MediaManagementValues options)
    {
        if (options.ChangeFileDate == FileDateMode.ReleaseDate && releaseDate is { } date)
        {
            files.SetModified(path, date);
        }
    }

    private async Task<DateTime?> ReleaseDateAsync(Movie movie, MediaManagementValues options, CancellationToken cancellationToken)
    {
        if (options.ChangeFileDate != FileDateMode.ReleaseDate || tmdb is null || movie.TmdbId <= 0)
        {
            return null;
        }

        try
        {
            var details = await tmdb.GetMovieAsync(movie.TmdbId, cancellationToken);
            return ParseDate(details.ReleaseDate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Release date of '{Title}' not available, the file date stays: {Message}", movie.Title, ex.Message);
            return null;
        }
    }

    private static DateTime? AirDate(IReadOnlyList<Episode> episodes) => ParseDate(episodes.Select(e => e.AirDate).FirstOrDefault(d => d is not null));

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
            : null;

    /// <summary>A reason to wait when an episode has no title yet and the format uses it.</summary>
    private string? WaitForTitle(IReadOnlyList<Episode> episodes, Series show, bool bulk, MediaManagementValues options)
    {
        if (!options.RenameEpisodes || options.EpisodeTitleRequired == EpisodeTitleRequired.Never
            || (options.EpisodeTitleRequired == EpisodeTitleRequired.BulkSeasonReleases && !bulk))
        {
            return null;
        }

        var format = FileNameBuilder.FormatFor(show, false) == EpisodeFormat.Anime ? options.AnimeEpisodeFormat : options.StandardEpisodeFormat;
        if (!format.Contains("episode", StringComparison.OrdinalIgnoreCase)
            || !(format.Contains("Title}", StringComparison.OrdinalIgnoreCase) || format.Contains("CleanTitle", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var now = Now;
        var missing = episodes.FirstOrDefault(e =>
            (string.IsNullOrWhiteSpace(e.Title) || e.Title.Trim() is "TBA" or "TBD" || e.Title.Trim().Equals($"Episode {e.EpisodeNumber}", StringComparison.OrdinalIgnoreCase))
            && ParseDate(e.AirDate) is { } aired && now - aired < EpisodeTitleWait);
        return missing is null
            ? null
            : $"The title of {Label([missing])} is not known yet. StreamZH tries again until 48 hours after the air date (Settings → Media Management → Episode title required).";
    }

    /// <summary>The media info of the downloaded file belongs to the placed copy now (same content, maybe another time stamp).</summary>
    private void SaveDetails(string path, NamingFileInfo info)
    {
        var placed = new FileInfo(path);
        var media = info.MediaInfo is { } probed && placed.Exists ? probed with { FileSize = placed.Length, FileModified = placed.LastWriteTimeUtc } : info.MediaInfo;
        fileDetails.Save(new MediaFileDetails(
            path, info.Quality.Quality.Id, info.Quality.Revision.Version, info.Quality.Revision.Real, info.Quality.Revision.IsRepack,
            [.. info.Languages.Select(l => l.Id)], info.ReleaseGroup, info.SceneName, info.Edition, info.VideoCodec, info.AudioCodec, Now,
            media, media is null ? null : Now));
    }

    /// <summary>Cleanup, download list, events. Problems here no longer make the import fail.</summary>
    private async Task AfterImportAsync(DownloadItem item, Outcome outcome, CancellationToken cancellationToken)
    {
        var options = settings.Current;
        if (options.ImportMode == ImportMode.Move && !downloads.KeepFiles && outcome.EverythingImported && item.OutputPath is { } folder && IsDownloadFolder(folder))
        {
            files.DeleteFolder(folder);
        }

        if (options.RemoveCompletedDownloads && engine is not null)
        {
            try
            {
                await engine.DeleteAsync(item.Id, deleteFiles: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not remove download {Id} from the download history", item.Id);
            }
        }

        var imported = outcome.Files.Where(f => f.Destination is not null).ToList();
        if (imported.Count == 0)
        {
            return;
        }

        await events.PublishAsync(
            new MediaImported(
                outcome.MediaType,
                outcome.MovieId,
                outcome.SeriesId,
                [.. imported.SelectMany(f => f.EpisodeIds).Distinct()],
                outcome.Title,
                [.. imported.Select(f => f.Destination!)],
                outcome.Folder,
                imported.Any(f => f.IsUpgrade),
                item.Title,
                item.Id),
            cancellationToken);
        await events.PublishAsync(new LibraryChanged(outcome.MediaType, outcome.MovieId ?? outcome.SeriesId), cancellationToken);
    }

    /// <summary>Only folders inside the complete or incomplete download folder are ever deleted.</summary>
    private bool IsDownloadFolder(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return new[] { downloads.CompleteFolder, downloads.IncompleteFolder }
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar)
            .Any(root => full.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
    }

    private HistoryItem History(DownloadItem item, HistoryEventType type, DownloadImport? state, ImportedFile? file, Dictionary<string, string?> data)
    {
        var parsed = ReleaseParser.Parse(item.Title);
        data["downloadId"] = item.Id.ToString(CultureInfo.InvariantCulture);
        return new HistoryItem
        {
            Date = Now,
            EventType = type,
            MediaType = state?.MediaType ?? item.Link.MediaType,
            MovieId = state?.MovieId ?? item.Link.MovieId,
            SeriesId = state?.SeriesId ?? item.Link.SeriesId,
            EpisodeIds = file?.EpisodeIds ?? state?.EpisodeIds ?? item.Link.EpisodeIds,
            SourceTitle = item.Title,
            QualityId = parsed?.Quality.Quality.Id ?? Core.Quality.Quality.Unknown.Id,
            LanguageIds = [.. (parsed?.Languages ?? []).Select(l => l.Id)],
            ReleaseGroup = parsed?.ReleaseGroup,
            Indexer = item.Indexer,
            Size = file?.Destination is { } path && File.Exists(path) ? new FileInfo(path).Length : item.TotalBytes,
            Data = data,
        };
    }

    private static string Label(IReadOnlyList<Episode> episodes) =>
        ReleaseMatching.Label(episodes[0].SeasonNumber, [.. episodes.Select(e => e.EpisodeNumber).Order()]);

    private static string Size(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{(bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} GB"
            : $"{(bytes / (1024.0 * 1024)).ToString("0", CultureInfo.InvariantCulture)} MB";

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private sealed record Outcome(
        ImportState State,
        string? Message,
        IReadOnlyList<ImportedFile> Files,
        MediaType MediaType,
        int? MovieId,
        int? SeriesId,
        string Folder,
        string Title,
        bool EverythingImported)
    {
        public static Outcome Failed(string message, IReadOnlyList<ImportedFile>? files = null) =>
            new(ImportState.Failed, message, files ?? [], MediaType.Movie, null, null, string.Empty, string.Empty, false);
    }
}
