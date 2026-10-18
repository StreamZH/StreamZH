// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core;
using StreamZH.Core.Jobs;
using StreamZH.Data;

namespace StreamZH.Library.Trash;

public static class TrashServiceCollectionExtensions
{
    /// <summary>
    /// TRaSH Guides sync (Recyclarr inside StreamZH): the local copy of the guides, applying templates, the Recyclarr
    /// importer, the scheduled sync and the hook that keeps the user's score changes. Needs the profile and Media
    /// Management services.
    /// </summary>
    public static IServiceCollection AddTrashGuides(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(TrashGuideStore.HttpClientName, client =>
        {
            // The archive of the guides repository is tens of MB.
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        });
        services.AddSingleton<TrashRepository>();
        services.AddSingleton<TrashSettings>();
        services.AddSingleton<TrashGuideStore>();
        services.AddSingleton<TrashApplyService>();
        services.AddSingleton<TrashSyncService>();
        services.AddSingleton<RecyclarrImporter>();
        services.AddSingleton<IQualityProfileObserver, TrashProfileObserver>();
        services.AddSingleton<IScheduledTask, TrashSyncTask>();
        return services;
    }
}
