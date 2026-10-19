// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>
/// Reads the library of Radarr or Sonarr once per preview or run: the movie and series lists, root folders and quality
/// profiles are cached on the <see cref="MigrationContext"/>, so the movies, history and blocklist sections of one run
/// share them. GET only.
/// </summary>
public sealed class ArrLibraryReader
{
    /// <summary>Records per page for paged lists (history, blocklist).</summary>
    public int PageSize { get; init; } = 250;

    /// <summary>Stops a paged read that never ends (an app that ignores the page parameter).</summary>
    public const int MaxPages = 2000;

    private static readonly ConditionalWeakTable<MigrationContext, Cache> Caches = new();

    /// <summary>TMDB ids found for TVDB ids (TMDB's /find), kept for the app's lifetime: preview and run ask once.</summary>
    public ConcurrentDictionary<int, int?> TmdbIdsByTvdbId { get; } = new();

    /// <summary>TMDB ids found for IMDb ids of series.</summary>
    public ConcurrentDictionary<string, int?> TmdbIdsByImdbId { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ArrMovie>> MoviesAsync(MigrationContext context, CancellationToken cancellationToken) =>
        Get(context).Lazy("movies", () => ListAsync<ArrMovie>(context, "/movie", cancellationToken));

    public Task<IReadOnlyList<ArrSeries>> SeriesAsync(MigrationContext context, CancellationToken cancellationToken) =>
        Get(context).Lazy("series", () => ListAsync<ArrSeries>(context, "/series", cancellationToken));

    public Task<IReadOnlyList<ArrRootFolder>> RootFoldersAsync(MigrationContext context, CancellationToken cancellationToken) =>
        Get(context).Lazy("rootFolders", () => ListAsync<ArrRootFolder>(context, "/rootfolder", cancellationToken));

    public Task<IReadOnlyList<ArrQualityProfile>> QualityProfilesAsync(MigrationContext context, CancellationToken cancellationToken) =>
        Get(context).Lazy("qualityProfiles", () => ListAsync<ArrQualityProfile>(context, "/qualityprofile", cancellationToken));

    public Task<IReadOnlyList<ArrEpisode>> EpisodesAsync(MigrationContext context, int seriesId, CancellationToken cancellationToken) =>
        Get(context).Lazy($"episodes:{seriesId}", () => ListAsync<ArrEpisode>(context, $"/episode?seriesId={seriesId.ToString(CultureInfo.InvariantCulture)}", cancellationToken));

    public Task<IReadOnlyList<ArrEpisodeFile>> EpisodeFilesAsync(MigrationContext context, int seriesId, CancellationToken cancellationToken) =>
        Get(context).Lazy($"episodeFiles:{seriesId}", () => ListAsync<ArrEpisodeFile>(context, $"/episodefile?seriesId={seriesId.ToString(CultureInfo.InvariantCulture)}", cancellationToken));

    /// <summary>
    /// Radarr's movie files by movie id: inline in /movie, and for movies whose file is not inline, read in chunks from
    /// /moviefile?movieFileIds=… (older versions).
    /// </summary>
    public Task<IReadOnlyDictionary<int, ArrMovieFile>> MovieFilesAsync(MigrationContext context, CancellationToken cancellationToken) =>
        Get(context).Lazy<IReadOnlyDictionary<int, ArrMovieFile>>("movieFiles", async () =>
        {
            var movies = await MoviesAsync(context, cancellationToken);
            var files = new Dictionary<int, ArrMovieFile>();
            foreach (var movie in movies.Where(m => m.MovieFile is not null))
            {
                files[movie.Id] = movie.MovieFile!;
            }

            var missing = movies.Where(m => m.MovieFile is null && m.HasFile && m.MovieFileId > 0).ToList();
            foreach (var chunk in missing.Chunk(100))
            {
                var query = string.Join('&', chunk.Select(m => "movieFileIds=" + m.MovieFileId.ToString(CultureInfo.InvariantCulture)));
                foreach (var file in await ListAsync<ArrMovieFile>(context, "/moviefile?" + query, cancellationToken))
                {
                    if (chunk.FirstOrDefault(m => m.MovieFileId == file.Id) is { } owner)
                    {
                        files[owner.Id] = file;
                    }
                }
            }

            return files;
        });

    /// <summary>
    /// Pages through a paged list newest first (sortKey=date, descending) until <paramref name="until"/> says stop for
    /// a record (older than wanted) or the list ends.
    /// </summary>
    public async Task<IReadOnlyList<T>> PagedAsync<T>(
        MigrationContext context, string path, Func<T, bool> until, Action<int, int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var all = new List<T>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var result = await Client(context).GetAsync<ArrPage<T>>(
                context.Source,
                context.Connection,
                $"{path}{separator}page={page.ToString(CultureInfo.InvariantCulture)}&pageSize={PageSize.ToString(CultureInfo.InvariantCulture)}&sortKey=date&sortDirection=descending",
                cancellationToken);
            var records = result.Records ?? [];
            foreach (var record in records)
            {
                if (until(record))
                {
                    return all;
                }

                all.Add(record);
            }

            progress?.Invoke(all.Count, result.TotalRecords);
            if (records.Count == 0 || page * Math.Max(1, result.PageSize) >= result.TotalRecords)
            {
                break;
            }
        }

        return all;
    }

    /// <summary>A value computed once per preview or run (e.g. TMDB ids found for Sonarr series).</summary>
    public static Cache Get(MigrationContext context) => Caches.GetValue(context, _ => new Cache());

    private static async Task<IReadOnlyList<T>> ListAsync<T>(MigrationContext context, string path, CancellationToken cancellationToken) =>
        await Client(context).GetAsync<List<T>>(context.Source, context.Connection, path, cancellationToken);

    /// <summary>The API client from the host's services (a typed HTTP client, so it is fetched per call, not kept).</summary>
    private static ArrApiClient Client(MigrationContext context) => context.Services.GetRequiredService<ArrApiClient>();

    public sealed class Cache
    {
        private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);

        public Task<T> Lazy<T>(string key, Func<Task<T>> factory) => (Task<T>)_values.GetOrAdd(key, _ => factory());

        public T Value<T>(string key, Func<T> factory)
            where T : class => (T)_values.GetOrAdd(key, _ => factory());
    }
}
