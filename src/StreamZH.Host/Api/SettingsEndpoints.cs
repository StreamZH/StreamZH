// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Settings;
using StreamZH.Library.Search;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Host.Api;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapGet("/metadata", (ConfigRepository config) => MetadataSettings.From(config));

        group.MapPut("/metadata", (UpdateMetadataSettings request, ConfigRepository config, JobQueue jobs, MovieRepository movies, SeriesRepository series) =>
        {
            var language = request.Language ?? TmdbSettings.DefaultLanguage;
            if (!TmdbSettings.Languages.Contains(language))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["language"] = [$"Use one of: {string.Join(", ", TmdbSettings.Languages)}."] });
            }

            if (request.TmdbToken is { } token)
            {
                config.Set(ConfigKeys.TmdbToken, token.Trim());
            }

            var before = TmdbSettings.ForTmdb(config.Get(ConfigKeys.TmdbLanguage) ?? TmdbSettings.DefaultLanguage);
            config.Set(ConfigKeys.TmdbLanguage, language);

            // New language: fetch titles and descriptions again for the whole library.
            if (before != TmdbSettings.ForTmdb(language))
            {
                LibraryEndpoints.EnqueueRefreshAll(jobs, movies, series);
            }

            return Results.Ok(MetadataSettings.From(config));
        });

        group.MapPost("/metadata/test", async (TestMetadataRequest request, ITmdbClient tmdb, CancellationToken ct) =>
        {
            await tmdb.TestAsync(string.IsNullOrWhiteSpace(request.TmdbToken) ? null : request.TmdbToken.Trim(), ct);
            return Results.Ok(new { ok = true });
        });

        group.MapGet("/downloads", (DownloadSettings downloads) => DownloadSettingsResource.From(downloads));

        group.MapPut("/downloads", (UpdateDownloadSettings request, DownloadSettings downloads, ILogger<DownloadSettings> logger) =>
        {
            downloads.Update(
                request.Client,
                request.NzbFolder,
                request.IncompleteFolder,
                request.CompleteFolder,
                request.SpeedLimitKBps,
                request.MinFreeSpaceGb,
                request.KeepFiles,
                request.AbortHopeless,
                request.ArticleTries);
            logger.LogInformation(
                "Download settings saved: {Client}, incomplete {Incomplete}, complete {Complete}, NZB folder {NzbFolder}, limit {Limit} KB/s, "
                + "min. free {MinFree} GB, keep files {KeepFiles}, abort hopeless {AbortHopeless}, {Tries} tries per article",
                DownloadSettings.ClientValue(downloads.Client),
                downloads.IncompleteFolder,
                downloads.CompleteFolder,
                downloads.NzbFolder,
                downloads.SpeedLimitKBps,
                downloads.MinFreeSpaceGb,
                downloads.KeepFiles,
                downloads.AbortHopeless,
                downloads.ArticleTries);
            return Results.Ok(DownloadSettingsResource.From(downloads));
        });

        return app;
    }
}

public sealed record MetadataSettings(bool TmdbConfigured, string? TmdbTokenHint, string Language, IReadOnlyList<string> Languages)
{
    public static MetadataSettings From(ConfigRepository config)
    {
        var token = config.Get(ConfigKeys.TmdbToken);
        var hint = string.IsNullOrEmpty(token) ? null : $"…{token[^Math.Min(4, token.Length)..]}";
        return new MetadataSettings(!string.IsNullOrEmpty(token), hint, config.Get(ConfigKeys.TmdbLanguage) ?? TmdbSettings.DefaultLanguage, TmdbSettings.Languages);
    }
}

public sealed record UpdateMetadataSettings(string? TmdbToken, string? Language);

/// <summary>
/// Download settings. <see cref="Client"/> is "builtin" or "blackhole"; the folders come with their defaults and a
/// problem text when StreamZH cannot write there. <see cref="SpeedLimitKBps"/> 0 = unlimited. The last four are the
/// engine's expert settings: pause below <see cref="MinFreeSpaceGb"/> free (0 = never), keep the download folder after
/// a successful unpack, fail a download as soon as it cannot be repaired, and tries per server and article.
/// </summary>
public sealed record DownloadSettingsResource(
    string NzbFolder,
    string DefaultNzbFolder,
    bool IsDefault,
    string? Problem,
    string Client,
    string IncompleteFolder,
    string DefaultIncompleteFolder,
    string? IncompleteProblem,
    string CompleteFolder,
    string DefaultCompleteFolder,
    string? CompleteProblem,
    long SpeedLimitKBps,
    double MinFreeSpaceGb,
    bool KeepFiles,
    bool AbortHopeless,
    int ArticleTries)
{
    public static DownloadSettingsResource From(DownloadSettings downloads)
    {
        var folder = downloads.NzbFolder;
        var incomplete = downloads.IncompleteFolder;
        var complete = downloads.CompleteFolder;
        return new DownloadSettingsResource(
            folder,
            downloads.DefaultNzbFolder,
            folder == downloads.DefaultNzbFolder,
            DownloadSettings.CheckWritable(folder),
            DownloadSettings.ClientValue(downloads.Client),
            incomplete,
            downloads.DefaultIncompleteFolder,
            DownloadSettings.CheckWritable(incomplete),
            complete,
            downloads.DefaultCompleteFolder,
            DownloadSettings.CheckWritable(complete),
            downloads.SpeedLimitKBps,
            downloads.MinFreeSpaceGb,
            downloads.KeepFiles,
            downloads.AbortHopeless,
            downloads.ArticleTries);
    }
}

/// <summary>Null fields keep their value; an empty folder means the default one.</summary>
public sealed record UpdateDownloadSettings(
    string? NzbFolder,
    string? Client = null,
    string? IncompleteFolder = null,
    string? CompleteFolder = null,
    long? SpeedLimitKBps = null,
    double? MinFreeSpaceGb = null,
    bool? KeepFiles = null,
    bool? AbortHopeless = null,
    int? ArticleTries = null);

public sealed record TestMetadataRequest(string? TmdbToken);
