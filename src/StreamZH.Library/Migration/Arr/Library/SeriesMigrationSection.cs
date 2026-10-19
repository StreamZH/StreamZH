// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>
/// Sonarr's series: every series becomes a StreamZH series. Sonarr is TVDB-based and StreamZH uses TMDB: Sonarr v4's
/// TMDB id is used when it has one, else TMDB's /find by TVDB id, else by IMDb id; series without a match are left out.
/// Monitoring (series, seasons, episodes), series type, quality profile, root folder, folder and added date are taken
/// over; episodes come from TMDB as usual and are matched to Sonarr's (season/episode, absolute numbers for anime, air
/// dates for daily shows). Episode files — also multi-episode files — are linked where they are, never moved or renamed.
/// Options: "series.monitor" = "asIs" (default) or "none"; "series.overwrite" (default off).
/// </summary>
public sealed class SeriesMigrationSection(ArrLibraryReader reader, ILogger<SeriesMigrationSection> logger) : IMigrationSection
{
    public const string MonitorOption = "series.monitor";
    public const string OverwriteOption = "series.overwrite";

    /// <summary>
    /// Previews look series without a TMDB id up on TMDB only up to this many (one request each); above it the preview
    /// says "looked up during the run" so a big Sonarr v3 library previews quickly.
    /// </summary>
    public const int PreviewLookupLimit = 50;

    public string Source => MigrationSources.Sonarr;

    public string Key => "series";

    public string Label => "Series";

    public string Description => "Every series with its seasons, episode monitoring, series type, quality profile and folder. Episode files are linked where they are: nothing is moved, renamed or downloaded.";

    public int Order => 550;

    public bool DefaultSelected => true;

    public bool Advanced => false;

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        using var scope = context.Services.CreateScope();
        var plan = await PlanAsync(context, scope.ServiceProvider, preview: true, cancellationToken);
        return new MigrationSectionPlan([.. plan.Entries.Select(e => e.Item)], plan.Warnings);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        using var scope = context.Services.CreateScope();
        var services = scope.ServiceProvider;
        if (!services.GetRequiredService<ITmdbClient>().IsConfigured)
        {
            run.Failed(Label, MovieMigrationSection.TmdbMissing);
            return;
        }

        var plan = await PlanAsync(context, services, preview: false, cancellationToken);
        var library = services.GetRequiredService<LibraryService>();
        var repository = services.GetRequiredService<SeriesRepository>();
        var store = services.GetRequiredService<LibraryMigrationStore>();
        var details = services.GetRequiredService<MediaFileDetailsRepository>();
        var events = services.GetRequiredService<IEventBus>();
        var targets = plan.Targets(services, allowCreate: true);
        var warned = new HashSet<string>(StringComparer.Ordinal);
        void WarnOnce(string? text)
        {
            if (text is not null && warned.Add(text))
            {
                run.Warn(text);
            }
        }

        var monitorNone = context.Option(MonitorOption, "asIs").Equals("none", StringComparison.OrdinalIgnoreCase);
        var overwrite = context.OptionOn(OverwriteOption, false);
        var find = services.GetService<ITmdbFindClient>();
        var seen = new HashSet<int>();
        var total = plan.Entries.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = plan.Entries[i];
            var label = entry.Item.Label;
            run.Progress(i, total, $"Series {(i + 1).ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)}");
            if (context.IsExcluded(entry.Item.Key))
            {
                run.Skipped(label, "Left out in the wizard.");
                continue;
            }

            if (entry.Item.Action == MigrationAction.Skip)
            {
                run.Skipped(label, entry.Item.Reason ?? NoMatch(entry.Series));
                continue;
            }

            int tmdbId;
            if (entry.TmdbId is { } knownId)
            {
                tmdbId = knownId;
            }
            else
            {
                try
                {
                    var (found, _) = await TmdbIdAsync(entry.Series, find, lookUp: true, cancellationToken);
                    if (found is not { } foundId)
                    {
                        run.Skipped(label, NoMatch(entry.Series));
                        continue;
                    }

                    tmdbId = foundId;
                }
                catch (TmdbException ex)
                {
                    run.Failed(label, $"TMDB lookup failed: {ex.Message}");
                    continue;
                }
            }

            if (!seen.Add(tmdbId))
            {
                run.Skipped(label, "Another Sonarr series has the same TMDB id.");
                continue;
            }

            try
            {
                var type = SeriesTypeOf(entry.Series);
                var profile = targets.Profile(entry.Series.QualityProfileId);
                var existingId = repository.TmdbIdToId().TryGetValue(tmdbId, out var id) ? id : (int?)null;
                Series item;
                bool created;
                if (existingId is null)
                {
                    if (repository.FolderPaths().Contains(entry.LocalFolder))
                    {
                        run.Skipped(label, $"Another StreamZH series already uses the folder {entry.LocalFolder}.");
                        continue;
                    }

                    var root = targets.RootFolder(entry.Series.RootFolderPath, entry.LocalFolder, allowCreate: true);
                    if (root.Id is not { } rootId)
                    {
                        run.Skipped(label, root.Warning ?? "No root folder.");
                        continue;
                    }

                    WarnOnce(root.Warning);
                    WarnOnce(profile.Warning);
                    item = await TmdbRetry.RunAsync(
                        () => library.AddSeriesAsync(tmdbId, rootId, entry.Series.Monitored && !monitorNone, profile.Id, entry.LocalFolder, type, cancellationToken),
                        cancellationToken);
                    if (entry.Series.Added != default)
                    {
                        store.SetSeriesAdded(item.Id, entry.Series.Added);
                    }

                    created = true;
                }
                else
                {
                    item = repository.Find(existingId.Value)!;
                    if (overwrite)
                    {
                        WarnOnce(profile.Warning);
                        item.Monitored = entry.Series.Monitored && !monitorNone;
                        item.QualityProfileId = profile.Id ?? item.QualityProfileId;
                        repository.Update(item);
                        if (item.SeriesType != type)
                        {
                            repository.SetSeriesType(item.Id, type);
                            item.SeriesType = type;
                        }
                    }

                    created = false;
                }

                context.Ids.Set("series", entry.Series.Id, item.Id);
                if (repository.Episodes(item.Id).Count == 0)
                {
                    try
                    {
                        await TmdbRetry.RunAsync(async () =>
                        {
                            await library.RefreshSeriesAsync(item.Id, manual: false, cancellationToken);
                            return true;
                        }, cancellationToken);
                    }
                    catch (TmdbException ex)
                    {
                        run.Warn($"The episodes could not be read from TMDB ({ex.Message}); refresh the series later to link its files.", label);
                    }
                }

                await SyncEpisodesAsync(context, run, entry, item, created || overwrite, monitorNone, repository, store, details, cancellationToken);
                await events.PublishAsync(new LibraryChanged(MediaType.Series, item.Id), cancellationToken);
                if (created)
                {
                    run.Created(label);
                }
                else if (overwrite)
                {
                    run.Updated(label);
                }
                else
                {
                    run.Linked(label);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is TmdbException or LibraryException or IOException or UnauthorizedAccessException or ValidationFailedException)
            {
                logger.LogWarning("Could not take over the series {Title}: {Message}", label, ex.Message);
                run.Failed(label, ex is TmdbException { StatusCode: 404 } ? $"TMDB does not know the series {tmdbId}." : ex.Message);
            }
        }

        run.Progress(total, total, $"{total.ToString(CultureInfo.InvariantCulture)} series");
        foreach (var warning in plan.Warnings.Where(w => w != MovieMigrationSection.TmdbMissing))
        {
            run.Info(warning);
        }
    }

    /// <summary>
    /// Takes over episode monitoring (new series, or with "overwrite") and links Sonarr's episode files to the matched
    /// episodes. Files StreamZH cannot see are reported; episodes that already have a file in StreamZH keep it unless
    /// "overwrite" is on.
    /// </summary>
    private async Task SyncEpisodesAsync(
        MigrationContext context,
        MigrationSectionRun run,
        SeriesEntry entry,
        Series item,
        bool takeMonitoring,
        bool monitorNone,
        SeriesRepository repository,
        LibraryMigrationStore store,
        MediaFileDetailsRepository details,
        CancellationToken cancellationToken)
    {
        var sonarrEpisodes = await reader.EpisodesAsync(context, entry.Series.Id, cancellationToken);
        var sonarrFiles = await reader.EpisodeFilesAsync(context, entry.Series.Id, cancellationToken);
        var local = repository.Episodes(item.Id);
        var matched = EpisodeMatcher.Match(item.SeriesType, sonarrEpisodes, local);
        foreach (var (sonarrId, episode) in matched)
        {
            context.Ids.Set("episode", sonarrId, episode.Id);
        }

        var monitored = new Dictionary<int, bool>();
        if (takeMonitoring)
        {
            var bySonarrMatch = matched.ToDictionary(m => m.Value.Id, m => sonarrEpisodes.First(e => e.Id == m.Key));
            var seasons = (entry.Series.Seasons ?? []).GroupBy(s => s.SeasonNumber).ToDictionary(g => g.Key, g => g.First().Monitored);
            foreach (var episode in local)
            {
                var value = !monitorNone && (bySonarrMatch.TryGetValue(episode.Id, out var sonarr)
                    ? sonarr.Monitored
                    : seasons.TryGetValue(episode.SeasonNumber, out var season) ? season : entry.Series.Monitored && episode.SeasonNumber > 0);
                if (value != episode.Monitored)
                {
                    monitored[episode.Id] = value;
                }
            }
        }

        var files = new Dictionary<int, MediaFile>();
        var localById = local.ToDictionary(e => e.Id);
        foreach (var file in sonarrFiles)
        {
            var episodes = sonarrEpisodes.Where(e => e.EpisodeFileId == file.Id).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
            var name = Path.GetFileName(file.RelativePath ?? file.Path ?? $"file {file.Id}");
            var targetsOfFile = episodes.Select(e => matched.GetValueOrDefault(e.Id)).OfType<Episode>().ToList();
            if (targetsOfFile.Count == 0)
            {
                run.Warn($"{name}: no matching episode on TMDB ({string.Join(", ", episodes.Select(Number))}); the file is not linked.", entry.Item.Label);
                continue;
            }

            if (targetsOfFile.Count < episodes.Count)
            {
                run.Warn($"{name}: only some of its episodes match on TMDB; it is linked to {string.Join(", ", targetsOfFile.Select(e => Number(e.SeasonNumber, e.EpisodeNumber)))}.", entry.Item.Label);
            }

            var localPath = MovieMigrationSection.LocalFilePath(context, entry.Series.Path, file.Path, file.RelativePath);
            var onDisk = localPath is null ? null : new FileInfo(localPath);
            if (onDisk is null || !onDisk.Exists)
            {
                run.Warn($"The file {localPath ?? name} was not found; {string.Join(", ", targetsOfFile.Select(e => Number(e.SeasonNumber, e.EpisodeNumber)))} show as missing. Check the path mapping.", entry.Item.Label);
                continue;
            }

            if (!LibraryTargets.IsInside(onDisk.FullName, item.FolderPath))
            {
                run.Warn($"Sonarr's file {onDisk.FullName} is outside StreamZH's folder {item.FolderPath}; it is not linked.", entry.Item.Label);
                continue;
            }

            var wanted = targetsOfFile.Where(e => takeMonitoring || localById[e.Id].File is null || SameFile(item.FolderPath, localById[e.Id].File!, onDisk)).ToList();
            if (wanted.Count == 0)
            {
                continue;
            }

            var mediaFile = new MediaFile(Path.GetRelativePath(item.FolderPath, onDisk.FullName), onDisk.Length, onDisk.LastWriteTimeUtc);
            foreach (var episode in wanted)
            {
                files[episode.Id] = mediaFile;
            }

            if (takeMonitoring || details.Find(onDisk.FullName) is null)
            {
                details.Save(ArrLibraryMapping.DetailsOf(context.Source, file, onDisk.FullName, null, onDisk));
            }
        }

        store.UpdateEpisodes(monitored, files);
    }

    private static bool SameFile(string folder, MediaFile file, FileInfo onDisk) =>
        PathComparer.Instance.Equals(Path.GetFullPath(Path.Combine(folder, file.RelativePath)), onDisk.FullName);

    private static string Number(ArrEpisode e) => Number(e.SeasonNumber, e.EpisodeNumber);

    private static string Number(int season, int episode) =>
        $"S{season.ToString("00", CultureInfo.InvariantCulture)}E{episode.ToString("00", CultureInfo.InvariantCulture)}";

    internal static SeriesType SeriesTypeOf(ArrSeries series) =>
        SeriesTypes.TryParse(series.SeriesType, out var type) ? type : SeriesType.Standard;

    private static string NoMatch(ArrSeries series) =>
        $"No TMDB match for TVDB {series.TvdbId.ToString(CultureInfo.InvariantCulture)} – add it by hand.";

    /// <summary>
    /// The TMDB id of a Sonarr series: Sonarr v4's own, else TMDB's /find by TVDB id, then by IMDb id (when
    /// <paramref name="lookUp"/>). Null when there is none; <c>Pending</c> when it still has to be looked up.
    /// </summary>
    internal async Task<(int? TmdbId, bool Pending)> TmdbIdAsync(ArrSeries series, ITmdbFindClient? find, bool lookUp, CancellationToken cancellationToken)
    {
        if (series.TmdbId > 0)
        {
            return (series.TmdbId, false);
        }

        if (series.TvdbId > 0 && reader.TmdbIdsByTvdbId.TryGetValue(series.TvdbId, out var known))
        {
            if (known is not null)
            {
                return (known, false);
            }
        }
        else if (series.TvdbId > 0)
        {
            if (!lookUp || find is null || !find.IsConfigured)
            {
                return (null, true);
            }

            var found = await TmdbRetry.RunAsync(() => find.FindByTvdbIdAsync(series.TvdbId, cancellationToken), cancellationToken);
            reader.TmdbIdsByTvdbId[series.TvdbId] = found?.TmdbId;
            if (found is not null)
            {
                return (found.TmdbId, false);
            }
        }

        if (series.ImdbId is { Length: > 2 } imdb)
        {
            if (reader.TmdbIdsByImdbId.TryGetValue(imdb, out var byImdb))
            {
                return (byImdb, false);
            }

            if (!lookUp || find is null || !find.IsConfigured)
            {
                return (null, true);
            }

            var found = await TmdbRetry.RunAsync(() => find.FindByImdbIdAsync(imdb, TmdbMediaKind.Series, cancellationToken), cancellationToken);
            reader.TmdbIdsByImdbId[imdb] = found?.TmdbId;
            return (found?.TmdbId, false);
        }

        return (null, false);
    }

    private async Task<SeriesPlan> PlanAsync(MigrationContext context, IServiceProvider services, bool preview, CancellationToken cancellationToken)
    {
        var sonarrSeries = await reader.SeriesAsync(context, cancellationToken);
        var roots = await reader.RootFoldersAsync(context, cancellationToken);
        var profiles = await reader.QualityProfilesAsync(context, cancellationToken);
        foreach (var root in roots)
        {
            context.Paths.Use(root.Path ?? string.Empty);
        }

        var repository = services.GetRequiredService<SeriesRepository>();
        var inLibrary = repository.TmdbIdToId();
        var folders = repository.FolderPaths();
        var plan = new SeriesPlan(context, roots, profiles);
        var targets = plan.Targets(services, allowCreate: false);
        var overwrite = context.OptionOn(OverwriteOption, false);
        var tmdbConfigured = services.GetRequiredService<ITmdbClient>().IsConfigured;
        var find = services.GetService<ITmdbFindClient>();
        if (!tmdbConfigured)
        {
            plan.Warnings.Add(MovieMigrationSection.TmdbMissing);
        }

        // Preview: TMDB lookups only for a handful of series; the run looks every one up while it goes (with progress).
        var needLookup = sonarrSeries.Count(s => s.TmdbId <= 0 && !(s.TvdbId > 0 && reader.TmdbIdsByTvdbId.ContainsKey(s.TvdbId)));
        var lookUp = tmdbConfigured && preview && needLookup <= PreviewLookupLimit;
        var seen = new HashSet<int>();
        foreach (var series in sonarrSeries.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = series.Year > 0 ? $"{series.Title} ({series.Year.ToString(CultureInfo.InvariantCulture)})" : series.Title ?? $"Series {series.Id}";
            if (!string.IsNullOrEmpty(series.RootFolderPath))
            {
                context.Paths.Use(series.RootFolderPath);
            }

            var key = $"series:{(series.TvdbId > 0 ? series.TvdbId.ToString(CultureInfo.InvariantCulture) : "sonarr" + series.Id.ToString(CultureInfo.InvariantCulture))}";
            var localFolder = context.Paths.Map(series.Path ?? string.Empty);
            int? tmdbId;
            bool pending;
            try
            {
                (tmdbId, pending) = await TmdbIdAsync(series, find, lookUp, cancellationToken);
            }
            catch (TmdbException ex)
            {
                plan.Entries.Add(new SeriesEntry(series, null, localFolder, new MigrationItem(key, label, MigrationAction.Skip, Reason: $"TMDB lookup failed: {ex.Message}")));
                continue;
            }

            if (tmdbId is null && !pending)
            {
                plan.Entries.Add(new SeriesEntry(series, null, localFolder, new MigrationItem(key, label, MigrationAction.Skip, Reason: NoMatch(series))));
                continue;
            }

            if (tmdbId is { } known && !seen.Add(known))
            {
                plan.Entries.Add(new SeriesEntry(series, null, localFolder, new MigrationItem(key, label, MigrationAction.Skip, Reason: "Another Sonarr series has the same TMDB id.")));
                continue;
            }

            if (string.IsNullOrWhiteSpace(series.Path))
            {
                plan.Entries.Add(new SeriesEntry(series, tmdbId, localFolder, new MigrationItem(key, label, MigrationAction.Skip, Reason: "Sonarr has no folder for this series.")));
                continue;
            }

            var exists = tmdbId is { } t && inLibrary.ContainsKey(t);
            if (!exists && folders.Contains(localFolder))
            {
                plan.Entries.Add(new SeriesEntry(series, tmdbId, localFolder, new MigrationItem(
                    key, label, MigrationAction.Skip, Reason: $"Another StreamZH series already uses the folder {localFolder}.")));
                continue;
            }

            var problems = new List<string>();
            if (!exists)
            {
                var root = targets.RootFolder(series.RootFolderPath, localFolder, allowCreate: false);
                if (root.Id is null && !root.WillCreate)
                {
                    plan.Entries.Add(new SeriesEntry(series, tmdbId, localFolder, new MigrationItem(key, label, MigrationAction.Skip, Reason: root.Warning)));
                    continue;
                }

                if (root.Warning is not null)
                {
                    problems.Add(root.Warning);
                }
            }

            if ((!exists || overwrite) && targets.Profile(series.QualityProfileId).Warning is { } profileWarning)
            {
                problems.Add(profileWarning);
            }

            if (context.Paths.StorageProblem(localFolder) is { } outside)
            {
                // 0.12 (SV3 W-02/W-03): not looked at; the take-over refuses it until the owner allows the folder.
                problems.Add(outside);
            }
            else if (!context.Paths.FolderExists(localFolder))
            {
                problems.Add($"StreamZH cannot see the folder {localFolder}" + (context.Paths.IsMapped(series.Path) ? "." : "; add a path mapping."));
            }

            var type = SeriesTypes.ToApi(SeriesTypeOf(series));
            var files = series.Statistics?.EpisodeFileCount ?? 0;
            var detail = $"{char.ToUpperInvariant(type[0])}{type[1..]} · {files.ToString(CultureInfo.InvariantCulture)} {(files == 1 ? "file" : "files")}"
                         + (series.Statistics?.SizeOnDisk > 0 ? $" · {ArrLibraryMapping.Size(series.Statistics.SizeOnDisk)}" : string.Empty)
                         + $" · {localFolder}";
            if (pending)
            {
                detail = $"TMDB lookup needed (TVDB {series.TvdbId.ToString(CultureInfo.InvariantCulture)}): done during the run. " + detail;
            }
            else if (exists)
            {
                detail = (overwrite ? "Already in StreamZH: monitoring, type and profile are updated, files are linked. " : "Already in StreamZH: its data stays, files are linked. ") + detail;
            }

            var action = exists ? (overwrite ? MigrationAction.Update : MigrationAction.Link) : MigrationAction.Create;
            plan.Entries.Add(new SeriesEntry(series, tmdbId, localFolder, new MigrationItem(
                key, label, action, detail, Warning: problems.Count == 0 ? null : string.Join(" ", problems.Distinct()))));
        }

        if (preview && !lookUp && needLookup > 0 && tmdbConfigured)
        {
            plan.Warnings.Add($"{needLookup.ToString(CultureInfo.InvariantCulture)} series have no TMDB id in Sonarr; they are looked up on TMDB during the run.");
        }

        if (sonarrSeries.Any(s => s.Tags is { Count: > 0 }))
        {
            plan.Warnings.Add("StreamZH has no tags yet; Sonarr's series tags are not taken over.");
        }

        if (sonarrSeries.Any(s => !s.SeasonFolder))
        {
            plan.Warnings.Add("Some series in Sonarr have no season folders; StreamZH keeps their files where they are and has one season folder setting for all series (Settings → Media Management).");
        }

        return plan;
    }

    private sealed record SeriesEntry(ArrSeries Series, int? TmdbId, string LocalFolder, MigrationItem Item);

    private sealed class SeriesPlan(MigrationContext context, IReadOnlyList<ArrRootFolder> roots, IReadOnlyList<ArrQualityProfile> profiles)
    {
        public List<SeriesEntry> Entries { get; } = [];

        public List<string> Warnings { get; } = [];

        public LibraryTargets Targets(IServiceProvider services, bool allowCreate) => new(
            context,
            MediaType.Series,
            roots,
            profiles,
            services.GetRequiredService<RootFolderRepository>(),
            services.GetRequiredService<QualityProfileService>(),
            allowCreate ? services.GetService<RootFolderService>() : null);
    }
}
