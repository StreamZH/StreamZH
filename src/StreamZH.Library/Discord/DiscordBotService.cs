// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;

namespace StreamZH.Library.Discord;

/// <summary>How long the bot waits before the next connection attempt (doubling, with jitter, at most five minutes).</summary>
public static class DiscordBackoff
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    /// <param name="attempt">1 for the first retry.</param>
    /// <param name="jitter">0..1; ±20 % around the doubled wait.</param>
    public static TimeSpan Delay(int attempt, double jitter)
    {
        var doubled = First.TotalSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 16));
        var seconds = Math.Min(Max.TotalSeconds, doubled) * (0.8 + (0.4 * Math.Clamp(jitter, 0, 1)));
        return TimeSpan.FromSeconds(Math.Min(seconds, Max.TotalSeconds));
    }
}

/// <summary>Waits and randomness of the bot service (tests replace them to run without sleeping).</summary>
public sealed record DiscordBotOptions
{
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (wait, ct) => Task.Delay(wait, ct);

    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;
}

/// <summary>
/// Runs the built-in Discord bot while Settings → Discord has it switched on and a token saved: connects the gateway,
/// registers the slash commands when the session is ready, hands interactions to <see cref="DiscordInteractionHandler"/>
/// and reconnects with backoff when the connection is lost. A rejected token stops the bot until the settings change.
/// Saving the settings restarts it with the new values. The token is never logged.
/// </summary>
public sealed class DiscordBotService(
    DiscordBotSettings settings,
    IDiscordGateway gateway,
    IDiscordApi api,
    DiscordInteractionHandler handler,
    DiscordBotStatusHolder status,
    DiscordBotOptions options,
    IEventBus events,
    TimeProvider time,
    ILogger<DiscordBotService> logger) : BackgroundService
{
    private readonly Lock _tasksLock = new();
    private readonly HashSet<Task> _running = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The status window shows the bot's state (System → Status); tell it when the state changes.
        status.Changed += OnStatusChanged;
        try
        {
            await RunAsync(stoppingToken);
        }
        finally
        {
            status.Set(DiscordBotStatus.Off);
            status.Changed -= OnStatusChanged;
        }
    }

    private void OnStatusChanged() => _ = events.PublishAsync(new OngoingChanged()).AsTask();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var changed = settings.ChangeToken;
            var values = settings.Values;
            var token = settings.Token;
            if (!values.Enabled || token is null)
            {
                status.Set(DiscordBotStatus.Off);
                failures = 0;
                await WaitAsync(Timeout.InfiniteTimeSpan, changed, stoppingToken);
                continue;
            }

            using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, changed);
            var connected = false;
            status.Update(s => s with
            {
                State = failures == 0 ? DiscordBotStates.Connecting : DiscordBotStates.Reconnecting,
                Error = failures == 0 ? null : s.Error,
                NextRetry = null,
                Since = failures == 0 ? time.GetUtcNow() : s.Since,
            });

            try
            {
                var login = new DiscordGatewayLogin(token, Intents: 0, values.StatusText);
                await gateway.RunAsync(
                    login,
                    async (gatewayEvent, ct) =>
                    {
                        switch (gatewayEvent)
                        {
                            case DiscordReady ready:
                                connected = true;
                                failures = 0;
                                OnReady(ready, token, session.Token);
                                break;
                            case DiscordResumed:
                                connected = true;
                                failures = 0;
                                status.Update(s => s with { State = DiscordBotStates.Connected, Error = null, NextRetry = null });
                                break;
                            case DiscordInteractionCreated created:
                                Track(Task.Run(() => handler.HandleAsync(created.Interaction, stoppingToken), CancellationToken.None));
                                break;
                        }

                        await Task.CompletedTask;
                    },
                    session.Token);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                // Settings saved: start over with the new values at once.
                logger.LogInformation("Discord settings changed, restarting the bot");
                failures = 0;
                continue;
            }
            catch (DiscordGatewayException ex) when (ex.Fatal)
            {
                logger.LogWarning("Discord bot stopped: {Error}", ex.Message);
                status.Update(s => s with { State = DiscordBotStates.Error, Error = ex.Message, NextRetry = null, Since = time.GetUtcNow() });
                failures = 0;
                await WaitAsync(Timeout.InfiniteTimeSpan, changed, stoppingToken);
                continue;
            }
            catch (Exception ex) when (ex is DiscordGatewayException or DiscordApiException)
            {
                failures = connected ? 1 : failures + 1;
                var wait = ex is DiscordGatewayException { RetryAfter: { } after } && after > TimeSpan.Zero
                    ? after
                    : DiscordBackoff.Delay(failures, options.Jitter());
                Retry(ex.Message, wait, failures);
                await WaitAsync(wait, changed, stoppingToken);
                continue;
            }

            // The gateway ended without an error (it only does so when cancelled); try again after a short pause.
            failures++;
            var pause = DiscordBackoff.Delay(failures, options.Jitter());
            Retry("The connection to Discord ended.", pause, failures);
            await WaitAsync(pause, changed, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Task[] running;
        lock (_tasksLock)
        {
            running = [.. _running];
        }

        // Let answers that are being sent finish (briefly).
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
    }

    /// <summary>Running interaction answers (tests wait for them).</summary>
    public Task IdleAsync()
    {
        lock (_tasksLock)
        {
            return Task.WhenAll([.. _running]);
        }
    }

    private void Retry(string error, TimeSpan wait, int failures)
    {
        // The first lost connection is normal (Discord restarts its servers); repeated failures are worth a warning.
        if (failures > 2)
        {
            logger.LogWarning("Discord bot cannot connect ({Error}); retrying in {Seconds:0} s", error, wait.TotalSeconds);
        }
        else
        {
            logger.LogInformation("Discord bot disconnected ({Error}); retrying in {Seconds:0} s", error, wait.TotalSeconds);
        }

        status.Update(s => s with { State = DiscordBotStates.Reconnecting, Error = error, NextRetry = time.GetUtcNow() + wait });
    }

    private void OnReady(DiscordReady ready, string token, CancellationToken cancellationToken)
    {
        if (ready.ApplicationId.Length > 0)
        {
            settings.RememberApplicationId(ready.ApplicationId);
        }

        status.Update(s => s with
        {
            State = DiscordBotStates.Connected,
            BotName = ready.User.DisplayName,
            BotId = ready.User.Id,
            ApplicationId = ready.ApplicationId,
            Guilds = ready.GuildIds.Count,
            Error = null,
            NextRetry = null,
            Since = time.GetUtcNow(),
        });
        logger.LogInformation("Discord bot {BotName} connected ({Guilds} servers)", ready.User.DisplayName, ready.GuildIds.Count);

        // Registering the commands takes a few REST calls; the gateway loop must not wait for them.
        Track(RegisterCommandsAsync(ready, token, cancellationToken));
    }

    /// <summary>
    /// Guild commands in the configured servers (they show up at once), else global commands. Commands of the other
    /// kind are cleared so nothing shows up twice after switching.
    /// </summary>
    public async Task RegisterCommandsAsync(DiscordReady ready, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ready);
        var values = settings.Values;
        var applicationId = ready.ApplicationId;
        var problems = new List<string>();
        try
        {
            if (values.GuildIds.Count > 0)
            {
                var commands = DiscordCommandSet.Build(values, global: false);
                foreach (var guildId in values.GuildIds)
                {
                    if (!ready.GuildIds.Contains(guildId))
                    {
                        problems.Add($"The bot is not a member of server {guildId}; add it with the invite link.");
                        continue;
                    }

                    await RegisterAsync(token, applicationId, guildId, commands, problems, cancellationToken);
                }

                await RegisterAsync(token, applicationId, null, [], problems, cancellationToken);
                foreach (var other in ready.GuildIds.Where(g => !values.GuildIds.Contains(g)))
                {
                    await RegisterAsync(token, applicationId, other, [], problems, cancellationToken);
                }
            }
            else
            {
                await RegisterAsync(token, applicationId, null, DiscordCommandSet.Build(values, global: true), problems, cancellationToken);
                foreach (var guildId in ready.GuildIds)
                {
                    await RegisterAsync(token, applicationId, guildId, [], problems, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        status.Update(s => s with { PermissionProblem = problems.Count == 0 ? null : string.Join(" ", problems.Distinct()) });
    }

    private async Task RegisterAsync(
        string token, string applicationId, string? guildId, IReadOnlyList<DiscordCommandDefinition> commands, List<string> problems, CancellationToken cancellationToken)
    {
        try
        {
            await api.RegisterCommandsAsync(token, applicationId, guildId, commands, cancellationToken);
        }
        catch (DiscordApiException ex) when (ex.Forbidden)
        {
            // Clearing old commands may fail where the bot never had the scope; only a failed registration matters.
            if (commands.Count > 0)
            {
                problems.Add(guildId is null
                    ? "Discord refused the bot's commands; check the application in the Developer Portal."
                    : $"The bot cannot add its commands in server {guildId}; invite it again with the invite link (it needs the applications.commands scope).");
            }
        }
        catch (DiscordApiException ex)
        {
            if (commands.Count > 0)
            {
                logger.LogWarning("Could not register the Discord commands{Where}: {Error}", guildId is null ? string.Empty : $" in server {guildId}", ex.Message);
                problems.Add($"The bot's commands could not be registered: {ex.Message}");
            }
        }
    }

    private void Track(Task task)
    {
        lock (_tasksLock)
        {
            _running.Add(task);
        }

        _ = task.ContinueWith(
            t =>
            {
                lock (_tasksLock)
                {
                    _running.Remove(t);
                }

                if (t.Exception is { } ex)
                {
                    logger.LogWarning(ex.GetBaseException(), "Discord interaction failed");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Waits for the time, the settings to change, or the app to stop (never throws for the first two).</summary>
    private async Task WaitAsync(TimeSpan wait, CancellationToken changed, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(changed, stoppingToken);
        try
        {
            await options.Delay(wait, linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Settings changed or the app stops; the loop decides what is next.
        }
    }
}
