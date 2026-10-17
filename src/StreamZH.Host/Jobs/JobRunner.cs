// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Jobs;

namespace StreamZH.Host.Jobs;

/// <summary>Works through the job queue one job at a time.</summary>
public sealed class JobRunner(JobQueue queue, IServiceProvider services, ILogger<JobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await queue.StartedAsync(job);
            var success = true;
            try
            {
                using var scope = services.CreateScope();
                await job.Run(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                success = false;
                logger.LogError(ex, "Job '{Job}' failed: {Message}", job.Name, ex.Message);
            }

            await queue.FinishedAsync(success);
        }
    }
}