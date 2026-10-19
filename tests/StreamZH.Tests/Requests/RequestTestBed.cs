// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Requests;
using StreamZH.Library.Search;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Requests;

/// <summary>A database with root folders, the library services, a fake TMDB and the request services.</summary>
internal sealed class RequestTestBed : IDisposable
{
    /// <summary>Season 1 of the fake show aired in January 2008, season 2 in March 2009.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    public RequestTestBed(bool withRootFolders = true)
    {
        Directory.CreateDirectory(Root);
        Database = new Database(Path.Combine(Root, "streamzh.db"));
        Database.Migrate();
        Config = new ConfigRepository(Database);
        Movies = new MovieRepository(Database);
        Series = new SeriesRepository(Database);
        RootFolders = new RootFolderRepository(Database);
        Requests = new RequestRepository(Database);
        Users = new UserRepository(Database);
        if (withRootFolders)
        {
            MovieRoot = RootFolders.Add(Path.Combine(Root, "movies"), MediaType.Movie);
            SeriesRoot = RootFolders.Add(Path.Combine(Root, "tv"), MediaType.Series);
        }

        Events = new InProcessEventBus();
        Events.Subscribe<RequestChanged>((e, _) =>
        {
            lock (Changes)
            {
                Changes.Add(e);
            }

            return ValueTask.CompletedTask;
        });
        Events.Subscribe<RequestActivity>((e, _) =>
        {
            lock (Activities)
            {
                Activities.Add(e);
            }

            return ValueTask.CompletedTask;
        });
        Jobs = new JobQueue(Events);
        Hold = new AutomationHold(Config, Time, Events);
        Settings = new RequestSettings(Config, new QualityProfileRepository(Database), RootFolders);
        var profiles = new QualityProfileService(new QualityProfileRepository(Database), Config, NullLogger<QualityProfileService>.Instance);
        Library = new LibraryService(Tmdb, RootFolders, Movies, Series, profiles, Events, NullLogger<LibraryService>.Instance);
        Availability = new RequestAvailability(Requests, Movies, Series, Events, Time, NullLogger<RequestAvailability>.Instance);
        Service = new RequestService(
            Requests,
            Users,
            Settings,
            Settings,
            Tmdb,
            Library,
            Movies,
            Series,
            RootFolders,
            Hold,
            Jobs,
            Availability,
            Events,
            Time,
            NullLogger<RequestService>.Instance);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-requests-{Guid.NewGuid():N}");

    public Database Database { get; }

    public ConfigRepository Config { get; }

    public MovieRepository Movies { get; }

    public SeriesRepository Series { get; }

    public RootFolderRepository RootFolders { get; }

    public RequestRepository Requests { get; }

    public UserRepository Users { get; }

    public int MovieRoot { get; }

    public int SeriesRoot { get; }

    public InProcessEventBus Events { get; }

    public JobQueue Jobs { get; }

    public AutomationHold Hold { get; }

    public RequestSettings Settings { get; }

    public FakeTmdbClient Tmdb { get; } = new();

    public TimeProvider Time { get; } = new RequestClock(Now);

    public LibraryService Library { get; }

    public RequestAvailability Availability { get; }

    public RequestService Service { get; }

    public List<RequestChanged> Changes { get; } = [];

    public List<RequestActivity> Activities { get; } = [];

    /// <summary>A user with the given role; the own request settings are left empty unless given.</summary>
    public RequestActor User(
        string name, UserRole role, bool? autoApprove = null, int? movieLimit = null, int? seriesLimit = null, int? limitDays = null, bool enabled = true)
    {
        var id = Users.Create(name, "hash", role);
        using var connection = Database.Open();
        connection.Execute(
            "UPDATE Users SET AutoApprove = @autoApprove, MovieRequestLimit = @movieLimit, SeriesRequestLimit = @seriesLimit, RequestLimitDays = @limitDays, Enabled = @enabled, DisplayName = @display WHERE Id = @id",
            new { id, autoApprove, movieLimit, seriesLimit, limitDays, enabled, display = char.ToUpperInvariant(name[0]) + name[1..] });
        return new RequestActor(id, role);
    }

    public List<Job> QueuedJobs()
    {
        var jobs = new List<Job>();
        while (Jobs.Reader.TryRead(out var job))
        {
            jobs.Add(job);
        }

        return jobs;
    }

    /// <summary>Gives the library movie a file.</summary>
    public void GiveFile(Movie movie)
    {
        movie.File = new MediaFile("Dune (2021).mkv", 1_000, Now.UtcDateTime);
        Movies.Update(movie);
    }

    /// <summary>Gives the episodes of the series files (season, episode).</summary>
    public void GiveFiles(int seriesId, params (int Season, int Episode)[] episodes) =>
        Series.SetEpisodeFiles(seriesId, episodes.ToDictionary(e => e, e => new MediaFile($"S{e.Season:00}E{e.Episode:00}.mkv", 100, Now.UtcDateTime)));

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

internal sealed class RequestClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Charts, search and details for Discover tests (one page per chart; details of Dune and the show).</summary>
internal sealed class FakeDiscoverTmdb : ITmdbDiscoverClient
{
    public int Calls { get; private set; }

    /// <summary>Thrown by every call when set (TMDB down).</summary>
    public Exception? Failure { get; set; }

    public List<TmdbListEntry> Entries { get; } =
    [
        new(TmdbMediaKind.Movie, FakeTmdbClient.Dune.TmdbId, "Dune", 2021, "2021-09-15", "/dune.jpg", 7.8, 12000, "en", "Paul Atreides...", "/dune-backdrop.jpg"),
        new(TmdbMediaKind.Movie, FakeTmdbClient.Arrival.TmdbId, "Arrival", 2016, "2016-11-10", "/arrival.jpg", 7.6, 9000, "en", "Linguist...", null),
        new(TmdbMediaKind.Series, FakeTmdbClient.Show.TmdbId, "Breaking Bad", 2008, "2008-01-20", "/bb.jpg", 8.9, 15000, "en", "Walter White...", null),
    ];

    public Task<TmdbListPage> GetChartAsync(TmdbMediaKind kind, TmdbChart chart, int page, CancellationToken cancellationToken = default)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        Calls++;
        return Task.FromResult(new TmdbListPage(null, [.. Entries.Where(e => e.Kind == kind)], page, 3));
    }

    public Task<TmdbListPage> SearchAsync(TmdbMediaKind? kind, string query, int page, CancellationToken cancellationToken = default)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        Calls++;
        return Task.FromResult(new TmdbListPage(
            null, [.. Entries.Where(e => (kind is null || e.Kind == kind) && e.Title.Contains(query, StringComparison.OrdinalIgnoreCase))], page, 1));
    }

    public Task<TmdbMovieDetails> GetMovieDetailsAsync(int tmdbId, string certificationCountry, CancellationToken cancellationToken = default)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        Calls++;
        var movie = new[] { FakeTmdbClient.Dune, FakeTmdbClient.Arrival }.FirstOrDefault(m => m.TmdbId == tmdbId)
            ?? throw new TmdbException("Not found on TMDB.", 404);
        return Task.FromResult(new TmdbMovieDetails(
            movie, "Beyond fear, destiny awaits.", 7.8, 12000, "2021-09-15", "PG-13", ["Legendary Pictures"], ["United States of America"], null,
            [new TmdbCastMember("Jane Doe", "Chani", 0, "/jane.jpg")], ["John Doe"], []));
    }

    public Task<TmdbSeriesDetails> GetSeriesDetailsAsync(int tmdbId, string certificationCountry, CancellationToken cancellationToken = default)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        Calls++;
        if (tmdbId != FakeTmdbClient.Show.TmdbId)
        {
            throw new TmdbException("Not found on TMDB.", 404);
        }

        return Task.FromResult(new TmdbSeriesDetails(
            FakeTmdbClient.Show, 8.9, 15000, "2008-01-20", "TV-MA", ["AMC"], [],
            [
                new TmdbSeasonSummary(0, "Specials", null, 1, "2009-02-17"),
                new TmdbSeasonSummary(1, "Season 1", "/s1.jpg", 7, "2008-01-20"),
                new TmdbSeasonSummary(2, "Season 2", "/s2.jpg", 13, "2009-03-08"),
            ]));
    }
}
