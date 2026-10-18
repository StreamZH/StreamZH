// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Renamer;
using StreamZH.Library.Rss;
using StreamZH.Library.Search;
using StreamZH.Library.Titles;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.Titles;

public sealed class AlternativeTitleServiceTests : IDisposable
{
    private readonly SearchTestBed _bed = new();
    private readonly AlternativeTitleRepository _repository;
    private readonly FakeTitles _tmdb = new();

    public AlternativeTitleServiceTests()
    {
        _repository = new AlternativeTitleRepository(_bed.Database);
    }

    private AlternativeTitleService Service(string language = "de-DE") =>
        new(_repository, NullLogger<AlternativeTitleService>.Instance, _tmdb, new Settings("token", language));

    [Fact]
    public async Task Refresh_keeps_translations_and_usable_alternative_titles_without_duplicates()
    {
        _tmdb.Movie = new TmdbTitles(
            [
                new("Der Wüstenplanet", "DE", null),
                new("Dune", "US", null),
                new("Duna", "BR", null),
                new("Дюна", "RU", null),
                new("DUNE: Part One", "US", "working title"),
                new("Dune - Part One", "GB", null),
            ],
            [
                new("de", "DE", "Der Wüstenplanet"),
                new("de", "AT", "Dune: Der Wüstenplanet"),
                new("fr", "FR", "Dune: Première partie"),
                new("en", "US", "Dune"),
                new("en", "GB", "Dune: Part One"),
            ]);

        Assert.True(await Service().RefreshMovieAsync(_bed.Dune));

        var titles = _repository.For(MediaType.Movie, _bed.Dune.Id);
        Assert.Equal(
            ["Der Wüstenplanet", "Dune: Der Wüstenplanet", "Dune: Part One"],
            titles.Where(t => t.Source == AlternativeTitleSources.Translation).Select(t => t.Title).Order(StringComparer.Ordinal));
        Assert.Equal(["Duna"], titles.Where(t => t.Source == AlternativeTitleSources.Tmdb).Select(t => t.Title));
        var german = titles.Single(t => t.Title == "Der Wüstenplanet");
        Assert.Equal(Language.German.Id, german.LanguageId);
        Assert.Equal("derwuestenplanet", german.CleanTitle);
        Assert.Equal(Language.PortugueseBrazil.Id, titles.Single(t => t.Title == "Duna").LanguageId);
    }

    [Fact]
    public async Task The_metadata_and_original_language_are_translated_too()
    {
        _tmdb.Movie = new TmdbTitles([], [new("fr", "FR", "Dune : Première partie"), new("it", "IT", "Dune (Italia)")]);

        await Service("fr-FR").RefreshMovieAsync(_bed.Dune);

        Assert.Equal(["Dune : Première partie"], _repository.For(MediaType.Movie, _bed.Dune.Id).Select(t => t.Title));
    }

    [Fact]
    public async Task A_refresh_replaces_tmdb_titles_and_keeps_yours()
    {
        var service = Service();
        _tmdb.Series = new TmdbTitles([new("Breaking Bad – Reazioni collaterali", "IT", null)], [new("de", "DE", "Breaking Bad (DE)")]);
        await service.RefreshSeriesAsync(_bed.Show);
        var mine = service.AddUserTitle(MediaType.Series, _bed.Show.Id, _bed.Show.Title, _bed.Show.OriginalTitle, " Breaking Bad German ", 2);

        _tmdb.Series = new TmdbTitles([], [new("de", "DE", "Breaking Bad German"), new("de", "CH", "Breaking Bad CH")]);
        await service.RefreshSeriesAsync(_bed.Show);

        var titles = _repository.For(MediaType.Series, _bed.Show.Id);
        Assert.Equal(["Breaking Bad CH", "Breaking Bad German"], titles.Select(t => t.Title).Order(StringComparer.Ordinal));
        var user = titles.Single(t => t.Source == AlternativeTitleSources.User);
        Assert.Equal(mine.Id, user.Id);
        Assert.Equal(2, user.SeasonNumber);
    }

    [Fact]
    public async Task Tmdb_problems_keep_the_old_titles()
    {
        var service = Service();
        _tmdb.Movie = new TmdbTitles([], [new("de", "DE", "Der Wüstenplanet")]);
        await service.RefreshMovieAsync(_bed.Dune);

        _tmdb.Error = new TmdbException("TMDB is not reachable: timeout");
        Assert.False(await service.RefreshMovieAsync(_bed.Dune));

        Assert.Equal(["Der Wüstenplanet"], _repository.For(MediaType.Movie, _bed.Dune.Id).Select(t => t.Title));
    }

    [Fact]
    public void Your_titles_are_validated_and_only_yours_can_be_removed()
    {
        var service = Service();
        var id = _bed.Dune.Id;

        Assert.Equal("title", Assert.Single(Assert.Throws<ValidationFailedException>(() => service.AddUserTitle(MediaType.Movie, id, "Dune", "Dune", " ", null)).Errors).Key);
        Assert.Contains(
            "already known",
            Assert.Throws<ValidationFailedException>(() => service.AddUserTitle(MediaType.Movie, id, "Dune", "Dune", "DUNE", null)).Errors["title"][0],
            StringComparison.Ordinal);
        Assert.True(Assert.Throws<ValidationFailedException>(() => service.AddUserTitle(MediaType.Movie, id, "Dune", "Dune", "Wüstenplanet", 1)).Errors.ContainsKey("seasonNumber"));
        Assert.True(Assert.Throws<ValidationFailedException>(() => service.AddUserTitle(MediaType.Movie, id, "Dune", "Dune", new string('x', 201), null)).Errors.ContainsKey("title"));

        var added = service.AddUserTitle(MediaType.Movie, id, "Dune", "Dune", "Wüstenplanet", null);
        Assert.Equal("wuestenplanet", added.CleanTitle);
        Assert.Throws<ValidationFailedException>(() => service.AddUserTitle(MediaType.Movie, id, "Dune", "Dune", "Wuestenplanet", null));

        var tmdbTitle = _repository.Add(new AlternativeTitle { MediaType = MediaType.Movie, ItemId = id, Title = "Duna", CleanTitle = "duna", Source = AlternativeTitleSources.Tmdb });
        Assert.Equal(LibraryError.Conflict, Assert.Throws<LibraryException>(() => service.DeleteUserTitle(MediaType.Movie, id, tmdbTitle)).Error);
        Assert.Equal(LibraryError.NotFound, Assert.Throws<LibraryException>(() => service.DeleteUserTitle(MediaType.Series, id, added.Id)).Error);

        service.DeleteUserTitle(MediaType.Movie, id, added.Id);
        Assert.Equal(["Duna"], _repository.For(MediaType.Movie, id).Select(t => t.Title));
    }

    [Fact]
    public async Task Adding_and_removing_a_movie_fetches_and_drops_its_titles()
    {
        _repository.Add(new AlternativeTitle { MediaType = MediaType.Movie, ItemId = 2, Title = "Left over", CleanTitle = "leftover", Source = AlternativeTitleSources.User });
        _tmdb.Movie = new TmdbTitles([], [new("de", "DE", "Arrival (DE)")]);
        var library = new LibraryService(
            new ArrivalTmdb(), new RootFolderRepository(_bed.Database), _bed.Movies, _bed.Series, new QualityProfileService(_bed.Profiles, _bed.Config, NullLogger<QualityProfileService>.Instance),
            _bed.Events, NullLogger<LibraryService>.Instance, null, Service());
        var root = new RootFolderRepository(_bed.Database).All().Single();

        var movie = await library.AddMovieAsync(329865, root.Id, monitored: true);

        Assert.Equal(2, movie.Id);
        Assert.Equal(["Arrival (DE)"], _repository.For(MediaType.Movie, movie.Id).Select(t => t.Title));

        await library.DeleteMovieAsync(movie.Id);
        Assert.Empty(_repository.For(MediaType.Movie, movie.Id));
    }

    [Fact]
    public async Task Search_and_rss_use_the_alternative_titles()
    {
        _repository.Add(new AlternativeTitle
        {
            MediaType = MediaType.Movie, ItemId = _bed.Dune.Id, Title = "Der Wüstenplanet", CleanTitle = "derwuestenplanet",
            Source = AlternativeTitleSources.Translation, LanguageId = Language.German.Id,
        });
        _bed.AddIndexer(1, "Alpha");
        var release = _bed.Release("Der.Wuestenplanet.2021.German.DL.1080p.WEB.H264-GRP", 6);
        _bed.Client.Returns(1, release);
        _bed.Client.ReturnsRecent(1, release);
        var search = new ReleaseSearchService(
            _bed.Indexers, _bed.Client, _bed.Movies, _bed.Series, new DecisionTargetFactory(_bed.Profiles), _bed.Definitions, _bed.Cache,
            NullLogger<ReleaseSearchService>.Instance, _bed.Time, _bed.Options, _repository);

        var result = await search.SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);

        Assert.True(Assert.Single(result.Decisions).Accepted);
        var criteria = Assert.IsType<MovieSearchCriteria>(Assert.Single(_bed.Client.Searches).Criteria);
        Assert.Equal(["Der Wüstenplanet", "Dune"], criteria.Titles);

        var rss = new RssSyncService(
            _bed.Indexers, _bed.Client, _bed.Movies, _bed.Series, new DecisionTargetFactory(_bed.Profiles), _bed.Definitions, _bed.Grab(), _bed.History,
            _bed.Downloads, _bed.Options, NullLogger<RssSyncService>.Instance, _bed.Engine, _bed.Time, alternativeTitles: _repository);
        var synced = await rss.SyncAsync();
        Assert.Equal(1, synced.Matched);
        Assert.Single(synced.Grabbed);
    }

    [Fact]
    public async Task Search_alternative_titles_can_be_switched_off()
    {
        _repository.Add(new AlternativeTitle
        {
            MediaType = MediaType.Movie, ItemId = _bed.Dune.Id, Title = "Der Wüstenplanet", CleanTitle = "derwuestenplanet",
            Source = AlternativeTitleSources.Translation, LanguageId = Language.German.Id,
        });
        _bed.Options.Save(_bed.Options.Values with { SearchAlternativeTitles = false });
        _bed.AddIndexer(1, "Alpha");
        var search = new ReleaseSearchService(
            _bed.Indexers, _bed.Client, _bed.Movies, _bed.Series, new DecisionTargetFactory(_bed.Profiles), _bed.Definitions, _bed.Cache,
            NullLogger<ReleaseSearchService>.Instance, _bed.Time, _bed.Options, _repository);

        await search.SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);

        Assert.Equal(["Dune"], Assert.IsType<MovieSearchCriteria>(Assert.Single(_bed.Client.Searches).Criteria).Titles);
        Assert.False(_bed.Options.Values.SearchAlternativeTitles);
    }

    [Fact]
    public async Task Episode_searches_use_titles_of_the_season_german_first()
    {
        foreach (var (title, season, language) in new[] { ("Breaking Bad DE", (int?)null, Language.German.Id), ("Breaking Bad Season Two", 2, 0), ("Breaking Bad Season One", 1, 0) })
        {
            _repository.Add(new AlternativeTitle
            {
                MediaType = MediaType.Series, ItemId = _bed.Show.Id, Title = title, CleanTitle = AlternativeTitleRules.CleanTitle(title),
                Source = season is null ? AlternativeTitleSources.Translation : AlternativeTitleSources.Xem, LanguageId = language > 0 ? language : null,
                SeasonNumber = season,
            });
        }

        _bed.AddIndexer(1, "Alpha");
        var search = new ReleaseSearchService(
            _bed.Indexers, _bed.Client, _bed.Movies, _bed.Series, new DecisionTargetFactory(_bed.Profiles), _bed.Definitions, _bed.Cache,
            NullLogger<ReleaseSearchService>.Instance, _bed.Time, _bed.Options, _repository);

        await search.SearchEpisodesAsync(_bed.Show.Id, 2, [_bed.Episode(2, 1).Id], IndexerUse.InteractiveSearch);

        var criteria = Assert.IsType<EpisodeSearchCriteria>(Assert.Single(_bed.Client.Searches).Criteria);
        Assert.Equal(["Breaking Bad DE", "Breaking Bad", "Breaking Bad Season Two"], criteria.Titles);
    }

    [Fact]
    public void The_renamer_finds_library_items_by_alternative_titles()
    {
        _repository.Add(new AlternativeTitle
        {
            MediaType = MediaType.Series, ItemId = _bed.Show.Id, Title = "Breaking Bad – Die Serie", CleanTitle = AlternativeTitleRules.CleanTitle("Breaking Bad – Die Serie"),
            Source = AlternativeTitleSources.User,
        });
        var catalog = new RenamerCatalog(
            _bed.Movies, _bed.Series, new MediaFileDetailsRepository(_bed.Database), new RenamerTmdbCache(), alternativeTitles: _repository);

        Assert.Equal(_bed.Show.Id, catalog.MatchSeries("Breaking Bad Die Serie", 0)?.Id);
        Assert.Null(new RenamerCatalog(_bed.Movies, _bed.Series, new MediaFileDetailsRepository(_bed.Database), new RenamerTmdbCache())
            .MatchSeries("Breaking Bad Die Serie", 0));
    }

    public void Dispose() => _bed.Dispose();

    private sealed record Settings(string? Token, string Language) : ITmdbSettings;

    private sealed class FakeTitles : ITmdbTitlesClient
    {
        public TmdbTitles Movie { get; set; } = TmdbTitles.None;
        public TmdbTitles Series { get; set; } = TmdbTitles.None;
        public TmdbException? Error { get; set; }

        public Task<TmdbTitles> GetMovieTitlesAsync(int tmdbId, CancellationToken cancellationToken = default) =>
            Error is { } e ? Task.FromException<TmdbTitles>(e) : Task.FromResult(Movie);

        public Task<TmdbTitles> GetSeriesTitlesAsync(int tmdbId, CancellationToken cancellationToken = default) =>
            Error is { } e ? Task.FromException<TmdbTitles>(e) : Task.FromResult(Series);
    }

    private sealed class ArrivalTmdb : ITmdbClient
    {
        public bool IsConfigured => true;

        public Task<IReadOnlyList<TmdbSearchResult>> SearchMoviesAsync(string query, int? year, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbSearchResult>> SearchSeriesAsync(string query, int? year, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TmdbMovie> GetMovieAsync(int tmdbId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TmdbMovie(329865, "tt2543164", "Arrival", "Arrival", 2016, null, 116, "Released", [], null, null, "en"));

        public Task<TmdbSeries> GetSeriesAsync(int tmdbId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task TestAsync(string? token = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
