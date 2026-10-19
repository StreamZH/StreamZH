// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Arr.Library;

namespace StreamZH.Tests.Library.Migration;

public sealed class SeriesMigrationTests : IDisposable
{
    private readonly LibraryMigrationTestBed _bed = new();

    [Fact]
    public async Task Preview_finds_TMDB_ids_and_says_what_happens()
    {
        var context = _bed.Context(MigrationSources.Sonarr);

        var plan = await _bed.SeriesSection.PreviewAsync(context, CancellationToken.None);

        var items = plan.Items.ToDictionary(i => i.Key);
        Assert.Equal(["series:450001", "series:424536", "series:999999", "series:71256"], plan.Items.Select(i => i.Key));
        Assert.Equal(MigrationAction.Create, items["series:450001"].Action);
        Assert.Equal($"Standard · 5 files · 5.6 GB · {Path.Combine(_bed.TvFolder, "Crystal Lake")}", items["series:450001"].Detail);
        Assert.StartsWith("Anime · 1 file", items["series:424536"].Detail, StringComparison.Ordinal);

        // Sonarr v3 has no TMDB ids: a few series are looked up during the preview already.
        Assert.Equal(MigrationAction.Create, items["series:71256"].Action);
        Assert.StartsWith("Daily", items["series:71256"].Detail, StringComparison.Ordinal);
        Assert.Equal((MigrationAction.Skip, "No TMDB match for TVDB 999999 – add it by hand."), (items["series:999999"].Action, items["series:999999"].Reason));
        Assert.Equal(2, _bed.Tmdb.FindCalls);
        Assert.DoesNotContain(_bed.Tmdb.Calls, c => c.StartsWith("tv/", StringComparison.Ordinal));
        Assert.Contains("StreamZH has no tags yet; Sonarr's series tags are not taken over.", plan.Warnings!);
        Assert.Empty(_bed.Series.All());
    }

    [Fact]
    public async Task A_big_Sonarr_v3_library_previews_without_TMDB_lookups()
    {
        var series = JsonNode.Parse(FakeArr.Fixture("sonarr-series.json"))!.AsArray();
        var template = series[2]!;
        var many = new JsonArray();
        for (var i = 0; i < SeriesMigrationSection.PreviewLookupLimit + 1; i++)
        {
            var copy = template.DeepClone();
            copy["id"] = 1000 + i;
            copy["tvdbId"] = 500000 + i;
            copy["title"] = $"Show {i}";
            copy["path"] = $"/tv/Show {i}";
            many.Add(copy);
        }

        _bed.Sonarr.Overrides["/series"] = many.ToJsonString();

        var plan = await _bed.SeriesSection.PreviewAsync(_bed.Context(MigrationSources.Sonarr), CancellationToken.None);

        Assert.Equal(0, _bed.Tmdb.FindCalls);
        Assert.All(plan.Items, i => Assert.StartsWith("TMDB lookup needed (TVDB 5000", i.Detail, StringComparison.Ordinal));
        Assert.Contains("51 series have no TMDB id in Sonarr; they are looked up on TMDB during the run.", plan.Warnings!);
    }

    [Fact]
    public async Task Run_adds_series_with_monitoring_and_links_episode_files_without_moving_them()
    {
        var report = await _bed.RunAsync(_bed.SeriesSection, _bed.Context(MigrationSources.Sonarr));

        Assert.Equal((3, 1, 0), (report.Created, report.Skipped, report.Failed));
        var all = _bed.Series.All().ToDictionary(s => s.TmdbId);
        Assert.Equal([2224, 209867, 250001], all.Keys.Order());

        // Crystal Lake: standard numbering, E06 unmonitored like in Sonarr, a multi-episode file, E02 missing on disk.
        var crystal = all[250001];
        Assert.Equal((SeriesType.Standard, true, 2), (crystal.SeriesType, crystal.Monitored, crystal.QualityProfileId));
        Assert.Equal(Path.Combine(_bed.TvFolder, "Crystal Lake"), crystal.FolderPath);
        Assert.Equal(new DateTime(2024, 10, 1, 18, 0, 0, DateTimeKind.Utc), crystal.Added);
        var episodes = _bed.Series.Episodes(crystal.Id).ToDictionary(e => e.EpisodeNumber);
        Assert.Equal(8, episodes.Count);
        Assert.False(episodes[6].Monitored);
        Assert.True(episodes[7].Monitored);
        Assert.Equal("Season 01/Crystal.Lake.S01E01.German.DL.1080p.WEB.h264-ZZGtv.mkv", episodes[1].File?.RelativePath.Replace('\\', '/'));
        Assert.Null(episodes[2].File);
        Assert.Equal(episodes[3].File, episodes[4].File);
        Assert.Equal("Season 01/Crystal.Lake.S01E03E04.German.DL.1080p.WEB.h264-ZZGtv.mkv", episodes[3].File?.RelativePath.Replace('\\', '/'));
        Assert.Contains(report.Messages, m => m.Level == MigrationMessageLevel.Warn && m.Text.Contains("Crystal.Lake.S01E02.German.DL.1080p.WEB.h264-ZZGtv.mkv was not found; S01E02 show as missing", StringComparison.Ordinal));

        // Sonarr's "Bluray-1080p Remux" (Sonarr id 20) is StreamZH's Remux-1080p, not Radarr's id 20 (Bluray-480p).
        var remux = _bed.Details.Find(_bed.TvFile("Crystal Lake", "Season 01/Crystal.Lake.S01E05.German.DL.1080p.BluRay.REMUX.AVC-PtBM.mkv"))!;
        Assert.Equal(Quality.Remux1080p.Id, remux.QualityId);
        Assert.Equal("PtBM", remux.ReleaseGroup);
        Assert.Null(remux.SceneName);
        var multi = _bed.Details.Find(_bed.TvFile("Crystal Lake", "Season 01/Crystal.Lake.S01E03E04.German.DL.1080p.WEB.h264-ZZGtv.mkv"))!;
        Assert.Equal((Quality.WebDl1080p.Id, "Crystal.Lake.S01E03E04.German.DL.1080p.WEB.h264-ZZGtv"), (multi.QualityId, multi.SceneName));

        // Anime: TheTVDB's S02E01 is absolute 3, which is TMDB's S01E03.
        var frieren = all[209867];
        Assert.Equal((SeriesType.Anime, 1), (frieren.SeriesType, frieren.QualityProfileId));
        var anime = _bed.Series.Episodes(frieren.Id).Where(e => e.SeasonNumber == 1).ToDictionary(e => e.EpisodeNumber);
        Assert.Equal("Season 02/[SubsPlease] Sousou no Frieren - 03 (1080p) [9A1B2C3D].mkv", anime[3].File?.RelativePath.Replace('\\', '/'));
        Assert.Equal(3, anime[3].AbsoluteEpisodeNumber);
        Assert.All(anime.Values.Where(e => e.EpisodeNumber != 3), e => Assert.Null(e.File));
        Assert.False(_bed.Series.Episodes(frieren.Id).Single(e => e.SeasonNumber == 0).Monitored);

        // Daily show (Sonarr v3): found by its TVDB id, matched by air date, one language per file.
        var daily = all[2224];
        Assert.Equal(SeriesType.Daily, daily.SeriesType);
        var show = _bed.Series.Episodes(daily.Id).Single(e => e.File is not null);
        Assert.Equal((31, 2, "2026-10-14"), (show.SeasonNumber, show.EpisodeNumber, show.AirDate));
        var dailyDetails = _bed.Details.Find(_bed.TvFile("The Daily Show", "Season 2026/The.Daily.Show.2026.10.14.Guest.1.1080p.WEB.h264-EDITH.mkv"))!;
        Assert.Equal([Language.English.Id], dailyDetails.LanguageIds);

        Assert.Contains(report.Messages, m => m.Item == "Obscure Local Show (2019)" && m.Text == "No TMDB match for TVDB 999999 – add it by hand.");
        Assert.True(File.Exists(_bed.TvFile("Crystal Lake", "Season 01/Crystal.Lake.S01E03E04.German.DL.1080p.WEB.h264-ZZGtv.mkv")));
        Assert.All(_bed.Sonarr.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task A_second_run_is_idempotent_and_a_rescan_keeps_the_files()
    {
        await _bed.RunAsync(_bed.SeriesSection, _bed.Context(MigrationSources.Sonarr));
        var before = Snapshot();

        var report = await _bed.RunAsync(_bed.SeriesSection, _bed.Context(MigrationSources.Sonarr));

        Assert.Equal((0, 3, 1), (report.Created, report.Linked, report.Skipped));
        Assert.Equal(before, Snapshot());

        // The disk scan of StreamZH finds the same files (Sonarr's names parse).
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(_bed.Services);
        var library = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<LibraryService>(scope.ServiceProvider);
        await library.RescanAllAsync();
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Monitor_none_overwrite_and_left_out_series()
    {
        var report = await _bed.RunAsync(_bed.SeriesSection, _bed.Context(
            MigrationSources.Sonarr, new Dictionary<string, string> { ["series.monitor"] = "none" }, exclude: ["series:424536"]));

        Assert.Equal(2, report.Created);
        Assert.DoesNotContain(_bed.Series.All(), s => s.TmdbId == 209867);
        Assert.All(_bed.Series.All(), s => Assert.False(s.Monitored));
        Assert.All(_bed.Series.All().SelectMany(s => _bed.Series.Episodes(s.Id)), e => Assert.False(e.Monitored));
        Assert.NotNull(_bed.Series.Episodes(_bed.Series.All().Single(s => s.TmdbId == 250001).Id).Single(e => e.EpisodeNumber == 1).File);

        var overwrite = await _bed.RunAsync(_bed.SeriesSection, _bed.Context(MigrationSources.Sonarr, new Dictionary<string, string> { ["series.overwrite"] = "on" }));

        Assert.Equal((1, 2), (overwrite.Created, overwrite.Updated));
        var crystal = _bed.Series.All().Single(s => s.TmdbId == 250001);
        Assert.True(crystal.Monitored);
        Assert.Equal(7, _bed.Series.Episodes(crystal.Id).Count(e => e.Monitored));
    }

    [Fact]
    public async Task Without_TMDB_the_run_stops_before_changing_anything()
    {
        _bed.Tmdb.IsConfigured = false;

        var plan = await _bed.SeriesSection.PreviewAsync(_bed.Context(MigrationSources.Sonarr), CancellationToken.None);
        var report = await _bed.RunAsync(_bed.SeriesSection, _bed.Context(MigrationSources.Sonarr));

        Assert.Contains(plan.Warnings!, w => w.StartsWith("TMDB is not set up", StringComparison.Ordinal));
        Assert.All(plan.Items.Where(i => i.Key is "series:71256"), i => Assert.StartsWith("TMDB lookup needed", i.Detail, StringComparison.Ordinal));
        Assert.Equal(1, report.Failed);
        Assert.Empty(_bed.Series.All());
        Assert.Equal(0, _bed.Tmdb.FindCalls);
    }

    [Fact]
    public void Episodes_match_by_number_absolute_number_or_air_date()
    {
        Episode Local(int id, int season, int number, string air, int? absolute) =>
            new() { Id = id, SeasonNumber = season, EpisodeNumber = number, AirDate = air, AbsoluteEpisodeNumber = absolute };
        var local = new[] { Local(1, 1, 1, "2024-01-01", 1), Local(2, 1, 2, "2024-01-08", 2), Local(3, 1, 3, "2024-01-15", 3), Local(4, 0, 1, "2023-12-01", null) };
        ArrEpisode Sonarr(int id, int season, int number, string air, int? absolute = null) =>
            new() { Id = id, SeasonNumber = season, EpisodeNumber = number, AirDate = air, AbsoluteEpisodeNumber = absolute };

        var standard = EpisodeMatcher.Match(SeriesType.Standard, [Sonarr(10, 1, 1, "2024-01-01"), Sonarr(11, 2, 1, "2024-01-15"), Sonarr(12, 0, 1, "1999-01-01")], local);
        Assert.Equal([(10, 1), (11, 3), (12, 4)], standard.Select(m => (m.Key, m.Value.Id)).Order());

        var anime = EpisodeMatcher.Match(SeriesType.Anime, [Sonarr(20, 2, 1, "x", 3), Sonarr(21, 1, 3, "y", 1)], local);
        Assert.Equal([(20, 3), (21, 1)], anime.Select(m => (m.Key, m.Value.Id)).Order());

        var daily = EpisodeMatcher.Match(SeriesType.Daily, [Sonarr(30, 2024, 2, "2024-01-08")], local);
        Assert.Equal(2, daily[30].Id);
    }

    private List<(int TmdbId, int Season, int Episode, bool Monitored, string? File)> Snapshot() =>
    [
        .. _bed.Series.All().SelectMany(s => _bed.Series.Episodes(s.Id).Select(e => (s.TmdbId, e.SeasonNumber, e.EpisodeNumber, e.Monitored, e.File?.RelativePath)))
            .OrderBy(x => x.TmdbId).ThenBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber),
    ];

    public void Dispose() => _bed.Dispose();
}
