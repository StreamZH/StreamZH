// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Library;
using StreamZH.Library.ImportLists;
using static StreamZH.Tests.Library.ImportLists.FakeListTmdb;

namespace StreamZH.Tests.Library.ImportLists;

public sealed class ImportListSyncTests : IDisposable
{
    private readonly ImportListTestBed _bed = new();

    [Fact]
    public async Task Adds_new_movies_and_skips_library_items_exclusions_and_duplicates()
    {
        _bed.AddMovieByHand(Arrival.TmdbId);
        _bed.Lists.AddExclusion(MediaType.Movie, DuneTwo.TmdbId, DuneTwo.Title, DuneTwo.Year, ImportListTestBed.Now.UtcDateTime);
        var list = _bed.AddList("a-watchlist");
        var other = _bed.AddList("b-friends");
        _bed.Source.Set("a-watchlist", Item(Dune), Item(Dune), Item(Arrival), Item(DuneTwo));
        _bed.Source.Set("b-friends", Item(Dune, tmdbId: false));

        var summary = await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.Equal((2, 4, 1, 0), (summary.Lists, summary.Found, summary.Added, summary.Failed));
        Assert.Equal(["Dune (2021)"], summary.AddedTitles);
        Assert.Equal([Arrival.TmdbId, Dune.TmdbId], _bed.Movies.All().Select(m => m.TmdbId).Order());
        var dune = _bed.Movies.FindByTmdbId(Dune.TmdbId)!;
        Assert.True(dune.Monitored);
        Assert.StartsWith(Path.Combine(_bed.Root, "movies"), dune.FolderPath, StringComparison.Ordinal);

        // The first list added it; the item is not added twice by the second.
        Assert.Equal(list.Id, _bed.Lists.FindOrigin(MediaType.Movie, Dune.TmdbId)!.ListId);
        Assert.Null(_bed.Lists.FindOrigin(MediaType.Movie, Arrival.TmdbId));
        var status = _bed.Lists.Find(list.Id)!.Status;
        Assert.Equal((3, 1, 0), (status.LastFound!.Value, status.LastAdded!.Value, status.Failures));
        Assert.Equal(["Dune (2021)"], status.LastAddedTitles);
        Assert.Equal((1, 0), (_bed.Lists.Find(other.Id)!.Status.LastFound!.Value, _bed.Lists.Find(other.Id)!.Status.LastAdded!.Value));
        Assert.Equal([$"search-new-movie:{dune.Id}"], _bed.QueuedJobs().Select(j => j.Key));
    }

    [Fact]
    public async Task Imdb_ids_are_matched_through_tmdb_and_unknown_ones_are_reported()
    {
        _bed.AddList("imdb");
        _bed.Source.Set("imdb", Item(Arrival, tmdbId: false), new ImportListItem(MediaType.Movie, "Unknown", 1999, ImdbId: "tt9999999"));

        var preview = await _bed.Sync().PreviewAsync(_bed.Lists.All()[0], CancellationToken.None);
        var summary = await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.Equal(
            [(ImportListItemStates.New, (int?)Arrival.TmdbId), (ImportListItemStates.NotFound, null)],
            preview.Items.Select(i => (i.State, i.TmdbId)));
        Assert.Equal("tt9999999", preview.Items[1].ImdbId);
        Assert.Equal(1, summary.Added);
        Assert.NotNull(_bed.Movies.FindByTmdbId(Arrival.TmdbId));
    }

    [Fact]
    public async Task Monitor_none_and_no_search_on_add()
    {
        _bed.AddList("quiet", monitor: ImportListMonitor.None, searchOnAdd: true);
        _bed.AddList("nosearch", searchOnAdd: false);
        _bed.Source.Set("quiet", Item(Dune));
        _bed.Source.Set("nosearch", Item(Arrival));

        await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.False(_bed.Movies.FindByTmdbId(Dune.TmdbId)!.Monitored);
        Assert.True(_bed.Movies.FindByTmdbId(Arrival.TmdbId)!.Monitored);
        Assert.Empty(_bed.QueuedJobs());
    }

    [Fact]
    public async Task Without_automatic_add_the_list_is_only_read()
    {
        var list = _bed.AddList("manual", enableAuto: false);
        _bed.Source.Set("manual", Item(Dune), Item(Arrival));

        var summary = await _bed.Sync().SyncAllAsync(CancellationToken.None);
        var added = await _bed.Sync().AddAsync(_bed.Lists.Find(list.Id)!, Arrival.TmdbId, CancellationToken.None);

        Assert.Equal((2, 0), (summary.Found, summary.Added));
        Assert.Equal(["Arrival (2016)"], added);
        Assert.Equal([Arrival.TmdbId], _bed.Movies.All().Select(m => m.TmdbId));
        Assert.Equal(list.Id, _bed.Lists.FindOrigin(MediaType.Movie, Arrival.TmdbId)!.ListId);
        var again = await Assert.ThrowsAsync<LibraryException>(() => _bed.Sync().AddAsync(list, Arrival.TmdbId, CancellationToken.None));
        Assert.Equal(LibraryError.AlreadyExists, again.Error);
    }

    [Fact]
    public async Task Minimum_availability_waits_until_the_movie_is_available()
    {
        _bed.AddList("released", availability: ImportListAvailability.Released);
        _bed.AddList("cinemas", availability: ImportListAvailability.InCinemas);
        _bed.Source.Set("released", Item(Fresh), Item(Arrival), new ImportListItem(MediaType.Movie, "Upcoming", 2027, Upcoming.TmdbId));
        _bed.Source.Set("cinemas", new ImportListItem(MediaType.Movie, "Upcoming", 2027, Upcoming.TmdbId));

        var preview = await _bed.Sync().PreviewAsync(_bed.Lists.FindByName("released")!, CancellationToken.None);
        await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.Equal(
            [(ImportListItemStates.Waiting, "2026-12-17"), (ImportListItemStates.New, null), (ImportListItemStates.Waiting, "2027-05-30")],
            preview.Items.Select(i => (i.State, i.AvailableFrom)));
        Assert.Equal([Arrival.TmdbId], _bed.Movies.All().Select(m => m.TmdbId));

        // In cinemas: "Fresh" would be in; "Upcoming" still waits.
        _bed.Source.Set("cinemas", Item(Fresh), new ImportListItem(MediaType.Movie, "Upcoming", 2027, Upcoming.TmdbId));
        await _bed.Sync().SyncAllAsync(CancellationToken.None);
        Assert.Equal([Fresh.TmdbId, Arrival.TmdbId], _bed.Movies.All().Select(m => m.TmdbId).Order());
    }

    [Fact]
    public async Task Movie_and_collection_adds_the_rest_of_the_collection()
    {
        _bed.AddList("dune", monitor: ImportListMonitor.MovieAndCollection);
        _bed.Source.Set("dune", Item(Dune), Item(Arrival));

        var summary = await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.Equal(["Dune (2021)", "Dune: Part Two (2024)", "Arrival (2016)"], summary.AddedTitles);
        Assert.NotNull(_bed.Lists.FindOrigin(MediaType.Movie, DuneTwo.TmdbId));

        // A new part of a collection of a movie already in the library still follows; an excluded part does not.
        await _bed.Library.DeleteMovieAsync(_bed.Movies.FindByTmdbId(DuneTwo.TmdbId)!.Id);
        var again = await _bed.Sync().SyncAllAsync(CancellationToken.None);
        Assert.Equal(["Dune: Part Two (2024)"], again.AddedTitles);

        await _bed.Library.DeleteMovieAsync(_bed.Movies.FindByTmdbId(DuneTwo.TmdbId)!.Id);
        _bed.Lists.AddExclusion(MediaType.Movie, DuneTwo.TmdbId, DuneTwo.Title, DuneTwo.Year, ImportListTestBed.Now.UtcDateTime);
        Assert.Equal(0, (await _bed.Sync().SyncAllAsync(CancellationToken.None)).Added);
    }

    [Theory]
    [InlineData(ImportListMonitor.All, "1:1,1:2,1:3,2:1,2:2")]
    [InlineData(ImportListMonitor.Future, "2:1,2:2")]
    [InlineData(ImportListMonitor.Missing, "1:1,1:2,1:3,2:1,2:2")]
    [InlineData(ImportListMonitor.FirstSeason, "1:1,1:2,1:3")]
    [InlineData(ImportListMonitor.LatestSeason, "2:1,2:2")]
    [InlineData(ImportListMonitor.None, "")]
    public async Task Series_monitor_options_set_the_monitored_episodes(string monitor, string expected)
    {
        _bed.AddList("shows", MediaType.Series, monitor);
        _bed.Source.Set("shows", new ImportListItem(MediaType.Series, "Breaking Bad", 2008, TvdbId: 81189));

        var summary = await _bed.Sync().SyncAllAsync(CancellationToken.None);

        var show = Assert.Single(_bed.Series.All());
        Assert.Equal(1, summary.Added);
        Assert.Equal(monitor != ImportListMonitor.None, show.Monitored);
        var monitored = _bed.Series.Episodes(show.Id).Where(e => e.Monitored).Select(e => $"{e.SeasonNumber}:{e.EpisodeNumber}");
        Assert.Equal(expected, string.Join(',', monitored));
        Assert.Equal(6, _bed.Series.Episodes(show.Id).Count);
        Assert.Equal(monitor == ImportListMonitor.None ? [] : [$"search-missing-episodes:{show.Id}"], _bed.QueuedJobs().Select(j => j.Key));
    }

    [Fact]
    public async Task A_failing_list_counts_failures_until_it_works_again()
    {
        var list = _bed.AddList("plex");
        _bed.Source.Failing["plex"] = "Plex rejected the credentials (401)";

        await _bed.Sync().SyncAllAsync(CancellationToken.None);
        var summary = await _bed.Sync().SyncAsync(list.Id, CancellationToken.None);

        var failed = _bed.Lists.Find(list.Id)!.Status;
        Assert.Equal(1, summary.Failed);
        Assert.Equal((2, "Plex rejected the credentials (401)", true), (failed.Failures, failed.LastError, failed.Failing));
        Assert.Null(failed.LastSuccess);

        _bed.Source.Failing.Clear();
        _bed.Source.Set("plex", Item(Dune));
        await _bed.Sync().SyncAsync(list.Id, CancellationToken.None);
        var ok = _bed.Lists.Find(list.Id)!.Status;
        Assert.Equal((0, null, 1), (ok.Failures, ok.LastError, ok.LastAdded));
        Assert.NotNull(ok.LastSuccess);
    }

    [Fact]
    public async Task A_removed_root_folder_fails_the_list()
    {
        var list = _bed.AddList("lost");
        _bed.Source.Set("lost", Item(Dune));
        _bed.RootFolders.Delete(_bed.MovieRoot);

        await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.Equal("cannot add: root folder not found", _bed.Lists.Find(list.Id)!.Status.LastError);
    }

    [Fact]
    public async Task Disabled_lists_are_not_synced()
    {
        _bed.AddList("off", enabled: false);
        _bed.Source.Set("off", Item(Dune));

        var summary = await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.Equal(0, summary.Lists);
        Assert.Empty(_bed.Movies.All());
    }

    // ---- Clean library ----

    [Fact]
    public async Task Clean_library_disabled_and_log_only_change_nothing()
    {
        await AddDuneAndArrivalThenDropDune();

        var disabled = await _bed.Sync().SyncAllAsync(CancellationToken.None);
        _bed.Options.Save(new ImportListOptionValues(24, CleanLibraryLevel.LogOnly));
        var logOnly = await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.Null(disabled.Cleaned);
        Assert.Equal(1, logOnly.Cleaned);
        Assert.True(_bed.Movies.FindByTmdbId(Dune.TmdbId)!.Monitored);
    }

    [Fact]
    public async Task Clean_library_unmonitors_only_items_a_list_added()
    {
        await AddDuneAndArrivalThenDropDune();
        _bed.AddMovieByHand(DuneTwo.TmdbId);
        _bed.Options.Save(new ImportListOptionValues(24, CleanLibraryLevel.KeepAndUnmonitor));

        var summary = await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.Cleaned);
        Assert.False(_bed.Movies.FindByTmdbId(Dune.TmdbId)!.Monitored);
        Assert.True(_bed.Movies.FindByTmdbId(Arrival.TmdbId)!.Monitored);
        Assert.True(_bed.Movies.FindByTmdbId(DuneTwo.TmdbId)!.Monitored);
    }

    [Fact]
    public async Task Clean_library_removes_items_but_keeps_their_files()
    {
        await AddDuneAndArrivalThenDropDune();
        var dune = _bed.Movies.FindByTmdbId(Dune.TmdbId)!;
        Directory.CreateDirectory(dune.FolderPath);
        await File.WriteAllTextAsync(Path.Combine(dune.FolderPath, "Dune (2021).mkv"), "video");
        _bed.Options.Save(new ImportListOptionValues(24, CleanLibraryLevel.RemoveAndKeepFiles));

        var summary = await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.Cleaned);
        Assert.Null(_bed.Movies.FindByTmdbId(Dune.TmdbId));
        Assert.NotNull(_bed.Movies.FindByTmdbId(Arrival.TmdbId));
        Assert.True(File.Exists(Path.Combine(dune.FolderPath, "Dune (2021).mkv")));
        Assert.Null(_bed.Lists.FindOrigin(MediaType.Movie, Dune.TmdbId));
    }

    [Fact]
    public async Task Clean_library_is_skipped_when_a_list_fails_or_suddenly_comes_back_empty()
    {
        await AddDuneAndArrivalThenDropDune();
        _bed.Options.Save(new ImportListOptionValues(24, CleanLibraryLevel.RemoveAndKeepFiles));
        _bed.AddList("broken");
        _bed.Source.Failing["broken"] = "TMDB: not found (check the number)";

        var failed = await _bed.Sync().SyncAllAsync(CancellationToken.None);
        Assert.Equal("broken could not be read", failed.CleanSkipped);

        _bed.Source.Failing.Clear();
        _bed.Source.Set("broken", Item(Fresh));
        await _bed.Sync().SyncAllAsync(CancellationToken.None); // cleans Dune, the list now had an item
        _bed.Source.Set("broken");
        var empty = await _bed.Sync().SyncAllAsync(CancellationToken.None);

        Assert.Equal("broken came back empty", empty.CleanSkipped);
        Assert.NotNull(_bed.Movies.FindByTmdbId(Fresh.TmdbId));
        Assert.Null(_bed.Movies.FindByTmdbId(Dune.TmdbId));
    }

    [Fact]
    public async Task Syncing_one_list_never_cleans()
    {
        await AddDuneAndArrivalThenDropDune();
        _bed.Options.Save(new ImportListOptionValues(24, CleanLibraryLevel.RemoveAndKeepFiles));

        var summary = await _bed.Sync().SyncAsync(_bed.Lists.All()[0].Id, CancellationToken.None);

        Assert.Null(summary.Cleaned);
        Assert.NotNull(_bed.Movies.FindByTmdbId(Dune.TmdbId));
    }

    [Fact]
    public async Task Preview_shows_library_and_excluded_items()
    {
        _bed.AddMovieByHand(Arrival.TmdbId);
        _bed.Lists.AddExclusion(MediaType.Movie, Dune.TmdbId, "Dune", 2021, ImportListTestBed.Now.UtcDateTime);
        var list = _bed.AddList("mixed");
        _bed.Source.Set("mixed", Item(Dune), Item(Arrival), Item(DuneTwo), new ImportListItem(MediaType.Series, "Breaking Bad", 2008, 1396));

        var preview = await _bed.Sync().PreviewAsync(list, CancellationToken.None);

        Assert.Equal("mixed", preview.Name);
        Assert.Equal(
            [ImportListItemStates.Excluded, ImportListItemStates.InLibrary, ImportListItemStates.New],
            preview.Items.Select(i => i.State));
        Assert.Single(_bed.Movies.All());
    }

    private async Task AddDuneAndArrivalThenDropDune()
    {
        _bed.AddList("watchlist", searchOnAdd: false);
        _bed.Source.Set("watchlist", Item(Dune), Item(Arrival));
        await _bed.Sync().SyncAllAsync(CancellationToken.None);
        Assert.Equal(2, _bed.Movies.All().Count);
        _bed.Source.Set("watchlist", Item(Arrival));
    }

    public void Dispose() => _bed.Dispose();
}
