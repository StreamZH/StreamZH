// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Library.Discord;

namespace StreamZH.Tests.Discord;

public sealed class DiscordNotifierTests : IDisposable
{
    private const string NotifyChannel = "100000000000000040";

    private readonly DiscordTestBed _bed = new();
    private readonly DiscordNotifier _notifier;

    public DiscordNotifierTests()
    {
        _bed.Configure(new DiscordSettingsChange { Enabled = true, BotToken = DiscordTestBed.Token });
        _notifier = new DiscordNotifier(
            _bed.Events, _bed.Settings, _bed.Api, _bed.Requests, _bed.Links, _bed.Users, _bed.Status, NullLogger<DiscordNotifier>.Instance);
    }

    public void Dispose()
    {
        _notifier.Dispose();
        _bed.Dispose();
    }

    private int Add(RequestStatus status, int? requestedBy = null, string? discordUserId = null, int? decidedBy = 1, string? reason = null)
    {
        var now = DateTime.UtcNow;
        return _bed.Requests.Add(new MediaRequest
        {
            MediaType = MediaType.Series, TmdbId = 1399, Title = "Example Show", Year = 2011, PosterPath = "/poster1399.jpg", Seasons = [1],
            Status = status, RequestedBy = requestedBy, DiscordUserId = discordUserId, DiscordName = discordUserId is null ? null : "guest",
            DecidedBy = decidedBy, Reason = reason, Created = now, Updated = now,
        });
    }

    [Fact]
    public async Task An_admins_approval_goes_to_the_linked_user_by_direct_message_and_to_the_channel()
    {
        _bed.LinkJane();
        _bed.Configure(new DiscordSettingsChange { NotifyChannelId = NotifyChannel });
        var id = Add(RequestStatus.Approved, requestedBy: _bed.JaneId);

        await _notifier.NotifyAsync(new RequestChanged(id, RequestStatus.Approved), default);

        Assert.Equal(DiscordTestBed.JaneDiscordId, Assert.Single(_bed.Api.DirectMessagesOpened));
        var sent = _bed.Api.Sent.ToList();
        Assert.Equal(2, sent.Count);
        var dm = sent.Single(s => s.ChannelId == "dm-" + DiscordTestBed.JaneDiscordId).Message;
        Assert.Equal("Approved: Example Show (2011)", dm.Embeds![0].Title);
        Assert.Equal("Season 1", dm.Embeds[0].Footer!.Text);

        var channel = sent.Single(s => s.ChannelId == NotifyChannel).Message;
        Assert.Equal($"<@{DiscordTestBed.JaneDiscordId}>", channel.Content);
        Assert.Equal([DiscordTestBed.JaneDiscordId], channel.AllowedMentions.Users);
        Assert.Empty(channel.AllowedMentions.Parse);
        Assert.Contains("Requested by jane", channel.Embeds![0].Footer!.Text, StringComparison.Ordinal);

        // The same change twice is announced once.
        await _notifier.NotifyAsync(new RequestChanged(id, RequestStatus.Approved), default);
        Assert.Equal(2, _bed.Api.Sent.Count);
    }

    [Fact]
    public async Task A_declined_guest_request_tells_the_guest_why()
    {
        var id = Add(RequestStatus.Declined, discordUserId: DiscordTestBed.GuestDiscordId, reason: "Not on *our* list");

        await _notifier.NotifyAsync(new RequestChanged(id, RequestStatus.Declined), default);

        var (channel, message) = Assert.Single(_bed.Api.Sent);
        Assert.Equal("dm-" + DiscordTestBed.GuestDiscordId, channel);
        Assert.Equal("Reason: Not on \\*our\\* list", message.Embeds![0].Description);
    }

    [Fact]
    public async Task Auto_approvals_stale_events_and_switched_off_messages_send_nothing()
    {
        _bed.LinkJane();
        var auto = Add(RequestStatus.Approved, requestedBy: _bed.JaneId, decidedBy: null);
        await _notifier.NotifyAsync(new RequestChanged(auto, RequestStatus.Approved), default);

        // The request moved on since the event was published.
        var moved = Add(RequestStatus.Processing, requestedBy: _bed.JaneId);
        await _notifier.NotifyAsync(new RequestChanged(moved, RequestStatus.Approved), default);

        _bed.Configure(new DiscordSettingsChange { DmRequesters = false });
        var available = Add(RequestStatus.Available, requestedBy: _bed.JaneId);
        await _notifier.NotifyAsync(new RequestChanged(available, RequestStatus.Available), default);

        _bed.Configure(new DiscordSettingsChange { DmRequesters = true, Enabled = false });
        await _notifier.NotifyAsync(new RequestChanged(available, RequestStatus.Available), default);

        Assert.Empty(_bed.Api.Sent);
    }

    [Fact]
    public async Task Users_who_block_direct_messages_and_a_closed_channel_are_handled()
    {
        _bed.Configure(new DiscordSettingsChange { NotifyChannelId = NotifyChannel });
        _bed.Api.OpenDmError = _ => new DiscordApiException("no DMs", HttpStatusCode.Forbidden, DiscordApiException.CannotDmUser);
        _bed.Api.SendError = _ => new DiscordApiException("Missing Access", HttpStatusCode.Forbidden, DiscordApiException.MissingAccess);
        var id = Add(RequestStatus.Available, discordUserId: DiscordTestBed.GuestDiscordId);

        await _notifier.NotifyAsync(new RequestChanged(id, RequestStatus.Available), default);

        Assert.Empty(_bed.Api.Sent);
        Assert.StartsWith(DiscordNotifier.ChannelProblem, _bed.Status.Current.PermissionProblem, StringComparison.Ordinal);

        // Once the channel works again the problem clears.
        _bed.Api.SendError = _ => null;
        var next = Add(RequestStatus.Available, discordUserId: DiscordTestBed.GuestDiscordId);
        await _notifier.NotifyAsync(new RequestChanged(next, RequestStatus.Available), default);
        Assert.Null(_bed.Status.Current.PermissionProblem);
    }

    [Fact]
    public async Task Events_from_the_bus_are_delivered_in_the_background()
    {
        _bed.LinkJane();
        var id = Add(RequestStatus.Available, requestedBy: _bed.JaneId);
        await _notifier.StartAsync(default);

        await _bed.Events.PublishAsync(new RequestChanged(id, RequestStatus.Processing));
        await _bed.Events.PublishAsync(new RequestChanged(id, RequestStatus.Available));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_bed.Api.Sent.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await _notifier.StopAsync(default);
        var (_, message) = Assert.Single(_bed.Api.Sent);
        Assert.StartsWith("Available:", message.Embeds![0].Title, StringComparison.Ordinal);
    }
}
