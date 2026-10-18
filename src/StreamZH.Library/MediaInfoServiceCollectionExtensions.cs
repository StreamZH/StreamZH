// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Jobs;
using StreamZH.Library.MediaFiles;

namespace StreamZH.Library;

public static class MediaInfoServiceCollectionExtensions
{
    /// <summary>Media info with ffprobe: settings, the analysis service, the weekly refresh and the scan hook (needs AddPostProcessing).</summary>
    public static IServiceCollection AddMediaInfo(this IServiceCollection services)
    {
        services.TryAddSingleton<MediaInfoSettings>();
        services.TryAddSingleton<MediaInfoService>();
        services.AddSingleton<IScheduledTask, MediaInfoRefreshTask>();
        services.AddSingleton<MediaInfoScanHandler>();
        services.AddHostedService(sp => sp.GetRequiredService<MediaInfoScanHandler>());
        return services;
    }
}
