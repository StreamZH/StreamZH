// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Requests;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Requests;

/// <summary>Availability tracking, the role defaults and limits of users, and the Discover status mapping.</summary>
public sealed class RequestAvailabilityTests : IDisposable
{
    private static readonly int Dune = FakeTmdbClient.Dune.TmdbId;
    private static readonly int Show = FakeTmdbClient.Show.TmdbId;

    private readonly RequestTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public async Task A_movie_becomes_available_when_its_file_is_imported()
    {
        var watcher = new RequestAvailabilityWatcher(_bed.Events, _bed.Availability, NullLogger<RequestAvailabilityWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);
        var jane = _bed.User("jane", UserRole.Friend);
        var request = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, Dune));
        _bed.Activities.Clear();

        var movie = _bed.Movies.Find(request.ItemId!.Value)!;
        _bed.GiveFile(movie);
        await _bed.Events.PublishAsync(new MediaImported(MediaType.Movie, movie.Id, null, [], movie.Title, [], movie.FolderPath, false, null, null));

        var available = _bed.Requests.Find(request.Id)!;
        Assert.Equal(RequestStatus.Available, available.Status);
        Assert.Equal(RequestTestBed.Now.UtcDateTime, available.Available);
        Assert.Equal(RequestActivityKind.Available, Assert.Single(_bed.Activities).Kind);
        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_series_is_partially_then_fully_available_for_the_requested_seasons_and_specials_do_not_count()
    {
        var jane = _bed.User("jane", UserRole.Friend);
        var request = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Series, Show, [1]));
        var id = request.ItemId!.Value;

        _bed.GiveFiles(id, (1, 1), (1, 2));
        Assert.Equal(1, await _bed.Availability.CheckAllAsync());
        Assert.Equal(RequestStatus.PartiallyAvailable, _bed.Requests.Find(request.Id)!.Status);

        // All 7 episodes of season 1; season 2 and the special have no files and are not part of the request.
        _bed.GiveFiles(id, [.. Enumerable.Range(1, 7).Select(e => (1, e))]);
        await _bed.Events.PublishAsync(new LibraryChanged(MediaType.Series, id));
        await _bed.Availability.CheckItemAsync(MediaType.Series, id);
        Assert.Equal(RequestStatus.Available, _bed.Requests.Find(request.Id)!.Status);
        Assert.Equal(0, await _bed.Availability.CheckAllAsync());
    }

    [Fact]
    public void Unaired_episodes_and_specials_are_ignored()
    {
        var today = "2026-10-18";
        Episode Ep(int season, int number, string? air, bool file) => new()
        {
            SeasonNumber = season, EpisodeNumber = number, AirDate = air, File = file ? new MediaFile("x.mkv", 1, DateTime.UtcNow) : null,
        };

        Assert.Equal(LibraryAvailability.Full, RequestAvailability.Of([Ep(1, 1, "2026-01-01", true), Ep(1, 2, "2027-01-01", false), Ep(0, 1, "2020-01-01", false)], null, today));
        Assert.Equal(LibraryAvailability.Partial, RequestAvailability.Of([Ep(1, 1, "2026-01-01", true), Ep(2, 1, "2026-02-01", false)], null, today));
        Assert.Equal(LibraryAvailability.Full, RequestAvailability.Of([Ep(1, 1, "2026-01-01", true), Ep(2, 1, "2026-02-01", false)], [1], today));
        Assert.Equal(LibraryAvailability.None, RequestAvailability.Of([Ep(3, 1, "2027-01-01", false), Ep(3, 2, null, false)], [3], today));
        Assert.Equal(new HashSet<int> { 1 }, RequestAvailability.AvailableSeasons([Ep(1, 1, "2026-01-01", true), Ep(2, 1, "2026-02-01", false)], today));
    }

    [Fact]
    public async Task A_removed_item_fails_the_request()
    {
        var jane = _bed.User("jane", UserRole.Friend);
        var request = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, Dune));

        await _bed.Library.DeleteMovieAsync(request.ItemId!.Value);
        await _bed.Availability.CheckAllAsync();

        var failed = _bed.Requests.Find(request.Id)!;
        Assert.Equal(RequestStatus.Failed, failed.Status);
        Assert.Equal(RequestAvailability.RemovedReason, failed.Reason);
    }

    [Fact]
    public async Task Approving_a_title_that_is_complete_makes_the_request_available_at_once_without_search()
    {
        var series = await _bed.Library.AddSeriesAsync(Show, _bed.SeriesRoot, monitored: false);
        await _bed.Library.RefreshSeriesAsync(series.Id, manual: false, CancellationToken.None);
        _bed.GiveFiles(series.Id, [.. Enumerable.Range(1, 7).Select(e => (1, e))]);
        var bob = _bed.User("bob", UserRole.Requester);
        var admin = _bed.User("dave", UserRole.Admin);
        var request = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Series, Show, [2]));

        // The admin widens the request to season 1, which is complete already.
        var approved = await _bed.Service.ApproveAsync(admin, request.Id, new ApproveRequestInput(Seasons: [1]));

        Assert.Equal(RequestStatus.Available, approved.Status);
        Assert.Empty(_bed.QueuedJobs());
        Assert.True(_bed.Series.Find(series.Id)!.Monitored);
    }

    [Fact]
    public void The_settings_are_the_defaults_of_users_without_own_values_and_owners_always_approve()
    {
        var defaults = new RequestSettings(_bed.Config);
        defaults.Save(new RequestSettingsValues(AutoApproveFriends: true, AutoApproveRequesters: false, DefaultMovieLimit: 3, DefaultSeriesLimit: 1, LimitDays: 14));
        Assert.True(defaults.AutoApprove(UserRole.Owner) && defaults.AutoApprove(UserRole.Admin));
        UserAccount User(UserRole role, bool? auto = null, int? movies = null, int? days = null) =>
            new() { Id = 1, Username = "jane", Role = role, AutoApprove = auto, MovieRequestLimit = movies, RequestLimitDays = days };

        Assert.Equal(new RequestPolicy(true, null, null, 14), UserRequestPolicy.For(User(UserRole.Owner), defaults));
        Assert.Equal(new RequestPolicy(true, null, null, 14), UserRequestPolicy.For(User(UserRole.Admin), defaults));
        Assert.Equal(new RequestPolicy(true, 3, 1, 14), UserRequestPolicy.For(User(UserRole.Friend), defaults));
        Assert.Equal(new RequestPolicy(false, 3, 1, 14), UserRequestPolicy.For(User(UserRole.Requester), defaults));
        Assert.Equal(new RequestPolicy(true, 10, null, 30), UserRequestPolicy.For(User(UserRole.Requester, auto: true, movies: 10, days: 30), defaults));
        Assert.Equal(1, UserRequestPolicy.For(User(UserRole.Friend), defaults).LimitFor(MediaType.Series));
    }

    [Fact]
    public async Task Discover_shows_library_and_request_state_per_title_and_caches_tmdb()
    {
        var tmdb = new FakeDiscoverTmdb();
        var settings = new FixedLanguage();
        var discover = new DiscoverService(tmdb, settings, new DiscoverCache(_bed.Time), new DiscoverLookup(_bed.Database), _bed.Series, _bed.Requests, _bed.Time);
        var dune = await _bed.Library.AddMovieAsync(Dune, _bed.MovieRoot, monitored: true);
        _bed.GiveFile(dune);
        var bob = _bed.User("bob", UserRole.Requester);
        var request = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, FakeTmdbClient.Arrival.TmdbId));
        var jane = _bed.User("jane", UserRole.Friend);
        var show = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Series, Show, [1]));
        _bed.GiveFiles(show.ItemId!.Value, (1, 1));
        await _bed.Availability.CheckAllAsync();

        var movies = await discover.ChartAsync(MediaType.Movie, TmdbChart.Trending, 1);
        await discover.ChartAsync(MediaType.Movie, TmdbChart.Trending, 1);
        var search = await discover.SearchAsync("breaking", 1);

        Assert.Equal(2, tmdb.Calls);
        Assert.Equal((1, 3), (movies.Page, movies.TotalPages));
        var duneState = movies.Results.Single(r => r.TmdbId == Dune).State;
        Assert.Equal(new TitleState(true, true, false, dune.Id, null, null), duneState);
        var arrivalState = movies.Results.Single(r => r.TmdbId == FakeTmdbClient.Arrival.TmdbId).State;
        Assert.Equal(new TitleState(false, false, false, null, RequestStatus.Pending, request.Id), arrivalState);
        var showState = Assert.Single(search.Results).State;
        Assert.Equal((true, false, true, RequestStatus.PartiallyAvailable, show.Id), (showState.InLibrary, showState.Available, showState.PartiallyAvailable, showState.Requested, showState.RequestId));
        Assert.Equal("Paul Atreides...", movies.Results[0].Overview);
        Assert.Equal("/dune-backdrop.jpg", movies.Results[0].BackdropPath);

        var details = await discover.DetailsAsync(MediaType.Series, Show, "US");
        Assert.Equal([1, 2], details.Seasons.Select(s => s.SeasonNumber));
        var season1 = details.Seasons[0];
        Assert.Equal((7, false, true, RequestStatus.PartiallyAvailable, show.Id), (season1.EpisodeCount, season1.Available, season1.PartiallyAvailable, season1.Requested, season1.RequestId));
        Assert.Equal((false, (RequestStatus?)null), (details.Seasons[1].Available, details.Seasons[1].Requested));

        var movie = await discover.DetailsAsync(MediaType.Movie, Dune, "US");
        Assert.Equal(("PG-13", 155, true), (movie.Certification, movie.Runtime, movie.Entry.State.Available));
        Assert.Empty(movie.Seasons);
    }

    private sealed class FixedLanguage : ITmdbSettings
    {
        public string? Token => "token";

        public string Language => "en-US";
    }
}
