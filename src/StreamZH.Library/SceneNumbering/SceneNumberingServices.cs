// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core;
using StreamZH.Core.Decisions;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Library.SceneNumbering;

/// <summary>
/// Maps a parsed release to library episodes with everything the series knows: its type, TheXEM scene numbering and the
/// scene season of a TheXEM name in the release title ("[Group] Title Season 2 - 05").
/// </summary>
public sealed class EpisodeMapper(AlternativeTitleRepository titles)
{
    public (IReadOnlyList<Episode> Episodes, Rejection? Rejection) Map(
        ParsedRelease parsed, Series series, IReadOnlyList<Episode> library, bool sceneSource = true)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(series);
        int? sceneSeason = null;
        if (sceneSource && parsed.AbsoluteEpisodeNumbers.Count > 0 && series.Id > 0)
        {
            sceneSeason = ReleaseMatching.SceneSeasonFor(parsed, titles.For(MediaType.Series, series.Id));
        }

        return ReleaseMatching.MapEpisodes(parsed, library, series, sceneSource, sceneSeason);
    }
}

/// <summary>Scene numbering and scene names from TheXEM every 12 hours (Sonarr's "Update Scene Mapping" task).</summary>
public sealed class SceneMappingsTask : IScheduledTask
{
    public const string TaskKey = "scene-mappings";

    public string Key => TaskKey;

    public string Name => "Scene mappings";

    public TimeSpan? Interval => XemService.CacheLifetime;

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<XemService>().UpdateAllAsync(cancellationToken);
}

public static class SceneNumberingServiceCollectionExtensions
{
    /// <summary>TheXEM client and cache, the episode mapper and the "Scene mappings" task.</summary>
    /// <param name="baseUrl">Another TheXEM address (development and tests); null = thexem.info.</param>
    public static IServiceCollection AddSceneNumbering(this IServiceCollection services, string? baseUrl = null)
    {
        services.AddHttpClient(XemClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            }
        });
        services.AddSingleton<XemClient>();
        services.AddSingleton<XemService>();
        services.AddSingleton<EpisodeMapper>();
        services.AddSingleton<IScheduledTask, SceneMappingsTask>();
        return services;
    }
}
