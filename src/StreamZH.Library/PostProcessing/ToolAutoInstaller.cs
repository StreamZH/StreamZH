// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StreamZH.Library.PostProcessing;

/// <param name="StartupDelay">Wait after startup, so installing never slows the start down.</param>
/// <param name="Enabled">False skips the check at startup (tests); the settings switch is separate.</param>
public sealed record ToolAutoInstallOptions(TimeSpan StartupDelay, bool Enabled = true)
{
    public ToolAutoInstallOptions()
        : this(TimeSpan.FromSeconds(5))
    {
    }
}

/// <summary>
/// A few seconds after startup, installs par2 and 7-Zip when they are missing (Settings → Tools → "Install automatically").
/// Runs once per start: a failure is shown in the status window and retried at the next start or with the Install button.
/// </summary>
public sealed class ToolAutoInstaller(ToolInstaller installer, ToolAutoInstallOptions options, TimeProvider time, ILogger<ToolAutoInstaller> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(options.StartupDelay, time, stoppingToken).ConfigureAwait(false);
            await installer.InstallMissingAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Automatic tool installation failed");
        }
    }
}
