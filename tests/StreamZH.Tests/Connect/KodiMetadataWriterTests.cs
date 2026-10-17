// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Metadata;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Connect;

public sealed class KodiMetadataWriterTests : IDisposable
{
    private readonly LibraryFixture _library = new();
    private readonly FakeTmdbDetails _tmdb = new();
    private readonly FakeImages _images = new();
    private readonly MetadataConfig _settings;
    private readonly KodiMetadataWriter _writer;

    public KodiMetadataWriterTests()
    {
        _settings = new MetadataConfig(_library.Config, _library.Paths);
        _settings.UpdateKodi(new KodiMetadataOptions(Enabled: true));
        _writer = new KodiMetadataWriter(
            _library.Movies, _library.Series, _settings, _tmdb, new MetadataImageStore(_images, _settings), NullLogger<KodiMetadataWriter>.Instance);
    }

    private string MovieFile(string name) => Path.Combine(_library.MovieFolder, name);

    private string SeriesFile(string relative) => Path.Combine(_library.SeriesFolder, relative);

    [Fact]
    public async Task Movie_nfo_and_images_are_written_next_to_the_video()
    {
        _library.Config.Set(ConfigKeys.MetadataCertificationCountry, "DE");
        _library.AddMovieFile();

        var result = await _writer.WriteMovieAsync(_library.Movie.Id, default);

        Assert.Equal(new MetadataWriteResult(1, 2, 0, 0), result);
        Assert.Equal("DE", _tmdb.LastCountry);
        var nfo = File.ReadAllText(MovieFile("Dune (2021) WEBDL-1080p.nfo"));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<!--Created by StreamZH-->\n<movie>", nfo, StringComparison.Ordinal);
        var movie = XDocument.Parse(nfo).Root!;
        Assert.Equal("Dune", movie.Element("title")!.Value);
        Assert.Equal("Paul Atreides & the spice.", movie.Element("plot")!.Value);
        Assert.Equal("12", movie.Element("mpaa")!.Value);
        Assert.Equal("tt1160419", movie.Element("id")!.Value);
        Assert.Equal("438631", movie.Elements("uniqueid").Single(u => (string?)u.Attribute("type") == "tmdb").Value);
        Assert.Equal("7.8", movie.Element("ratings")!.Element("rating")!.Element("value")!.Value);
        Assert.Equal("Dune Collection", movie.Element("set")!.Element("name")!.Value);
        Assert.Equal(["Science Fiction", "Adventure"], movie.Elements("genre").Select(g => g.Value));
        Assert.Equal("Denis Villeneuve", movie.Element("director")!.Value);
        var actor = movie.Element("actor")!;
        Assert.Equal(("Timothée Chalamet", "Paul Atreides"), (actor.Element("name")!.Value, actor.Element("role")!.Value));
        Assert.Equal("https://image.tmdb.org/t/p/original/tc.jpg", actor.Element("thumb")!.Value);
        Assert.Equal("2021-09-15", movie.Element("premiered")!.Value);

        Assert.Equal("original/dune.jpg", File.ReadAllText(MovieFile("poster.jpg")));
        Assert.Equal("original/dune-back.jpg", File.ReadAllText(MovieFile("fanart.jpg")));

        // Second run: NFO rewritten, images kept.
        Assert.Equal(new MetadataWriteResult(1, 0, 0, 0), await _writer.WriteMovieAsync(_library.Movie.Id, default));
        Assert.Equal(2, _images.Downloads.Count);
    }

    [Fact]
    public async Task Options_movie_nfo_original_title_and_url_only()
    {
        var movie = _library.Movies.Find(_library.Movie.Id)!;
        movie.OriginalTitle = "Dune: Part One";
        _library.Movies.Update(movie);
        _library.AddMovieFile("Dune.mkv");
        _settings.Update(null, nfoOriginalTitle: true, imageCache: null);
        _settings.UpdateKodi(new KodiMetadataOptions(Enabled: true, UseMovieNfo: true, MovieImages: false, MovieMetadataUrl: true, MovieCollection: false));

        await _writer.WriteMovieAsync(_library.Movie.Id, default);

        Assert.False(File.Exists(MovieFile("Dune.nfo")));
        Assert.False(File.Exists(MovieFile("poster.jpg")));
        var text = File.ReadAllText(MovieFile("movie.nfo"));
        Assert.EndsWith("</movie>\nhttps://www.themoviedb.org/movie/438631\nhttps://www.imdb.com/title/tt1160419/\n", text, StringComparison.Ordinal);
        var root = XDocument.Parse(text[..(text.IndexOf("</movie>", StringComparison.Ordinal) + 8)]).Root!;
        Assert.Equal("Dune: Part One", root.Element("title")!.Value);
        Assert.Null(root.Element("set"));

        _settings.UpdateKodi(new KodiMetadataOptions(Enabled: true, MovieMetadata: false, MovieMetadataUrl: true, MovieImages: false));
        await _writer.WriteMovieAsync(_library.Movie.Id, default);
        Assert.False(File.Exists(MovieFile("movie.nfo")));
        Assert.Equal("# Created by StreamZH\nhttps://www.themoviedb.org/movie/438631\nhttps://www.imdb.com/title/tt1160419/\n", File.ReadAllText(MovieFile("Dune.nfo")));
    }

    [Fact]
    public async Task Rename_moves_our_nfo_and_keeps_the_release_nfo()
    {
        var old = _library.AddMovieFile("Dune.2021.mkv");
        await _writer.WriteMovieAsync(_library.Movie.Id, default);
        Assert.True(File.Exists(MovieFile("Dune.2021.nfo")));

        // Rename the video like Media Management does; a release NFO sits where the new NFO goes.
        File.Move(old, MovieFile("Dune (2021).mkv"));
        File.WriteAllText(MovieFile("Dune (2021).nfo"), "Scene release info");
        var movie = _library.Movies.Find(_library.Movie.Id)!;
        movie.File = new MediaFile("Dune (2021).mkv", 1024, DateTime.UtcNow);
        _library.Movies.Update(movie);

        var result = await _writer.WriteMovieAsync(_library.Movie.Id, default);

        Assert.False(File.Exists(MovieFile("Dune.2021.nfo")));
        Assert.Equal(1, result.Deleted + result.Moved);
        Assert.Equal("Scene release info", File.ReadAllText(MovieFile("Dune (2021).nfo-orig")));
        Assert.Contains("<!--Created by StreamZH-->", File.ReadAllText(MovieFile("Dune (2021).nfo")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Works_without_tmdb_from_the_library_data()
    {
        _tmdb.Fail = true;
        _library.AddMovieFile();

        await _writer.WriteMovieAsync(_library.Movie.Id, default);

        var movie = XDocument.Parse(File.ReadAllText(MovieFile("Dune (2021) WEBDL-1080p.nfo"))).Root!;
        Assert.Equal("Dune", movie.Element("title")!.Value);
        Assert.Null(movie.Element("mpaa"));
        Assert.Null(movie.Element("ratings"));
    }

    [Fact]
    public async Task Nothing_is_written_while_disabled_or_without_a_video()
    {
        Assert.Equal(MetadataWriteResult.None, await _writer.WriteMovieAsync(_library.Movie.Id, default));

        _settings.UpdateKodi(new KodiMetadataOptions(Enabled: false));
        _library.AddMovieFile();
        Assert.Equal(MetadataWriteResult.None, await _writer.WriteMovieAsync(_library.Movie.Id, default));
        Assert.Empty(Directory.EnumerateFiles(_library.MovieFolder, "*.nfo"));
    }

    [Fact]
    public async Task Series_episode_and_season_metadata()
    {
        _library.SetEpisodeFiles((1, 1, "Season 01/Breaking Bad - S01E01.mkv"), (1, 2, "Season 01/Breaking Bad - S01E02-E03.mkv"), (1, 3, "Season 01/Breaking Bad - S01E02-E03.mkv"));

        var result = await _writer.WriteSeriesAsync(_library.Show.Id, default);

        // tvshow.nfo + 2 episode NFOs; poster, fanart, 2 season posters, 2 thumbs.
        Assert.Equal(new MetadataWriteResult(3, 6, 0, 0), result);
        var show = XDocument.Parse(File.ReadAllText(SeriesFile("tvshow.nfo"))).Root!;
        Assert.Equal("tvshow", show.Name.LocalName);
        Assert.Equal("81189", show.Element("id")!.Value);
        Assert.Equal("TV-MA", show.Element("mpaa")!.Value);
        Assert.Equal("AMC", show.Element("studio")!.Value);
        Assert.Contains(show.Elements("thumb"), t => (string?)t.Attribute("season") == "1");
        Assert.True(File.Exists(SeriesFile("poster.jpg")));
        Assert.True(File.Exists(SeriesFile("fanart.jpg")));
        Assert.True(File.Exists(SeriesFile("season01-poster.jpg")));
        Assert.True(File.Exists(SeriesFile("season-specials-poster.jpg")));

        var multi = File.ReadAllText(SeriesFile("Season 01/Breaking Bad - S01E02-E03.nfo"));
        Assert.Equal(2, KodiNfo.ReadEpisodeNumbers(multi).Count);
        Assert.Equal(1, multi.Split("<?xml").Length - 1);
        var single = XDocument.Parse(File.ReadAllText(SeriesFile("Season 01/Breaking Bad - S01E01.nfo"))).Root!;
        Assert.Equal(("Episode 1", "1", "1", "2008-01-20"), (single.Element("title")!.Value, single.Element("season")!.Value, single.Element("episode")!.Value, single.Element("aired")!.Value));
        Assert.Equal("8.1", single.Element("ratings")!.Element("rating")!.Element("value")!.Value);
        Assert.Equal("Vince Gilligan", single.Element("director")!.Value);
        Assert.Equal("original/still-1-1.jpg", File.ReadAllText(SeriesFile("Season 01/Breaking Bad - S01E01-thumb.jpg")));
        Assert.Equal("original/still-1-2.jpg", File.ReadAllText(SeriesFile("Season 01/Breaking Bad - S01E02-E03-thumb.jpg")));
    }

    [Fact]
    public async Task Renamed_episode_files_take_their_nfo_and_thumb_along_and_removed_ones_lose_them()
    {
        _library.SetEpisodeFiles((1, 1, "Season 01/bb.s01e01.mkv"), (1, 2, "Season 01/bb.s01e02.mkv"));
        await _writer.WriteSeriesAsync(_library.Show.Id, default);
        var downloads = _images.Downloads.Count;

        File.Move(SeriesFile("Season 01/bb.s01e01.mkv"), SeriesFile("Season 01/Breaking Bad - S01E01 - Pilot.mkv"));
        File.Delete(SeriesFile("Season 01/bb.s01e02.mkv"));
        _library.SetEpisodeFiles((1, 1, "Season 01/Breaking Bad - S01E01 - Pilot.mkv"));

        var result = await _writer.WriteSeriesAsync(_library.Show.Id, default);

        Assert.Equal((1, 1), (result.Moved, result.Deleted));
        Assert.True(File.Exists(SeriesFile("Season 01/Breaking Bad - S01E01 - Pilot.nfo")));
        Assert.Equal("original/still-1-1.jpg", File.ReadAllText(SeriesFile("Season 01/Breaking Bad - S01E01 - Pilot-thumb.jpg")));
        Assert.Empty(Directory.EnumerateFiles(SeriesFile("Season 01"), "bb.*"));
        Assert.Equal(downloads, _images.Downloads.Count);
    }

    [Fact]
    public async Task Image_cache_is_filled_once_and_used_for_the_media_folders()
    {
        _settings.Update(null, null, imageCache: true);
        var store = new MetadataImageStore(_images, _settings);

        var cached = await store.GetCachedAsync("w342", "/dune.jpg", default);
        Assert.Equal(Path.Combine(_library.Paths.DataDirectory, "MediaCover", "w342", "dune.jpg"), cached);
        Assert.Equal("w342/dune.jpg", File.ReadAllText(cached));
        await store.GetCachedAsync("w342", "/dune.jpg", default);
        Assert.Single(_images.Downloads);

        _library.AddMovieFile();
        await _writer.WriteMovieAsync(_library.Movie.Id, default);
        await _writer.WriteMovieAsync(_library.Movie.Id, default);
        Assert.Equal(["w342/dune.jpg", "original/dune.jpg", "original/dune-back.jpg"], _images.Downloads);
        Assert.True(File.Exists(Path.Combine(_library.Paths.DataDirectory, "MediaCover", "original", "dune.jpg")));

        await Assert.ThrowsAsync<TmdbException>(() => store.GetCachedAsync("w342", "/../../etc/passwd", default));
        await Assert.ThrowsAsync<TmdbException>(() => store.GetCachedAsync("huge", "/dune.jpg", default));
    }

    [Fact]
    public async Task Imports_and_renames_queue_a_job_only_while_enabled()
    {
        var events = new InProcessEventBus();
        var jobs = new JobQueue(events);
        var handler = new MetadataEventHandler(events, jobs, _settings, _library.Movies, _library.Series);
        await handler.StartAsync(default);

        await events.PublishAsync(new MediaImported(MediaType.Movie, _library.Movie.Id, null, [], "Dune", [], _library.MovieFolder, false, null, null));
        await events.PublishAsync(new MediaRenamed(MediaType.Series, null, _library.Show.Id, _library.SeriesFolder));
        await events.PublishAsync(new MediaRenamed(MediaType.Series, null, _library.Show.Id, _library.SeriesFolder));
        Assert.Equal(2, jobs.Status.Queued);

        var queued = new List<Job>();
        while (jobs.Reader.TryRead(out var job))
        {
            queued.Add(job);
        }

        Assert.Equal(["Write metadata for Dune", "Write metadata for Breaking Bad"], queued.Select(j => j.Name));

        _settings.UpdateKodi(new KodiMetadataOptions(Enabled: false));
        var fresh = new JobQueue(events);
        var off = new MetadataEventHandler(events, fresh, _settings, _library.Movies, _library.Series);
        await off.StartAsync(default);
        await events.PublishAsync(new MediaImported(MediaType.Movie, _library.Movie.Id, null, [], "Dune", [], _library.MovieFolder, false, null, null));
        Assert.Equal(0, fresh.Status.Queued);
        await handler.StopAsync(default);
        await off.StopAsync(default);
    }

    public void Dispose() => _library.Dispose();

    private sealed class FakeTmdbDetails : ITmdbDetailsClient
    {
        public bool Fail { get; set; }

        public string? LastCountry { get; private set; }

        public Task<TmdbMovieDetails> GetMovieDetailsAsync(int tmdbId, string certificationCountry, CancellationToken cancellationToken = default)
        {
            LastCountry = certificationCountry;
            if (Fail)
            {
                throw new TmdbException("TMDB is not set up. Add your TMDB API key under Settings.");
            }

            var movie = new TmdbMovie(tmdbId, "tt1160419", "Dune", "Dune", 2021, "Paul", 155, "Released", ["Science Fiction"], "/dune.jpg", "/dune-back.jpg");
            return Task.FromResult(new TmdbMovieDetails(
                movie, "Beyond fear, destiny awaits.", 7.78, 12000, "2021-09-15", certificationCountry == "DE" ? "12" : "PG-13",
                ["Legendary Pictures"], ["United States of America"], "Dune Collection",
                [new TmdbCastMember("Timothée Chalamet", "Paul Atreides", 0, "/tc.jpg")], ["Denis Villeneuve"], ["Jon Spaihts"]));
        }

        public Task<TmdbSeriesDetails> GetSeriesDetailsAsync(int tmdbId, string certificationCountry, CancellationToken cancellationToken = default)
        {
            var series = new TmdbSeries(tmdbId, 81189, "tt0903747", "Breaking Bad", "Breaking Bad", 2008, null, "Ended", "AMC", ["Drama"], "/bb.jpg", "/bb-back.jpg", [0, 1]);
            return Task.FromResult(new TmdbSeriesDetails(
                series, 8.9, 15000, "2008-01-20", "TV-MA", ["AMC"], [],
                [new TmdbSeasonSummary(0, "Specials", "/s0.jpg"), new TmdbSeasonSummary(1, "Season 1", "/s1.jpg")]));
        }

        public Task<TmdbSeasonDetails> GetSeasonDetailsAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TmdbSeasonDetails(seasonNumber, null, "/s1.jpg", [.. Enumerable.Range(1, 3).Select(e => new TmdbEpisodeDetails(
                seasonNumber, e, $"Episode {e}", null, null, $"/still-{seasonNumber}-{e}.jpg", 8.1, 300, 47, ["Vince Gilligan"], []))]));
    }

    /// <summary>Writes "size/path" as the image content, so tests can see what was downloaded.</summary>
    private sealed class FakeImages : ITmdbImageClient
    {
        public ConcurrentQueue<string> DownloadQueue { get; } = new();

        public IReadOnlyList<string> Downloads => [.. DownloadQueue];

        public async Task DownloadAsync(string path, string size, Stream destination, CancellationToken cancellationToken = default)
        {
            DownloadQueue.Enqueue($"{size}{path}");
            await destination.WriteAsync(Encoding.UTF8.GetBytes($"{size}{path}"), cancellationToken);
        }
    }
}
