// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.PostProcessing;
using StreamZH.Parsing;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.MediaFiles;

/// <summary>Settings → Media Management → Media info: analyse files with ffprobe, and how often the stored analyses are checked.</summary>
public sealed class MediaInfoSettings(ConfigRepository config)
{
    public const int DefaultRefreshIntervalDays = 7;
    public const int MaxRefreshIntervalDays = 365;

    /// <summary>Radarr's "Analyse video files": on unless switched off.</summary>
    public bool Analyze => !string.Equals(config.Get(ConfigKeys.MediaInfoAnalyze), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Days between runs of "Refresh media info"; 0 = off.</summary>
    public int RefreshIntervalDays =>
        int.TryParse(config.Get(ConfigKeys.MediaInfoRefreshIntervalDays), out var days) ? Math.Clamp(days, 0, MaxRefreshIntervalDays) : DefaultRefreshIntervalDays;

    public TimeSpan? RefreshInterval => RefreshIntervalDays > 0 ? TimeSpan.FromDays(RefreshIntervalDays) : null;

    public void Update(bool analyze, int refreshIntervalDays)
    {
        config.Set(ConfigKeys.MediaInfoAnalyze, analyze ? "true" : "false");
        config.Set(ConfigKeys.MediaInfoRefreshIntervalDays, Math.Clamp(refreshIntervalDays, 0, MaxRefreshIntervalDays).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>The analysis of one file, or why there is none.</summary>
public sealed record MediaInfoResult(MediaInfo? MediaInfo, DateTime? Updated, string? Problem);

/// <summary>
/// Reads the media info of video files with ffprobe (like Radarr and Sonarr): codecs, resolution, HDR, audio tracks with
/// languages, subtitles. Stored with the file details; library files without details get a row (quality from the name, the
/// resolution from the video when the name has none). Only new, changed or outdated analyses run again unless forced.
/// </summary>
public sealed class MediaInfoService(
    PostProcessTools tools,
    IToolProcessRunner runner,
    MediaFileDetailsRepository details,
    MovieRepository movies,
    SeriesRepository series,
    MediaInfoSettings settings,
    ILogger<MediaInfoService> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>Each ffprobe call may take this long (a file on a slow network share).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>ffprobe is there and analysing is switched on.</summary>
    public bool Enabled => settings.Analyze && tools.Find(PostProcessTool.FFprobe).Found;

    public bool FfprobeFound => tools.Find(PostProcessTool.FFprobe).Found;

    /// <summary>
    /// Runs ffprobe on a file (streams and format, plus the first frame's side data for HDR video).
    /// </summary>
    /// <exception cref="MediaInfoException">ffprobe is missing, failed or its output could not be read.</exception>
    public async Task<MediaInfo> ProbeAsync(string path, string? sceneName, CancellationToken cancellationToken)
    {
        if (tools.Find(PostProcessTool.FFprobe).Path is not { } ffprobe)
        {
            throw new MediaInfoException("ffprobe was not found. Install it in Settings → Downloads → Tools.");
        }

        var (size, modified) = SafeStat(path);
        var streams = await RunAsync(ffprobe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-probesize", "50M", path], path, cancellationToken)
            .ConfigureAwait(false);
        string? frames = null;
        if (FfprobeParser.NeedsFrameSideData(streams))
        {
            try
            {
                frames = await RunAsync(
                    ffprobe,
                    ["-v", "error", "-print_format", "json", "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_frames", "-show_entries", "frame=side_data_list", path],
                    path,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (MediaInfoException e)
            {
                // Without the first frame HDR10 and HDR10+ look like plain PQ; the rest is still right.
                logger.LogDebug("Could not read the frame side data of {Path}: {Message}", path, e.Message);
            }
        }

        return FfprobeParser.Parse(streams, frames, sceneName ?? Path.GetFileName(path), size) with { FileModified = modified };
    }

    private async Task<string> RunAsync(string ffprobe, IReadOnlyList<string> arguments, string path, CancellationToken cancellationToken)
    {
        // All output is collected here: the runner keeps only the last part of long outputs, and a remux with many
        // subtitle tracks gives a long JSON.
        var all = new System.Text.StringBuilder();
        ToolProcessResult result;
        try
        {
            result = await runner.RunAsync(ffprobe, arguments, null, segment => all.AppendLine(segment), ProbeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new MediaInfoException($"ffprobe could not be started: {e.Message}", e);
        }
        catch (TimeoutException e)
        {
            throw new MediaInfoException($"ffprobe did not finish reading {Path.GetFileName(path)} within {ProbeTimeout.TotalSeconds:0} seconds.", e);
        }

        var output = all.Length > 0 ? all.ToString() : result.Output;
        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (result.ExitCode != 0 || start < 0)
        {
            var reason = Par2Repairer.LastLine(output);
            throw new MediaInfoException($"ffprobe could not read {Path.GetFileName(path)}{(string.IsNullOrWhiteSpace(reason) ? "." : ": " + reason)}");
        }

        // The output mixes standard output and error; the JSON starts at the first brace and ends at the last.
        var end = output.LastIndexOf('}');
        return output[start..(end + 1)];
    }

    /// <summary>The stored analysis of a file, or null.</summary>
    public MediaFileDetails? Stored(string path) => details.Find(path);

    /// <summary>True when the file has no analysis, a different size, was changed after the analysis or the analysis is outdated.</summary>
    public static bool NeedsAnalysis(MediaFileDetails? stored, FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (stored?.MediaInfo is not { } info || stored.MediaInfoUpdated is not { } updated)
        {
            return true;
        }

        if (info.Revision < MediaInfo.CurrentRevision || (info.FileSize is { } size && size != file.Length))
        {
            return true;
        }

        // Written since the analysis (copies may round the time a little).
        return info.FileModified is { } modified
            ? Math.Abs((file.LastWriteTimeUtc - modified).TotalSeconds) > 2
            : file.LastWriteTimeUtc > updated.AddMinutes(1);
    }

    /// <summary>
    /// Analyses one library file and stores the result: a row is created for a file without details (quality and the rest
    /// from the name); the quality's resolution comes from the video when neither the release nor the file name states one.
    /// </summary>
    public async Task<MediaInfoResult> AnalyzeAsync(string path, string itemFolder, bool force, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return new MediaInfoResult(null, null, "The file no longer exists.");
        }

        var stored = details.Find(path);
        if (!force && !NeedsAnalysis(stored, file))
        {
            return new MediaInfoResult(stored!.MediaInfo, stored.MediaInfoUpdated, null);
        }

        if (!FfprobeFound)
        {
            return new MediaInfoResult(stored?.MediaInfo, stored?.MediaInfoUpdated, "ffprobe was not found. Install it in Settings → Downloads → Tools.");
        }

        MediaInfo info;
        try
        {
            info = await ProbeAsync(path, stored?.SceneName, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaInfoException e)
        {
            logger.LogWarning("Media info of {Path} could not be read: {Message}", path, e.Message);
            return new MediaInfoResult(stored?.MediaInfo, stored?.MediaInfoUpdated, e.Message);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        details.Save(WithMediaInfo(stored, path, itemFolder, info, now));
        logger.LogDebug("Media info of {Path}: {Video} {Audio}", path, info.Video?.Codec, info.PrimaryAudio?.Codec);
        return new MediaInfoResult(info, now, null);
    }

    /// <summary>
    /// The details row with a new analysis: an existing row keeps its release facts (quality only gains the resolution when
    /// no name states one); a new row gets quality, languages, group and edition from the file and folder names.
    /// </summary>
    public static MediaFileDetails WithMediaInfo(MediaFileDetails? stored, string path, string itemFolder, MediaInfo info, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(info);
        var fileName = Path.GetFileName(path);
        if (stored is not null)
        {
            var quality = new QualityModel(Quality.FindById(stored.QualityId), new Revision(stored.RevisionVersion, stored.RevisionReal, stored.IsRepack));
            var augmented = MediaInfoQuality.Augment(quality, info, stored.SceneName, fileName);
            return stored with { QualityId = augmented.Quality.Id, MediaInfo = info, MediaInfoUpdated = now };
        }

        var relative = Path.GetRelativePath(itemFolder, path);
        var (parsedQuality, languages) = Search.DecisionTargetFactory.Existing(new MediaFile(relative, 0, DateTime.MinValue), itemFolder);
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(itemFolder));
        var qualityModel = MediaInfoQuality.Augment(parsedQuality ?? QualityModel.Unknown, info, fileName, folderName);
        var parsed = ReleaseParser.ParseEpisode(fileName) ?? ReleaseParser.Parse(fileName);
        return new MediaFileDetails(
            path,
            qualityModel.Quality.Id,
            qualityModel.Revision.Version,
            qualityModel.Revision.Real,
            qualityModel.Revision.IsRepack,
            [.. languages.Where(l => l.Id > 0).Select(l => l.Id)],
            parsed?.ReleaseGroup,
            null,
            parsed?.Edition,
            parsed?.VideoCodec,
            parsed?.AudioCodec,
            now,
            info,
            now);
    }

    // ------------------------------------------------------------------ library

    /// <summary>Analyses the movie's file (new or changed only unless forced). Returns the number of files analysed.</summary>
    public async Task<int> AnalyzeMovieAsync(int movieId, bool force, CancellationToken cancellationToken)
    {
        if (movies.Find(movieId) is not { File: { } file } movie || !IsVideo(file))
        {
            return 0;
        }

        return await AnalyzeFilesAsync(movie.FolderPath, [file], force, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Analyses the episode files of a series (new or changed only unless forced).</summary>
    public async Task<int> AnalyzeSeriesAsync(int seriesId, bool force, CancellationToken cancellationToken)
    {
        if (series.Find(seriesId) is not { } show)
        {
            return 0;
        }

        var files = series.Episodes(seriesId).Select(e => e.File).OfType<MediaFile>().Where(IsVideo).DistinctBy(f => f.RelativePath).ToList();
        return await AnalyzeFilesAsync(show.FolderPath, files, force, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"Refresh media info": every library file that is new, changed or analysed by an older StreamZH.</summary>
    public async Task<int> AnalyzeLibraryAsync(bool force, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            logger.LogInformation(settings.Analyze ? "Media info not refreshed: ffprobe was not found" : "Media info not refreshed: analysing video files is switched off");
            return 0;
        }

        var count = 0;
        foreach (var movie in movies.All())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (movie.File is { } file && IsVideo(file))
            {
                count += await AnalyzeFilesAsync(movie.FolderPath, [file], force, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var show in series.All())
        {
            cancellationToken.ThrowIfCancellationRequested();
            count += await AnalyzeSeriesAsync(show.Id, force, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Media info refreshed: {Count} file(s) analysed", count);
        return count;
    }

    /// <summary>True when an item has a file that needs analysing (cheap: no ffprobe, one query per folder).</summary>
    public bool HasWork(string folder, IReadOnlyList<MediaFile> files)
    {
        if (!Enabled || files.Count == 0)
        {
            return false;
        }

        var stored = details.InFolder(folder);
        return files.Where(IsVideo).Any(f =>
        {
            var info = new FileInfo(Path.Combine(folder, f.RelativePath));
            return info.Exists && NeedsAnalysis(stored.GetValueOrDefault(MediaFileDetailsRepository.Key(info.FullName)), info);
        });
    }

    private async Task<int> AnalyzeFilesAsync(string folder, IReadOnlyList<MediaFile> files, bool force, CancellationToken cancellationToken)
    {
        if (!Enabled || files.Count == 0)
        {
            return 0;
        }

        var stored = details.InFolder(folder);
        var count = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(Path.Combine(folder, file.RelativePath));
            if (!info.Exists || (!force && !NeedsAnalysis(stored.GetValueOrDefault(MediaFileDetailsRepository.Key(info.FullName)), info)))
            {
                continue;
            }

            var result = await AnalyzeAsync(info.FullName, folder, force: true, cancellationToken).ConfigureAwait(false);
            if (result.Problem is null)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Disc folders (BDMV, VIDEO_TS) are not analysed.</summary>
    private static bool IsVideo(MediaFile file) => DiskScanner.VideoExtensions.Contains(Path.GetExtension(file.RelativePath))
                                                   && !Path.GetExtension(file.RelativePath).Equals(".iso", StringComparison.OrdinalIgnoreCase);

    private static (long? Size, DateTime? Modified) SafeStat(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc) : (null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}
