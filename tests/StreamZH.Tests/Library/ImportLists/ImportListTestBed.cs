// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.ImportLists;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Library.ImportLists;

/// <summary>A database, the library services and fakes for TMDB and one list source, for sync tests.</summary>
internal sealed class ImportListTestBed : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    public ImportListTestBed()
    {
        Directory.CreateDirectory(Root);
        Database = new Database(Path.Combine(Root, "streamzh.db"));
        Database.Migrate();
        Config = new ConfigRepository(Database);
        Movies = new MovieRepository(Database);
        Series = new SeriesRepository(Database);
        Lists = new ImportListRepository(Database);
        RootFolders = new RootFolderRepository(Database);
        MovieRoot = RootFolders.Add(Path.Combine(Root, "movies"), MediaType.Movie);
        SeriesRoot = RootFolders.Add(Path.Combine(Root, "tv"), MediaType.Series);
        Events = new InProcessEventBus();
        Jobs = new JobQueue(Events);
        Options = new ImportListOptions(Config);
        var profiles = new QualityProfileService(new QualityProfileRepository(Database), Config, NullLogger<QualityProfileService>.Instance);
        Library = new LibraryService(Tmdb, RootFolders, Movies, Series, profiles, Events, NullLogger<LibraryService>.Instance);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-lists-{Guid.NewGuid():N}");

    public Database Database { get; }

    public ConfigRepository Config { get; }

    public MovieRepository Movies { get; }

    public SeriesRepository Series { get; }

    public ImportListRepository Lists { get; }

    public RootFolderRepository RootFolders { get; }

    public int MovieRoot { get; }

    public int SeriesRoot { get; }

    public InProcessEventBus Events { get; }

    public JobQueue Jobs { get; }

    public ImportListOptions Options { get; }

    public FakeListTmdb Tmdb { get; } = new();

    public FakeListSource Source { get; } = new();

    public LibraryService Library { get; }

    public ImportListSyncService Sync() => new(
        Lists, [Source], Tmdb, Tmdb, Library, Movies, Series, Options, Jobs, Events, new FixedTime(Now), NullLogger<ImportListSyncService>.Instance);

    /// <summary>Adds a list that reads <see cref="Source"/> ("rssList" with its items under <paramref name="name"/>).</summary>
    public ImportListDefinition AddList(
        string name,
        MediaType mediaType = MediaType.Movie,
        string? monitor = null,
        bool enableAuto = true,
        bool searchOnAdd = true,
        string availability = ImportListAvailability.Announced,
        bool enabled = true)
    {
        var definition = new ImportListDefinition(
            0,
            name,
            ImportListCatalog.Rss,
            mediaType,
            enabled,
            enableAuto,
            searchOnAdd,
            monitor ?? ImportListMonitor.Default(mediaType),
            mediaType == MediaType.Series ? SeriesRoot : MovieRoot,
            null,
            availability,
            new Dictionary<string, JsonElement> { ["url"] = JsonSerializer.SerializeToElement($"https://example.com/{name}") });
        return Lists.Find(Lists.Add(definition, Now.UtcDateTime))!;
    }

    /// <summary>A movie added by hand (not by a list).</summary>
    public Movie AddMovieByHand(int tmdbId) => Library.AddMovieAsync(tmdbId, MovieRoot, true).GetAwaiter().GetResult();

    public List<Job> QueuedJobs()
    {
        var jobs = new List<Job>();
        while (Jobs.Reader.TryRead(out var job))
        {
            jobs.Add(job);
        }

        return jobs;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Returns the items set per list URL; throws for URLs in <see cref="Failing"/>.</summary>
internal sealed class FakeListSource : IImportListSource
{
    public Dictionary<string, List<ImportListItem>> Items { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Failing { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Implementations { get; } = [ImportListCatalog.Rss];

    public void Set(string list, params ImportListItem[] items) => Items[list] = [.. items];

    public Task<ImportListFetch> FetchAsync(ImportListContext context, CancellationToken cancellationToken)
    {
        var name = context.Settings.Text("url")!.Split('/')[^1];
        if (Failing.TryGetValue(name, out var error))
        {
            throw new ImportListException(error);
        }

        return Task.FromResult(new ImportListFetch(Items.GetValueOrDefault(name) ?? [], name));
    }
}

/// <summary>TMDB with a few movies (two in a collection), one series and IMDb lookups.</summary>
internal sealed class FakeListTmdb : ITmdbClient, ITmdbListClient
{
    public static readonly TmdbMovie Dune = new(438631, "tt1160419", "Dune", "Dune", 2021, null, 155, "Released", [], null, null, "en", "2021-09-15");
    public static readonly TmdbMovie DuneTwo = new(693134, "tt15239678", "Dune: Part Two", "Dune: Part Two", 2024, null, 167, "Released", [], null, null, "en", "2024-02-27");
    public static readonly TmdbMovie Arrival = new(329865, "tt2543164", "Arrival", "Arrival", 2016, null, 116, "Released", [], null, null, "en", "2016-11-10");

    /// <summary>In cinemas 30 days before <see cref="ImportListTestBed.Now"/>: in cinemas, not yet "released".</summary>
    public static readonly TmdbMovie Fresh = new(1000, "tt0001000", "Fresh", "Fresh", 2026, null, 100, "Released", [], null, null, "en", "2026-09-18");

    public static readonly TmdbMovie Upcoming = new(1001, null, "Upcoming", "Upcoming", 2027, null, 0, "Post Production", [], null, null, "en", "2027-03-01");

    public static readonly TmdbSeries Show = new(1396, 81189, "tt0903747", "Breaking Bad", "Breaking Bad", 2008, null, "Returning Series", "AMC", [], null, null, [0, 1, 2]);

    public List<TmdbMovie> AllMovies { get; } = [Dune, DuneTwo, Arrival, Fresh, Upcoming];

    public bool IsConfigured => true;

    public Task<IReadOnlyList<TmdbSearchResult>> SearchMoviesAsync(string query, int? year, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TmdbSearchResult>>([]);

    public Task<IReadOnlyList<TmdbSearchResult>> SearchSeriesAsync(string query, int? year, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TmdbSearchResult>>([]);

    public Task<TmdbMovie> GetMovieAsync(int tmdbId, CancellationToken cancellationToken = default) =>
        AllMovies.FirstOrDefault(m => m.TmdbId == tmdbId) is { } movie ? Task.FromResult(movie) : throw new TmdbException("Not found on TMDB.", 404);

    public Task<TmdbSeries> GetSeriesAsync(int tmdbId, CancellationToken cancellationToken = default) =>
        tmdbId == Show.TmdbId ? Task.FromResult(Show) : throw new TmdbException("Not found on TMDB.", 404);

    /// <summary>Season 1 aired, season 2 airs after <see cref="ImportListTestBed.Now"/>; one special.</summary>
    public Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TmdbEpisode>>(seasonNumber switch
        {
            0 => [new TmdbEpisode(0, 1, "Special", "2009-02-17", null)],
            1 => [.. Enumerable.Range(1, 3).Select(e => new TmdbEpisode(1, e, $"Episode {e}", $"2008-01-2{e}", null))],
            _ => [.. Enumerable.Range(1, 2).Select(e => new TmdbEpisode(2, e, $"Episode {e}", $"2026-12-0{e}", null))],
        });

    public Task TestAsync(string? token = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<TmdbListPage> GetListAsync(string listId, int page, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<TmdbListPage> GetCollectionAsync(int collectionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TmdbListPage("Dune Collection", [Entry(Dune), Entry(DuneTwo)], 1, 1));

    public Task<TmdbPersonCredits> GetPersonCreditsAsync(int personId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<TmdbListPage> DiscoverAsync(TmdbMediaKind kind, IReadOnlyDictionary<string, string> parameters, int page, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<TmdbFindResult> FindAsync(string externalId, TmdbExternalSource source, CancellationToken cancellationToken = default) =>
        Task.FromResult(source == TmdbExternalSource.Imdb
            ? new TmdbFindResult([.. AllMovies.Where(m => m.ImdbId == externalId).Select(Entry)], [])
            : new TmdbFindResult([], externalId == "81189" ? [new TmdbListEntry(TmdbMediaKind.Series, Show.TmdbId, Show.Title, Show.Year)] : []));

    public Task<string> GetCompanyNameAsync(int companyId, CancellationToken cancellationToken = default) => Task.FromResult("Company");

    public Task<string> GetKeywordNameAsync(int keywordId, CancellationToken cancellationToken = default) => Task.FromResult("Keyword");

    public Task<(int Id, string Name)?> GetMovieCollectionAsync(int movieId, CancellationToken cancellationToken = default) =>
        Task.FromResult<(int Id, string Name)?>(movieId is 438631 or 693134 ? (496243, "Dune Collection") : null);

    public static ImportListItem Item(TmdbMovie m, bool tmdbId = true) =>
        new(MediaType.Movie, m.Title, m.Year, tmdbId ? m.TmdbId : null, m.ImdbId);

    private static TmdbListEntry Entry(TmdbMovie m) => new(TmdbMediaKind.Movie, m.TmdbId, m.Title, m.Year, m.ReleaseDate);
}
