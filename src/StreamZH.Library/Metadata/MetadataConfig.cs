// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core;
using StreamZH.Data;

namespace StreamZH.Library.Metadata;

/// <summary>What the Kodi (XBMC) / Emby metadata writer creates. Defaults are Radarr's and Sonarr's.</summary>
/// <param name="Enabled">Master switch; off (default) writes nothing, as before 0.6.</param>
/// <param name="MovieMetadata">movie.nfo or &lt;video name&gt;.nfo with title, plot, ratings, cast …</param>
/// <param name="MovieMetadataUrl">Adds the TMDB and IMDb links to the movie NFO; alone, the NFO has only the links.</param>
/// <param name="MovieImages">poster.jpg and fanart.jpg in the movie folder.</param>
/// <param name="UseMovieNfo">Name the NFO movie.nfo instead of after the video file.</param>
/// <param name="MovieCollection">Write the TMDB collection as &lt;set&gt;.</param>
public sealed record KodiMetadataOptions(
    bool Enabled = false,
    bool MovieMetadata = true,
    bool MovieMetadataUrl = false,
    bool MovieImages = true,
    bool UseMovieNfo = false,
    bool MovieCollection = true,
    bool SeriesMetadata = true,
    bool SeriesMetadataUrl = false,
    bool EpisodeMetadata = true,
    bool SeriesImages = true,
    bool SeasonImages = true,
    bool EpisodeImages = true);

/// <summary>
/// Metadata settings besides the TMDB token and language: the certification country, the original title in NFO files,
/// the local image cache and the Kodi metadata writer.
/// </summary>
public sealed class MetadataConfig(ConfigRepository config, AppPaths paths)
{
    public const string DefaultCertificationCountry = "US";

    /// <summary>Countries TMDB has certifications for that StreamZH offers (ISO 3166-1).</summary>
    public static readonly IReadOnlyList<string> CertificationCountries =
        ["US", "GB", "DE", "AT", "CH", "FR", "IT", "ES", "NL", "BE", "DK", "SE", "NO", "FI", "PT", "BR", "CA", "AU", "NZ", "IE", "JP", "KR"];

    /// <summary>Country whose age rating is used for {Movie Certification} and the &lt;mpaa&gt; tag of NFO files.</summary>
    public string CertificationCountry =>
        config.Get(ConfigKeys.MetadataCertificationCountry) is { Length: 2 } country && CertificationCountries.Contains(country)
            ? country
            : DefaultCertificationCountry;

    /// <summary>NFO &lt;title&gt; is the original title (e.g. "Le Fabuleux Destin d'Amélie Poulain") instead of the translated one.</summary>
    public bool NfoOriginalTitle => Flag(ConfigKeys.MetadataNfoOriginalTitle, false);

    /// <summary>Posters and backdrops go through StreamZH and are kept in &lt;data&gt;/MediaCover, so browsers never contact TMDB.</summary>
    public bool ImageCache => Flag(ConfigKeys.MetadataImageCache, false);

    public string ImageCacheDirectory => Path.Combine(paths.DataDirectory, "MediaCover");

    public KodiMetadataOptions Kodi => new(
        Flag(ConfigKeys.KodiMetadataEnabled, false),
        Flag(ConfigKeys.KodiMovieMetadata, true),
        Flag(ConfigKeys.KodiMovieMetadataUrl, false),
        Flag(ConfigKeys.KodiMovieImages, true),
        Flag(ConfigKeys.KodiUseMovieNfo, false),
        Flag(ConfigKeys.KodiMovieCollection, true),
        Flag(ConfigKeys.KodiSeriesMetadata, true),
        Flag(ConfigKeys.KodiSeriesMetadataUrl, false),
        Flag(ConfigKeys.KodiEpisodeMetadata, true),
        Flag(ConfigKeys.KodiSeriesImages, true),
        Flag(ConfigKeys.KodiSeasonImages, true),
        Flag(ConfigKeys.KodiEpisodeImages, true));

    /// <summary>Saves the general options; null keeps the value.</summary>
    /// <exception cref="ValidationFailedException">Unknown certification country (key certificationCountry).</exception>
    public void Update(string? certificationCountry, bool? nfoOriginalTitle, bool? imageCache)
    {
        string? country = null;
        if (certificationCountry is not null)
        {
            country = certificationCountry.Trim().ToUpperInvariant();
            if (!CertificationCountries.Contains(country))
            {
                throw new ValidationFailedException(new Dictionary<string, string[]>
                {
                    ["certificationCountry"] = [$"Use one of: {string.Join(", ", CertificationCountries)}."],
                });
            }
        }

        if (country is not null)
        {
            config.Set(ConfigKeys.MetadataCertificationCountry, country);
        }

        SetFlag(ConfigKeys.MetadataNfoOriginalTitle, nfoOriginalTitle);
        SetFlag(ConfigKeys.MetadataImageCache, imageCache);
    }

    public void UpdateKodi(KodiMetadataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SetFlag(ConfigKeys.KodiMetadataEnabled, options.Enabled);
        SetFlag(ConfigKeys.KodiMovieMetadata, options.MovieMetadata);
        SetFlag(ConfigKeys.KodiMovieMetadataUrl, options.MovieMetadataUrl);
        SetFlag(ConfigKeys.KodiMovieImages, options.MovieImages);
        SetFlag(ConfigKeys.KodiUseMovieNfo, options.UseMovieNfo);
        SetFlag(ConfigKeys.KodiMovieCollection, options.MovieCollection);
        SetFlag(ConfigKeys.KodiSeriesMetadata, options.SeriesMetadata);
        SetFlag(ConfigKeys.KodiSeriesMetadataUrl, options.SeriesMetadataUrl);
        SetFlag(ConfigKeys.KodiEpisodeMetadata, options.EpisodeMetadata);
        SetFlag(ConfigKeys.KodiSeriesImages, options.SeriesImages);
        SetFlag(ConfigKeys.KodiSeasonImages, options.SeasonImages);
        SetFlag(ConfigKeys.KodiEpisodeImages, options.EpisodeImages);
    }

    private bool Flag(string key, bool fallback) => config.Get(key) switch
    {
        "true" => true,
        "false" => false,
        _ => fallback,
    };

    private void SetFlag(string key, bool? value)
    {
        if (value is { } v)
        {
            config.Set(key, v ? "true" : "false");
        }
    }
}
