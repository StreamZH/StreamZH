// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>
/// The recent history of Radarr or Sonarr (option "history.days", default 90): grabs, imports, failed downloads and
/// deleted files become StreamZH history rows of the taken-over movie or episodes, so StreamZH does not grab again what
/// the old app just grabbed. Events of items that were not taken over are left out; renames and ignored downloads too.
/// Sonarr writes one record per episode; the records of one download are joined into one row.
/// </summary>
public sealed class HistoryMigrationSection(string source, ArrLibraryReader reader) : IMigrationSection
{
    public const string DaysOption = "history.days";
    public const int DefaultDays = 90;
    private const int BatchSize = 500;

    public string Source => source;

    public string Key => "history";

    public string Label => "History";

    public string Description => "Grabs, imports, failed downloads and deleted files of the last 90 days (option), so StreamZH does not grab again what was just grabbed.";

    public int Order => 800;

    public bool DefaultSelected => false;

    public bool Advanced => true;

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        using var scope = context.Services.CreateScope();
        var (events, warnings) = await ReadAsync(context, scope.ServiceProvider, null, cancellationToken);
        return new MigrationSectionPlan([.. events.Select(e => e.Item)], warnings);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        using var scope = context.Services.CreateScope();
        var services = scope.ServiceProvider;
        var (events, warnings) = await ReadAsync(context, services, run, cancellationToken);
        var resolver = await OldItemResolver.CreateAsync(context, reader, services, cancellationToken);
        var store = services.GetRequiredService<LibraryMigrationStore>();
        var batch = new List<HistoryItem>();
        for (var i = 0; i < events.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = events[i];
            var label = entry.Item.Label;
            if (i % 50 == 0)
            {
                run.Progress(i, events.Count, $"Event {(i + 1).ToString(CultureInfo.InvariantCulture)} of {events.Count.ToString(CultureInfo.InvariantCulture)}");
            }

            if (context.IsExcluded(entry.Item.Key))
            {
                run.Skipped(label, "Left out in the wizard.");
                continue;
            }

            if (entry.Item.Action == MigrationAction.Skip)
            {
                run.Skipped(label, entry.Item.Reason ?? "Not taken over.");
                continue;
            }

            var first = entry.Records[0];
            int? movieId = null, seriesId = null;
            var episodeIds = new List<int>();
            if (source == MigrationSources.Radarr)
            {
                movieId = resolver.Movie(first.MovieId);
                if (movieId is null)
                {
                    run.Skipped(label, "The movie is not in StreamZH.");
                    continue;
                }
            }
            else
            {
                seriesId = resolver.Series(first.SeriesId);
                if (seriesId is null)
                {
                    run.Skipped(label, "The series is not in StreamZH.");
                    continue;
                }

                foreach (var record in entry.Records)
                {
                    if (await resolver.EpisodeAsync(record.SeriesId, record.EpisodeId, cancellationToken) is { } episodeId && !episodeIds.Contains(episodeId))
                    {
                        episodeIds.Add(episodeId);
                    }
                }
            }

            batch.Add(ToHistory(context, entry, movieId, seriesId, episodeIds));
            run.Created(label);
            if (batch.Count >= BatchSize)
            {
                store.AddHistory(batch);
                batch.Clear();
            }
        }

        store.AddHistory(batch);
        run.Progress(events.Count, events.Count, $"{events.Count.ToString(CultureInfo.InvariantCulture)} events");
        foreach (var warning in warnings)
        {
            run.Info(warning);
        }
    }

    private HistoryItem ToHistory(MigrationContext context, HistoryEntry entry, int? movieId, int? seriesId, IReadOnlyList<int> episodeIds)
    {
        var record = entry.Records[0];
        var data = record.Data ?? [];
        string? Get(string key) => data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        var quality = ArrLibraryMapping.QualityModelOf(source, record.Quality);
        var details = new Dictionary<string, string?>
        {
            [LibraryMigrationStore.MigratedFromKey] = entry.Marker,
            ["downloadClient"] = Get("downloadClientName") ?? Get("downloadClient"),
            ["sourceDownloadId"] = string.IsNullOrWhiteSpace(record.DownloadId) ? null : record.DownloadId,
            ["guid"] = Get("guid"),
            ["infoUrl"] = Get("nzbInfoUrl"),
            ["downloadUrl"] = Get("downloadUrl"),
            ["publishDate"] = Get("publishedDate"),
            ["protocol"] = Get("protocol"),
            ["message"] = Get("message"),
            ["reason"] = Get("reason"),
            ["importedPath"] = Get("importedPath") is { } imported ? context.Paths.Map(imported) : null,
            ["droppedPath"] = Get("droppedPath"),
        };

        return new HistoryItem
        {
            Date = DateTime.SpecifyKind(record.Date, DateTimeKind.Utc),
            EventType = entry.EventType,
            MediaType = source == MigrationSources.Radarr ? MediaType.Movie : MediaType.Series,
            MovieId = movieId,
            SeriesId = seriesId,
            EpisodeIds = episodeIds,
            SourceTitle = record.SourceTitle?.Trim() ?? string.Empty,
            QualityId = quality.Quality.Id,
            LanguageIds = [.. ArrLibraryMapping.LanguagesOf(record.AllLanguages).Select(l => l.Id)],
            ReleaseGroup = Get("releaseGroup"),
            Indexer = Get("indexer"),
            Size = long.TryParse(Get("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
            Data = details.Where(d => d.Value is not null).ToDictionary(d => d.Key, d => d.Value),
        };
    }

    /// <summary>StreamZH's event for an old app's event type; null for events StreamZH does not keep.</summary>
    public static HistoryEventType? EventTypeOf(string? eventType) => eventType?.Trim().ToLowerInvariant() switch
    {
        "grabbed" => HistoryEventType.Grabbed,
        "downloadfolderimported" or "moviefolderimported" or "seriesfolderimported" => HistoryEventType.Imported,
        "downloadfailed" => HistoryEventType.DownloadFailed,
        "moviefiledeleted" or "episodefiledeleted" => HistoryEventType.FileDeleted,
        _ => null,
    };

    private static string EventLabel(HistoryEventType type) => type switch
    {
        HistoryEventType.Grabbed => "Grabbed",
        HistoryEventType.Imported => "Imported",
        HistoryEventType.DownloadFailed => "Download failed",
        HistoryEventType.FileDeleted => "File deleted",
        _ => type.ToString(),
    };

    private async Task<(List<HistoryEntry> Events, List<string> Warnings)> ReadAsync(
        MigrationContext context, IServiceProvider services, MigrationSectionRun? run, CancellationToken cancellationToken)
    {
        var days = int.TryParse(context.Option(DaysOption, DefaultDays.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            ? Math.Clamp(d, 1, 3650)
            : DefaultDays;
        var since = DateTime.UtcNow.AddDays(-days);
        var records = await reader.PagedAsync<ArrHistoryRecord>(
            context,
            "/history",
            r => r.Date != default && DateTime.SpecifyKind(r.Date, DateTimeKind.Utc) < since,
            run is null ? null : (done, total) => run.Progress(0, 1, $"Reading history: {done.ToString(CultureInfo.InvariantCulture)} events"),
            cancellationToken);
        var resolver = await OldItemResolver.CreateAsync(context, reader, services, cancellationToken);
        var migrated = services.GetRequiredService<LibraryMigrationStore>().MigratedHistory();
        var warnings = new List<string>();

        var kept = records.Where(r => EventTypeOf(r.EventType) is not null).ToList();
        var leftOut = records.Count - kept.Count;
        if (leftOut > 0)
        {
            warnings.Add($"{leftOut.ToString(CultureInfo.InvariantCulture)} renamed, ignored or unknown events are left out.");
        }

        // Sonarr: one record per episode; the records of one download (same event, download id and release) are one row.
        var groups = source == MigrationSources.Sonarr
            ? kept.GroupBy(r => string.IsNullOrWhiteSpace(r.DownloadId)
                    ? $"id:{r.Id.ToString(CultureInfo.InvariantCulture)}"
                    : $"{r.EventType}|{r.DownloadId}|{r.SourceTitle}|{r.SeriesId.ToString(CultureInfo.InvariantCulture)}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(r => r.Id).ToList())
            : kept.Select(r => new List<ArrHistoryRecord> { r });

        var entries = new List<HistoryEntry>();
        foreach (var group in groups)
        {
            var first = group[0];
            var type = EventTypeOf(first.EventType)!.Value;
            var key = $"history:{first.Id.ToString(CultureInfo.InvariantCulture)}";
            var marker = $"{source}:{first.Id.ToString(CultureInfo.InvariantCulture)}";
            var label = $"{EventLabel(type)}: {first.SourceTitle}";
            var title = source == MigrationSources.Radarr ? resolver.MovieTitle(first.MovieId) : resolver.SeriesTitle(first.SeriesId);
            var quality = ArrLibraryMapping.QualityOf(source, first.Quality?.Quality);
            var detail = string.Join(
                " · ",
                new[]
                {
                    DateTime.SpecifyKind(first.Date, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    title,
                    quality == Quality.Unknown ? null : quality.Name,
                    first.Data?.GetValueOrDefault("indexer"),
                    group.Count > 1 ? $"{group.Count.ToString(CultureInfo.InvariantCulture)} episodes" : null,
                }.Where(p => !string.IsNullOrWhiteSpace(p)));

            MigrationItem item;
            if (migrated.Contains(marker))
            {
                item = new MigrationItem(key, label, MigrationAction.Skip, detail, Reason: "Already taken over.");
            }
            else if (source == MigrationSources.Radarr && resolver.Movie(first.MovieId) is null && !resolver.MovieCanBeTakenOver(first.MovieId))
            {
                item = new MigrationItem(key, label, MigrationAction.Skip, detail, Reason: "The movie is not in Radarr's library any more.");
            }
            else if (source == MigrationSources.Sonarr && resolver.Series(first.SeriesId) is null && !resolver.SeriesKnown(first.SeriesId))
            {
                item = new MigrationItem(key, label, MigrationAction.Skip, detail, Reason: "The series is not in Sonarr's library any more.");
            }
            else
            {
                var inStreamZh = source == MigrationSources.Radarr ? resolver.Movie(first.MovieId) is not null : resolver.Series(first.SeriesId) is not null;
                item = new MigrationItem(
                    key,
                    label,
                    MigrationAction.Create,
                    detail,
                    Warning: inStreamZh ? null : $"Only taken over together with its {(source == MigrationSources.Radarr ? "movie" : "series")}.");
            }

            entries.Add(new HistoryEntry(group, type, marker, item));
        }

        return (entries, warnings);
    }

    private sealed record HistoryEntry(IReadOnlyList<ArrHistoryRecord> Records, HistoryEventType EventType, string Marker, MigrationItem Item);
}
