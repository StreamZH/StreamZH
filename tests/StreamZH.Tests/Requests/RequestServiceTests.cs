// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Library.Requests;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Requests;

/// <summary>The request lifecycle: create, auto-approve, approve, decline, cancel, retry, duplicates and limits.</summary>
public sealed class RequestServiceTests : IDisposable
{
    private static readonly int Dune = FakeTmdbClient.Dune.TmdbId;
    private static readonly int Arrival = FakeTmdbClient.Arrival.TmdbId;
    private static readonly int Show = FakeTmdbClient.Show.TmdbId;

    private readonly RequestTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public async Task A_friend_is_approved_at_once_the_movie_is_added_monitored_and_searched()
    {
        var jane = _bed.User("jane", UserRole.Friend);

        var request = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, Dune, Note: "  For movie night  "));

        Assert.Equal(RequestStatus.Processing, request.Status);
        Assert.Equal("Dune", request.Title);
        Assert.Equal(2021, request.Year);
        Assert.Equal("/dune.jpg", request.PosterPath);
        Assert.Equal("For movie night", request.Note);
        Assert.Equal(jane.UserId, request.RequestedBy);
        Assert.Null(request.DecidedBy);
        var movie = _bed.Movies.FindByTmdbId(Dune)!;
        Assert.True(movie.Monitored);
        Assert.Equal(movie.Id, request.ItemId);
        Assert.Equal(_bed.MovieRoot, request.RootFolderId);
        Assert.Equal($"search-new-movie:{movie.Id}", Assert.Single(_bed.QueuedJobs()).Key);
        Assert.Equal([RequestStatus.Pending, RequestStatus.Approved, RequestStatus.Processing], _bed.Changes.Select(c => c.Status));

        // One "new request" message, after the automatic approval (no separate "approved").
        Assert.Equal(RequestActivityKind.Created, Assert.Single(_bed.Activities).Kind);
    }

    [Fact]
    public async Task A_requester_waits_for_an_admin_and_nothing_is_added()
    {
        var bob = _bed.User("bob", UserRole.Requester);

        var request = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Dune));

        Assert.Equal(RequestStatus.Pending, request.Status);
        Assert.Null(request.ItemId);
        Assert.Null(_bed.Movies.FindByTmdbId(Dune));
        Assert.Empty(_bed.QueuedJobs());
        Assert.Equal(RequestActivityKind.Created, Assert.Single(_bed.Activities).Kind);
    }

    [Fact]
    public async Task Auto_approve_follows_the_settings_and_the_own_user_value()
    {
        _bed.Settings.Save(new RequestSettingsValues(AutoApproveFriends: false, AutoApproveRequesters: true));
        var friend = _bed.User("jane", UserRole.Friend);
        var requester = _bed.User("bob", UserRole.Requester);
        var trusted = _bed.User("carol", UserRole.Requester, autoApprove: false);
        var admin = _bed.User("dave", UserRole.Admin);

        Assert.Equal(RequestStatus.Pending, (await _bed.Service.CreateAsync(friend, new CreateRequestInput(MediaType.Movie, Dune))).Status);
        Assert.Equal(RequestStatus.Processing, (await _bed.Service.CreateAsync(requester, new CreateRequestInput(MediaType.Movie, Arrival))).Status);
        Assert.Equal(RequestStatus.Pending, (await _bed.Service.CreateAsync(trusted, new CreateRequestInput(MediaType.Series, Show, [1]))).Status);
        Assert.Equal(RequestStatus.Processing, (await _bed.Service.CreateAsync(admin, new CreateRequestInput(MediaType.Series, Show, [2]))).Status);
    }

    [Fact]
    public async Task Side_by_side_mode_adds_and_monitors_but_does_not_search()
    {
        _bed.Hold.Set(true);
        var jane = _bed.User("jane", UserRole.Friend);

        var movie = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, Dune));
        var show = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Series, Show, [1]));

        Assert.Equal(RequestStatus.Processing, movie.Status);
        Assert.Equal(RequestStatus.Processing, show.Status);
        Assert.True(_bed.Movies.FindByTmdbId(Dune)!.Monitored);
        Assert.Empty(_bed.QueuedJobs());
    }

    [Fact]
    public async Task Search_on_approve_can_be_switched_off()
    {
        _bed.Settings.Save(new RequestSettingsValues(SearchOnApprove: false));
        var jane = _bed.User("jane", UserRole.Friend);

        await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, Dune));

        Assert.NotNull(_bed.Movies.FindByTmdbId(Dune));
        Assert.Empty(_bed.QueuedJobs());
    }

    [Fact]
    public async Task A_series_request_monitors_only_the_requested_seasons()
    {
        var jane = _bed.User("jane", UserRole.Friend);

        var request = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Series, Show, [2, 2]));

        Assert.Equal([2], request.Seasons);
        var id = _bed.Series.TmdbIdToId()[Show];
        var episodes = _bed.Series.Episodes(id);
        Assert.All(episodes.Where(e => e.SeasonNumber == 2), e => Assert.True(e.Monitored));
        Assert.All(episodes.Where(e => e.SeasonNumber != 2), e => Assert.False(e.Monitored));
        var job = Assert.Single(_bed.QueuedJobs());
        Assert.Equal($"request-search:{request.Id}", job.Key);
    }

    [Fact]
    public async Task Monitor_all_monitors_every_season_but_never_specials()
    {
        _bed.Settings.Save(new RequestSettingsValues(SeriesMonitor: RequestSeriesMonitor.All));
        var jane = _bed.User("jane", UserRole.Friend);

        await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Series, Show, [1]));

        var episodes = _bed.Series.Episodes(_bed.Series.TmdbIdToId()[Show]);
        Assert.All(episodes, e => Assert.Equal(e.SeasonNumber > 0, e.Monitored));
    }

    [Fact]
    public async Task An_existing_series_keeps_what_it_monitors_and_gets_the_requested_season()
    {
        var series = await _bed.Library.AddSeriesAsync(Show, _bed.SeriesRoot, monitored: true);
        await _bed.Library.RefreshSeriesAsync(series.Id, manual: false, CancellationToken.None);
        _bed.Series.SetSeasonMonitored(series.Id, 1, true);
        _bed.Series.SetSeasonMonitored(series.Id, 2, false);
        var jane = _bed.User("jane", UserRole.Friend);

        var request = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Series, Show, [2]));

        Assert.Equal(series.Id, request.ItemId);
        Assert.All(_bed.Series.Episodes(series.Id).Where(e => e.SeasonNumber > 0), e => Assert.True(e.Monitored));
    }

    [Fact]
    public async Task An_admin_approves_with_a_root_folder_and_profile_and_declines_with_a_reason()
    {
        var bob = _bed.User("bob", UserRole.Requester);
        var admin = _bed.User("dave", UserRole.Admin);
        var otherRoot = _bed.RootFolders.Add(Path.Combine(_bed.Root, "movies-4k"), MediaType.Movie);
        var first = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Dune));
        var second = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Arrival));
        _bed.Activities.Clear();

        var approved = await _bed.Service.ApproveAsync(admin, first.Id, new ApproveRequestInput(RootFolderId: otherRoot));
        var declined = await _bed.Service.DeclineAsync(admin, second.Id, "  Not in 4K  ");

        Assert.Equal(RequestStatus.Processing, approved.Status);
        Assert.Equal(admin.UserId, approved.DecidedBy);
        Assert.Equal(otherRoot, _bed.Movies.FindByTmdbId(Dune)!.RootFolderId);
        Assert.Equal(RequestStatus.Declined, declined.Status);
        Assert.Equal("Not in 4K", declined.Reason);
        Assert.Null(_bed.Movies.FindByTmdbId(Arrival));
        Assert.Equal([RequestActivityKind.Approved, RequestActivityKind.Declined], _bed.Activities.Select(a => a.Kind));

        var again = await Assert.ThrowsAsync<RequestException>(() => _bed.Service.ApproveAsync(admin, second.Id));
        Assert.Equal(RequestError.Conflict, again.Error);
    }

    [Fact]
    public async Task Only_admins_approve_decline_and_retry()
    {
        var bob = _bed.User("bob", UserRole.Requester);
        var jane = _bed.User("jane", UserRole.Friend);
        var request = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Dune));

        Assert.Equal(RequestError.Forbidden, (await Assert.ThrowsAsync<RequestException>(() => _bed.Service.ApproveAsync(jane, request.Id))).Error);
        Assert.Equal(RequestError.Forbidden, (await Assert.ThrowsAsync<RequestException>(() => _bed.Service.DeclineAsync(bob, request.Id, null))).Error);
        Assert.Equal(RequestError.Forbidden, (await Assert.ThrowsAsync<RequestException>(() => _bed.Service.RetryAsync(jane, request.Id))).Error);
    }

    [Fact]
    public async Task Users_cancel_only_their_own_pending_requests()
    {
        var bob = _bed.User("bob", UserRole.Requester);
        var carol = _bed.User("carol", UserRole.Requester);
        var jane = _bed.User("jane", UserRole.Friend);
        var admin = _bed.User("dave", UserRole.Admin);
        var pending = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Dune));
        var processing = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, Arrival));

        Assert.Equal(RequestError.NotFound, (await Assert.ThrowsAsync<RequestException>(() => _bed.Service.DeleteAsync(carol, pending.Id))).Error);
        Assert.Equal(RequestError.Forbidden, (await Assert.ThrowsAsync<RequestException>(() => _bed.Service.DeleteAsync(jane, processing.Id))).Error);

        await _bed.Service.DeleteAsync(bob, pending.Id);
        await _bed.Service.DeleteAsync(admin, processing.Id);

        Assert.Null(_bed.Requests.Find(pending.Id));
        Assert.Null(_bed.Requests.Find(processing.Id));
        Assert.NotNull(_bed.Movies.FindByTmdbId(Arrival));
    }

    [Fact]
    public async Task A_duplicate_returns_the_open_request_and_an_available_title_is_refused()
    {
        var bob = _bed.User("bob", UserRole.Requester);
        var carol = _bed.User("carol", UserRole.Requester);
        var first = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Dune));

        var duplicate = await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(carol, new CreateRequestInput(MediaType.Movie, Dune)));
        Assert.Equal(RequestError.AlreadyRequested, duplicate.Error);
        Assert.Equal(first.Id, duplicate.Existing!.Id);

        var movie = await _bed.Library.AddMovieAsync(Arrival, _bed.MovieRoot, monitored: true);
        _bed.GiveFile(movie);
        var available = await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(carol, new CreateRequestInput(MediaType.Movie, Arrival)));
        Assert.Equal(RequestError.AlreadyAvailable, available.Error);
    }

    [Fact]
    public async Task Seasons_that_are_requested_or_available_are_left_out()
    {
        var bob = _bed.User("bob", UserRole.Requester);
        var carol = _bed.User("carol", UserRole.Requester);
        var first = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Series, Show, [1]));

        // "All seasons" while season 1 is requested: only season 2 is left.
        var rest = await _bed.Service.CreateAsync(carol, new CreateRequestInput(MediaType.Series, Show));
        Assert.Equal([2], rest.Seasons);

        var duplicate = await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(carol, new CreateRequestInput(MediaType.Series, Show, [1, 2])));
        Assert.Equal(RequestError.AlreadyRequested, duplicate.Error);
        Assert.Equal(first.Id, duplicate.Existing!.Id);

        var unknown = await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(carol, new CreateRequestInput(MediaType.Series, Show, [9])));
        Assert.Equal(("seasons", RequestError.Invalid), (unknown.Field, unknown.Error));
        var specials = await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(carol, new CreateRequestInput(MediaType.Series, Show, [0])));
        Assert.Equal("seasons", specials.Field);
    }

    [Fact]
    public async Task A_whole_series_request_stays_all_seasons()
    {
        var jane = _bed.User("jane", UserRole.Friend);

        var request = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Series, Show));

        Assert.Null(request.Seasons);
        Assert.All(_bed.Series.Episodes(request.ItemId!.Value), e => Assert.Equal(e.SeasonNumber > 0, e.Monitored));
    }

    [Fact]
    public async Task Limits_count_requests_in_the_window_and_say_when_the_next_one_is_possible()
    {
        _bed.Settings.Save(new RequestSettingsValues(AutoApproveRequesters: false, DefaultMovieLimit: 1, LimitDays: 7));
        var bob = _bed.User("bob", UserRole.Requester);
        var custom = _bed.User("carol", UserRole.Requester, movieLimit: 5, limitDays: 1);

        await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Dune));
        var limit = await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Arrival)));

        Assert.Equal(RequestError.LimitReached, limit.Error);
        Assert.Equal("limit", limit.Field);
        Assert.Contains("1 movie every 7 days", limit.Message, StringComparison.Ordinal);
        Assert.Contains("2026-10-25 12:00", limit.Message, StringComparison.Ordinal);

        // Series have their own limit (none here); own limits replace the global one.
        await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Series, Show, [1]));
        await _bed.Service.CreateAsync(custom, new CreateRequestInput(MediaType.Movie, Arrival));
    }

    [Fact]
    public async Task Declined_and_old_requests_do_not_count_for_the_limit()
    {
        _bed.Settings.Save(new RequestSettingsValues(DefaultMovieLimit: 1, LimitDays: 7));
        var bob = _bed.User("bob", UserRole.Requester);
        var admin = _bed.User("dave", UserRole.Admin);
        var first = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Dune));
        await _bed.Service.DeclineAsync(admin, first.Id, null);

        var second = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Dune));
        _bed.Requests.Update(second with { Created = RequestTestBed.Now.UtcDateTime.AddDays(-8) });

        Assert.Equal(RequestStatus.Pending, (await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Arrival))).Status);
    }

    [Fact]
    public async Task Disabled_users_switched_off_requests_and_bad_input_are_refused()
    {
        var gone = _bed.User("eve", UserRole.Friend, enabled: false);
        var jane = _bed.User("jane", UserRole.Friend);

        Assert.Equal(RequestError.Forbidden, (await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(gone, new CreateRequestInput(MediaType.Movie, Dune)))).Error);
        Assert.Equal("seasons", (await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, Dune, [1])))).Field);
        Assert.Equal("note", (await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, Dune, Note: new string('x', 1001))))).Field);
        Assert.Equal(RequestError.NotFound, (await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, 999)))).Error);

        _bed.Settings.Save(new RequestSettingsValues(Enabled: false));
        Assert.Equal(RequestError.Disabled, (await Assert.ThrowsAsync<RequestException>(() => _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, Dune)))).Error);
    }

    [Fact]
    public async Task Discord_guests_always_wait_and_are_stored_with_their_discord_name()
    {
        var guest = RequestActor.DiscordGuest("100000000000000001", "janedoe");

        var request = await _bed.Service.CreateAsync(guest, new CreateRequestInput(MediaType.Movie, Dune));

        Assert.Equal(RequestStatus.Pending, request.Status);
        Assert.Null(request.RequestedBy);
        Assert.Equal((RequestSource.Discord, "100000000000000001", "janedoe"), (request.Source, request.DiscordUserId, request.DiscordName));
        Assert.True(RequestService.CanSee(guest, request));
        Assert.False(RequestService.CanSee(RequestActor.DiscordGuest("100000000000000002", "other"), request));
    }

    [Fact]
    public async Task Without_a_root_folder_the_request_waits_and_a_failed_one_can_be_retried()
    {
        using var bed = new RequestTestBed(withRootFolders: false);
        var jane = bed.User("jane", UserRole.Friend);
        var admin = bed.User("dave", UserRole.Admin);

        var waiting = await bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, Dune));
        Assert.Equal(RequestStatus.Pending, waiting.Status);
        var noRoot = await Assert.ThrowsAsync<RequestException>(() => bed.Service.ApproveAsync(admin, waiting.Id));
        Assert.Equal("rootFolderId", noRoot.Field);

        // Approved with a root folder that disappears before the movie is added: failed, then retried.
        var root = bed.RootFolders.Add(Path.Combine(bed.Root, "movies"), MediaType.Movie);
        bed.Requests.Update(waiting with { Status = RequestStatus.Failed, RootFolderId = root, Reason = "Root folder not found." });
        var retried = await bed.Service.RetryAsync(admin, waiting.Id);
        Assert.Equal(RequestStatus.Processing, retried.Status);
        Assert.Null(retried.Reason);
        Assert.NotNull(bed.Movies.FindByTmdbId(Dune));

    }

    [Fact]
    public async Task An_unknown_profile_is_refused_and_a_library_error_fails_the_request_with_the_reason()
    {
        var admin = _bed.User("dave", UserRole.Admin);
        var bob = _bed.User("bob", UserRole.Requester);
        var request = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Movie, Dune));
        var profile = await Assert.ThrowsAsync<RequestException>(() => _bed.Service.ApproveAsync(admin, request.Id, new ApproveRequestInput(QualityProfileId: 999)));
        Assert.Equal("qualityProfileId", profile.Field);

        // TMDB lost the movie between request and approval.
        _bed.Requests.Update(request with { TmdbId = 999 });
        var failed = await _bed.Service.ApproveAsync(admin, request.Id);

        Assert.Equal(RequestStatus.Failed, failed.Status);
        Assert.Equal("Not found on TMDB.", failed.Reason);
        Assert.Equal(RequestStatus.Failed, _bed.Changes[^1].Status);
    }
}
