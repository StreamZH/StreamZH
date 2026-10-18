// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Search;
using StreamZH.Library.Wanted;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.Wanted;

/// <summary>Wanted → Missing and Cutoff unmet. The test bed has Dune (no file, released) and Breaking Bad (20 aired episodes, no files).</summary>
public sealed class WantedServiceTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    private static WantedQuery Movies(bool monitored = true, bool available = true) => new(WantedItemType.Movies, MonitoredOnly: monitored, AvailableOnly: available);

    private static WantedQuery Episodes(bool monitored = true, bool available = true) => new(WantedItemType.Episodes, MonitoredOnly: monitored, AvailableOnly: available);

    private Movie AddMovie(string title, int tmdbId, string? inCinemas, bool monitored = true, string? file = null)
    {
        var movie = new Movie
        {
            TmdbId = tmdbId, Title = title, OriginalTitle = title, SortTitle = SortTitles.For(title), Year = 2026, Status = "Post Production",
            Monitored = monitored, QualityProfileId = QualityDefaults.DefaultProfileId, OriginalLanguageId = Language.English.Id,
            RootFolderId = _bed.Dune.RootFolderId, FolderPath = Path.Combine(_bed.DataDirectory, "media", title), Added = DateTime.UtcNow,
            InCinemas = inCinemas, File = file is null ? null : new MediaFile(file, 1, DateTime.UtcNow),
        };
        _bed.Movies.Add(movie);
        return movie;
    }

    private void SetMovieFile(Movie movie, string name)
    {
        movie.File = new MediaFile(name, 1, DateTime.UtcNow);
        _bed.Movies.Update(movie);
    }

    private void SetEpisodeFiles(params (int Season, int Episode, string Name)[] files) =>
        _bed.Series.SetEpisodeFiles(_bed.Show.Id, files.ToDictionary(f => (f.Season, f.Episode), f => new MediaFile(f.Name, 1, DateTime.UtcNow)));

    [Fact]
    public void Missing_movies_follow_the_monitored_flag_and_the_minimum_availability()
    {
        var future = AddMovie("Future Film", 1, "2026-12-01");
        AddMovie("Owned Film", 2, "2020-01-01", file: "Owned.Film.2020.1080p.BluRay.mkv");

        var page = _bed.Wanted().Missing(Movies());
        Assert.Equal([_bed.Dune.Id], page.Records.Select(r => r.Id));
        Assert.Equal((WantedStatus.Missing, (string?)null), (page.Records[0].Status, page.Records[0].AvailableFrom));

        var all = _bed.Wanted().Missing(Movies(available: false));
        var notYet = Assert.Single(all.Records, r => r.Id == future.Id);
        Assert.Equal((WantedStatus.NotAvailable, "2026-12-01"), (notYet.Status, notYet.AvailableFrom));

        // Minimum availability "Announced": everything is available.
        _bed.Options.Save(_bed.Options.Values with { MinimumAvailability = MinimumAvailability.Announced });
        Assert.Equal(2, _bed.Wanted().Missing(Movies()).TotalRecords);

        // "Released" with a known digital release date in the past: available although the cinema date was recent.
        _bed.Options.Save(_bed.Options.Values with { MinimumAvailability = MinimumAvailability.Released });
        future.InCinemas = "2026-09-20";
        future.DigitalRelease = "2026-10-10";
        _bed.Movies.Update(future);
        Assert.Contains(future.Id, _bed.Wanted().Missing(Movies()).Records.Select(r => r.Id));

        _bed.Dune.Monitored = false;
        _bed.Movies.Update(_bed.Dune);
        Assert.DoesNotContain(_bed.Dune.Id, _bed.Wanted().Missing(Movies()).Records.Select(r => r.Id));
        var unmonitored = Assert.Single(_bed.Wanted().Missing(Movies(monitored: false)).Records, r => r.Id == _bed.Dune.Id);
        Assert.False(unmonitored.Monitored);
    }

    [Fact]
    public void Missing_episodes_are_aired_and_monitored_specials_only_when_monitored()
    {
        _bed.Series.UpsertEpisodes(
            _bed.Show.Id,
            [
                .. _bed.Episodes,
                new Episode { SeasonNumber = 0, EpisodeNumber = 1, Title = "Special", AirDate = "2009-02-17" },
                new Episode { SeasonNumber = 3, EpisodeNumber = 1, Title = "Tomorrow", AirDate = "2026-10-18" },
                new Episode { SeasonNumber = 3, EpisodeNumber = 2, Title = "TBA" },
            ],
            monitorNew: true);
        SetEpisodeFiles((1, 1, "Breaking.Bad.S01E01.German.DL.1080p.BluRay.mkv"));

        var page = _bed.Wanted().Missing(Episodes());
        Assert.Equal(19, page.TotalRecords);
        Assert.All(page.Records, r => Assert.Equal(("episode", WantedStatus.Missing), (r.Type, r.Status)));

        var withUnaired = _bed.Wanted().Missing(Episodes(available: false) with { PageSize = 100 });
        Assert.Equal(21, withUnaired.TotalRecords);
        Assert.Equal(WantedStatus.Unaired, withUnaired.Records.Single(r => r.EpisodeTitle == "Tomorrow").Status);
        Assert.DoesNotContain(withUnaired.Records, r => r.SeasonNumber == 0);

        // A special the user monitors on its own is wanted like any other episode.
        _bed.Series.SetEpisodeMonitored(_bed.Episode(0, 1).Id, true);
        Assert.Contains(_bed.Wanted().Missing(Episodes() with { PageSize = 100 }).Records, r => r.SeasonNumber == 0);

        _bed.Show.Monitored = false;
        _bed.Series.Update(_bed.Show);
        Assert.Equal(0, _bed.Wanted().Missing(Episodes()).TotalRecords);
        Assert.Equal(19, _bed.Wanted().Missing(Episodes(monitored: false)).TotalRecords);
    }

    [Fact]
    public void Lists_are_paged_and_sorted_with_counts_for_both_tabs()
    {
        var wanted = _bed.Wanted();

        var newest = wanted.Missing(new WantedQuery(WantedItemType.Episodes, PageSize: 5));
        Assert.Equal((1, 5, 20, 5), (newest.Page, newest.PageSize, newest.TotalRecords, newest.Records.Count));
        Assert.Equal(new WantedCounts(1, 20), newest.Counts);
        Assert.All(newest.Records, r => Assert.Equal("2009-03-08", r.Date));
        Assert.Equal([1, 2, 3, 4, 5], newest.Records.Select(r => r.EpisodeNumber!.Value));
        Assert.Equal("descending", newest.SortDirection);

        var byTitle = wanted.Missing(new WantedQuery(WantedItemType.Episodes, Sort: WantedSortKey.Title, Descending: false, PageSize: 7, Page: 2));
        Assert.Equal([(2, 1), (2, 2), (2, 3), (2, 4), (2, 5), (2, 6), (2, 7)], byTitle.Records.Select(r => (r.SeasonNumber!.Value, r.EpisodeNumber!.Value)));

        var oldest = wanted.Missing(new WantedQuery(WantedItemType.Episodes, Descending: false, PageSize: 1));
        Assert.Equal((1, 1), (oldest.Records[0].SeasonNumber, oldest.Records[0].EpisodeNumber));

        // A page past the end shows the last page.
        var past = wanted.Missing(new WantedQuery(WantedItemType.Episodes, PageSize: 8, Page: 99));
        Assert.Equal((3, 4), (past.Page, past.Records.Count));
    }

    [Fact]
    public void Cutoff_unmet_movies_use_the_stored_quality_of_the_import()
    {
        // Deutsch 1080p: cutoff Bluray-1080p, German, dual language preferred.
        SetMovieFile(_bed.Dune, "Dune.2021.German.DL.720p.WEB.h264-GRP.mkv");
        var low = Assert.Single(_bed.Wanted().Cutoff(Movies()).Records);
        Assert.Equal((WantedStatus.CutoffUnmet, "WEBDL-720p", "Bluray-1080p", "Deutsch 1080p", "German"), (low.Status, low.Quality, low.Cutoff, low.Profile, low.WantedLanguage));
        Assert.Equal([CutoffRules.ReasonQuality], low.Reasons);

        // The import stored Bluray-1080p German + English for this file: the name no longer decides.
        _bed.FileDetails().Save(new MediaFileDetails(
            Path.Combine(_bed.Dune.FolderPath, _bed.Dune.File!.RelativePath), Quality.Bluray1080p.Id, 1, 0, false,
            [Language.German.Id, Language.English.Id], "GRP", null, null, null, null, DateTime.UtcNow));
        Assert.Empty(_bed.Wanted().Cutoff(Movies()).Records);

        // An English file is in the wrong language for a German profile, even when upgrades are off.
        SetMovieFile(_bed.Dune, "Dune.2021.1080p.BluRay.x264-GRP.mkv");
        _bed.Profiles.Update(_bed.Profiles.Find(QualityDefaults.DefaultProfileId)! with { UpgradeAllowed = false });
        var wrong = Assert.Single(_bed.Wanted().Cutoff(Movies()).Records);
        Assert.Equal([CutoffRules.ReasonLanguage], wrong.Reasons);

        // Missing movies are not on the cutoff list, unmonitored ones only with the filter off.
        _bed.Dune.Monitored = false;
        _bed.Movies.Update(_bed.Dune);
        Assert.Empty(_bed.Wanted().Cutoff(Movies()).Records);
        Assert.Single(_bed.Wanted().Cutoff(Movies(monitored: false)).Records);
    }

    [Fact]
    public void Cutoff_unmet_movies_include_files_below_the_custom_format_cutoff_score()
    {
        var formats = new CustomFormatRepository(_bed.Database);
        var good = formats.Add(CustomFormats.CustomFormatTestData.Movie("Good group", 0, CustomFormats.CustomFormatTestData.Title("-GOOD$")));
        _bed.Profiles.Update(_bed.Profiles.Find(QualityDefaults.DefaultProfileId)! with
        {
            FormatItems = [new Core.CustomFormats.ProfileFormatItem(good, 100)],
            CutoffFormatScore = 100,
        });

        SetMovieFile(_bed.Dune, "Dune.2021.German.DL.1080p.BluRay.x264-OTHER.mkv");
        var below = Assert.Single(_bed.Wanted().Cutoff(Movies()).Records);
        Assert.Equal([CutoffRules.ReasonFormatScore], below.Reasons);

        SetMovieFile(_bed.Dune, "Dune.2021.German.DL.1080p.BluRay.x264-GOOD.mkv");
        Assert.Empty(_bed.Wanted().Cutoff(Movies()).Records);
    }

    [Fact]
    public void Cutoff_unmet_episodes_check_quality_and_language_per_episode()
    {
        SetEpisodeFiles(
            (1, 1, "Breaking.Bad.S01E01.German.DL.1080p.BluRay.x264-GRP.mkv"),
            (1, 2, "Breaking.Bad.S01E02.German.DL.720p.WEB.h264-GRP.mkv"),
            (1, 3, "Breaking.Bad.S01E03.1080p.BluRay.x264-GRP.mkv"));

        var page = _bed.Wanted().Cutoff(Episodes() with { Sort = WantedSortKey.Title, Descending = false });
        Assert.Equal([2, 3], page.Records.Select(r => r.EpisodeNumber!.Value));
        Assert.Equal([CutoffRules.ReasonQuality], page.Records[0].Reasons);
        Assert.Equal([CutoffRules.ReasonLanguage], page.Records[1].Reasons);
        Assert.Equal(new WantedCounts(0, 2), page.Counts);

        var byQuality = _bed.Wanted().Cutoff(Episodes() with { Sort = WantedSortKey.Quality, Descending = true });
        Assert.Equal([3, 2], byQuality.Records.Select(r => r.EpisodeNumber!.Value));
        Assert.Equal(new WantedSummary(1, 17, 0, 2), _bed.Wanted().Summary());
    }

    [Fact]
    public void Items_on_their_way_show_their_queue_state()
    {
        // Blackhole folder: a grab in the last 12 hours.
        var episode = _bed.Episode(2, 1);
        _bed.History.Add(new HistoryItem { Date = _bed.Time.Now.UtcDateTime.AddHours(-1), EventType = HistoryEventType.Grabbed, MediaType = MediaType.Movie, MovieId = _bed.Dune.Id, SourceTitle = "Dune" });
        _bed.History.Add(new HistoryItem
        {
            Date = _bed.Time.Now.UtcDateTime.AddHours(-2), EventType = HistoryEventType.Grabbed, MediaType = MediaType.Series, SeriesId = _bed.Show.Id,
            EpisodeIds = [episode.Id], SourceTitle = "Breaking.Bad.S02E01",
        });

        Assert.Equal(QueueSnapshot.Grabbed, _bed.Wanted().Missing(Movies()).Records.Single().Queue);
        var episodes = _bed.Wanted().Missing(Episodes() with { PageSize = 100 }).Records;
        Assert.Equal(QueueSnapshot.Grabbed, episodes.Single(r => r.Id == episode.Id).Queue);
        Assert.Single(episodes, r => r.Queue is not null);

        _bed.Time.Now += TimeSpan.FromHours(13);
        Assert.Null(_bed.Wanted().Missing(Movies()).Records.Single().Queue);
    }

    [Fact]
    public async Task With_the_built_in_engine_the_queue_and_waiting_imports_count()
    {
        _bed.UseBuiltInClient();
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1, _bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12));
        Assert.Equal(1, await _bed.Automatic().SearchMoviesAsync([_bed.Dune.Id]));

        Assert.Equal(QueueSnapshot.Downloading, _bed.Wanted().Missing(Movies()).Records.Single().Queue);

        await _bed.Engine.DeleteAsync(_bed.Engine.Active().Single().Id, deleteFiles: true);
        var output = Directory.CreateDirectory(Path.Combine(_bed.DataDirectory, "complete", "Dune.2021")).FullName;
        var downloads = new DownloadRepository(_bed.Database);
        var record = downloads.Add(new DownloadRecord
        {
            Title = "Dune.2021.German.DL.1080p.BluRay.x264-GRP", MediaType = MediaType.Movie, MovieId = _bed.Dune.Id,
            NzbPath = Path.Combine(_bed.DataDirectory, "x.nzb"), WorkFolder = output, Added = DateTime.UtcNow,
        }, []);
        downloads.Update(record with { Status = Core.Downloads.DownloadStatus.Completed, OutputPath = output, Completed = DateTime.UtcNow });

        Assert.Equal(QueueSnapshot.WaitingForImport, _bed.Wanted().Missing(Movies()).Records.Single().Queue);
    }

    [Fact]
    public async Task Automatic_searches_record_when_they_last_searched()
    {
        _bed.AddIndexer(1, "Alpha");
        Assert.Null(_bed.Wanted().Missing(Movies()).Records.Single().LastSearched);

        await _bed.AutomaticWithLastSearch().SearchMoviesAsync([_bed.Dune.Id]);
        await _bed.AutomaticWithLastSearch().SearchEpisodesAsync(_bed.Show.Id, [_bed.Episode(1, 4).Id]);

        Assert.Equal(_bed.Time.Now.UtcDateTime, _bed.Wanted().Missing(Movies()).Records.Single().LastSearched);
        var episodes = _bed.Wanted().Missing(Episodes() with { Sort = WantedSortKey.LastSearched, PageSize = 100 }).Records;
        Assert.Equal(_bed.Episode(1, 4).Id, episodes[0].Id);
        Assert.Equal(_bed.Time.Now.UtcDateTime, episodes[0].LastSearched);
        Assert.All(episodes.Skip(1), r => Assert.Null(r.LastSearched));
    }

    [Fact]
    public async Task Unmonitor_selected_changes_movies_and_episodes()
    {
        var changed = new List<LibraryChanged>();
        _bed.Events.Subscribe<LibraryChanged>((e, _) =>
        {
            changed.Add(e);
            return ValueTask.CompletedTask;
        });
        var e1 = _bed.Episode(1, 1).Id;
        var e2 = _bed.Episode(1, 2).Id;

        Assert.Equal(3, await _bed.Wanted().SetMonitoredAsync([_bed.Dune.Id, 999], [e1, e2, 12345], monitored: false));

        Assert.False(_bed.Movies.Find(_bed.Dune.Id)!.Monitored);
        Assert.False(_bed.Episode(1, 1).Monitored);
        Assert.Equal(18, _bed.Wanted().Missing(Episodes()).TotalRecords);
        Assert.Equal([(MediaType.Movie, (int?)_bed.Dune.Id), (MediaType.Series, _bed.Show.Id)], changed.Select(c => (c.MediaType, c.Id)));

        // Nothing to change the second time.
        Assert.Equal(0, await _bed.Wanted().SetMonitoredAsync([_bed.Dune.Id], [e1], monitored: false));
    }

    public void Dispose() => _bed.Dispose();
}
