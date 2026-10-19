// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Arr.Library;

namespace StreamZH.Tests.Library.Migration;

public sealed class HistoryBlocklistMigrationTests : IDisposable
{
    private readonly LibraryMigrationTestBed _bed = new();

    private HistoryRepository History => new(_bed.Database);

    private BlocklistRepository Blocklist => new(_bed.Database);

    [Fact]
    public async Task Radarr_history_preview_pages_back_90_days_and_leaves_out_renames()
    {
        var section = new HistoryMigrationSection(MigrationSources.Radarr, _bed.Reader);

        var plan = await section.PreviewAsync(_bed.Context(MigrationSources.Radarr), CancellationToken.None);

        Assert.Equal(["history:601", "history:600", "history:598", "history:597", "history:596"], plan.Items.Select(i => i.Key));
        Assert.Equal("Grabbed: Dune.Part.Two.2024.German.DL.2160p.WEB.H265.REPACK-W4K", plan.Items[0].Label);
        Assert.Contains("Dune: Part Two (2024) · WEBDL-2160p · NZBgeek (Prowlarr)", plan.Items[0].Detail, StringComparison.Ordinal);
        Assert.Equal("Only taken over together with its movie.", plan.Items[0].Warning);
        Assert.Equal((MigrationAction.Skip, "The movie is not in Radarr's library any more."), (plan.Items[4].Action, plan.Items[4].Reason));
        Assert.Contains("1 renamed, ignored or unknown events are left out.", plan.Warnings!);

        // Pages of two: stops at the first record older than 90 days (the 200-day-old grab is never read past).
        Assert.Equal(4, _bed.Radarr.Requests.Count(r => r.PathAndQuery.StartsWith("/api/v3/history", StringComparison.Ordinal)));
        Assert.Contains(_bed.Radarr.Requests, r => r.PathAndQuery.Contains("sortKey=date&sortDirection=descending", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Radarr_history_becomes_StreamZH_history_once()
    {
        await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr));
        var section = new HistoryMigrationSection(MigrationSources.Radarr, _bed.Reader);

        var report = await _bed.RunAsync(section, _bed.Context(MigrationSources.Radarr));

        Assert.Equal((4, 1), (report.Created, report.Skipped));
        var (rows, total) = History.Page(1, 50);
        Assert.Equal(4, total);
        var dune = _bed.Movies.FindByTmdbId(693134)!;
        var grab = rows.Single(r => r.EventType == HistoryEventType.Grabbed);
        Assert.Equal((MediaType.Movie, dune.Id, Quality.WebDl2160p.Id), (grab.MediaType, grab.MovieId, grab.QualityId));
        Assert.Equal(("NZBgeek (Prowlarr)", "W4K", 23456789012L), (grab.Indexer, grab.ReleaseGroup, grab.Size));
        Assert.Equal("SABnzbd", grab.Data["downloadClient"]);
        Assert.Equal("radarr:601", grab.Data["migratedFrom"]);
        Assert.Equal([Language.German.Id, Language.English.Id], grab.LanguageIds);

        var imported = rows.Single(r => r.EventType == HistoryEventType.Imported);
        Assert.Equal(_bed.MovieFile("Freitag der 13. (2009)", "Freitag.der.13.2009.German.DL.1080p.BluRay.x264-DETAiLS.mkv"), imported.Data["importedPath"]);
        Assert.Equal("Unpacking failed, archive requires a password", rows.Single(r => r.EventType == HistoryEventType.DownloadFailed).Data["message"]);
        Assert.Equal("Upgrade", rows.Single(r => r.EventType == HistoryEventType.FileDeleted).Data["reason"]);

        // StreamZH now knows Dune was just grabbed and does not grab it again.
        Assert.True(History.MovieHasEventSince(dune.Id, HistoryEventType.Grabbed, DateTime.UtcNow.AddHours(-12)));

        var again = await _bed.RunAsync(section, _bed.Context(MigrationSources.Radarr));
        Assert.Equal(0, again.Created);
        Assert.Equal(4, History.Page(1, 50).Total);
    }

    [Fact]
    public async Task Sonarr_history_joins_the_records_of_a_multi_episode_download()
    {
        await _bed.RunAsync(_bed.SeriesSection, _bed.Context(MigrationSources.Sonarr));
        var section = new HistoryMigrationSection(MigrationSources.Sonarr, _bed.Reader);

        // A separate run: the episode mapping is rebuilt from Sonarr's episodes.
        var report = await _bed.RunAsync(section, _bed.Context(MigrationSources.Sonarr, new Dictionary<string, string> { ["history.days"] = "11" }));

        Assert.Equal(2, report.Created);
        var crystal = _bed.Series.All().Single(s => s.TmdbId == 250001);
        var episodes = _bed.Series.Episodes(crystal.Id).ToDictionary(e => e.EpisodeNumber, e => e.Id);
        var rows = History.Page(1, 50).Records;
        var grab = rows.Single(r => r.EventType == HistoryEventType.Grabbed);
        Assert.Equal((crystal.Id, MediaType.Series), (grab.SeriesId!.Value, grab.MediaType!.Value));
        Assert.Equal([episodes[3], episodes[4]], grab.EpisodeIds.Order());
        Assert.Equal([episodes[3], episodes[4]], History.EpisodesWithEventSince(crystal.Id, HistoryEventType.Grabbed, DateTime.UtcNow.AddHours(-12)).Order());
        Assert.Equal([episodes[1]], rows.Single(r => r.EventType == HistoryEventType.Imported).EpisodeIds);
    }

    [Fact]
    public async Task Blocklists_are_taken_over_for_usenet_releases_once()
    {
        await _bed.RunAsync(_bed.MoviesSection, _bed.Context(MigrationSources.Radarr));
        var radarr = new BlocklistMigrationSection(MigrationSources.Radarr, _bed.Reader);

        var plan = await radarr.PreviewAsync(_bed.Context(MigrationSources.Radarr), CancellationToken.None);
        Assert.Equal([MigrationAction.Create, MigrationAction.Skip], plan.Items.OrderBy(i => i.Key).Select(i => i.Action));
        Assert.Equal("A torrent release; StreamZH downloads from Usenet only.", plan.Items.Single(i => i.Key == "blocklist:32").Reason);

        var report = await _bed.RunAsync(radarr, _bed.Context(MigrationSources.Radarr));

        Assert.Equal((1, 1), (report.Created, report.Skipped));
        var item = Assert.Single(Blocklist.Page(1, 10).Records);
        Assert.Equal(
            ("Blade.Runner.2049.2017.German.DL.1080p.WEB.x264-OLD", _bed.Movies.FindByTmdbId(335984)!.Id, "NZBgeek (Prowlarr)", Quality.WebDl1080p.Id),
            (item.SourceTitle, item.MovieId!.Value, item.Indexer, item.QualityId));
        Assert.Equal("Unpacking failed, archive requires a password", item.Message);
        Assert.Equal(new DateTime(2026, 9, 9, 9, 0, 0, DateTimeKind.Utc), item.Date);

        var again = await _bed.RunAsync(radarr, _bed.Context(MigrationSources.Radarr));
        Assert.Equal(0, again.Created);
        Assert.Single(Blocklist.Page(1, 10).Records);

        // Sonarr: episode ids are mapped to StreamZH's episodes.
        await _bed.RunAsync(_bed.SeriesSection, _bed.Context(MigrationSources.Sonarr));
        await _bed.RunAsync(new BlocklistMigrationSection(MigrationSources.Sonarr, _bed.Reader), _bed.Context(MigrationSources.Sonarr));
        var crystal = _bed.Series.All().Single(s => s.TmdbId == 250001);
        var episode = Assert.Single(Blocklist.TitlesForSeries(crystal.Id));
        Assert.Equal("Crystal.Lake.S01E06.German.DL.1080p.WEB.h264-FAKE", episode);
        var row = Blocklist.Page(1, 10, mediaType: MediaType.Series).Records.Single();
        Assert.Equal([_bed.Series.Episodes(crystal.Id).Single(e => e.EpisodeNumber == 6).Id], row.EpisodeIds);
    }

    [Fact]
    public async Task Items_that_were_not_taken_over_are_skipped()
    {
        var report = await _bed.RunAsync(new BlocklistMigrationSection(MigrationSources.Radarr, _bed.Reader), _bed.Context(MigrationSources.Radarr));
        var history = await _bed.RunAsync(new HistoryMigrationSection(MigrationSources.Radarr, _bed.Reader), _bed.Context(MigrationSources.Radarr));

        Assert.Equal(0, report.Created);
        Assert.Equal(0, history.Created);
        Assert.Contains(report.Messages, m => m.Text == "The movie is not in StreamZH.");
        Assert.Empty(Blocklist.Page(1, 10).Records);
    }

    public void Dispose() => _bed.Dispose();
}
