// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Downloads;
using StreamZH.Library.PostProcessing;

namespace StreamZH.Library;

public static class PostProcessingServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="IPostProcessor"/> (native par2 verify, external par2 repair and 7-Zip extraction) and the tool search.
    /// Needs <c>ConfigRepository</c> for the configured tool paths.
    /// </summary>
    public static IServiceCollection AddPostProcessing(this IServiceCollection services)
    {
        services.TryAddSingleton(ToolEnvironment.Current);
        services.TryAddSingleton<IToolProcessRunner, ToolProcessRunner>();
        services.TryAddSingleton<IPostProcessToolSettings, ConfigPostProcessToolSettings>();
        services.TryAddSingleton<PostProcessTools>();
        services.TryAddSingleton<IPostProcessor, PostProcessor>();
        return services;
    }
}
