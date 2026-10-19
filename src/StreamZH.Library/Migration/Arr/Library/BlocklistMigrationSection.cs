// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>
/// Radarr's or Sonarr's blocklist: releases that failed or were removed as bad are added to StreamZH's blocklist for
/// the taken-over movie or series (and episodes), so StreamZH does not grab them again. Torrent releases are left out
/// (StreamZH downloads from Usenet only); releases blocklisted before are not added twice.
/// </summary>
public sealed class BlocklistMigrationSection(string source, ArrLibraryReader reader) : IMigrationSection
{
    private const int BatchSize = 500;

    public string Source => source;

    public string Key => "blocklist";

    public string Label => "Blocklist";

    public string Description => "Releases the old app blocked after failed downloads, so StreamZH does not grab them again.";

    public int Order => 850;

    public bool DefaultSelected => true;

    public bool Advanced => false;

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        using var scope = context.Services.CreateScope();
        var entries = await ReadAsync(context, scope.ServiceProvider, cancellationToken);
        return new MigrationSectionPlan([.. entries.Select(e => e.Item)]);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        using var scope = context.Services.CreateScope();
        var services = scope.ServiceProvider;
        run.Progress(0, 1, "Reading the blocklist");
        var entries = await ReadAsync(context, services, cancellationToken);
        var resolver = await OldItemResolver.CreateAsync(context, reader, services, cancellationToken);
        var store = services.GetRequiredService<LibraryMigrationStore>();
        var keys = new HashSet<string>(store.BlocklistKeys(), StringComparer.OrdinalIgnoreCase);
        var batch = new List<BlocklistItem>();
        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (record, item) = entries[i];
            if (i % 50 == 0)
            {
                run.Progress(i, entries.Count, $"Release {(i + 1).ToString(CultureInfo.InvariantCulture)} of {entries.Count.ToString(CultureInfo.InvariantCulture)}");
            }

            if (context.IsExcluded(item.Key))
            {
                run.Skipped(item.Label, "Left out in the wizard.");
                continue;
            }

            if (item.Action == MigrationAction.Skip)
            {
                run.Skipped(item.Label, item.Reason ?? "Not taken over.");
                continue;
            }

            int? movieId = null, seriesId = null;
            var episodeIds = new List<int>();
            if (source == MigrationSources.Radarr)
            {
                movieId = resolver.Movie(record.MovieId);
            }
            else
            {
                seriesId = resolver.Series(record.SeriesId);
                foreach (var episode in record.EpisodeIds ?? [])
                {
                    if (await resolver.EpisodeAsync(record.SeriesId, episode, cancellationToken) is { } id && !episodeIds.Contains(id))
                    {
                        episodeIds.Add(id);
                    }
                }
            }

            if (movieId is null && seriesId is null)
            {
                run.Skipped(item.Label, $"The {(source == MigrationSources.Radarr ? "movie" : "series")} is not in StreamZH.");
                continue;
            }

            var title = record.SourceTitle!.Trim();
            var date = DateTime.SpecifyKind(record.Date, DateTimeKind.Utc);
            if (!keys.Add(LibraryMigrationStore.BlocklistKey(title, movieId, seriesId, date)))
            {
                run.Skipped(item.Label, "Already in StreamZH's blocklist.");
                continue;
            }

            batch.Add(new BlocklistItem
            {
                Date = date,
                MediaType = source == MigrationSources.Radarr ? MediaType.Movie : MediaType.Series,
                MovieId = movieId,
                SeriesId = seriesId,
                EpisodeIds = episodeIds,
                SourceTitle = title,
                QualityId = ArrLibraryMapping.QualityOf(source, record.Quality?.Quality).Id,
                LanguageIds = [.. ArrLibraryMapping.LanguagesOf(record.AllLanguages).Select(l => l.Id)],
                Indexer = string.IsNullOrWhiteSpace(record.Indexer) ? null : record.Indexer.Trim(),
                Size = 0,
                Message = string.IsNullOrWhiteSpace(record.Message) ? $"Blocklisted in {context.App.AppName}." : record.Message.Trim(),
            });
            run.Created(item.Label);
            if (batch.Count >= BatchSize)
            {
                store.AddBlocklist(batch);
                batch.Clear();
            }
        }

        store.AddBlocklist(batch);
        run.Progress(entries.Count, entries.Count, $"{entries.Count.ToString(CultureInfo.InvariantCulture)} releases");
    }

    private async Task<List<(ArrBlocklistRecord Record, MigrationItem Item)>> ReadAsync(MigrationContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        var records = await reader.PagedAsync<ArrBlocklistRecord>(context, "/blocklist", _ => false, null, cancellationToken);
        var resolver = await OldItemResolver.CreateAsync(context, reader, services, cancellationToken);
        var existing = services.GetRequiredService<LibraryMigrationStore>().BlocklistKeys();
        var result = new List<(ArrBlocklistRecord, MigrationItem)>();
        foreach (var record in records)
        {
            var key = $"blocklist:{record.Id.ToString(CultureInfo.InvariantCulture)}";
            var label = string.IsNullOrWhiteSpace(record.SourceTitle) ? $"Release {record.Id}" : record.SourceTitle.Trim();
            var radarr = source == MigrationSources.Radarr;
            var title = radarr ? resolver.MovieTitle(record.MovieId) : resolver.SeriesTitle(record.SeriesId);
            var detail = string.Join(
                " · ",
                new[]
                {
                    DateTime.SpecifyKind(record.Date, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    title,
                    record.Indexer,
                    record.Protocol,
                    record.Message,
                }.Where(p => !string.IsNullOrWhiteSpace(p)));
            var itemId = radarr ? resolver.Movie(record.MovieId) : resolver.Series(record.SeriesId);
            MigrationItem item;
            if (string.IsNullOrWhiteSpace(record.SourceTitle))
            {
                item = new MigrationItem(key, label, MigrationAction.Skip, detail, Reason: "The release has no name.");
            }
            else if (string.Equals(record.Protocol, "torrent", StringComparison.OrdinalIgnoreCase))
            {
                item = new MigrationItem(key, label, MigrationAction.Skip, detail, Reason: "A torrent release; StreamZH downloads from Usenet only.");
            }
            else if (itemId is null && !(radarr ? resolver.MovieCanBeTakenOver(record.MovieId) : resolver.SeriesKnown(record.SeriesId)))
            {
                item = new MigrationItem(key, label, MigrationAction.Skip, detail, Reason: $"The {(radarr ? "movie" : "series")} is not in {context.App.AppName}'s library any more.");
            }
            else if (itemId is not null && existing.Contains(LibraryMigrationStore.BlocklistKey(record.SourceTitle.Trim(), radarr ? itemId : null, radarr ? null : itemId, DateTime.SpecifyKind(record.Date, DateTimeKind.Utc))))
            {
                item = new MigrationItem(key, label, MigrationAction.Skip, detail, Reason: "Already in StreamZH's blocklist.");
            }
            else
            {
                item = new MigrationItem(key, label, MigrationAction.Create, detail, Warning: itemId is null ? $"Only taken over together with its {(radarr ? "movie" : "series")}." : null);
            }

            result.Add((record, item));
        }

        return result;
    }
}
