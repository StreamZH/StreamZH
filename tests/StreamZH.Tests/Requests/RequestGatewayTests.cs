// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Library.Discord;
using StreamZH.Library.Metadata;
using StreamZH.Library.Requests;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Requests;

/// <summary>The Discord bot's gateway to Discover and requests.</summary>
public sealed class RequestGatewayTests : IDisposable
{
    private static readonly int Dune = FakeTmdbClient.Dune.TmdbId;
    private static readonly int Show = FakeTmdbClient.Show.TmdbId;

    private readonly RequestTestBed _bed = new();
    private readonly FakeDiscoverTmdb _tmdb = new();
    private readonly ServiceProvider _services;
    private readonly RequestGateway _gateway;

    public RequestGatewayTests()
    {
        var discover = new DiscoverService(_tmdb, new EnglishTmdb(), new DiscoverCache(_bed.Time), new DiscoverLookup(_bed.Database), _bed.Series, _bed.Requests, _bed.Time);
        _services = new ServiceCollection().AddSingleton(_bed.Service).AddSingleton(discover).BuildServiceProvider();
        _gateway = new RequestGateway(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _bed.Users,
            new MetadataConfig(_bed.Config, new AppPaths(_bed.Root)),
            _bed.Hold,
            NullLogger<RequestGateway>.Instance);
    }

    public void Dispose()
    {
        _services.Dispose();
        _bed.Dispose();
    }

    [Fact]
    public async Task Search_and_details_carry_the_library_and_request_state()
    {
        var bob = _bed.User("bob", UserRole.Requester);
        var request = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Series, Show, [2]));

        var found = Assert.Single(await _gateway.SearchAsync(MediaType.Series, "breaking", default));
        var details = await _gateway.DetailsAsync(MediaType.Series, Show, default);

        Assert.Equal((Show, "Breaking Bad", 2008, "/bb.jpg", 8.9), (found.TmdbId, found.Title, found.Year, found.PosterPath, found.Rating));
        Assert.Equal(new RequestTitleState(false, false, RequestStatus.Pending, request.Id), found.State);
        Assert.Equal(
            [new RequestSeasonInfo(1, "Season 1", 7, false, null), new RequestSeasonInfo(2, "Season 2", 13, false, RequestStatus.Pending)],
            details!.Seasons);
        Assert.Null(await _gateway.DetailsAsync(MediaType.Movie, 999, default));
        Assert.Empty(await _gateway.SearchAsync(MediaType.Movie, "  ", default));
    }

    [Fact]
    public async Task A_linked_user_requests_with_their_role_and_a_guest_always_waits()
    {
        var jane = _bed.User("jane", UserRole.Friend);

        var linked = await _gateway.CreateAsync(new DiscordRequestDraft(MediaType.Movie, Dune, null, jane.UserId, "100000000000000001", "janedoe"), default);
        var guest = await _gateway.CreateAsync(new DiscordRequestDraft(MediaType.Series, Show, [1], null, "100000000000000002", "johndoe"), default);

        Assert.Equal(RequestOutcomeKind.Created, linked.Kind);
        Assert.Equal((RequestStatus.Processing, RequestSource.Discord, jane.UserId, "100000000000000001", (int?)null),
            (linked.Request!.Status, linked.Request.Source, linked.Request.RequestedBy, linked.Request.DiscordUserId, linked.Request.DecidedBy));
        Assert.Null(linked.Message);
        Assert.Equal((RequestStatus.Pending, (int?)null, "johndoe"), (guest.Request!.Status, guest.Request.RequestedBy, guest.Request.DiscordName));
        Assert.Contains(_bed.Changes, c => c.RequestId == guest.Request.Id && c.Status == RequestStatus.Pending);
    }

    [Fact]
    public async Task Refusals_become_outcomes()
    {
        var jane = _bed.User("jane", UserRole.Friend);
        var gone = _bed.User("eve", UserRole.Friend, enabled: false);
        var limited = _bed.User("bob", UserRole.Requester, movieLimit: 0, limitDays: 7);
        var first = await _gateway.CreateAsync(new DiscordRequestDraft(MediaType.Series, Show, [1], null, "100000000000000002", "johndoe"), default);

        async Task<RequestOutcome> Create(MediaType type, int tmdbId, int? user, IReadOnlyList<int>? seasons = null) =>
            await _gateway.CreateAsync(new DiscordRequestDraft(type, tmdbId, seasons, user, "100000000000000001", "janedoe"), default);

        var duplicate = await Create(MediaType.Series, Show, jane.UserId, [1]);
        Assert.Equal((RequestOutcomeKind.AlreadyRequested, first.Request!.Id), (duplicate.Kind, duplicate.Request!.Id));
        Assert.Equal(RequestOutcomeKind.NotAllowed, (await Create(MediaType.Movie, Dune, gone.UserId)).Kind);
        Assert.Equal(RequestOutcomeKind.NotAllowed, (await Create(MediaType.Movie, Dune, 999)).Kind);
        Assert.Equal(RequestOutcomeKind.LimitReached, (await Create(MediaType.Movie, Dune, limited.UserId)).Kind);
        Assert.Equal(RequestOutcomeKind.NotFound, (await Create(MediaType.Movie, 999, jane.UserId)).Kind);
        Assert.Equal(RequestOutcomeKind.Invalid, (await Create(MediaType.Series, Show, jane.UserId, [9])).Kind);

        var movie = await _bed.Library.AddMovieAsync(Dune, _bed.MovieRoot, monitored: true);
        _bed.GiveFile(movie);
        Assert.Equal(RequestOutcomeKind.AlreadyAvailable, (await Create(MediaType.Movie, Dune, jane.UserId)).Kind);

        _bed.Settings.Save(new RequestSettingsValues(Enabled: false));
        Assert.Equal(RequestOutcomeKind.NotAllowed, (await Create(MediaType.Movie, FakeTmdbClient.Arrival.TmdbId, jane.UserId)).Kind);
    }

    [Fact]
    public async Task Side_by_side_mode_is_mentioned_and_tmdb_problems_are_short_sentences()
    {
        _bed.Hold.Set(true);
        var jane = _bed.User("jane", UserRole.Friend);

        var created = await _gateway.CreateAsync(new DiscordRequestDraft(MediaType.Movie, Dune, null, jane.UserId, "100000000000000001", "janedoe"), default);
        Assert.Equal(RequestGateway.HoldMessage, created.Message);

        _tmdb.Failure = new TmdbException("TMDB is not reachable: timeout");
        var error = await Assert.ThrowsAsync<RequestGatewayException>(() => _gateway.SearchAsync(MediaType.Movie, "arrival", default));
        Assert.Equal(RequestGateway.TmdbProblem, error.Message);
    }

    private sealed class EnglishTmdb : ITmdbSettings
    {
        public string? Token => "token";

        public string Language => "en-US";
    }
}
