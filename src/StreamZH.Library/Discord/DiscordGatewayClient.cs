// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using StreamZH.Core;

namespace StreamZH.Library.Discord;

/// <summary>What the bot needs to sign in to the gateway.</summary>
/// <param name="Intents">Gateway intents; StreamZH needs none (interactions arrive without any) and never asks for privileged ones.</param>
/// <param name="StatusText">Custom status shown under the bot's name; empty = none.</param>
public sealed record DiscordGatewayLogin(string Token, int Intents, string? StatusText);

public abstract record DiscordGatewayEvent;

/// <summary>The session is ready (after IDENTIFY); <see cref="GuildIds"/> are the servers the bot is in.</summary>
public sealed record DiscordReady(DiscordUser User, string ApplicationId, IReadOnlyList<string> GuildIds) : DiscordGatewayEvent;

/// <summary>A dropped connection was resumed; no events were lost.</summary>
public sealed record DiscordResumed : DiscordGatewayEvent;

public sealed record DiscordInteractionCreated(DiscordInteraction Interaction) : DiscordGatewayEvent;

/// <summary>The gateway session ended. <see cref="Fatal"/>: retrying with the same settings cannot work (wrong token, intents).</summary>
public sealed class DiscordGatewayException(string message, bool fatal, int? closeCode = null, Exception? inner = null, TimeSpan? retryAfter = null)
    : Exception(message, inner)
{
    public bool Fatal { get; } = fatal;

    public int? CloseCode { get; } = closeCode;

    /// <summary>Discord said when to try again (daily login limit).</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The Discord gateway (websocket) seen from the bot: one call runs a session until it is cancelled or fails.</summary>
public interface IDiscordGateway
{
    /// <summary>
    /// Connects, identifies and keeps the session alive (heartbeats; a dropped connection is resumed) until
    /// <paramref name="cancellationToken"/> is cancelled. Events are handed to <paramref name="onEvent"/> in order; it
    /// must return quickly (long work belongs on another task).
    /// </summary>
    /// <exception cref="DiscordGatewayException">The session could not start or was lost for good.</exception>
    Task RunAsync(DiscordGatewayLogin login, Func<DiscordGatewayEvent, CancellationToken, Task> onEvent, CancellationToken cancellationToken);
}

/// <summary>How the gateway client connects and waits (tests replace both).</summary>
public sealed record DiscordGatewayOptions
{
    public Func<Uri, CancellationToken, Task<WebSocket>> Connect { get; init; } = DefaultConnect(null);

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (wait, ct) => Task.Delay(wait, ct);

    /// <summary>0..1, spreads the first heartbeat like Discord asks.</summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    /// <summary>Time to wait for HELLO after connecting.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A ClientWebSocket through the app's proxy (Settings → General) when one is set.</summary>
    public static Func<Uri, CancellationToken, Task<WebSocket>> DefaultConnect(Func<IWebProxy?>? proxy) => async (uri, ct) =>
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", $"DiscordBot ({AppInfo.Name}, {AppInfo.Version})");
        if (proxy?.Invoke() is { } webProxy)
        {
            socket.Options.Proxy = webProxy;
        }

        try
        {
            await socket.ConnectAsync(uri, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    };
}

/// <summary>
/// Lean client for Discord's gateway v10 (JSON, no compression): HELLO → IDENTIFY (or RESUME), heartbeats with jitter
/// and zombie detection (no ACK → reconnect and resume), RECONNECT and INVALID_SESSION handling, the resume URL from
/// READY. Close codes that cannot be fixed by retrying (4004 authentication failed, 4010–4014) end the run as fatal.
/// The token is only sent inside IDENTIFY/RESUME and never logged.
/// </summary>
public sealed class DiscordGatewayClient(IDiscordApi api, DiscordGatewayOptions options, ILogger<DiscordGatewayClient> logger) : IDiscordGateway
{
    /// <summary>Reconnects in a row without a READY/RESUMED before the run gives up (the caller backs off).</summary>
    public const int MaxReconnects = 3;

    private const int MaxMessageBytes = 16 * 1024 * 1024;

    private const int OpDispatch = 0;
    private const int OpHeartbeat = 1;
    private const int OpIdentify = 2;
    private const int OpResume = 6;
    private const int OpReconnect = 7;
    private const int OpInvalidSession = 9;
    private const int OpHello = 10;
    private const int OpHeartbeatAck = 11;

    /// <summary>Close code StreamZH uses to drop a connection but keep the session resumable.</summary>
    private const WebSocketCloseStatus ResumableClose = (WebSocketCloseStatus)4000;

    private enum ConnectionEnd
    {
        Resume,
        NewSession,
    }

    private sealed class SessionState
    {
        public string? SessionId { get; set; }
        public string? ResumeUrl { get; set; }
        public long? Sequence { get; set; }

        /// <summary>The current connection got READY or RESUMED.</summary>
        public bool Established { get; set; }

        public bool CanResume => SessionId is not null && ResumeUrl is not null;

        public void Clear()
        {
            SessionId = null;
            ResumeUrl = null;
            Sequence = null;
        }
    }

    public async Task RunAsync(DiscordGatewayLogin login, Func<DiscordGatewayEvent, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(onEvent);

        DiscordGatewayInfo info;
        try
        {
            info = await api.GetGatewayAsync(login.Token, cancellationToken);
        }
        catch (DiscordApiException ex) when (ex.Unauthorized)
        {
            throw new DiscordGatewayException(ex.Message, fatal: true, inner: ex);
        }
        catch (DiscordApiException ex)
        {
            throw new DiscordGatewayException(ex.Message, fatal: false, inner: ex);
        }

        if (info.SessionStartsRemaining <= 0)
        {
            throw new DiscordGatewayException(
                "Discord allows no more bot logins for now (daily limit).", fatal: false, retryAfter: info.SessionStartsResetAfter);
        }

        var state = new SessionState();
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = state.CanResume ? state.ResumeUrl! : info.Url;
            try
            {
                var (end, established) = await RunConnectionAsync(url, login, state, onEvent, cancellationToken);
                failures = established ? 0 : failures + 1;
                if (end == ConnectionEnd.NewSession)
                {
                    state.Clear();

                    // Discord asks for a random 1–5 s pause before identifying again.
                    await options.Delay(TimeSpan.FromSeconds(1 + (4 * options.Jitter())), cancellationToken);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or InvalidOperationException or TimeoutException
                                       && !cancellationToken.IsCancellationRequested)
            {
                failures = state.Established ? 1 : failures + 1;
                logger.LogDebug("Discord gateway connection failed ({Error})", ex.Message);
                if (failures > MaxReconnects)
                {
                    throw new DiscordGatewayException("The connection to Discord was lost.", fatal: false, inner: ex);
                }

                await options.Delay(TimeSpan.FromSeconds(failures), cancellationToken);
                continue;
            }

            if (failures > MaxReconnects)
            {
                throw new DiscordGatewayException("Discord keeps closing the connection.", fatal: false);
            }
        }
    }

    private async Task<(ConnectionEnd End, bool Established)> RunConnectionAsync(
        string url,
        DiscordGatewayLogin login,
        SessionState state,
        Func<DiscordGatewayEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"{url.TrimEnd('/')}/?v=10&encoding=json");
        using var socket = await options.Connect(uri, cancellationToken);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var sendLock = new SemaphoreSlim(1, 1);
        var established = false;
        state.Established = false;
        var acked = true;
        Task? heartbeat = null;

        async Task SendAsync(object payload)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await sendLock.WaitAsync(connection.Token);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, connection.Token);
            }
            finally
            {
                sendLock.Release();
            }
        }

        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            helloTimeout.CancelAfter(options.HelloTimeout);
            JsonNode? hello;
            try
            {
                hello = await ReceiveAsync(socket, helloTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Discord sent no HELLO.");
            }

            if (hello is null)
            {
                return (CloseOutcome(socket), false);
            }

            if (hello["op"]?.GetValue<int>() != OpHello || hello["d"]?["heartbeat_interval"]?.GetValue<double>() is not { } intervalMs)
            {
                throw new InvalidOperationException("Discord did not start with HELLO.");
            }

            var interval = TimeSpan.FromMilliseconds(intervalMs);
            if (state.CanResume)
            {
                await SendAsync(new { op = OpResume, d = new { token = login.Token, session_id = state.SessionId, seq = state.Sequence } });
            }
            else
            {
                await SendAsync(new { op = OpIdentify, d = Identify(login) });
            }

            heartbeat = Task.Run(
                async () =>
                {
                    await options.Delay(interval * options.Jitter(), connection.Token);
                    while (!connection.IsCancellationRequested)
                    {
                        if (!Volatile.Read(ref acked))
                        {
                            // Zombied connection: drop it without ending the session, the receive loop then resumes.
                            logger.LogDebug("Discord gateway sent no heartbeat ACK, reconnecting");
                            socket.Abort();
                            return;
                        }

                        Volatile.Write(ref acked, false);
                        await SendAsync(new { op = OpHeartbeat, d = state.Sequence });
                        await options.Delay(interval, connection.Token);
                    }
                },
                connection.Token);

            while (true)
            {
                var message = await ReceiveAsync(socket, cancellationToken);
                if (message is null)
                {
                    return (CloseOutcome(socket), established);
                }

                var op = message["op"]?.GetValue<int>();
                switch (op)
                {
                    case OpDispatch:
                        if (message["s"] is JsonValue s && s.TryGetValue<long>(out var sequence))
                        {
                            state.Sequence = sequence;
                        }

                        var type = message["t"]?.GetValue<string>();
                        var data = message["d"];
                        if (type == "READY" && data is not null)
                        {
                            state.SessionId = data["session_id"]?.GetValue<string>();
                            state.ResumeUrl = data["resume_gateway_url"]?.GetValue<string>();
                            established = state.Established = true;
                            await onEvent(Ready(data), cancellationToken);
                        }
                        else if (type == "RESUMED")
                        {
                            established = state.Established = true;
                            await onEvent(new DiscordResumed(), cancellationToken);
                        }
                        else if (type == "INTERACTION_CREATE" && data is not null
                                 && DiscordInteraction.Parse(JsonSerializer.Deserialize<JsonElement>(data.ToJsonString())) is { } interaction)
                        {
                            await onEvent(new DiscordInteractionCreated(interaction), cancellationToken);
                        }

                        break;
                    case OpHeartbeat:
                        await SendAsync(new { op = OpHeartbeat, d = state.Sequence });
                        break;
                    case OpHeartbeatAck:
                        Volatile.Write(ref acked, true);
                        break;
                    case OpReconnect:
                        logger.LogDebug("Discord asked the bot to reconnect");
                        await CloseAsync(socket, ResumableClose);
                        return (ConnectionEnd.Resume, established);
                    case OpInvalidSession:
                        var resumable = message["d"] is JsonValue d && d.TryGetValue<bool>(out var r) && r;
                        logger.LogDebug("Discord gateway session invalid (resumable: {Resumable})", resumable);
                        await CloseAsync(socket, ResumableClose);
                        return (resumable ? ConnectionEnd.Resume : ConnectionEnd.NewSession, established);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: close normally so Discord shows the bot offline right away.
            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure);
            throw;
        }
        finally
        {
            await connection.CancelAsync();
            if (heartbeat is not null)
            {
                try
                {
                    await heartbeat;
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
                {
                }
            }
        }
    }

    private static object Identify(DiscordGatewayLogin login)
    {
        var properties = new Dictionary<string, string> { ["os"] = Environment.OSVersion.Platform.ToString().ToLowerInvariant(), ["browser"] = AppInfo.Name, ["device"] = AppInfo.Name };
        object? presence = login.StatusText is { Length: > 0 } text
            ? new { since = (long?)null, activities = new[] { new { name = "Custom Status", type = 4, state = text } }, status = "online", afk = false }
            : null;
        return presence is null
            ? new { token = login.Token, intents = login.Intents, properties }
            : new { token = login.Token, intents = login.Intents, properties, presence };
    }

    private static DiscordReady Ready(JsonNode data)
    {
        var user = data["user"];
        var guilds = data["guilds"] is JsonArray list
            ? list.Select(g => g?["id"]?.GetValue<string>()).OfType<string>().ToList()
            : [];
        return new DiscordReady(
            new DiscordUser(user?["id"]?.GetValue<string>() ?? string.Empty, user?["username"]?.GetValue<string>() ?? string.Empty, user?["global_name"]?.GetValue<string?>()),
            data["application"]?["id"]?.GetValue<string>() ?? string.Empty,
            guilds);
    }

    /// <summary>What a closed connection means: fatal close codes throw, 4007/4009 need a new session, the rest resume.</summary>
    private static ConnectionEnd CloseOutcome(WebSocket socket)
    {
        var code = (int?)socket.CloseStatus;
        var fatal = code switch
        {
            4004 => "Discord rejected the bot token. Copy a new one from Bot → Reset Token in the Developer Portal.",
            4010 or 4011 => "The bot is in too many servers for one connection; StreamZH does not support sharding.",
            4012 => "Discord no longer supports this gateway version; update StreamZH.",
            4013 or 4014 => "Discord refused the gateway intents StreamZH asked for.",
            _ => null,
        };
        if (fatal is not null)
        {
            throw new DiscordGatewayException(fatal, fatal: true, code);
        }

        return code is 4007 or 4009 or 1000 or 1001 ? ConnectionEnd.NewSession : ConnectionEnd.Resume;
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseOutputAsync(status, null, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>One JSON message (frames joined), or null when the server closed the connection.</summary>
    private static async Task<JsonNode?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                throw new InvalidOperationException("Discord sent a message that is too large.");
            }

            if (result.EndOfMessage)
            {
                return JsonNode.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            }
        }
    }
}
