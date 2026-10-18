// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Jobs;
using StreamZH.Data;

namespace StreamZH.Library.Trash;

/// <param name="FormatsUpdated">Managed formats whose conditions or name changed.</param>
/// <param name="ProfilesUpdated">Template profiles applied again.</param>
public sealed record TrashSyncResult(TrashDownloadResult Download, int FormatsUpdated, int ProfilesUpdated, IReadOnlyList<string> Problems);

/// <summary>
/// "TRaSH Guides sync": downloads the guides when they changed; with auto update the formats linked to the guides and
/// every profile that follows a template are brought up to date (the user's score overrides stay when that is on).
/// </summary>
public sealed class TrashSyncService(
    TrashGuideStore store, TrashApplyService apply, TrashSettings settings, QualityProfileRepository profiles, TrashRepository trash,
    ILogger<TrashSyncService> logger)
{
    public async Task<TrashSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var download = await store.DownloadAsync(cancellationToken);
        if (!store.HasCopy || !settings.Values.AutoUpdate)
        {
            return new TrashSyncResult(download, 0, 0, []);
        }

        var result = Update();
        if (download.Error is null)
        {
            store.SetError(result.Problems.Count > 0 ? string.Join(" ", result.Problems.Take(3)) : null);
        }

        return result with { Download = download };
    }

    /// <summary>Updates managed formats and re-applies every template profile from the current copy.</summary>
    public TrashSyncResult Update()
    {
        var guides = store.Current;
        var warnings = new List<string>();
        var problems = new List<string>();
        if (guides.Movie.Profiles.Count + guides.Series.Profiles.Count == 0)
        {
            // Nothing readable: never treat every template as removed.
            return new TrashSyncResult(new TrashDownloadResult(false, store.Commit, null), 0, 0, ["The local copy of the TRaSH Guides could not be read."]);
        }

        var formatsUpdated = apply.UpdateManagedFormats(guides, warnings);
        var profilesUpdated = 0;

        foreach (var profile in profiles.All().Where(p => p.TrashMovieTemplate is not null || p.TrashSeriesTemplate is not null))
        {
            var movie = profile.TrashMovieTemplate;
            var series = profile.TrashSeriesTemplate;
            if ((movie is not null && guides.Movie.Profile(movie) is null) || (series is not null && guides.Series.Profile(series) is null))
            {
                // Kept as it is: re-applying only the other template would change its qualities.
                problems.Add($"{profile.Name}: its TRaSH template is no longer in the guides.");
                continue;
            }

            try
            {
                apply.Apply(
                    new TrashApplyRequest
                    {
                        ProfileId = profile.Id,
                        MovieTemplate = movie,
                        SeriesTemplate = series,
                        Options = trash.Options(profile.Id) ?? new(),
                    },
                    guides);
                profilesUpdated++;
            }
            catch (ValidationFailedException ex)
            {
                problems.Add($"{profile.Name} could not be updated: {string.Join(" ", ex.Errors.SelectMany(e => e.Value))}");
            }
        }

        if (apply.RememberedSizeChoices() is { Count: > 0 } sizes)
        {
            try
            {
                apply.ApplyQualitySizes(sizes, guides, warnings);
            }
            catch (ValidationFailedException ex)
            {
                problems.Add($"Quality sizes could not be updated: {string.Join(" ", ex.Errors.SelectMany(e => e.Value))}");
            }
        }

        foreach (var warning in warnings.Distinct())
        {
            logger.LogDebug("TRaSH sync: {Warning}", warning);
        }

        foreach (var problem in problems)
        {
            logger.LogWarning("TRaSH sync: {Problem}", problem);
        }

        logger.LogInformation("TRaSH sync: {Formats} custom formats and {Profiles} quality profiles updated", formatsUpdated, profilesUpdated);
        return new TrashSyncResult(new TrashDownloadResult(false, store.Commit, null), formatsUpdated, profilesUpdated, problems);
    }
}

/// <summary>
/// "TRaSH Guides sync" in System → Tasks, every N hours (default 24). It only runs once the guides were used (a local
/// copy exists), so installations that never use TRaSH do not download anything.
/// </summary>
public sealed class TrashSyncTask(TrashSettings settings) : IScheduledTask
{
    public const string TaskKey = "trash-sync";

    public string Key => TaskKey;

    public string Name => "TRaSH Guides sync";

    public TimeSpan? Interval => settings.Values is { Enabled: true } values ? TimeSpan.FromHours(values.SyncIntervalHours) : null;

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<TrashGuideStore>();
        return store.HasCopy ? services.GetRequiredService<TrashSyncService>().SyncAsync(cancellationToken) : Task.CompletedTask;
    }
}
