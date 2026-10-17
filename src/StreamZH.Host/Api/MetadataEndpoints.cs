// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Settings;
using StreamZH.Library;
using StreamZH.Library.Metadata;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Host.Api;

/// <summary>
/// Settings → Metadata: TMDB token and language, certification country, NFO options and the image cache; the Kodi
/// (XBMC) / Emby metadata writer with "write for the whole library"; and the poster/backdrop route the web UI uses
/// (cached in &lt;data&gt;/MediaCover when the cache is on, otherwise a redirect to the TMDB image CDN).
/// </summary>
public static class MetadataEndpoints
{
    /// <summary>Browsers keep a poster a week; TMDB file names change when the image changes.</summary>
    public static readonly TimeSpan ImageMaxAge = TimeSpan.FromDays(7);

    public static IEndpointRouteBuilder MapMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/v1/settings/metadata").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);

        settings.MapGet("/", (ConfigRepository config, MetadataConfig metadata) => MetadataSettings.From(config, metadata));

        settings.MapPut("/", (UpdateMetadataSettings request, ConfigRepository config, MetadataConfig metadata, JobQueue jobs, MovieRepository movies, SeriesRepository series) =>
        {
            var language = request.Language ?? config.Get(ConfigKeys.TmdbLanguage) ?? TmdbSettings.DefaultLanguage;
            if (!TmdbSettings.Languages.Contains(language))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["language"] = [$"Use one of: {string.Join(", ", TmdbSettings.Languages)}."] });
            }

            metadata.Update(request.CertificationCountry, request.NfoOriginalTitle, request.ImageCache);

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

            return Results.Ok(MetadataSettings.From(config, metadata));
        });

        settings.MapPost("/test", async (TestMetadataRequest request, ITmdbClient tmdb, CancellationToken ct) =>
        {
            await tmdb.TestAsync(string.IsNullOrWhiteSpace(request.TmdbToken) ? null : request.TmdbToken.Trim(), ct);
            return Results.Ok(new { ok = true });
        });

        settings.MapGet("/kodi", (MetadataConfig metadata) => metadata.Kodi);

        settings.MapPut("/kodi", (KodiMetadataOptions request, MetadataConfig metadata, ILogger<MetadataConfig> logger) =>
        {
            metadata.UpdateKodi(request);
            logger.LogInformation("Kodi metadata {State}", request.Enabled ? "on" : "off");
            return metadata.Kodi;
        });

        app.MapPost("/api/v1/metadata/write", (MetadataConfig metadata, JobQueue jobs) =>
        {
            if (!metadata.Kodi.Enabled)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["enabled"] = ["Turn on Kodi (XBMC) / Emby metadata first."] });
            }

            jobs.Enqueue(MetadataEventHandler.LibraryJob());
            return Results.Accepted(value: jobs.Status);
        }).WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);

        // Every signed-in user sees posters. Path: /api/v1/mediacover/w342/abc.jpg (TMDB size and file name).
        app.MapGet("/api/v1/mediacover/{size}/{file}", async (string size, string file, MetadataConfig metadata, MetadataImageStore store, HttpContext http, CancellationToken ct) =>
        {
            var path = "/" + file;
            if (!TmdbImages.Sizes.Contains(size) || !TmdbImages.IsValidPath(path))
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = $"private, max-age={(int)ImageMaxAge.TotalSeconds}";
            if (!metadata.ImageCache)
            {
                return Results.Redirect(TmdbImages.Url(path, size)!);
            }

            try
            {
                var cached = await store.GetCachedAsync(size, path, ct);
                return Results.File(cached, ContentType(path), enableRangeProcessing: false);
            }
            catch (TmdbException ex) when (ex.StatusCode == 404)
            {
                return Results.NotFound();
            }
            catch (TmdbException)
            {
                // TMDB not reachable: the browser may still get the image directly.
                http.Response.Headers.CacheControl = "no-store";
                return Results.Redirect(TmdbImages.Url(path, size)!);
            }
        }).WithTags("Library").RequireAuthorization();

        return app;
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        _ => "image/jpeg",
    };
}

/// <summary>
/// Metadata settings: TMDB (token hint, language), the certification country for {Movie Certification} and NFO files,
/// the original title in NFO files, and the local image cache.
/// </summary>
public sealed record MetadataSettings(
    bool TmdbConfigured,
    string? TmdbTokenHint,
    string Language,
    IReadOnlyList<string> Languages,
    string CertificationCountry = MetadataConfig.DefaultCertificationCountry,
    IReadOnlyList<string>? CertificationCountries = null,
    bool NfoOriginalTitle = false,
    bool ImageCache = false)
{
    public static MetadataSettings From(ConfigRepository config, MetadataConfig metadata)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(metadata);
        var token = config.Get(ConfigKeys.TmdbToken);
        var hint = string.IsNullOrEmpty(token) ? null : $"…{token[^Math.Min(4, token.Length)..]}";
        return new MetadataSettings(
            !string.IsNullOrEmpty(token),
            hint,
            config.Get(ConfigKeys.TmdbLanguage) ?? TmdbSettings.DefaultLanguage,
            TmdbSettings.Languages,
            metadata.CertificationCountry,
            MetadataConfig.CertificationCountries,
            metadata.NfoOriginalTitle,
            metadata.ImageCache);
    }
}

/// <summary>Null fields keep their value; an empty token removes it.</summary>
public sealed record UpdateMetadataSettings(
    string? TmdbToken,
    string? Language,
    string? CertificationCountry = null,
    bool? NfoOriginalTitle = null,
    bool? ImageCache = null);

public sealed record TestMetadataRequest(string? TmdbToken);
