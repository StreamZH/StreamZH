// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Library.Discord;

namespace StreamZH.Tests.Discord;

/// <summary>A gateway whose sessions follow a script: each RunAsync call takes the next step (the last one repeats).</summary>
public sealed class FakeGateway : IDiscordGateway
{
    public ConcurrentQueue<DiscordGatewayLogin> Logins { get; } = new();

    public ConcurrentQueue<Func<Func<DiscordGatewayEvent, CancellationToken, Task>, CancellationToken, Task>> Script { get; } = new();

    private Func<Func<DiscordGatewayEvent, CancellationToken, Task>, CancellationToken, Task> _last = (_, ct) => Task.Delay(Timeout.Infinite, ct);

    public Task RunAsync(DiscordGatewayLogin login, Func<DiscordGatewayEvent, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        Logins.Enqueue(login);
        if (Script.TryDequeue(out var step))
        {
            _last = step;
        }

        return _last(onEvent, cancellationToken);
    }

    public static Func<Func<DiscordGatewayEvent, CancellationToken, Task>, CancellationToken, Task> Fail(bool fatal, TimeSpan? retryAfter = null) =>
        (_, _) => throw new DiscordGatewayException(fatal ? "Discord rejected the bot token." : "The connection to Discord was lost.", fatal, retryAfter: retryAfter);

    public static Func<Func<DiscordGatewayEvent, CancellationToken, Task>, CancellationToken, Task> Connect(params DiscordGatewayEvent[] events) =>
        async (onEvent, ct) =>
        {
            foreach (var e in events)
            {
                await onEvent(e, ct);
            }

            await Task.Delay(Timeout.Infinite, ct);
        };
}

public sealed class DiscordBotServiceTests : IDisposable
{
    private static readonly DiscordReady Ready = new(
        new DiscordUser(FakeDiscordApi.BotId, "streamzh-bot", "StreamZH"), FakeDiscordApi.ApplicationId, [DiscordTestBed.GuildId, "100000000000000012"]);

    private readonly DiscordTestBed _bed = new();
    private readonly FakeGateway _gateway = new();
    private readonly ConcurrentQueue<TimeSpan> _waits = new();
    private readonly DiscordBotService _service;

    public DiscordBotServiceTests()
    {
        var options = new DiscordBotOptions
        {
            Jitter = () => 0.5,
            Delay = (wait, ct) =>
            {
                if (wait == Timeout.InfiniteTimeSpan)
                {
                    return Task.Delay(Timeout.Infinite, ct);
                }

                _waits.Enqueue(wait);
                return Task.CompletedTask;
            },
        };
        _service = new DiscordBotService(
            _bed.Settings, _gateway, _bed.Api, _bed.Handler, _bed.Status, options, _bed.Events, _bed.Time, NullLogger<DiscordBotService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        _bed.Dispose();
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Backoff_doubles_and_stops_at_five_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), DiscordBackoff.Delay(1, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(16), DiscordBackoff.Delay(4, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(5), DiscordBackoff.Delay(30, 1));
        Assert.Equal(TimeSpan.FromSeconds(1.6), DiscordBackoff.Delay(1, 0));
    }

    [Fact]
    public async Task Stays_off_without_a_token_and_starts_when_switched_on()
    {
        await _service.StartAsync(default);
        await Task.Delay(50);
        Assert.Empty(_gateway.Logins);
        Assert.Equal(DiscordBotStates.Off, _bed.Status.Current.State);

        _bed.Configure(new DiscordSettingsChange { Enabled = true, BotToken = DiscordTestBed.Token, StatusText = "Ask me" });
        await Until(() => !_gateway.Logins.IsEmpty);
        var login = Assert.Single(_gateway.Logins);
        Assert.Equal(0, login.Intents);
        Assert.Equal("Ask me", login.StatusText);
        await _service.StopAsync(default);
    }

    [Fact]
    public async Task Reconnects_with_backoff_then_registers_commands_and_answers_interactions()
    {
        _bed.LinkJane();
        _bed.Configure(new DiscordSettingsChange { Enabled = true, BotToken = DiscordTestBed.Token });
        _gateway.Script.Enqueue(FakeGateway.Fail(fatal: false));
        _gateway.Script.Enqueue(FakeGateway.Fail(fatal: false));
        _gateway.Script.Enqueue(FakeGateway.Fail(fatal: false));
        _gateway.Script.Enqueue(FakeGateway.Connect(Ready, new DiscordInteractionCreated(_bed.Command("help"))));

        await _service.StartAsync(default);
        await Until(() => _bed.Status.Current.IsConnected && _bed.Api.Registrations.Count >= 3 && !_bed.Api.Responses.IsEmpty);

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], _waits);
        Assert.Equal(4, _gateway.Logins.Count);
        var status = _bed.Status.Current;
        Assert.Equal("StreamZH", status.BotName);
        Assert.Equal(2, status.Guilds);
        Assert.Null(status.Error);

        // Global commands, and old guild commands cleared in every server.
        var registrations = _bed.Api.Registrations.ToList();
        Assert.Contains(registrations, r => r.GuildId is null && r.Commands.Any(c => c.Name == "movie"));
        Assert.Equal(2, registrations.Count(r => r.GuildId is not null && r.Commands.Count == 0));
        Assert.Equal(FakeDiscordApi.ApplicationId, _bed.Settings.Values.ApplicationId);

        // Saving the settings restarts the session at once (no backoff).
        _bed.Configure(new DiscordSettingsChange { StatusText = "New status" });
        await Until(() => _gateway.Logins.Count == 5);
        Assert.Equal(3, _waits.Count);
        await _service.StopAsync(default);
        Assert.Equal(DiscordBotStates.Off, _bed.Status.Current.State);
    }

    [Fact]
    public async Task A_rejected_token_stops_the_bot_until_the_settings_change()
    {
        _bed.Configure(new DiscordSettingsChange { Enabled = true, BotToken = DiscordTestBed.Token });
        _gateway.Script.Enqueue(FakeGateway.Fail(fatal: true));
        _gateway.Script.Enqueue(FakeGateway.Connect(Ready));

        await _service.StartAsync(default);
        await Until(() => _bed.Status.Current.State == DiscordBotStates.Error);
        await Task.Delay(50);
        Assert.Single(_gateway.Logins);
        Assert.Empty(_waits);
        Assert.Equal("Discord rejected the bot token.", _bed.Status.Current.Error);

        _bed.Configure(new DiscordSettingsChange { BotToken = DiscordTestBed.Token.Replace("000", "111", StringComparison.Ordinal) });
        await Until(() => _bed.Status.Current.IsConnected);
        Assert.Equal(2, _gateway.Logins.Count);
        await _service.StopAsync(default);
    }

    [Fact]
    public async Task Discords_retry_after_is_honoured()
    {
        _bed.Configure(new DiscordSettingsChange { Enabled = true, BotToken = DiscordTestBed.Token });
        _gateway.Script.Enqueue(FakeGateway.Fail(fatal: false, retryAfter: TimeSpan.FromHours(3)));
        _gateway.Script.Enqueue(FakeGateway.Connect(Ready));

        await _service.StartAsync(default);
        await Until(() => _bed.Status.Current.IsConnected);
        Assert.Equal([TimeSpan.FromHours(3)], _waits);
        await _service.StopAsync(default);
    }

    [Fact]
    public async Task Guild_commands_go_to_the_configured_servers_and_missing_ones_are_reported()
    {
        _bed.Configure(new DiscordSettingsChange { Enabled = true, BotToken = DiscordTestBed.Token, GuildIds = [DiscordTestBed.GuildId, "100000000000000013"] });

        await _service.RegisterCommandsAsync(Ready, DiscordTestBed.Token, default);

        var registrations = _bed.Api.Registrations.ToList();
        Assert.Equal(5, Assert.Single(registrations, r => r.GuildId == DiscordTestBed.GuildId).Commands.Count);
        Assert.Empty(Assert.Single(registrations, r => r.GuildId is null).Commands);
        Assert.Empty(Assert.Single(registrations, r => r.GuildId == "100000000000000012").Commands);
        Assert.DoesNotContain(registrations, r => r.GuildId == "100000000000000013");
        Assert.Contains("100000000000000013", _bed.Status.Current.PermissionProblem, StringComparison.Ordinal);
    }
}
