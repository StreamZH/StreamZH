// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Media;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Library.Notifications;
using StreamZH.Library.Requests;
using StreamZH.Tests.Connect;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Requests;

/// <summary>Request events in Connect: only connections with the request triggers on get them, with requester and poster.</summary>
public sealed class RequestNotificationTests : IAsyncLifetime
{
    private readonly RequestTestBed _bed = new();
    private readonly Recorder _requests = new("discord");
    private readonly Recorder _grabs = new("telegram");
    private ConnectDispatcher _dispatcher = null!;
    private RequestNotifier _notifier = null!;

    public async Task InitializeAsync()
    {
        var connections = new ConnectionRepository(_bed.Database);
        var service = new ConnectionService(connections, [_requests, _grabs], _bed.Config, _bed.Events, TimeProvider.System, NullLogger<ConnectionService>.Instance);
        _dispatcher = new ConnectDispatcher(
            _bed.Events, service, connections, new ConnectEventFactory(_bed.Movies, _bed.Series), new ConnectOptions([]), TimeProvider.System, NullLogger<ConnectDispatcher>.Instance);
        await _dispatcher.StartAsync(default);
        _notifier = new RequestNotifier(
            _bed.Events, _dispatcher, _bed.Requests, _bed.Users, _bed.Settings, _bed.Movies, _bed.Series, NullLogger<RequestNotifier>.Instance);
        await _notifier.StartAsync(default);

        Add(service, "discord", new ConnectionTriggers(OnRequestCreated: true, OnRequestApproved: true, OnRequestDeclined: true, OnRequestAvailable: true), new { webHookUrl = "https://example.com/hook" });
        Add(service, "telegram", new ConnectionTriggers(OnGrab: true), new { botToken = "123456:ABC-test", chatId = "100000001" });
    }

    public async Task DisposeAsync()
    {
        await _notifier.StopAsync(default);
        await _dispatcher.StopAsync(default);
        _bed.Dispose();
    }

    [Fact]
    public async Task New_auto_approved_declined_and_available_requests_reach_only_the_request_connections()
    {
        var bob = _bed.User("bob", UserRole.Requester);
        var jane = _bed.User("jane", UserRole.Friend);
        var admin = _bed.User("dave", UserRole.Admin);

        var pending = await _bed.Service.CreateAsync(bob, new CreateRequestInput(MediaType.Series, FakeTmdbClient.Show.TmdbId, [1], "Season 1 please"));
        await _bed.Service.DeclineAsync(admin, pending.Id, "Already on another server");
        var movie = await _bed.Service.CreateAsync(jane, new CreateRequestInput(MediaType.Movie, FakeTmdbClient.Dune.TmdbId));
        _bed.GiveFile(_bed.Movies.Find(movie.ItemId!.Value)!);
        await _bed.Availability.CheckAllAsync();
        await _dispatcher.WhenIdleAsync();

        Assert.Empty(_grabs.Received);
        Assert.Equal(
            ["New Series Request", "Series Request Declined", "Movie Request Automatically Approved", "Movie Now Available"],
            _requests.Received.Select(e => e.Subject));

        var created = _requests.Received[0];
        Assert.Equal(("Bob", "pending", false, "Season 1 please"), (created.Request!.RequestedBy, created.Request.Status, created.Request.Automatic, created.Request.Note));
        Assert.Equal("Breaking Bad (2008) · Season 1 · requested by Bob", created.Summary);
        Assert.Contains("Waiting for approval.", ConnectText.Body(created), StringComparison.Ordinal);
        Assert.Equal("Breaking Bad (2008) · Season 1 · requested by Bob · Already on another server", _requests.Received[1].Summary);
        Assert.Equal(movie.ItemId, _requests.Received[3].Media!.Id);

        // Discord: requester, seasons and the poster; webhook: the request block.
        var embed = DiscordSender.Payload(ConnectTestData.Context("discord", new { webHookUrl = "https://example.com/hook" }), created)["embeds"]![0]!;
        Assert.Equal("Season 1 please", embed["description"]!.GetValue<string>());
        Assert.Contains(embed["fields"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "Requested by" && f["value"]!.GetValue<string>() == "Bob");
        Assert.Equal("https://image.tmdb.org/t/p/w500/bb.jpg", embed["thumbnail"]!["url"]!.GetValue<string>());
        var hook = JsonDocument.Parse(WebhookSender.Payload(created, "My StreamZH").ToJsonString()).RootElement;
        Assert.Equal("RequestCreated", hook.GetProperty("eventType").GetString());
        Assert.Equal("Bob", hook.GetProperty("request").GetProperty("requestedBy").GetString());
        Assert.Equal([1], hook.GetProperty("request").GetProperty("seasons").EnumerateArray().Select(s => s.GetInt32()));
    }

    [Fact]
    public async Task Available_messages_can_be_switched_off_and_discord_guests_are_named()
    {
        _bed.Settings.Save(new RequestSettingsValues(NotifyOnAvailable: false));
        var guest = RequestActor.DiscordGuest("100000000000000001", "janedoe");
        var admin = _bed.User("dave", UserRole.Admin);
        var request = await _bed.Service.CreateAsync(guest, new CreateRequestInput(MediaType.Movie, FakeTmdbClient.Dune.TmdbId));
        var approved = await _bed.Service.ApproveAsync(admin, request.Id);
        _bed.GiveFile(_bed.Movies.Find(approved.ItemId!.Value)!);
        await _bed.Availability.CheckAllAsync();
        await _dispatcher.WhenIdleAsync();

        Assert.Equal(["New Movie Request", "Movie Request Approved"], _requests.Received.Select(e => e.Subject));
        Assert.Equal(("janedoe (Discord)", "discord"), (_requests.Received[0].Request!.RequestedBy, _requests.Received[0].Request!.Source));
    }

    private static void Add(ConnectionService service, string implementation, ConnectionTriggers triggers, object settings) =>
        service.Create(new ConnectionInput(
            implementation,
            implementation,
            true,
            triggers,
            JsonSerializer.SerializeToElement(settings).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));

    private sealed class Recorder(string implementation) : IConnectionSender
    {
        public string Implementation => implementation;

        public List<ConnectEvent> Received { get; } = [];

        public Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken)
        {
            lock (Received)
            {
                Received.Add(connectEvent);
            }

            return Task.CompletedTask;
        }

        public Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken) => Task.FromResult("ok");
    }
}
