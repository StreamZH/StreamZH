// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Arr.Library;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Library.Migration;

/// <summary>
/// Radarr v5 / Sonarr v4 stand-in answering GET /api/v3/… from the fixtures in Fixtures/Library (history and blocklist
/// paged like the real apps). "@HOURS_AGO:n@" and "@DAYS_AGO:n@" in a fixture become real UTC times. Records every request.
/// </summary>
public sealed partial class FakeArr(string app) : HttpMessageHandler
{
    public static string FixtureFolder => Path.Combine(AppContext.BaseDirectory, "Library", "Migration", "Fixtures", "Library");

    public ConcurrentQueue<(HttpMethod Method, string PathAndQuery)> Requests { get; } = new();

    /// <summary>Replaces the answer of a path (without /api/v3 and query), e.g. "/series".</summary>
    public Dictionary<string, string> Overrides { get; } = [];

    public string App { get; } = app;

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureFolder, name));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Enqueue((request.Method, uri.PathAndQuery));
        if (request.Method != HttpMethod.Get)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        }

        if (!request.Headers.TryGetValues("X-Api-Key", out var keys) || !keys.Contains("secret"))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        var path = uri.AbsolutePath.StartsWith("/api/v3", StringComparison.Ordinal) ? uri.AbsolutePath["/api/v3".Length..] : uri.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        string? body;
        if (Overrides.TryGetValue(path, out var replaced))
        {
            body = replaced;
        }
        else
        {
            body = path switch
            {
                "/system/status" => Fixture($"{App}-system-status.json"),
                "/movie" or "/series" or "/rootfolder" or "/qualityprofile" => Fixture($"{App}-{path.TrimStart('/')}.json"),
                "/moviefile" => "[]",
                "/episode" or "/episodefile" when query["seriesId"] is { } id && File.Exists(Path.Combine(FixtureFolder, $"{App}-{path.TrimStart('/')}-{id}.json")) =>
                    Fixture($"{App}-{path.TrimStart('/')}-{id}.json"),
                "/history" or "/blocklist" => Page(Fixture($"{App}-{path.TrimStart('/')}.json"), query),
                _ => null,
            };
        }

        if (body is null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Dates(body), Encoding.UTF8, "application/json") });
    }

    /// <summary>A page of a paged fixture (records sorted newest first like sortKey=date&amp;sortDirection=descending).</summary>
    private static string Page(string json, System.Collections.Specialized.NameValueCollection query)
    {
        var root = JsonNode.Parse(Dates(json))!.AsObject();
        var records = root["records"]!.AsArray().Select(r => r!.DeepClone()).OrderByDescending(r => r!["date"]!.GetValue<DateTime>()).ToList();
        var page = int.Parse(query["page"] ?? "1", CultureInfo.InvariantCulture);
        var size = int.Parse(query["pageSize"] ?? "10", CultureInfo.InvariantCulture);
        var slice = new JsonArray([.. records.Skip((page - 1) * size).Take(size)]);
        return new JsonObject
        {
            ["page"] = page,
            ["pageSize"] = size,
            ["sortKey"] = query["sortKey"] ?? "date",
            ["sortDirection"] = query["sortDirection"] ?? "descending",
            ["totalRecords"] = records.Count,
            ["records"] = slice,
        }.ToJsonString();
    }

    private static string Dates(string json) => DateToken().Replace(json, m =>
    {
        var amount = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var when = m.Groups[1].Value == "HOURS" ? DateTime.UtcNow.AddHours(-amount) : DateTime.UtcNow.AddDays(-amount);
        return when.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
    });

    [GeneratedRegex("@(HOURS|DAYS)_AGO:(\\d+)@")]
    private static partial Regex DateToken();
}

/// <summary>TMDB stand-in with the movies and shows of the fixtures; counts requests.</summary>
public sealed class LibraryFakeTmdb : ITmdbClient, ITmdbFindClient
{
    public bool IsConfigured { get; set; } = true;

    public ConcurrentQueue<string> Calls { get; } = new();

    public Dictionary<int, TmdbMovie> Movies { get; } = new()
    {
        [13207] = new(13207, "tt0758746", "Freitag der 13.", "Friday the 13th", 2009, "Crystal Lake…", 97, "Released", ["Horror"], "/f13.jpg", null, "en", "2009-02-13"),
        [693134] = new(693134, "tt15239678", "Dune: Part Two", "Dune: Part Two", 2024, "Paul…", 167, "Released", ["Science Fiction"], "/dune2.jpg", null, "en", "2024-02-27"),
        [329865] = new(329865, "tt2543164", "Arrival", "Arrival", 2016, "Linguist…", 116, "Released", ["Drama"], "/arrival.jpg", null, "en", "2016-11-10"),
        [335984] = new(335984, "tt1856101", "Blade Runner 2049", "Blade Runner 2049", 2017, "K…", 164, "Released", ["Science Fiction"], "/br.jpg", null, "en", "2017-10-04"),
    };

    public Dictionary<int, TmdbSeries> Series { get; } = new()
    {
        [250001] = new(250001, 450001, "tt22222222", "Crystal Lake", "Crystal Lake", 2025, "Pamela…", "Returning Series", "Peacock", ["Horror"], "/cl.jpg", null, [1], "en", [27], ["US"]),
        [209867] = new(209867, 424536, "tt22248376", "Frieren: Beyond Journey's End", "葬送のフリーレン", 2023, "Elf…", "Returning Series", "Nippon TV", ["Animation"], "/fr.jpg", null, [0, 1], "ja", [16], ["JP"]),
        [2224] = new(2224, 71256, "tt0115147", "The Daily Show", "The Daily Show", 1996, "News…", "Returning Series", "Comedy Central", ["Talk"], "/ds.jpg", null, [31], "en", [10767], ["US"]),
    };

    public Dictionary<(int, int), IReadOnlyList<TmdbEpisode>> Seasons { get; } = new()
    {
        [(250001, 1)] = [.. Enumerable.Range(1, 8).Select(e => new TmdbEpisode(1, e, $"Chapter {e}", $"2025-10-{e:00}", null))],
        [(209867, 0)] = [new TmdbEpisode(0, 1, "Special", "2024-03-01", null)],
        [(209867, 1)] = [.. new[] { 6, 13, 20, 27 }.Select((day, i) => new TmdbEpisode(1, i + 1, $"Episode {i + 1}", $"2023-10-{day:00}", null))],
        [(2224, 31)] = [.. Enumerable.Range(0, 3).Select(i => new TmdbEpisode(31, i + 1, $"Guest {i}", $"2026-10-{13 + i}", null))],
    };

    /// <summary>TVDB id → TMDB show for /find.</summary>
    public Dictionary<int, int> ByTvdb { get; } = new() { [450001] = 250001, [424536] = 209867, [71256] = 2224 };

    public int FindCalls => Calls.Count(c => c.StartsWith("find", StringComparison.Ordinal));

    public Task<IReadOnlyList<TmdbSearchResult>> SearchMoviesAsync(string query, int? year, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TmdbSearchResult>>([]);

    public Task<IReadOnlyList<TmdbSearchResult>> SearchSeriesAsync(string query, int? year, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TmdbSearchResult>>([]);

    public Task<TmdbMovie> GetMovieAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue($"movie/{tmdbId}");
        return Movies.TryGetValue(tmdbId, out var movie) ? Task.FromResult(movie) : throw new TmdbException("Not found on TMDB.", 404);
    }

    public Task<TmdbSeries> GetSeriesAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue($"tv/{tmdbId}");
        return Series.TryGetValue(tmdbId, out var series) ? Task.FromResult(series) : throw new TmdbException("Not found on TMDB.", 404);
    }

    public Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue($"tv/{tmdbId}/season/{seasonNumber}");
        return Task.FromResult(Seasons.TryGetValue((tmdbId, seasonNumber), out var episodes) ? episodes : (IReadOnlyList<TmdbEpisode>)[]);
    }

    public Task TestAsync(string? token = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<TmdbListEntry?> FindByTvdbIdAsync(int tvdbId, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue($"find/tvdb/{tvdbId}");
        return Task.FromResult(ByTvdb.TryGetValue(tvdbId, out var id) && Series.TryGetValue(id, out var s)
            ? new TmdbListEntry(TmdbMediaKind.Series, id, s.Title, s.Year)
            : null);
    }

    public Task<TmdbListEntry?> FindByImdbIdAsync(string imdbId, TmdbMediaKind kind, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue($"find/imdb/{imdbId}");
        return Task.FromResult<TmdbListEntry?>(null);
    }
}

/// <summary>
/// A migrated database in a temp folder, the library services and the library sections, with Radarr's /movies and
/// Sonarr's /tv mapped to media folders in the temp folder. The fixtures' files are created on disk (except Arrival's
/// and Crystal Lake S01E02, which are "missing").
/// </summary>
public sealed class LibraryMigrationTestBed : IDisposable
{
    public LibraryMigrationTestBed(bool createFiles = true)
    {
        Directory.CreateDirectory(MoviesFolder);
        Directory.CreateDirectory(TvFolder);
        Paths = new AppPaths(Path.Combine(Root, "data"));
        Directory.CreateDirectory(Paths.DataDirectory);
        Database = new Database(Paths.DatabaseFile);
        Database.Migrate();
        TmdbRetry.Delay = TimeSpan.Zero;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Database);
        services.AddSingleton<RootFolderRepository>();
        services.AddSingleton<MovieRepository>();
        services.AddSingleton<SeriesRepository>();
        services.AddSingleton<HistoryRepository>();
        services.AddSingleton<BlocklistRepository>();
        services.AddSingleton<MediaFileDetailsRepository>();
        services.AddSingleton<QualityProfileRepository>();
        services.AddSingleton<ConfigRepository>();
        services.AddSingleton(sp => new QualityProfileService(
            sp.GetRequiredService<QualityProfileRepository>(), sp.GetRequiredService<ConfigRepository>(), NullLogger<QualityProfileService>.Instance));
        services.AddSingleton<IEventBus>(Events);
        services.AddSingleton<ITmdbClient>(Tmdb);
        services.AddSingleton<ITmdbFindClient>(Tmdb);
        services.AddScoped(sp => new LibraryService(
            sp.GetRequiredService<ITmdbClient>(), sp.GetRequiredService<RootFolderRepository>(), sp.GetRequiredService<MovieRepository>(),
            sp.GetRequiredService<SeriesRepository>(), sp.GetRequiredService<QualityProfileService>(), sp.GetRequiredService<IEventBus>(),
            NullLogger<LibraryService>.Instance));
        services.AddScoped<RootFolderService>();
        services.AddHttpClient<ArrApiClient>().ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<ArrHandlers>().Current);
        services.AddSingleton(new ArrHandlers(this));
        services.AddSingleton(Reader);
        services.AddSingleton<LibraryMigrationStore>();
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        if (createFiles)
        {
            CreateFiles();
        }
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-libmig-{Guid.NewGuid():N}");

    public string MoviesFolder => Path.Combine(Root, "media", "movies");

    public string TvFolder => Path.Combine(Root, "media", "tv");

    public AppPaths Paths { get; }

    public Database Database { get; }

    public InProcessEventBus Events { get; } = new();

    public LibraryFakeTmdb Tmdb { get; } = new();

    public FakeArr Radarr { get; } = new("radarr");

    public FakeArr Sonarr { get; } = new("sonarr");

    /// <summary>Small pages, so the history and blocklist sections page like with a big library.</summary>
    public ArrLibraryReader Reader { get; } = new() { PageSize = 2 };

    public ServiceProvider Services { get; }

    public MovieRepository Movies => Services.GetRequiredService<MovieRepository>();

    public SeriesRepository Series => Services.GetRequiredService<SeriesRepository>();

    public MediaFileDetailsRepository Details => Services.GetRequiredService<MediaFileDetailsRepository>();

    public IReadOnlyList<PathMapping> Mappings => [new("/movies", MoviesFolder), new("/tv", TvFolder)];

    public MovieMigrationSection MoviesSection => new(Reader, NullLogger<MovieMigrationSection>.Instance);

    public SeriesMigrationSection SeriesSection => new(Reader, NullLogger<SeriesMigrationSection>.Instance);

    /// <summary>A fresh context (one per preview or run, like the migration service makes them).</summary>
    public MigrationContext Context(
        string source,
        IReadOnlyDictionary<string, string>? options = null,
        IReadOnlyList<string>? exclude = null,
        IReadOnlyList<PathMapping>? mappings = null)
    {
        var request = new MigrationRequest($"http://{source}:7878", "secret", mappings ?? Mappings, Exclude: exclude, Options: options);
        var app = source == MigrationSources.Radarr ? new MigrationAppInfo("Radarr", "5.26.2.10099") : new MigrationAppInfo("Sonarr", "4.0.15.2941");
        return new MigrationContext(source, new MigrationConnection(request.Url!, request.ApiKey!), app, request, new PathMapper(request.PathMappings), Services);
    }

    public static MigrationSectionRun NewRun(IMigrationSection section) => new(section.Key, section.Label, (_, _) => { });

    public async Task<MigrationSectionReport> RunAsync(IMigrationSection section, MigrationContext context)
    {
        var run = NewRun(section);
        await section.RunAsync(context, run, CancellationToken.None);
        return run.Report();
    }

    public string MovieFile(string folder, string name) => Path.Combine(MoviesFolder, folder, name);

    public string TvFile(string folder, string relative) => Path.Combine(TvFolder, folder, relative.Replace('/', Path.DirectorySeparatorChar));

    private void CreateFiles()
    {
        Write(MovieFile("Freitag der 13. (2009)", "Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS.mkv"), 4096);
        Write(MovieFile("Dune Part Two (2024)", "Dune.Part.Two.2024.German.DL.2160p.WEB.H265-W4K.mkv"), 8192);
        Directory.CreateDirectory(Path.Combine(MoviesFolder, "Arrival (2016)"));
        Directory.CreateDirectory(Path.Combine(MoviesFolder, "Blade Runner 2049 (2017)"));
        Write(TvFile("Crystal Lake", "Season 01/Crystal.Lake.S01E01.German.DL.1080p.WEB.h264-ZZGtv.mkv"), 2048);
        Write(TvFile("Crystal Lake", "Season 01/Crystal.Lake.S01E03E04.German.DL.1080p.WEB.h264-ZZGtv.mkv"), 4096);
        Write(TvFile("Crystal Lake", "Season 01/Crystal.Lake.S01E05.German.DL.1080p.BluRay.REMUX.AVC-PtBM.mkv"), 6144);
        Write(TvFile("Frieren - Beyond Journey's End", "Season 02/[SubsPlease] Sousou no Frieren - 03 (1080p) [9A1B2C3D].mkv"), 3072);
        Write(TvFile("The Daily Show", "Season 2026/The.Daily.Show.2026.10.14.Guest.1.1080p.WEB.h264-EDITH.mkv"), 1024);
    }

    private static void Write(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    public void Dispose()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Routes ArrApiClient to the fake of the host name in the URL (radarr or sonarr).</summary>
    public sealed class ArrHandlers(LibraryMigrationTestBed bed)
    {
        public HttpMessageHandler Current => new Router(bed);

        private sealed class Router(LibraryMigrationTestBed bed) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var fake = request.RequestUri!.Host == "sonarr" ? bed.Sonarr : bed.Radarr;
                return new HttpMessageInvoker(fake, disposeHandler: false).SendAsync(request, cancellationToken);
            }
        }
    }
}

internal static class JsonTestExtensions
{
    public static string ToJson(this object value) => JsonSerializer.Serialize(value, ArrApiClient.Json);
}
