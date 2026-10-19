// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Requests;
using StreamZH.Data;

namespace StreamZH.Library.Discord;

/// <summary>
/// Tells requesters on Discord what happened to their request: a direct message to the requester (a guest's Discord
/// account, or the Discord account linked to the StreamZH user) when it is approved by an admin, declined or available,
/// and/or a message in the notify channel. Works over REST with the saved token, so it does not need the gateway.
/// Events are queued, so the event bus never waits for Discord. Each request/status pair is announced once.
/// </summary>
public sealed class DiscordNotifier(
    IEventBus events,
    DiscordBotSettings settings,
    IDiscordApi api,
    RequestRepository requests,
    DiscordLinkRepository links,
    UserRepository users,
    DiscordBotStatusHolder status,
    ILogger<DiscordNotifier> logger) : BackgroundService
{
    private const int MaxRemembered = 2000;

    private readonly Channel<RequestChanged> _queue = Channel.CreateBounded<RequestChanged>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly ConcurrentDictionary<int, RequestStatus> _announced = new();

    /// <summary>The statuses people hear about.</summary>
    public static bool IsAnnounced(RequestStatus status) => status is RequestStatus.Approved or RequestStatus.Declined or RequestStatus.Available;

    private IDisposable? _subscription;

    /// <summary>Subscribes before the background loop starts, so no change published during startup is missed.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription ??= events.Subscribe<RequestChanged>((e, _) =>
        {
            if (IsAnnounced(e.Status))
            {
                _queue.Writer.TryWrite(e);
            }

            return ValueTask.CompletedTask;
        });
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var change in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await NotifyAsync(change, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Discord notification for request {RequestId} failed", change.RequestId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Sends the messages for one change (public for tests).</summary>
    public async Task NotifyAsync(RequestChanged change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var values = settings.Values;
        if (!values.Enabled || settings.Token is not { } token || (!values.DmRequesters && values.NotifyChannelId is null))
        {
            return;
        }

        // The request as it is now; a change that was overtaken (approved, then available at once) is not announced late.
        if (requests.Find(change.RequestId) is not { } request || request.Status != change.Status || !IsAnnounced(request.Status))
        {
            return;
        }

        // Auto-approved requests were confirmed when they were made; only an admin's approval is news.
        if (request.Status == RequestStatus.Approved && request.DecidedBy is null)
        {
            return;
        }

        if (_announced.TryGetValue(request.Id, out var last) && last == request.Status)
        {
            return;
        }

        if (_announced.Count > MaxRemembered)
        {
            _announced.Clear();
        }

        _announced[request.Id] = request.Status;

        var recipient = request.DiscordUserId is { Length: > 0 } discordId
            ? discordId
            : request.RequestedBy is { } userId ? links.ForUser(userId)?.DiscordUserId : null;
        var embed = Embed(request);

        if (values.DmRequesters && recipient is not null && DiscordBotSettings.IsSnowflake(recipient))
        {
            try
            {
                var channel = await api.OpenDirectMessageAsync(token, recipient, cancellationToken);
                await api.SendMessageAsync(token, channel, new DiscordMessage { Embeds = [embed] }, cancellationToken);
            }
            catch (DiscordApiException ex) when (ex.Code == DiscordApiException.CannotDmUser || ex.Forbidden)
            {
                // The user does not accept direct messages from server members; nothing StreamZH can fix.
                logger.LogDebug("Discord user does not accept direct messages from the bot");
            }
            catch (DiscordApiException ex)
            {
                logger.LogWarning("Could not send the Discord direct message about request {RequestId}: {Error}", request.Id, ex.Message);
            }
        }

        if (values.NotifyChannelId is { } notifyChannel)
        {
            var mention = recipient is not null && DiscordBotSettings.IsSnowflake(recipient) ? recipient : null;
            var message = new DiscordMessage
            {
                Content = mention is null ? null : $"<@{mention}>",
                Embeds = [embed with { Footer = new DiscordEmbedFooter(Footer(request, RequesterName(request))) }],
                AllowedMentions = new DiscordAllowedMentions([], mention is null ? null : [mention]),
            };
            try
            {
                await api.SendMessageAsync(token, notifyChannel, message, cancellationToken);
                if (status.Current.PermissionProblem?.StartsWith(ChannelProblem, StringComparison.Ordinal) == true)
                {
                    status.Update(s => s with { PermissionProblem = null });
                }
            }
            catch (DiscordApiException ex) when (ex.Forbidden || ex.Code == DiscordApiException.UnknownChannel)
            {
                logger.LogWarning("Discord bot cannot post in the notify channel: {Error}", ex.Message);
                status.Update(s => s with { PermissionProblem = ChannelProblem + " " + ex.Message });
            }
            catch (DiscordApiException ex)
            {
                logger.LogWarning("Could not post the Discord notification about request {RequestId}: {Error}", request.Id, ex.Message);
            }
        }
    }

    public const string ChannelProblem = "The bot cannot post in the notify channel (it needs View Channel, Send Messages and Embed Links there).";

    private string? RequesterName(MediaRequest request) =>
        request.RequestedBy is { } userId ? users.FindById(userId)?.Username ?? request.DiscordName : request.DiscordName;

    private static DiscordEmbed Embed(MediaRequest request)
    {
        var name = DiscordText.Name(request.Title, request.Year);
        var (title, description) = request.Status switch
        {
            RequestStatus.Approved => ($"Approved: {name}", "Your request was approved. StreamZH adds it and looks for it now."),
            RequestStatus.Declined => ($"Declined: {name}", request.Reason is { Length: > 0 } reason ? $"Reason: {DiscordText.Md(reason)}" : "Your request was declined."),
            _ => ($"Available: {name}", "Your request is ready to watch."),
        };
        return new DiscordEmbed
        {
            Title = DiscordText.Cut(title, 256),
            Description = DiscordText.Cut(description, 2000),
            Url = DiscordText.TmdbUrl(request.MediaType, request.TmdbId),
            Color = DiscordText.StatusColor(request.Status),
            Thumbnail = DiscordText.PosterUrl(request.PosterPath) is { } poster ? new DiscordEmbedMedia(poster) : null,
            Footer = new DiscordEmbedFooter(Footer(request, null)),
        };
    }

    private static string Footer(MediaRequest request, string? requester) =>
        string.Join(" · ", new[]
        {
            DiscordText.Seasons(request.MediaType, request.Seasons),
            requester is { Length: > 0 } who ? $"Requested by {who}" : string.Empty,
        }.Where(p => p.Length > 0)) is { Length: > 0 } text ? text : "StreamZH";
}
