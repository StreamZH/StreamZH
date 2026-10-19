// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Host.Security;

public static class DataFolderPermissionServices
{
    /// <summary>Logs what the start-up check of the data folder (<see cref="DataFolderPermissions.Apply"/>) did.</summary>
    public static IServiceCollection AddDataFolderPermissions(this IServiceCollection services, DataFolderPermissionReport report)
    {
        services.AddSingleton(report);
        services.AddHostedService<DataFolderPermissionLog>();
        return services;
    }
}

/// <summary>Writes the report of the data folder check to the log once the logging is up.</summary>
internal sealed class DataFolderPermissionLog(DataFolderPermissionReport report, ILogger<DataFolderPermissionLog> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var change in report.Changed)
        {
            logger.LogInformation("Data folder made private: {Change}", change);
        }

        foreach (var problem in report.Problems)
        {
            logger.LogWarning("Data folder permissions: {Problem}", problem);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
