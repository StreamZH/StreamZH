// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.MediaFiles;

namespace StreamZH.Host.Api;

/// <summary>
/// Media info of library files (what ffprobe found: video, audio tracks, subtitles), re-analysing a file or a series, and
/// Settings → Media Management → Media info.
/// </summary>
public static class MediaInfoEndpoints
{
    public static IEndpointRouteBuilder MapMediaInfoEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/mediainfo").WithTags("Media info").RequireAuthorization();

        api.MapGet("/movie/{id:int}", (int id, MovieRepository movies, MediaInfoService service, MediaInfoSettings settings) =>
            movies.Find(id) is { } movie ? Results.Ok(Resource(movie.FolderPath, movie.File, service, settings, null)) : Results.NotFound());

        api.MapPost("/movie/{id:int}/analyze", async (int id, MovieRepository movies, MediaInfoService service, MediaInfoSettings settings, CancellationToken ct) =>
        {
            if (movies.Find(id) is not { } movie)
            {
                return Results.NotFound();
            }

            return Results.Ok(await AnalyzeAsync(movie.FolderPath, movie.File, service, settings, ct));
        });

        api.MapGet("/series/{seriesId:int}/episode/{episodeId:int}", (int seriesId, int episodeId, SeriesRepository series, MediaInfoService service, MediaInfoSettings settings) =>
            Episode(series, seriesId, episodeId) is ({ } show, { } episode)
                ? Results.Ok(Resource(show.FolderPath, episode.File, service, settings, null))
                : Results.NotFound());

        api.MapPost("/series/{seriesId:int}/episode/{episodeId:int}/analyze", async (
            int seriesId, int episodeId, SeriesRepository series, MediaInfoService service, MediaInfoSettings settings, CancellationToken ct) =>
            Episode(series, seriesId, episodeId) is ({ } show, { } episode)
                ? Results.Ok(await AnalyzeAsync(show.FolderPath, episode.File, service, settings, ct))
                : Results.NotFound());

        // All episode files of a series, as a background job (a season pack can take a while on a network share).
        api.MapPost("/series/{id:int}/analyze", (int id, SeriesRepository series, JobQueue jobs) =>
        {
            if (series.Find(id) is not { } show)
            {
                return Results.NotFound();
            }

            jobs.Enqueue(MediaInfoScanHandler.SeriesJob(show.Id, show.Title, force: true));
            return Results.Accepted();
        });

        var settingsGroup = app.MapGroup("/api/v1/settings/mediainfo").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);
        settingsGroup.MapGet("/", (MediaInfoSettings settings, MediaInfoService service) => MediaInfoSettingsResource.From(settings, service));
        settingsGroup.MapPut("/", (MediaInfoSettingsResource request, MediaInfoSettings settings, MediaInfoService service) =>
        {
            if (request.RefreshIntervalDays is < 0 or > MediaInfoSettings.MaxRefreshIntervalDays)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["refreshIntervalDays"] = [$"Enter 0 (off) to {MediaInfoSettings.MaxRefreshIntervalDays} days."],
                });
            }

            settings.Update(request.Analyze, request.RefreshIntervalDays);
            return Results.Ok(MediaInfoSettingsResource.From(settings, service));
        });

        return app;
    }

    private static (Series? Show, Episode? Episode) Episode(SeriesRepository series, int seriesId, int episodeId)
    {
        if (series.Find(seriesId) is not { } show)
        {
            return (null, null);
        }

        return (show, series.Episodes(seriesId).FirstOrDefault(e => e.Id == episodeId));
    }

    private static MediaInfoResource Resource(string folder, MediaFile? file, MediaInfoService service, MediaInfoSettings settings, string? problem)
    {
        if (file is null)
        {
            return new MediaInfoResource(null, false, null, problem, service.FfprobeFound, settings.Analyze, null);
        }

        var path = Path.Combine(folder, file.RelativePath);
        var stored = service.Stored(path);
        return new MediaInfoResource(path, true, stored?.MediaInfoUpdated, problem, service.FfprobeFound, settings.Analyze, MediaInfoView.From(stored?.MediaInfo));
    }

    private static async Task<MediaInfoResource> AnalyzeAsync(
        string folder, MediaFile? file, MediaInfoService service, MediaInfoSettings settings, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return Resource(folder, null, service, settings, "There is no file to analyse.");
        }

        var result = await service.AnalyzeAsync(Path.Combine(folder, file.RelativePath), folder, force: true, cancellationToken);
        return Resource(folder, file, service, settings, result.Problem);
    }
}

/// <param name="Path">Full path of the file; null when the item has no file.</param>
/// <param name="Updated">When the file was analysed; null when it was not.</param>
/// <param name="Problem">Why the last analysis did not work (only in the answer to an analysis).</param>
/// <param name="MediaInfo">Null when the file was not analysed yet.</param>
public sealed record MediaInfoResource(
    string? Path,
    bool HasFile,
    DateTime? Updated,
    string? Problem,
    bool FfprobeFound,
    bool AnalyzeEnabled,
    MediaInfoView? MediaInfo);

/// <summary>The analysis as the detail pages show it, with the formatted values Radarr's tokens use.</summary>
public sealed record MediaInfoView(
    string? Container,
    double? RuntimeSeconds,
    long? OverallBitrate,
    MediaVideoView? Video,
    IReadOnlyList<MediaAudioView> AudioTracks,
    IReadOnlyList<MediaSubtitleView> Subtitles)
{
    public static MediaInfoView? From(MediaInfo? info) => info is null
        ? null
        : new MediaInfoView(
            info.Container,
            info.RuntimeSeconds,
            info.OverallBitrate,
            info.Video is { } v
                ? new MediaVideoView(v.CodecName, v.Codec, v.Profile, v.BitDepth, v.Width, v.Height, v.Resolution, v.FrameRate, v.ScanType, v.Hdr.ToString(),
                    v.DynamicRangeType, v.Is3D, v.Bitrate)
                : null,
            [.. info.AudioTracks.Select(a => new MediaAudioView(
                a.Codec, a.CodecName, a.Profile, a.Channels, a.ChannelsText, LanguageName(a.LanguageId, a.LanguageCode), a.LanguageCode, a.Title, a.Default, a.Commentary, a.Bitrate))],
            [.. info.Subtitles.Select(s => new MediaSubtitleView(
                s.Format, LanguageName(s.LanguageId, s.LanguageCode), s.LanguageCode, s.Title, s.Default, s.Forced, s.HearingImpaired))]);

    private static string? LanguageName(int id, string? code) =>
        Language.FindById(id) is { Id: > 0 } language ? language.Name : code?.ToUpperInvariant();
}

/// <param name="Hdr">None, Pq, Hdr10, Hdr10Plus, Hlg, DolbyVision, DolbyVisionHdr10, DolbyVisionHdr10Plus, DolbyVisionHlg, DolbyVisionSdr.</param>
/// <param name="DynamicRangeType">As in file names: DV HDR10, HDR10Plus, HLG …; empty for SDR.</param>
public sealed record MediaVideoView(
    string CodecName,
    string Codec,
    string? Profile,
    int BitDepth,
    int Width,
    int Height,
    int Resolution,
    double? FrameRate,
    string? ScanType,
    string Hdr,
    string DynamicRangeType,
    bool Is3D,
    long? Bitrate);

/// <param name="Language">Language name ("German"), the raw tag in upper case when StreamZH does not know it, null when unknown.</param>
public sealed record MediaAudioView(
    string Codec,
    string CodecName,
    string? Profile,
    decimal Channels,
    string ChannelsText,
    string? Language,
    string? LanguageCode,
    string? Title,
    bool Default,
    bool Commentary,
    long? Bitrate);

public sealed record MediaSubtitleView(
    string Format,
    string? Language,
    string? LanguageCode,
    string? Title,
    bool Default,
    bool Forced,
    bool HearingImpaired);

/// <param name="Analyze">Analyse video files with ffprobe (import, library scans, the weekly refresh).</param>
/// <param name="RefreshIntervalDays">Days between runs of "Refresh media info"; 0 = off.</param>
/// <param name="FfprobeFound">Read only.</param>
public sealed record MediaInfoSettingsResource(bool Analyze, int RefreshIntervalDays, bool FfprobeFound = false)
{
    public static MediaInfoSettingsResource From(MediaInfoSettings settings, MediaInfoService service) =>
        new(settings.Analyze, settings.RefreshIntervalDays, service.FfprobeFound);
}
