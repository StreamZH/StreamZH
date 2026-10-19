// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;

namespace StreamZH.Library.Migration.Arr.Settings;

public static class ArrSettingsMigration
{
    /// <summary>The settings sections of the migration wizard for Radarr and Sonarr (one implementation per part, registered per source).</summary>
    public static IEnumerable<IMigrationSection> Sections(string source) =>
    [
        new RootFoldersSection(source),
        new QualityDefinitionsSection(source),
        new TagsSection(source),
        new CustomFormatsSection(source),
        new QualityProfilesSection(source),
        new ReleaseProfilesSection(source),
        new NamingSection(source),
        new MediaManagementSection(source),
        new NotificationsSection(source),
        new ImportListExclusionsSection(source),
    ];

    /// <summary>
    /// Radarr's and Sonarr's settings: root folders, quality sizes, tags, custom formats, quality profiles, release
    /// profiles, naming, media management, connections and list exclusions. Needs the migration core (ArrApiClient).
    /// </summary>
    public static IServiceCollection AddArrSettingsMigration(this IServiceCollection services)
    {
        foreach (var source in new[] { MigrationSources.Radarr, MigrationSources.Sonarr })
        {
            foreach (var section in Sections(source))
            {
                services.AddSingleton<IMigrationSection>(section);
            }
        }

        return services;
    }
}
