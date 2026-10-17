// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core;
using StreamZH.Core.Downloads;
using StreamZH.Library.PostProcessing;

namespace StreamZH.Library;

public static class PostProcessingServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="IPostProcessor"/> (native par2 verify, external par2 repair and 7-Zip extraction), the tool search and the
    /// tool installer (installs missing tools into &lt;data&gt;/tools a few seconds after startup).
    /// Needs <c>ConfigRepository</c> for the configured tool paths, <see cref="AppPaths"/> for the install folder and
    /// <c>IEventBus</c> for the status window.
    /// </summary>
    public static IServiceCollection AddPostProcessing(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => ToolEnvironment.Current with { InstalledDirectory = sp.GetService<AppPaths>()?.ToolsDirectory });
        services.TryAddSingleton<IToolProcessRunner, ToolProcessRunner>();
        services.TryAddSingleton<IPostProcessToolSettings, ConfigPostProcessToolSettings>();
        services.TryAddSingleton<PostProcessTools>();
        services.TryAddSingleton<IPostProcessor, PostProcessor>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ToolAutoInstallOptions());
        services.TryAddSingleton(ToolCatalog.Official);
        services.TryAddSingleton<ToolInstaller>();
        services.AddHostedService<ToolAutoInstaller>();

        // GitHub redirects release downloads to its object storage (followed automatically); the files are a few MB.
        services.AddHttpClient(ToolInstaller.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        });
        return services;
    }
}
