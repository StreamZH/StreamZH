// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using StreamZH.Core.Jobs;
using StreamZH.Host.Backups;

namespace StreamZH.Host.General;

public static class GeneralServiceExtensions
{
    /// <summary>Settings → General and UI, outgoing HTTP (proxy, certificates), restart, update check and backups.</summary>
    public static IServiceCollection AddGeneralSettings(this IServiceCollection services, StartupRestore restore)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SettingsCache>();
        services.AddSingleton<HostSettings>();
        services.AddSingleton<SecuritySettings>();
        services.AddSingleton<ProxySettings>();
        services.AddSingleton<UiSettings>();
        services.AddSingleton<SettingsWebProxy>();
        services.AddSingleton<CertificateValidator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, OutgoingHttpFilter>());
        services.AddSingleton<AppLifecycle>();
        services.AddSingleton<IndexPage>();
        services.AddSingleton<UpdateChecker>();
        services.AddHttpClient(UpdateChecker.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(UpdateChecker.CreateHandler);

        services.AddSingleton(restore);
        services.AddSingleton<BackupSettings>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<IScheduledTask, BackupTask>();
        return services;
    }
}
