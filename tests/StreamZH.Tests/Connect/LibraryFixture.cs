// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Tests.Connect;

/// <summary>A temporary database and media folder with one movie (Dune) and one series (Breaking Bad, season 1).</summary>
public sealed class LibraryFixture : IDisposable
{
    public LibraryFixture()
    {
        Directory.CreateDirectory(Root);
        Database = new Database(Path.Combine(Root, "streamzh.db"));
        Database.Migrate();
        Config = new ConfigRepository(Database);
        Movies = new MovieRepository(Database);
        Series = new SeriesRepository(Database);
        Connections = new ConnectionRepository(Database);
        Paths = new AppPaths(Path.Combine(Root, "data"));

        var roots = new RootFolderRepository(Database);
        var movieRoot = roots.Add(Path.Combine(Root, "movies"), MediaType.Movie);
        var tvRoot = roots.Add(Path.Combine(Root, "tv"), MediaType.Series);

        MovieFolder = Path.Combine(Root, "movies", "Dune (2021)");
        Directory.CreateDirectory(MovieFolder);
        Movie = new Movie
        {
            TmdbId = 438631,
            ImdbId = "tt1160419",
            Title = "Dune",
            OriginalTitle = "Dune",
            SortTitle = "dune",
            Year = 2021,
            Overview = "Paul Atreides & the spice.",
            Runtime = 155,
            Status = "Released",
            Genres = ["Science Fiction", "Adventure"],
            PosterPath = "/dune.jpg",
            BackdropPath = "/dune-back.jpg",
            QualityProfileId = 1,
            RootFolderId = movieRoot,
            FolderPath = MovieFolder,
            Added = DateTime.UtcNow,
        };
        Movies.Add(Movie);

        SeriesFolder = Path.Combine(Root, "tv", "Breaking Bad");
        Directory.CreateDirectory(Path.Combine(SeriesFolder, "Season 01"));
        Show = new Series
        {
            TmdbId = 1396,
            TvdbId = 81189,
            ImdbId = "tt0903747",
            Title = "Breaking Bad",
            OriginalTitle = "Breaking Bad",
            SortTitle = "breaking bad",
            Year = 2008,
            Overview = "Walter White...",
            Status = "Ended",
            Network = "AMC",
            Genres = ["Drama"],
            PosterPath = "/bb.jpg",
            BackdropPath = "/bb-back.jpg",
            QualityProfileId = 1,
            RootFolderId = tvRoot,
            FolderPath = SeriesFolder,
            Added = DateTime.UtcNow,
        };
        Series.Add(Show);
        Series.UpsertEpisodes(
            Show.Id,
            [.. Enumerable.Range(1, 3).Select(e => new Episode { SeasonNumber = 1, EpisodeNumber = e, Title = $"Episode {e}", AirDate = $"2008-01-{19 + e}" })],
            monitorNew: true);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-connect-{Guid.NewGuid():N}");

    public Database Database { get; }

    public ConfigRepository Config { get; }

    public MovieRepository Movies { get; }

    public SeriesRepository Series { get; }

    public ConnectionRepository Connections { get; }

    public AppPaths Paths { get; }

    public Movie Movie { get; }

    public Series Show { get; }

    public string MovieFolder { get; }

    public string SeriesFolder { get; }

    public IReadOnlyList<Episode> Episodes => Series.Episodes(Show.Id);

    /// <summary>Creates the movie's video file and stores it as the movie file.</summary>
    public string AddMovieFile(string name = "Dune (2021) WEBDL-1080p.mkv")
    {
        var path = Path.Combine(MovieFolder, name);
        File.WriteAllBytes(path, new byte[1024]);
        var movie = Movies.Find(Movie.Id)!;
        movie.File = new MediaFile(name, 1024, DateTime.UtcNow);
        Movies.Update(movie);
        return path;
    }

    /// <summary>Creates the video files and stores them as the episode files ((season, episode) → path relative to the series folder).</summary>
    public void SetEpisodeFiles(params (int Season, int Episode, string RelativePath)[] files)
    {
        foreach (var file in files.Select(f => f.RelativePath).Distinct())
        {
            var path = Path.Combine(SeriesFolder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, new byte[512]);
            }
        }

        Series.SetEpisodeFiles(Show.Id, files.ToDictionary(f => (f.Season, f.Episode), f => new MediaFile(f.RelativePath, 512, DateTime.UtcNow)));
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
