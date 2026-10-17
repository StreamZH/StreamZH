// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;
using StreamZH.Tests.Library.Downloads;

namespace StreamZH.Tests.Library.Search;

/// <summary>Indexers configured in a test; records success and failure reports.</summary>
public sealed class FakeIndexerProvider : IIndexerProvider
{
    public List<IndexerDefinition> Indexers { get; } = [];
    public ConcurrentQueue<int> Successes { get; } = new();
    public ConcurrentQueue<(int IndexerId, Exception Error)> Failures { get; } = new();

    public static IndexerDefinition Definition(int id, string name, int priority = 25) =>
        new(id, name, IndexerDefinition.Newznab, $"https://{name.ToLowerInvariant()}.example", "/api", "key", [2000], [5000], priority, true, true, true);

    public IReadOnlyList<IndexerDefinition> Active(IndexerUse use) => [.. Indexers.Where(i => i.IsEnabledFor(use))];

    public IndexerDefinition? Find(int id) => Indexers.FirstOrDefault(i => i.Id == id);

    public void RecordSuccess(int indexerId) => Successes.Enqueue(indexerId);

    public void RecordFailure(int indexerId, Exception error) => Failures.Enqueue((indexerId, error));
}

/// <summary>Answers searches per indexer id from a handler; NZB downloads return a small valid NZB unless told otherwise.</summary>
public sealed class FakeIndexerClient : IIndexerClient
{
    public static readonly byte[] ValidNzb = Encoding.UTF8.GetBytes(
        """
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file poster="p" date="1760000000" subject="&quot;release.mkv&quot; yEnc (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments><segment bytes="1000" number="1">part1@test</segment></segments>
          </file>
        </nzb>
        """);

    public Dictionary<int, Func<SearchCriteria, CancellationToken, Task<IReadOnlyList<ReleaseInfo>>>> Handlers { get; } = [];
    public ConcurrentQueue<(int IndexerId, SearchCriteria Criteria)> Searches { get; } = new();
    public ConcurrentQueue<ReleaseInfo> Downloads { get; } = new();
    public Func<IndexerDefinition, ReleaseInfo, byte[]> Nzb { get; set; } = (_, _) => ValidNzb;

    public void Returns(int indexerId, params ReleaseInfo[] releases) =>
        Handlers[indexerId] = (_, _) => Task.FromResult<IReadOnlyList<ReleaseInfo>>(releases);

    public Task<IndexerCapabilities> GetCapabilitiesAsync(IndexerDefinition indexer, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(IndexerDefinition indexer, SearchCriteria criteria, CancellationToken cancellationToken)
    {
        Searches.Enqueue((indexer.Id, criteria));
        return Handlers.TryGetValue(indexer.Id, out var handler)
            ? handler(criteria, cancellationToken)
            : Task.FromResult<IReadOnlyList<ReleaseInfo>>([]);
    }

    public Task<IReadOnlyList<ReleaseInfo>> FetchRecentAsync(IndexerDefinition indexer, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReleaseInfo>>([]);

    public Task<byte[]> DownloadNzbAsync(IndexerDefinition indexer, ReleaseInfo release, CancellationToken cancellationToken)
    {
        Downloads.Enqueue(release);
        return Task.FromResult(Nzb(indexer, release));
    }
}

public sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A migrated database in a temp folder with a movie and a series, plus all search services wired to fakes.</summary>
public sealed class SearchTestBed : IDisposable
{
    public const long Gb = 1024L * 1024 * 1024;

    public SearchTestBed()
    {
        Directory.CreateDirectory(DataDirectory);
        Paths = new AppPaths(DataDirectory);
        Database = new Database(Paths.DatabaseFile);
        Database.Migrate();
        Movies = new MovieRepository(Database);
        Series = new SeriesRepository(Database);
        History = new HistoryRepository(Database);
        Profiles = new QualityProfileRepository(Database);
        Definitions = new QualityDefinitionRepository(Database);
        Config = new ConfigRepository(Database);
        Cache = new ReleaseCache(Time);
        Downloads = new DownloadSettings(Config, Paths);

        // The search tests check the NZB folder; the built-in engine has its own tests (UseBuiltInClient).
        Config.Set(ConfigKeys.DownloadClient, DownloadSettings.BlackholeValue);
        Engine = new DownloadEngine(
            new DownloadRepository(Database), History, Downloads, Config, new FakeArticleSource { MaxConnections = 0 }, new FakePostProcessor(),
            Events, new FakeFreeSpace(), new DownloadEngineOptions(), NullLogger<DownloadEngine>.Instance);
        Clients = new DownloadClientProvider(Downloads, new BuiltInDownloadClient(Engine), new BlackholeDownloadClient(Downloads));
        Events.Subscribe<ReleaseGrabbed>((e, _) =>
        {
            Grabbed.Enqueue(e);
            return ValueTask.CompletedTask;
        });
        Events.Subscribe<AutomaticSearchFinished>((e, _) =>
        {
            Finished.Enqueue(e);
            return ValueTask.CompletedTask;
        });

        var rootId = new RootFolderRepository(Database).Add(Path.Combine(DataDirectory, "media"), MediaType.Movie);
        Dune = new Movie
        {
            TmdbId = 438631, ImdbId = "tt1160419", Title = "Dune", OriginalTitle = "Dune", SortTitle = "dune", Year = 2021, Runtime = 155,
            Status = "Released", Monitored = true, QualityProfileId = QualityDefaults.DefaultProfileId, OriginalLanguageId = 1,
            RootFolderId = rootId, FolderPath = Path.Combine(DataDirectory, "media", "Dune (2021)"), Added = DateTime.UtcNow,
        };
        Movies.Add(Dune);

        Show = new Series
        {
            TmdbId = 1396, TvdbId = 81189, ImdbId = "tt0903747", Title = "Breaking Bad", OriginalTitle = "Breaking Bad", SortTitle = "breaking bad",
            Year = 2008, Status = "Ended", Monitored = true, QualityProfileId = QualityDefaults.DefaultProfileId, OriginalLanguageId = 1,
            RootFolderId = rootId, FolderPath = Path.Combine(DataDirectory, "media", "Breaking Bad (2008)"), Added = DateTime.UtcNow,
        };
        Series.Add(Show);
        Series.UpsertEpisodes(
            Show.Id,
            [
                .. Enumerable.Range(1, 7).Select(e => new Episode { SeasonNumber = 1, EpisodeNumber = e, Title = $"Episode {e}", AirDate = $"2008-01-{e + 19:00}" }),
                .. Enumerable.Range(1, 13).Select(e => new Episode { SeasonNumber = 2, EpisodeNumber = e, Title = $"Episode {e}", AirDate = "2009-03-08" }),
            ],
            monitorNew: true);
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-search-{Guid.NewGuid():N}");
    public AppPaths Paths { get; }
    public Database Database { get; }
    public MovieRepository Movies { get; }
    public SeriesRepository Series { get; }
    public HistoryRepository History { get; }
    public QualityProfileRepository Profiles { get; }
    public QualityDefinitionRepository Definitions { get; }
    public ConfigRepository Config { get; }
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    public FakeIndexerProvider Indexers { get; } = new();
    public FakeIndexerClient Client { get; } = new();
    public InProcessEventBus Events { get; } = new();
    public ConcurrentQueue<ReleaseGrabbed> Grabbed { get; } = new();
    public ConcurrentQueue<AutomaticSearchFinished> Finished { get; } = new();
    public ReleaseCache Cache { get; }
    public DownloadSettings Downloads { get; }
    public DownloadEngine Engine { get; }
    public DownloadClientProvider Clients { get; }
    public Movie Dune { get; }
    public Series Show { get; }

    public IReadOnlyList<Episode> Episodes => Series.Episodes(Show.Id);

    public Episode Episode(int season, int number) => Episodes.Single(e => e.SeasonNumber == season && e.EpisodeNumber == number);

    public ReleaseSearchService Search(TimeSpan? timeout = null) =>
        new(Indexers, Client, Movies, Series, new DecisionTargetFactory(Profiles), Definitions, Cache, NullLogger<ReleaseSearchService>.Instance, Time)
        {
            IndexerTimeout = timeout ?? TimeSpan.FromSeconds(10),
        };

    public GrabService Grab() => new(Indexers, Client, Cache, Clients, Downloads, History, Events, NullLogger<GrabService>.Instance, Time);

    public void UseBuiltInClient() => Config.Set(ConfigKeys.DownloadClient, DownloadSettings.BuiltInValue);

    public AutomaticSearchService Automatic() =>
        new(Search(), Grab(), Movies, Series, History, Downloads, Events, NullLogger<AutomaticSearchService>.Instance, Engine, Time);

    public IndexerDefinition AddIndexer(int id, string name, int priority = 25)
    {
        var definition = FakeIndexerProvider.Definition(id, name, priority);
        Indexers.Indexers.Add(definition);
        return definition;
    }

    public ReleaseInfo Release(string title, double sizeGb = 8, int indexerId = 1, double ageDays = 2, int tmdbId = 0, int tvdbId = 0, string? imdbId = null) => new()
    {
        Guid = $"guid-{indexerId}-{title}",
        Title = title,
        DownloadUrl = $"https://indexer{indexerId}.example/getnzb/{Uri.EscapeDataString(title)}",
        InfoUrl = $"https://indexer{indexerId}.example/details/{Uri.EscapeDataString(title)}",
        Size = (long)(sizeGb * Gb),
        PublishDate = Time.Now.AddDays(-ageDays),
        Grabs = 10,
        TmdbId = tmdbId,
        TvdbId = tvdbId,
        ImdbId = imdbId,
    };

    public string[] NzbFiles() =>
        Directory.Exists(Downloads.NzbFolder) ? [.. Directory.GetFiles(Downloads.NzbFolder, "*.nzb").Select(Path.GetFileName).Order()!] : [];

    public void Dispose()
    {
        Engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
