// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Library.Discord;
using StreamZH.Tests.Library;

namespace StreamZH.Tests.Discord;

public sealed class DiscordClientTests
{
    private const string Token = DiscordTestBed.Token;

    // ---- REST ----

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        public ConcurrentQueue<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            Bodies.Enqueue(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (DiscordRestClient Client, ScriptedHandler Handler, List<TimeSpan> Waits) Rest(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new ScriptedHandler(answer);
        var waits = new List<TimeSpan>();
        var client = new DiscordRestClient(new HttpClient(handler), new DiscordApiOptions(new Uri("http://discord.invalid/api/v10/")), new ManualTimeProvider(DateTimeOffset.UnixEpoch))
        {
            Delay = (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
        };
        return (client, handler, waits);
    }

    [Fact]
    public async Task Rest_calls_send_the_bot_token_and_retry_after_a_rate_limit()
    {
        var calls = 0;
        var (client, handler, waits) = Rest(_ => ++calls == 1
            ? Json(HttpStatusCode.TooManyRequests, """{"message":"You are being rate limited.","retry_after":1.5,"global":false}""")
            : Json(HttpStatusCode.OK, """{"id":"100000000000000099","username":"streamzh-bot","global_name":"StreamZH"}"""));

        var user = await client.GetCurrentUserAsync(Token, default);

        Assert.Equal("StreamZH", user.DisplayName);
        Assert.Equal([TimeSpan.FromSeconds(1.5)], waits);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bot", r.Headers.Authorization!.Scheme);
            Assert.Equal(Token, r.Headers.Authorization.Parameter);
            Assert.StartsWith("DiscordBot (StreamZH", r.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Rest_errors_are_explained_without_the_token()
    {
        var (client, _, _) = Rest(_ => Json(HttpStatusCode.Unauthorized, """{"message":"401: Unauthorized","code":0}"""));
        var error = await Assert.ThrowsAsync<DiscordApiException>(() => client.GetGuildsAsync(Token, default));
        Assert.True(error.Unauthorized);
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);

        var (forbidden, _, _) = Rest(_ => Json(HttpStatusCode.Forbidden, """{"message":"Missing Permissions","code":50013}"""));
        var denied = await Assert.ThrowsAsync<DiscordApiException>(() => forbidden.SendMessageAsync(Token, "100000000000000040", new DiscordMessage { Content = "x" }, default));
        Assert.True(denied.Forbidden);
        Assert.Contains("Missing Permissions", denied.Message, StringComparison.Ordinal);

        // A rate limit that would block for too long fails instead.
        var (slow, _, waits) = Rest(_ => Json(HttpStatusCode.TooManyRequests, """{"retry_after":600}"""));
        await Assert.ThrowsAsync<DiscordApiException>(() => slow.GetGuildsAsync(Token, default));
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Ids_are_checked_before_they_go_into_a_url()
    {
        var (client, handler, _) = Rest(_ => Json(HttpStatusCode.OK, "[]"));
        await Assert.ThrowsAsync<DiscordApiException>(() => client.GetChannelsAsync(Token, "../../users/@me", default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Interaction_answers_post_the_callback_without_the_bot_token()
    {
        var (client, handler, _) = Rest(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await client.RespondAsync("100000000000000077", "tok/en", DiscordInteractionResponse.DeferEphemeral(), default);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Headers.Authorization);
        Assert.EndsWith("/interactions/100000000000000077/tok%2Fen/callback", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        var body = Assert.Single(handler.Bodies);
        Assert.Equal("""{"type":5,"data":{"flags":64,"allowed_mentions":{"parse":[]}}}""", body);
    }

    // ---- Gateway ----

    /// <summary>A websocket whose server side is a script: it answers what the client sends.</summary>
    private sealed class FakeSocket(Action<FakeSocket, JsonNode> onSend) : WebSocket
    {
        private readonly Channel<(string? Text, int? Close)> _incoming = Channel.CreateUnbounded<(string?, int?)>();
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;

        public ConcurrentQueue<JsonNode> Sent { get; } = new();

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public void Push(object payload) => _incoming.Writer.TryWrite((System.Text.Json.JsonSerializer.Serialize(payload), null));

        public void PushClose(int code) => _incoming.Writer.TryWrite((null, code));

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var (text, close) = await _incoming.Reader.ReadAsync(cancellationToken);
            if (close is { } code)
            {
                _closeStatus = (WebSocketCloseStatus)code;
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, _closeStatus, null);
            }

            var bytes = Encoding.UTF8.GetBytes(text!);
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var node = JsonNode.Parse(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count))!;
            Sent.Enqueue(node);
            onSend(this, node);
            return Task.CompletedTask;
        }
    }

    private static object Hello => new { op = 10, d = new { heartbeat_interval = 41250 } };

    [Fact]
    public async Task Gateway_identifies_dispatches_and_resumes_after_a_reconnect_request_then_stops_on_a_rejected_token()
    {
        var urls = new List<Uri>();
        var sockets = new List<FakeSocket>();
        var interaction = new
        {
            id = "100000000000000077", application_id = FakeDiscordApi.ApplicationId, type = 2, token = "interaction-token",
            guild_id = DiscordTestBed.GuildId, channel_id = DiscordTestBed.RequestChannel,
            member = new { user = new { id = DiscordTestBed.JaneDiscordId, username = "jane" }, roles = new[] { DiscordTestBed.FriendsRole } },
            data = new { name = "movie", options = new[] { new { name = "title", type = 3, value = "example" } } },
        };

        FakeSocket First() => new((socket, sent) =>
        {
            if (sent["op"]!.GetValue<int>() != 2)
            {
                return;
            }

            socket.Push(new
            {
                op = 0, t = "READY", s = 1,
                d = new
                {
                    session_id = "session-1", resume_gateway_url = "wss://resume.invalid",
                    user = new { id = FakeDiscordApi.BotId, username = "streamzh-bot" },
                    application = new { id = FakeDiscordApi.ApplicationId },
                    guilds = new[] { new { id = DiscordTestBed.GuildId, unavailable = true } },
                },
            });
            socket.Push(new { op = 0, t = "INTERACTION_CREATE", s = 2, d = interaction });
            socket.Push(new { op = 7, d = (object?)null });
        });

        FakeSocket Second() => new((socket, sent) =>
        {
            if (sent["op"]!.GetValue<int>() == 6)
            {
                socket.Push(new { op = 0, t = "RESUMED", s = 3, d = new { } });
                socket.PushClose(4004);
            }
        });

        var options = new DiscordGatewayOptions
        {
            Connect = (uri, _) =>
            {
                urls.Add(uri);
                var socket = sockets.Count == 0 ? First() : Second();
                sockets.Add(socket);
                socket.Push(Hello);
                return Task.FromResult<WebSocket>(socket);
            },
            Delay = (_, ct) => Task.Delay(Timeout.Infinite, ct),
            Jitter = () => 0,
        };
        var gateway = new DiscordGatewayClient(new FakeDiscordApi(), options, NullLogger<DiscordGatewayClient>.Instance);
        var events = new List<DiscordGatewayEvent>();

        var error = await Assert.ThrowsAsync<DiscordGatewayException>(() => gateway.RunAsync(
            new DiscordGatewayLogin(Token, 0, "Ask me"),
            (e, _) =>
            {
                events.Add(e);
                return Task.CompletedTask;
            },
            default));

        Assert.True(error.Fatal);
        Assert.Equal(4004, error.CloseCode);
        Assert.Equal(["wss://gateway.invalid/?v=10&encoding=json", "wss://resume.invalid/?v=10&encoding=json"], urls.Select(u => u.ToString()));

        var identify = sockets[0].Sent.Single(s => s["op"]!.GetValue<int>() == 2)["d"]!;
        Assert.Equal(Token, identify["token"]!.GetValue<string>());
        Assert.Equal(0, identify["intents"]!.GetValue<int>());
        Assert.Equal("Ask me", identify["presence"]!["activities"]![0]!["state"]!.GetValue<string>());

        var resume = sockets[1].Sent.Single()["d"]!;
        Assert.Equal("session-1", resume["session_id"]!.GetValue<string>());
        Assert.Equal(2, resume["seq"]!.GetValue<long>());

        Assert.IsType<DiscordReady>(events[0]);
        var created = Assert.IsType<DiscordInteractionCreated>(events[1]);
        Assert.Equal("movie", created.Interaction.CommandName);
        Assert.Equal("example", created.Interaction.Options["title"]);
        Assert.Equal([DiscordTestBed.FriendsRole], created.Interaction.MemberRoles);
        Assert.IsType<DiscordResumed>(events[2]);
    }

    [Fact]
    public async Task Gateway_gives_up_after_repeated_lost_connections_so_the_bot_can_back_off()
    {
        var connects = 0;
        var options = new DiscordGatewayOptions
        {
            Connect = (_, _) =>
            {
                connects++;
                throw new WebSocketException("connection refused");
            },
            Delay = (_, _) => Task.CompletedTask,
        };
        var gateway = new DiscordGatewayClient(new FakeDiscordApi(), options, NullLogger<DiscordGatewayClient>.Instance);

        var error = await Assert.ThrowsAsync<DiscordGatewayException>(() => gateway.RunAsync(new DiscordGatewayLogin(Token, 0, null), (_, _) => Task.CompletedTask, default));

        Assert.False(error.Fatal);
        Assert.Equal(DiscordGatewayClient.MaxReconnects + 1, connects);
    }

    [Fact]
    public async Task Gateway_reports_a_rejected_token_before_connecting()
    {
        var api = new FakeDiscordApi { TokenError = new DiscordApiException("Discord rejected the bot token.", HttpStatusCode.Unauthorized) };
        var gateway = new DiscordGatewayClient(api, new DiscordGatewayOptions { Connect = (_, _) => throw new InvalidOperationException("must not connect") }, NullLogger<DiscordGatewayClient>.Instance);

        var error = await Assert.ThrowsAsync<DiscordGatewayException>(() => gateway.RunAsync(new DiscordGatewayLogin(Token, 0, null), (_, _) => Task.CompletedTask, default));
        Assert.True(error.Fatal);
    }
}
