// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Notifications;
using StreamZH.Library.Search;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.ImportLists;

/// <summary>A list item matched to TMDB.</summary>
public sealed record ImportListCandidate(MediaType MediaType, int TmdbId, string Title, int Year, string? ReleaseDate);

/// <summary>What a list returned, matched to TMDB ids. <see cref="Unmatched"/> are items TMDB does not know.</summary>
public sealed record ImportListResolved(IReadOnlyList<ImportListCandidate> Items, IReadOnlyList<ImportListItem> Unmatched, string? Name);

/// <summary>Preview states, in the order the preview sorts them.</summary>
public static class ImportListItemStates
{
    /// <summary>Would be added at the next sync (or by "Add").</summary>
    public const string New = "new";

    /// <summary>Not available yet (minimum availability of the list); added once it is.</summary>
    public const string Waiting = "waiting";
    public const string InLibrary = "inLibrary";
    public const string Excluded = "excluded";

    /// <summary>TMDB does not know the id the list gave.</summary>
    public const string NotFound = "notFound";
}

/// <param name="TmdbId">Null for items TMDB does not know.</param>
/// <param name="AvailableFrom">Waiting items: the day they count as available (yyyy-MM-dd), null when unknown.</param>
public sealed record ImportListPreviewItem(string State, MediaType MediaType, int? TmdbId, string Title, int Year, string? ImdbId, string? AvailableFrom);

public sealed record ImportListPreview(string? Name, IReadOnlyList<ImportListPreviewItem> Items);

/// <summary>Result of one sync run (all lists or one).</summary>
/// <param name="Cleaned">What "Clean library" did (or would do in log-only mode); null when it did not run.</param>
public sealed record ImportListSyncSummary(int Lists, int Found, int Added, int Failed, IReadOnlyList<string> AddedTitles, int? Cleaned, string? CleanSkipped);

/// <summary>
/// Reads the import lists, matches their items to TMDB, adds new ones through the library (folder format, empty folders
/// and search on add as everywhere else) and runs "Clean library". Like Radarr's ImportListSyncService, but cleaning
/// only touches items a list added and is skipped when any list failed or suddenly came back empty.
/// </summary>
public sealed class ImportListSyncService(
    ImportListRepository repository,
    IEnumerable<IImportListSource> sources,
    ITmdbListClient tmdbLists,
    ITmdbClient tmdb,
    LibraryService library,
    MovieRepository movies,
    SeriesRepository series,
    ImportListOptions options,
    JobQueue jobs,
    IEventBus events,
    TimeProvider time,
    ILogger<ImportListSyncService> logger)
{
    /// <summary>Titles kept per list for the "Added …" line of its card.</summary>
    public const int MaxAddedTitles = 10;

    private readonly Dictionary<string, IImportListSource> _sources = sources
        .SelectMany(s => s.Implementations.Select(i => (Key: i, Source: s)))
        .ToDictionary(x => x.Key, x => x.Source, StringComparer.OrdinalIgnoreCase);

    // Lookups of one run: external id → TMDB entry, movie → collection.
    private readonly Dictionary<string, ImportListCandidate?> _found = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (int Id, string Name)?> _collections = [];
    private readonly Dictionary<int, IReadOnlyList<TmdbListEntry>> _collectionParts = [];

    public IImportListSource? Source(string implementation) => _sources.GetValueOrDefault(implementation);

    /// <summary>Reads the list and matches every item to a TMDB id (IMDb and TVDB ids through TMDB's find).</summary>
    /// <exception cref="ImportListException">The list could not be read.</exception>
    public async Task<ImportListResolved> FetchAsync(ImportListDefinition list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);
        var source = Source(list.Implementation) ?? throw new ImportListException("this type is not supported");
        var fetch = await source.FetchAsync(new ImportListContext(list, new ConnectionSettings(list.Settings), time.GetUtcNow()), cancellationToken);

        var items = new List<ImportListCandidate>();
        var unmatched = new List<ImportListItem>();
        foreach (var item in fetch.Items.Where(i => i.MediaType == list.MediaType))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = item.TmdbId is { } id
                ? new ImportListCandidate(item.MediaType, id, item.Title, item.Year, item.ReleaseDate)
                : await FindAsync(item, cancellationToken);
            if (candidate is null)
            {
                unmatched.Add(item);
            }
            else
            {
                items.Add(candidate);
            }
        }

        return new ImportListResolved([.. items.DistinctBy(i => i.TmdbId)], unmatched, fetch.Name);
    }

    /// <summary>What the list would add, what it skips and why (nothing is changed).</summary>
    /// <exception cref="ImportListException">The list could not be read.</exception>
    public async Task<ImportListPreview> PreviewAsync(ImportListDefinition list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);
        var resolved = await FetchAsync(list, cancellationToken);
        var inLibrary = InLibrary(list.MediaType);
        var excluded = repository.ExcludedTmdbIds(list.MediaType);
        var items = new List<ImportListPreviewItem>();
        foreach (var c in resolved.Items)
        {
            string state;
            string? from = null;
            if (inLibrary.Contains(c.TmdbId))
            {
                state = ImportListItemStates.InLibrary;
            }
            else if (excluded.Contains(c.TmdbId))
            {
                state = ImportListItemStates.Excluded;
            }
            else if (await AvailableFromAsync(list, c, cancellationToken) is { } date)
            {
                state = ImportListItemStates.Waiting;
                from = date == DateTimeOffset.MaxValue ? null : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            else
            {
                state = ImportListItemStates.New;
            }

            items.Add(new ImportListPreviewItem(state, c.MediaType, c.TmdbId, c.Title, c.Year, null, from));
        }

        items.AddRange(resolved.Unmatched.Select(u => new ImportListPreviewItem(ImportListItemStates.NotFound, u.MediaType, null, u.Title, u.Year, u.ImdbId, null)));
        return new ImportListPreview(resolved.Name, items);
    }

    /// <summary>Syncs every enabled list, then cleans the library (when switched on). The scheduled "Import list sync".</summary>
    public Task<ImportListSyncSummary> SyncAllAsync(CancellationToken cancellationToken) =>
        RunAsync([.. repository.All().Where(l => l.Enabled)], clean: true, cancellationToken);

    /// <summary>Syncs one list ("Sync now" on a list); the library is not cleaned.</summary>
    /// <exception cref="LibraryException">No list with this id.</exception>
    public Task<ImportListSyncSummary> SyncAsync(int listId, CancellationToken cancellationToken)
    {
        var list = repository.Find(listId) ?? throw new LibraryException($"Import list {listId} not found.", LibraryError.NotFound);
        return RunAsync([list], clean: false, cancellationToken);
    }

    /// <summary>
    /// Adds one item of the list now ("Add" in the preview), with the list's options. Returns the "Title (Year)" of
    /// what was added (the movie plus, for "Movie and collection", the rest of its collection).
    /// </summary>
    /// <exception cref="LibraryException">Already in the library, or the root folder is gone.</exception>
    public async Task<IReadOnlyList<string>> AddAsync(ImportListDefinition list, int tmdbId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (InLibrary(list.MediaType).Contains(tmdbId))
        {
            throw new LibraryException("This is already in the library.", LibraryError.AlreadyExists);
        }

        var added = new List<string>();
        await AddItemAsync(list, tmdbId, added, InLibrary(list.MediaType).ToHashSet(), cancellationToken);
        await events.PublishAsync(new OngoingChanged(), cancellationToken);
        return added;
    }

    private async Task<ImportListSyncSummary> RunAsync(IReadOnlyList<ImportListDefinition> lists, bool clean, CancellationToken cancellationToken)
    {
        var results = new Dictionary<int, ImportListResolved>();
        var failed = new List<ImportListDefinition>();
        var addedTitles = new List<string>();
        var found = 0;
        foreach (var list in lists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var resolved = await FetchAsync(list, cancellationToken);
                results[list.Id] = resolved;
                found += resolved.Items.Count;
                var added = list.EnableAuto ? await AddNewAsync(list, resolved, cancellationToken) : [];
                addedTitles.AddRange(added);
                repository.SetSuccess(list.Id, time.GetUtcNow().UtcDateTime, resolved.Items.Count, added.Count, [.. added.Take(MaxAddedTitles)]);
                logger.LogInformation(
                    "Import list {List}: {Found} found, {Added} added{Unmatched}",
                    list.Name,
                    resolved.Items.Count,
                    added.Count,
                    resolved.Unmatched.Count > 0 ? $", {resolved.Unmatched.Count} not found on TMDB" : string.Empty);
            }
            catch (ImportListException ex)
            {
                failed.Add(list);
                repository.SetFailure(list.Id, time.GetUtcNow().UtcDateTime, ex.Message);
                logger.LogWarning("Import list {List} failed: {Error}", list.Name, ex.Message);
            }
        }

        int? cleaned = null;
        string? skipped = null;
        if (clean)
        {
            (cleaned, skipped) = await CleanAsync(lists, results, failed, cancellationToken);
        }

        if (lists.Count > 0)
        {
            logger.LogInformation(
                "Import list sync finished: {Lists} lists, {Found} found, {Added} added, {Failed} failed",
                lists.Count, found, addedTitles.Count, failed.Count);
        }

        await events.PublishAsync(new OngoingChanged(), cancellationToken);
        return new ImportListSyncSummary(lists.Count, found, addedTitles.Count, failed.Count, addedTitles, cleaned, skipped);
    }

    /// <summary>Adds the list's items that are not in the library, not excluded and available; returns their titles.</summary>
    private async Task<List<string>> AddNewAsync(ImportListDefinition list, ImportListResolved resolved, CancellationToken cancellationToken)
    {
        var known = InLibrary(list.MediaType).ToHashSet();
        var excluded = repository.ExcludedTmdbIds(list.MediaType);
        var added = new List<string>();
        foreach (var c in resolved.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (known.Contains(c.TmdbId))
            {
                // Already there: with "Movie and collection" the rest of its collection still follows.
                if (list.MediaType == MediaType.Movie && list.Monitor == ImportListMonitor.MovieAndCollection)
                {
                    await AddCollectionAsync(list, c.TmdbId, added, known, cancellationToken);
                }

                continue;
            }

            if (excluded.Contains(c.TmdbId) || await AvailableFromAsync(list, c, cancellationToken) is not null)
            {
                continue;
            }

            await AddItemAsync(list, c.TmdbId, added, known, cancellationToken);
        }

        return added;
    }

    /// <summary>Adds one movie or series with the list's options; errors of single items are logged, not thrown.</summary>
    /// <exception cref="LibraryException">The root folder of the list is gone.</exception>
    private async Task AddItemAsync(ImportListDefinition list, int tmdbId, List<string> added, HashSet<int> known, CancellationToken cancellationToken)
    {
        var monitored = list.Monitor != ImportListMonitor.None;
        try
        {
            if (list.MediaType == MediaType.Movie)
            {
                var movie = await library.AddMovieAsync(tmdbId, list.RootFolderId, monitored, list.QualityProfileId, cancellationToken: cancellationToken);
                Added(list, MediaType.Movie, tmdbId, $"{movie.Title} ({movie.Year})", added, known);
                if (list.SearchOnAdd && monitored)
                {
                    jobs.Enqueue(new Job(
                        $"search-new-movie:{movie.Id}",
                        $"Search {movie.Title}",
                        (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchNewMovieAsync(movie.Id, ct)));
                }

                if (list.Monitor == ImportListMonitor.MovieAndCollection)
                {
                    await AddCollectionAsync(list, tmdbId, added, known, cancellationToken);
                }
            }
            else
            {
                var item = await library.AddSeriesAsync(tmdbId, list.RootFolderId, monitored, list.QualityProfileId, cancellationToken: cancellationToken);
                Added(list, MediaType.Series, tmdbId, $"{item.Title} ({item.Year})", added, known);
                try
                {
                    await library.RefreshSeriesAsync(item.Id, manual: false, cancellationToken);
                    ApplySeriesMonitor(item.Id, list.Monitor);
                }
                catch (TmdbException ex)
                {
                    logger.LogWarning("Import list {List}: episodes of '{Title}' could not be loaded: {Error}", list.Name, item.Title, ex.Message);
                }

                if (list.SearchOnAdd && monitored)
                {
                    jobs.Enqueue(new Job(
                        $"search-missing-episodes:{item.Id}",
                        $"Search missing episodes of {item.Title}",
                        (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchMissingEpisodesAsync(item.Id, ct)));
                }
            }
        }
        catch (LibraryException ex) when (ex.Error == LibraryError.AlreadyExists)
        {
            known.Add(tmdbId);
        }
        catch (LibraryException ex) when (ex.Error == LibraryError.Invalid)
        {
            // The root folder was removed or is for the other media type: the whole list cannot add anything.
            throw new ImportListException($"cannot add: {ex.Message.TrimEnd('.').ToLowerInvariant()}", ex);
        }
        catch (TmdbException ex)
        {
            logger.LogWarning("Import list {List}: TMDB {Id} could not be added: {Error}", list.Name, tmdbId, ex.Message);
        }
    }

    private void Added(ImportListDefinition list, MediaType type, int tmdbId, string title, List<string> added, HashSet<int> known)
    {
        known.Add(tmdbId);
        added.Add(title);
        repository.SetOrigin(type, tmdbId, list.Id, list.Name, time.GetUtcNow().UtcDateTime);
        logger.LogInformation("Import list {List}: added {Type} '{Title}'", list.Name, type == MediaType.Movie ? "movie" : "series", title);
    }

    /// <summary>"Movie and collection": the other movies of the movie's collection (not excluded, available).</summary>
    private async Task AddCollectionAsync(ImportListDefinition list, int movieTmdbId, List<string> added, HashSet<int> known, CancellationToken cancellationToken)
    {
        try
        {
            if (!_collections.TryGetValue(movieTmdbId, out var collection))
            {
                collection = await tmdbLists.GetMovieCollectionAsync(movieTmdbId, cancellationToken);
                _collections[movieTmdbId] = collection;
            }

            if (collection is not { } c)
            {
                return;
            }

            if (!_collectionParts.TryGetValue(c.Id, out var parts))
            {
                parts = (await tmdbLists.GetCollectionAsync(c.Id, cancellationToken)).Results;
                _collectionParts[c.Id] = parts;
            }

            var excluded = repository.ExcludedTmdbIds(MediaType.Movie);
            foreach (var part in parts.Where(p => !known.Contains(p.TmdbId) && !excluded.Contains(p.TmdbId)))
            {
                var candidate = new ImportListCandidate(MediaType.Movie, part.TmdbId, part.Title, part.Year, part.ReleaseDate);
                if (await AvailableFromAsync(list, candidate, cancellationToken) is null)
                {
                    // Movie only for the parts, so a collection does not pull in further collections.
                    await AddItemAsync(list with { Monitor = ImportListMonitor.MovieOnly }, part.TmdbId, added, known, cancellationToken);
                }
            }
        }
        catch (TmdbException ex)
        {
            logger.LogWarning("Import list {List}: the collection of TMDB {Id} could not be read: {Error}", list.Name, movieTmdbId, ex.Message);
        }
    }

    /// <summary>Sonarr's monitor options, applied once after the episodes were loaded. Specials stay unmonitored.</summary>
    private void ApplySeriesMonitor(int seriesId, string monitor)
    {
        var episodes = series.Episodes(seriesId);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var seasons = episodes.Where(e => e.SeasonNumber > 0).Select(e => e.SeasonNumber).Distinct().ToList();
        var first = seasons.Count > 0 ? seasons.Min() : 1;
        var latest = seasons.Count > 0 ? seasons.Max() : 1;
        bool Aired(Episode e) => DateOnly.TryParseExact(e.AirDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d <= today;
        bool Want(Episode e) => e.SeasonNumber > 0 && monitor switch
        {
            ImportListMonitor.All => true,
            ImportListMonitor.Future => !Aired(e),
            ImportListMonitor.Missing => e.File is null || !Aired(e),
            ImportListMonitor.FirstSeason => e.SeasonNumber == first,
            ImportListMonitor.LatestSeason => e.SeasonNumber == latest,
            _ => false,
        };

        foreach (var episode in episodes.Where(e => e.Monitored != Want(e)))
        {
            series.SetEpisodeMonitored(episode.Id, Want(episode));
        }
    }

    /// <summary>Null when the movie may be added now; else from when (MaxValue: unknown). Series are always available.</summary>
    private async Task<DateTimeOffset?> AvailableFromAsync(ImportListDefinition list, ImportListCandidate candidate, CancellationToken cancellationToken)
    {
        if (list.MediaType != MediaType.Movie || list.MinimumAvailability is not (ImportListAvailability.InCinemas or ImportListAvailability.Released))
        {
            return null;
        }

        var stub = new Movie { Year = candidate.Year, InCinemas = candidate.ReleaseDate, Status = string.Empty };
        if (candidate.ReleaseDate is null)
        {
            try
            {
                var details = await tmdb.GetMovieAsync(candidate.TmdbId, cancellationToken);
                stub = new Movie { Year = details.Year, InCinemas = details.ReleaseDate, Status = details.Status };
            }
            catch (TmdbException)
            {
                return DateTimeOffset.MaxValue;
            }
        }

        var minimum = list.MinimumAvailability == ImportListAvailability.Released ? MinimumAvailability.Released : MinimumAvailability.InCinemas;
        return IndexerOptions.AvailableFrom(stub, minimum, 0, time.GetUtcNow());
    }

    private async Task<ImportListCandidate?> FindAsync(ImportListItem item, CancellationToken cancellationToken)
    {
        var (id, source) = item.ImdbId is { } imdb ? (imdb, TmdbExternalSource.Imdb)
            : item.TvdbId is { } tvdb ? (tvdb.ToString(CultureInfo.InvariantCulture), TmdbExternalSource.Tvdb)
            : (null, TmdbExternalSource.Imdb);
        if (id is null)
        {
            return null;
        }

        var key = $"{item.MediaType}:{source}:{id}";
        if (_found.TryGetValue(key, out var cached))
        {
            return cached;
        }

        TmdbFindResult result;
        try
        {
            result = await tmdbLists.FindAsync(id, source, cancellationToken);
        }
        catch (TmdbException ex) when (ex.StatusCode == 404)
        {
            result = new TmdbFindResult([], []);
        }
        catch (TmdbException ex)
        {
            throw new ImportListException($"TMDB: {ex.Message.TrimEnd('.')}", ex);
        }

        var entry = (item.MediaType == MediaType.Series ? result.Series : result.Movies).FirstOrDefault();
        var candidate = entry is null
            ? null
            : new ImportListCandidate(item.MediaType, entry.TmdbId, entry.Title.Length > 0 ? entry.Title : item.Title, entry.Year > 0 ? entry.Year : item.Year, entry.ReleaseDate);
        _found[key] = candidate;
        return candidate;
    }

    private IReadOnlySet<int> InLibrary(MediaType type) =>
        type == MediaType.Series ? series.TmdbIdToId().Keys.ToHashSet() : movies.TmdbIdToId().Keys.ToHashSet();

    /// <summary>
    /// "Clean library": items a list added that are on no enabled list any more. Skipped when there is no enabled list,
    /// when a list failed in this run, or when a list that had items before came back empty (an expired token or a
    /// changed list should not empty the library).
    /// </summary>
    private async Task<(int? Cleaned, string? Skipped)> CleanAsync(
        IReadOnlyList<ImportListDefinition> lists,
        Dictionary<int, ImportListResolved> results,
        List<ImportListDefinition> failed,
        CancellationToken cancellationToken)
    {
        var level = options.Values.CleanLibraryLevel;
        if (level == CleanLibraryLevel.Disabled)
        {
            return (null, null);
        }

        string? skip = lists.Count == 0 ? "no enabled import list"
            : failed.Count > 0 ? $"{failed[0].Name} could not be read"
            : lists.FirstOrDefault(l => results[l.Id].Items.Count == 0 && l.Status.LastFound is > 0) is { } empty ? $"{empty.Name} came back empty"
            : null;
        if (skip is not null)
        {
            logger.LogWarning("Clean library skipped: {Reason}", skip);
            return (null, skip);
        }

        var onLists = results.Values.SelectMany(r => r.Items).Select(i => (i.MediaType, i.TmdbId)).ToHashSet();
        var movieIds = movies.TmdbIdToId();
        var seriesIds = series.TmdbIdToId();
        var cleaned = 0;
        foreach (var origin in repository.Origins())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onLists.Contains((origin.MediaType, origin.TmdbId)))
            {
                continue;
            }

            // Kept by "Movie and collection": a movie of the collection of a listed movie.
            if (origin.MediaType == MediaType.Movie && IsCollectionPart(origin.TmdbId, onLists))
            {
                continue;
            }

            var ids = origin.MediaType == MediaType.Series ? seriesIds : movieIds;
            if (!ids.TryGetValue(origin.TmdbId, out var id))
            {
                repository.DeleteOrigin(origin.MediaType, origin.TmdbId);
                continue;
            }

            var title = origin.MediaType == MediaType.Series ? series.Find(id)?.Title : movies.Find(id)?.Title;
            switch (level)
            {
                case CleanLibraryLevel.LogOnly:
                    logger.LogInformation("Clean library (log only): '{Title}' is on no import list any more", title);
                    cleaned++;
                    break;
                case CleanLibraryLevel.KeepAndUnmonitor:
                    var monitored = origin.MediaType == MediaType.Series ? series.Find(id)?.Monitored : movies.Find(id)?.Monitored;
                    if (monitored == true)
                    {
                        if (origin.MediaType == MediaType.Series)
                        {
                            await library.SetSeriesMonitoredAsync(id, false);
                        }
                        else
                        {
                            await library.SetMovieMonitoredAsync(id, false);
                        }

                        logger.LogInformation("Clean library: '{Title}' is on no import list any more and no longer monitored", title);
                        cleaned++;
                    }

                    break;
                case CleanLibraryLevel.RemoveAndKeepFiles:
                    if (origin.MediaType == MediaType.Series)
                    {
                        await library.DeleteSeriesAsync(id);
                    }
                    else
                    {
                        await library.DeleteMovieAsync(id);
                    }

                    repository.DeleteOrigin(origin.MediaType, origin.TmdbId);
                    logger.LogInformation("Clean library: '{Title}' is on no import list any more and was removed (files kept)", title);
                    cleaned++;
                    break;
            }
        }

        return (cleaned, null);
    }

    private bool IsCollectionPart(int tmdbId, HashSet<(MediaType, int)> onLists) =>
        _collections.Any(c => c.Value is { } col && onLists.Contains((MediaType.Movie, c.Key))
            && _collectionParts.TryGetValue(col.Id, out var parts) && parts.Any(p => p.TmdbId == tmdbId));
}
