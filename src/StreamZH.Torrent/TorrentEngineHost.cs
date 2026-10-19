// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Data;

namespace StreamZH.Torrent;

/// <summary>How often the host ticks the engine and looks for an enabled "StreamZH" client (tests shorten both).</summary>
public sealed record TorrentEngineHostOptions(TimeSpan TickInterval, TimeSpan CheckInterval)
{
    public static TorrentEngineHostOptions Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
}

/// <summary>
/// Runs the built-in torrent engine while an enabled download client with the implementation "StreamZH" exists
/// (checked every <see cref="TorrentEngineHostOptions.CheckInterval"/>), ticks it, restarts it when its settings
/// change and stops it cleanly (fast-resume data written) when StreamZH shuts down.
/// </summary>
public sealed class TorrentEngineHost(
    TorrentEngine engine,
    TorrentEngineSettings settings,
    DownloadClientRepository clients,
    TorrentEngineHostOptions options,
    ILogger<TorrentEngineHost> logger) : BackgroundService
{
    private int _restartRequested;

    /// <summary>An enabled "StreamZH" client row exists.</summary>
    public bool IsWanted()
    {
        try
        {
            return clients.All().Any(c => c.Enable && string.Equals(c.Implementation, DownloadClientDefinition.BuiltInTorrent, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Download clients could not be read: {Error}", ex.Message);
            return engine.Wanted;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never hold up StreamZH's start with the first database read.
        await Task.Yield();
        settings.Changed += OnSettingsChanged;
        try
        {
            var lastCheck = DateTimeOffset.MinValue;
            using var timer = new PeriodicTimer(options.TickInterval);
            do
            {
                try
                {
                    if (DateTimeOffset.UtcNow - lastCheck >= options.CheckInterval)
                    {
                        lastCheck = DateTimeOffset.UtcNow;
                        engine.Wanted = IsWanted();
                        if (engine.Wanted && !engine.IsRunning)
                        {
                            await engine.StartAsync(stoppingToken);
                        }
                        else if (!engine.Wanted && engine.IsRunning)
                        {
                            await engine.StopAsync(stoppingToken);
                        }
                    }

                    if (Interlocked.Exchange(ref _restartRequested, 0) == 1)
                    {
                        await engine.RestartAsync(stoppingToken);
                    }

                    await engine.TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Torrent engine housekeeping failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            settings.Changed -= OnSettingsChanged;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await engine.StopAsync(CancellationToken.None);
    }

    private void OnSettingsChanged(TorrentEngineOptions options) => Interlocked.Exchange(ref _restartRequested, 1);
}
