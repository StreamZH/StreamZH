// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace StreamZH.Metadata.Tmdb;

/// <summary>
/// Finds the TMDB id of a show Sonarr knows by its TVDB or IMDb id (TMDB's /find), for taking over a Sonarr library
/// (Sonarr is TVDB-based, StreamZH uses TMDB). One request per lookup.
/// </summary>
public interface ITmdbFindClient
{
    bool IsConfigured { get; }

    /// <summary>The TMDB show of a TVDB id, or null when TMDB knows none.</summary>
    Task<TmdbListEntry?> FindByTvdbIdAsync(int tvdbId, CancellationToken cancellationToken = default);

    /// <summary>The TMDB movie or show (<paramref name="kind"/>) of an IMDb id (tt…), or null when TMDB knows none.</summary>
    Task<TmdbListEntry?> FindByImdbIdAsync(string imdbId, TmdbMediaKind kind, CancellationToken cancellationToken = default);
}

public sealed partial class TmdbClient : ITmdbFindClient
{
    public async Task<TmdbListEntry?> FindByTvdbIdAsync(int tvdbId, CancellationToken cancellationToken = default)
    {
        if (tvdbId <= 0)
        {
            return null;
        }

        var found = await FindAsync(tvdbId.ToString(CultureInfo.InvariantCulture), TmdbExternalSource.Tvdb, cancellationToken);
        return found.Series.FirstOrDefault();
    }

    public async Task<TmdbListEntry?> FindByImdbIdAsync(string imdbId, TmdbMediaKind kind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || !imdbId.Trim().StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var found = await FindAsync(imdbId.Trim(), TmdbExternalSource.Imdb, cancellationToken);
        return (kind == TmdbMediaKind.Movie ? found.Movies : found.Series).FirstOrDefault();
    }
}
