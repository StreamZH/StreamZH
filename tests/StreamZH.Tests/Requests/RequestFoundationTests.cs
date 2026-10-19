// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Tests.Host;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Requests;

public sealed class RequestFoundationTests : IAsyncLifetime
{
    private readonly StreamZhFactory _factory = new();
    private readonly SearchTestBed _bed = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _factory.Dispose();
        _bed.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_requester_reaches_only_endpoints_that_opt_in()
    {
        await _factory.CreateOwnerClientAsync();
        var passwords = _factory.Services.GetRequiredService<PasswordService>();
        _factory.Services.GetRequiredService<UserRepository>().Create("jane", passwords.Hash("requester password 123"), UserRole.Requester);
        var jane = _factory.CreateClient();
        (await jane.PostAsJsonAsync("/api/v1/auth/login", new { username = "jane", password = "requester password 123" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await jane.GetAsync("/api/v1/settings/ui")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await jane.GetAsync("/api/v1/movies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await jane.GetAsync("/api/v1/series")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await jane.GetAsync("/api/v1/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await jane.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await jane.GetAsync("/api/v1/migration/runs")).StatusCode);
    }

    [Fact]
    public void Requests_round_trip_and_count_for_limits()
    {
        var repo = new RequestRepository(_bed.Database);
        var now = DateTime.UtcNow;
        var id = repo.Add(new MediaRequest
        {
            MediaType = MediaType.Series, TmdbId = 1399, Title = "Example Show", Year = 2011, Seasons = [1, 2],
            RequestedBy = 7, Source = RequestSource.Discord, DiscordUserId = "100000000000000001", Created = now, Updated = now,
        });
        repo.Add(new MediaRequest { MediaType = MediaType.Series, TmdbId = 1, Title = "Declined", RequestedBy = 7, Status = RequestStatus.Declined, Created = now, Updated = now });

        var request = repo.Find(id)!;
        Assert.Equal([1, 2], request.Seasons);
        Assert.Equal(RequestSource.Discord, request.Source);
        Assert.Single(repo.Open(MediaType.Series, 1399));
        Assert.Equal(1, repo.CountSince(7, MediaType.Series, now.AddDays(-7)));

        repo.Update(request with { Status = RequestStatus.Available, Available = now });
        Assert.Empty(repo.Open(MediaType.Series, 1399));
        Assert.Equal(2, repo.Query(new RequestQuery(RequestedBy: 7)).TotalRecords);
        Assert.Single(repo.Query(new RequestQuery([RequestStatus.Available])).Records);
    }

    [Fact]
    public void Invites_are_used_atomically_and_discord_links_are_unique()
    {
        var invites = new InviteRepository(_bed.Database);
        var id = invites.Add(new string('a', 64), UserRole.Requester, "for Jane", 1, DateTime.UtcNow.AddDays(1), 1);
        Assert.True(invites.TryUse(id, DateTime.UtcNow));
        Assert.False(invites.TryUse(id, DateTime.UtcNow));
        Assert.False(invites.Find(id)!.IsUsable(DateTime.UtcNow));

        var links = new DiscordLinkRepository(_bed.Database);
        links.Set(1, "100000000000000001", "jane");
        links.Set(2, "100000000000000001", "jane");
        Assert.Null(links.ForUser(1));
        Assert.Equal(2, links.ForDiscord("100000000000000001")!.UserId);
    }
}
