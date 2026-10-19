// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;
using StreamZH.Tests.Library;

namespace StreamZH.Tests.Library.Downloads;

/// <summary>The queue switches, categories, duplicates and history of Settings → Downloads, through the engine.</summary>
public sealed class DownloadSwitchesTests : IDisposable
{
    /// <summary>The "date" of every test NZB (1760000000).</summary>
    private static readonly DateTimeOffset Posted = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);

    private readonly DownloadTestBed _bed = new();

    private TestRelease Simple(string title, int size = 12_000) => _bed.Release(title, ("data.mkv", size, 4000));

    private void Set(DownloadSettingsChange change) => _bed.Settings.Update(change);

    // ---------------------------------------------------------------- categories

    [Fact]
    public async Task Custom_category_gives_folder_priority_and_post_processing()
    {
        var anime = _bed.Categories.Create(new DownloadCategoryDefinition(0, "anime", "Anime/Shows", 1, PostProcessLevel.Unpack));
        var engine = await _bed.StartAsync();
        var release = Simple("Some.Anime.S01E01.1080p-GRP");

        var added = await engine.AddAsync(DownloadTestBed.Request(release, DownloadCategory.Other, priority: null, categoryName: "Anime"));
        Assert.Equal(1, added.Priority);
        Assert.Equal("anime", added.CategoryName);
        Assert.Equal(DownloadCategory.Other, added.Category);

        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        var job = Assert.Single(_bed.PostProcessor.Jobs);
        Assert.Equal(PostProcessLevel.Unpack, job.Level);
        Assert.Equal(Path.Combine(_bed.Settings.CompleteFolder, "Anime", "Shows", release.Title), job.TargetFolder);
        Assert.Equal(job.TargetFolder, done.OutputPath);
        Assert.Equal("anime", engine.History(1, 10).Items[0].CategoryName);
        Assert.Equal(anime.Id, _bed.Categories.Find("ANIME")!.Id);
    }

    [Fact]
    public async Task Default_categories_keep_the_folders_used_so_far_and_the_request_priority_wins()
    {
        _bed.Categories.Update(_bed.Categories.Find("series")!.Id, _bed.Categories.Find("series")! with { Priority = 2 });
        var engine = await _bed.StartAsync();

        var movie = await engine.AddAsync(DownloadTestBed.Request(Simple("A.Movie.2024.1080p-GRP"), DownloadCategory.Movie, priority: null));
        var series = await engine.AddAsync(DownloadTestBed.Request(Simple("A.Show.S01E01.1080p-GRP"), DownloadCategory.Series, priority: null));
        var low = await engine.AddAsync(DownloadTestBed.Request(Simple("A.Show.S01E02.1080p-GRP"), DownloadCategory.Series, priority: -1));

        Assert.Equal(("movies", 0), (movie.CategoryName, movie.Priority));
        Assert.Equal(("series", 2), (series.CategoryName, series.Priority));
        Assert.Equal(-1, low.Priority);
        var done = await DownloadTestBed.WaitForAsync(engine, movie.Id, DownloadStatus.Completed);
        Assert.Equal(Path.Combine(_bed.Settings.CompleteFolder, "movies", "A.Movie.2024.1080p-GRP"), done.OutputPath);
    }

    [Fact]
    public async Task Paused_category_priority_and_absolute_folder()
    {
        var folder = Path.Combine(_bed.DataDirectory, "downloads", "elsewhere");
        _bed.Categories.Create(new DownloadCategoryDefinition(0, "later", folder, DownloadCategoryService.PausedPriority, PostProcessLevel.Full));
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(Simple("Later.2024.1080p-GRP"), categoryName: "later", priority: null));
        Assert.Equal(DownloadStatus.Paused, added.Status);
        Assert.Equal(0, added.Priority);
        await Task.Delay(200);
        Assert.Empty(_bed.Articles.Fetched);

        await engine.ResumeAsync(added.Id);
        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        Assert.Equal(Path.Combine(folder, "Later.2024.1080p-GRP"), done.OutputPath);
    }

    [Fact]
    public async Task Unknown_category_is_refused_and_defaults_cannot_be_renamed_or_deleted()
    {
        var engine = await _bed.StartAsync();
        var error = await Assert.ThrowsAsync<LibraryException>(() => engine.AddAsync(DownloadTestBed.Request(Simple("X.2024-GRP"), categoryName: "nope")));
        Assert.Contains("nope", error.Message);

        var movies = _bed.Categories.Find("movies")!;
        Assert.True(movies.IsDefault);
        var rename = Assert.Throws<ValidationFailedException>(() => _bed.Categories.Update(movies.Id, movies with { Name = "films" }));
        Assert.Contains("name", rename.Errors.Keys);
        Assert.Throws<LibraryException>(() => _bed.Categories.Delete(movies.Id));
        Assert.Equal(["movies", "series", "other"], _bed.Categories.All().Select(c => c.Name));

        var invalid = Assert.Throws<ValidationFailedException>(() => _bed.Categories.Create(new DownloadCategoryDefinition(0, "MOVIES", "../up", 7, PostProcessLevel.Full)));
        Assert.Equal(["folder", "name", "priority"], invalid.Errors.Keys.Order());

        var custom = _bed.Categories.Create(new DownloadCategoryDefinition(0, "docs", string.Empty, null, PostProcessLevel.None));
        _bed.Categories.Delete(custom.Id);
        Assert.Null(_bed.Categories.Find("docs"));
    }

    [Fact]
    public async Task A_queued_download_can_move_to_a_custom_category_and_get_a_password()
    {
        _bed.Categories.Create(new DownloadCategoryDefinition(0, "keep", "kept", null, PostProcessLevel.None));
        var engine = await _bed.StartAsync();
        await engine.PauseAllAsync();
        var added = await engine.AddAsync(DownloadTestBed.Request(Simple("Move.Me.2024-GRP")));

        var updated = await engine.UpdateAsync(added.Id, null, null, null, "keep", "secret");
        Assert.Equal("keep", updated!.CategoryName);
        Assert.Equal("secret", _bed.Repository.Find(added.Id)!.Password);
        await Assert.ThrowsAsync<LibraryException>(() => engine.UpdateAsync(added.Id, null, null, null, "nope"));

        await engine.ResumeAllAsync();
        await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        Assert.Equal(PostProcessLevel.None, Assert.Single(_bed.PostProcessor.Jobs).Level);
    }

    // ---------------------------------------------------------------- duplicates

    [Fact]
    public async Task Duplicate_detection_off_by_default()
    {
        var engine = await _bed.StartAsync();
        await engine.PauseAllAsync();
        var release = Simple("Same.Release.2024.1080p-GRP");
        await engine.AddAsync(DownloadTestBed.Request(release));
        var second = await engine.AddAsync(DownloadTestBed.Request(release));
        Assert.False(second.Duplicate);
        Assert.Null(second.StatusMessage);
    }

    [Fact]
    public async Task Duplicates_are_tagged_by_name_and_by_nzb()
    {
        Set(new DownloadSettingsChange { DuplicateAction = "tag" });
        var engine = await _bed.StartAsync();
        await engine.PauseAllAsync();
        var release = Simple("Same.Release.2024.1080p-GRP");
        var first = await engine.AddAsync(DownloadTestBed.Request(release));

        var byName = await engine.AddAsync(DownloadTestBed.Request(Simple("same release 2024 1080p GRP", 4000)));
        var byNzb = await engine.AddAsync(DownloadTestBed.Request(release) with { Title = "Renamed.By.Indexer" });

        Assert.False(first.Duplicate);
        Assert.True(byName.Duplicate);
        Assert.True(byNzb.Duplicate);
        Assert.Equal(DownloadStatus.Queued, byNzb.Status);
        Assert.Equal("Duplicate: 'Same.Release.2024.1080p-GRP' is in the queue.", byNzb.StatusMessage);
        Assert.True(_bed.Repository.Find(byNzb.Id)!.Duplicate);
    }

    [Fact]
    public async Task Duplicate_pause_and_fail_and_the_history_counts_but_failed_downloads_do_not()
    {
        var finished = new ConcurrentQueue<DownloadFinished>();
        _bed.Events.Subscribe<DownloadFinished>((e, _) =>
        {
            finished.Enqueue(e);
            return ValueTask.CompletedTask;
        });
        var engine = await _bed.StartAsync();
        var release = Simple("Done.Before.2024.1080p-GRP");
        var first = await engine.AddAsync(DownloadTestBed.Request(release));
        await DownloadTestBed.WaitForAsync(engine, first.Id, DownloadStatus.Completed);

        Set(new DownloadSettingsChange { DuplicateAction = "pause" });
        var paused = await engine.AddAsync(DownloadTestBed.Request(release));
        Assert.Equal(DownloadStatus.Paused, paused.Status);
        Assert.StartsWith("Paused, duplicate: 'Done.Before.2024.1080p-GRP' was downloaded on ", paused.StatusMessage);
        await engine.DeleteAsync(paused.Id, deleteFiles: true);

        Set(new DownloadSettingsChange { DuplicateAction = "fail" });
        var failed = await engine.AddAsync(DownloadTestBed.Request(release));
        Assert.Equal(DownloadStatus.Failed, failed.Status);
        Assert.StartsWith("Duplicate: 'Done.Before.2024.1080p-GRP' was downloaded", failed.Error);
        await DownloadTestBed.WaitUntilAsync(() => finished.Count(f => f.Item.Id == failed.Id) == 1);
        Assert.Equal(DownloadStatus.Failed, _bed.Repository.Find(failed.Id)!.Status);

        // A release that failed is no duplicate: grabbing it again (or another copy) is allowed.
        var other = Simple("Failed.Before.2024.1080p-GRP");
        _bed.Articles.Missing[other.Files[0].MessageIds[0]] = 0;
        _bed.Articles.Missing[other.Files[0].MessageIds[1]] = 0;
        var broken = await engine.AddAsync(DownloadTestBed.Request(other));
        await DownloadTestBed.WaitForAsync(engine, broken.Id, DownloadStatus.Failed);
        var again = await engine.AddAsync(DownloadTestBed.Request(other));
        Assert.NotEqual(DownloadStatus.Failed, again.Status);
        Assert.False(again.Duplicate);
        await Task.Delay(100);
        Assert.Equal(1, finished.Count(f => f.Item.Id == failed.Id));
    }

    // ---------------------------------------------------------------- unwanted, par2, backup

    [Theory]
    [InlineData("warn", DownloadStatus.Queued)]
    [InlineData("pause", DownloadStatus.Paused)]
    [InlineData("fail", DownloadStatus.Failed)]
    public async Task Unwanted_extensions_in_the_nzb(string action, DownloadStatus expected)
    {
        Set(new DownloadSettingsChange { UnwantedAction = action, UnwantedExtensions = ".exe, scr" });
        var engine = await _bed.StartAsync();
        await engine.PauseAllAsync();
        var release = _bed.Release("Has.An.Exe.2024-GRP", ("data.mkv", 8000, 4000), ("setup.exe", 400, 4000));

        var added = await engine.AddAsync(DownloadTestBed.Request(release));

        Assert.Equal(expected, added.Status);
        Assert.Contains("setup.exe", expected == DownloadStatus.Failed ? added.Error : added.StatusMessage);
    }

    [Fact]
    public async Task Download_all_par2_files_fetches_the_recovery_volumes_right_away()
    {
        Set(new DownloadSettingsChange { DownloadAllPar2 = true });
        var engine = await _bed.StartAsync();
        var release = _bed.Release("All.Par2.2024-GRP", ("movie.par2", 500, 4000), ("movie.mkv", 8000, 4000), ("movie.vol00+01.par2", 4000, 4000));

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);

        Assert.All(engine.Files(added.Id)!, f => Assert.True(f.Wanted));
        Assert.Contains(release["movie.vol00+01.par2"].MessageIds[0], _bed.Articles.Fetched);
    }

    [Fact]
    public async Task Nzb_backup_folder_gets_a_copy_of_every_nzb()
    {
        var backup = Path.Combine(_bed.DataDirectory, "downloads", "nzb-backup");
        Set(new DownloadSettingsChange { NzbBackupFolder = backup });
        var engine = await _bed.StartAsync();
        var release = Simple("Backed.Up.2024-GRP");

        await engine.AddAsync(DownloadTestBed.Request(release));

        Assert.Equal(release.Nzb, await File.ReadAllBytesAsync(Path.Combine(backup, "Backed.Up.2024-GRP.nzb")));
    }

    // ---------------------------------------------------------------- queue switches

    [Fact]
    public async Task Pause_downloading_on_start()
    {
        Set(new DownloadSettingsChange { PauseOnStart = true });
        var engine = await _bed.StartAsync();

        Assert.True(engine.Status().Paused);
        Assert.True(engine.PausedOnStart);
        Assert.Contains("startup", engine.Status().StatusMessage);
        var added = await engine.AddAsync(DownloadTestBed.Request(Simple("Waits.2024-GRP")));
        await Task.Delay(200);
        Assert.Empty(_bed.Articles.Fetched);

        await engine.ResumeAllAsync();
        Assert.False(engine.PausedOnStart);
        await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        Assert.Equal("false", _bed.Config.Get(ConfigKeys.DownloadsPaused));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Max_active_downloads_one_is_strictly_one_after_the_other(int maxActive, bool overlaps)
    {
        Set(new DownloadSettingsChange { MaxActiveDownloads = maxActive });
        _bed.Articles.MaxConnections = 2;
        var first = Simple("First.2024-GRP", 8000);
        var second = Simple("Second.2024-GRP", 8000);
        var last = first.Files[0].MessageIds[^1];
        _bed.Articles.Block = id => id == last;
        var engine = await _bed.StartAsync();

        var a = await engine.AddAsync(DownloadTestBed.Request(first));
        var b = await engine.AddAsync(DownloadTestBed.Request(second));
        await DownloadTestBed.WaitUntilAsync(() => _bed.Articles.Fetched.Contains(last));
        await Task.Delay(300);

        Assert.Equal(overlaps, _bed.Articles.Fetched.Any(id => id.StartsWith("Second", StringComparison.Ordinal)));
        _bed.Articles.Release();
        await DownloadTestBed.WaitForAsync(engine, a.Id, DownloadStatus.Completed);
        await DownloadTestBed.WaitForAsync(engine, b.Id, DownloadStatus.Completed);
    }

    [Fact]
    public async Task Propagation_delay_waits_after_the_post_date()
    {
        var clock = new ManualTimeProvider(Posted.AddMinutes(10));
        _bed.Options.Time = clock;
        Set(new DownloadSettingsChange { PropagationDelayMinutes = 30 });
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(Simple("Fresh.2024-GRP")));
        await DownloadTestBed.WaitUntilAsync(() => engine.Find(added.Id)!.StatusMessage?.StartsWith("Propagation delay", StringComparison.Ordinal) == true);
        Assert.Equal("Propagation delay: starts in 20 min", engine.Find(added.Id)!.StatusMessage);
        await Task.Delay(200);
        Assert.Empty(_bed.Articles.Fetched);

        clock.Advance(TimeSpan.FromMinutes(21));
        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        Assert.Null(done.StatusMessage);
    }

    [Fact]
    public async Task Pre_check_fails_a_download_that_cannot_be_completed_without_downloading_it()
    {
        Set(new DownloadSettingsChange { PreCheck = true });
        var engine = await _bed.StartAsync();
        var release = _bed.Release("Taken.Down.2024-GRP", ("data.mkv", 40_000, 4000));
        foreach (var id in release.AllMessageIds.Take(5))
        {
            _bed.Articles.Missing[id] = 0;
        }

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        var failed = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Failed);

        Assert.StartsWith("Pre-check: 5 of 10 articles are missing (", failed.Error);
        Assert.Contains("% of the data), more than par2 can repair", failed.Error);
        Assert.Equal(10, _bed.Articles.Checked.Count);
        Assert.Empty(_bed.Articles.Fetched);
    }

    [Fact]
    public async Task Pre_check_lets_a_complete_download_through_once()
    {
        Set(new DownloadSettingsChange { PreCheck = true });
        var engine = await _bed.StartAsync();
        var release = _bed.Release("Complete.2024-GRP", ("data.mkv", 20_000, 4000));

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);

        Assert.Equal(5, _bed.Articles.Checked.Count);
        Assert.All(release.AllMessageIds, id => Assert.Equal(1, _bed.Articles.Count(id)));
    }

    [Theory]
    [InlineData("pause", DownloadStatus.Paused)]
    [InlineData("fail", DownloadStatus.Failed)]
    [InlineData("off", DownloadStatus.Completed)]
    public async Task Password_protected_rar_is_noticed_from_its_first_article(string action, DownloadStatus expected)
    {
        Set(new DownloadSettingsChange { EncryptedAction = action });
        _bed.Articles.MaxConnections = 1;
        var release = _bed.Release("Locked.2024-GRP", ("locked.part1.rar", 20_000, 4000));
        PutAtStart(release, "locked.part1.rar", EncryptedRar4());
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        var item = await DownloadTestBed.WaitForAsync(engine, added.Id, expected);

        if (expected == DownloadStatus.Paused)
        {
            Assert.Contains("password protected", item.StatusMessage);
            await Task.Delay(200);
            Assert.True(_bed.Articles.Fetched.Count < 5);

            // With the password it continues.
            await engine.UpdateAsync(added.Id, null, null, null, password: "secret");
            await engine.ResumeAsync(added.Id);
            await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        }
        else if (expected == DownloadStatus.Failed)
        {
            Assert.Contains("password protected", item.Error);
        }
    }

    [Fact]
    public async Task A_known_password_skips_the_encrypted_rar_action()
    {
        Set(new DownloadSettingsChange { EncryptedAction = "fail" });
        var release = _bed.Release("Locked.Known.2024-GRP", ("locked.rar", 8000, 4000));
        PutAtStart(release, "locked.rar", EncryptedRar4());
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(release) with { Password = "secret" });
        await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
    }

    [Fact]
    public async Task Auto_sort_by_size_puts_the_smallest_first()
    {
        Set(new DownloadSettingsChange { AutoSort = "smallest" });
        var engine = await _bed.StartAsync();
        await engine.PauseAllAsync();

        await engine.AddAsync(DownloadTestBed.Request(Simple("Big.2024-GRP", 30_000)));
        await engine.AddAsync(DownloadTestBed.Request(Simple("Small.2024-GRP", 4000)));
        await engine.AddAsync(DownloadTestBed.Request(Simple("Middle.2024-GRP", 12_000)));
        await engine.AddAsync(DownloadTestBed.Request(Simple("Urgent.2024-GRP", 50_000), priority: 2));

        Assert.Equal(["Urgent.2024-GRP", "Small.2024-GRP", "Middle.2024-GRP", "Big.2024-GRP"], engine.Active().Select(d => d.Title));
        Assert.Equal(["Urgent.2024-GRP", "Small.2024-GRP", "Middle.2024-GRP", "Big.2024-GRP"], _bed.CreateEngine().Active().Select(d => d.Title));
    }

    [Fact]
    public async Task Paused_category_waits_and_is_remembered()
    {
        var engine = await _bed.StartAsync();
        await engine.SetCategoryPausedAsync("movies", paused: true);

        var movie = await engine.AddAsync(DownloadTestBed.Request(Simple("Movie.2024-GRP")));
        var other = await engine.AddAsync(DownloadTestBed.Request(Simple("Other.2024-GRP"), DownloadCategory.Other));
        await DownloadTestBed.WaitForAsync(engine, other.Id, DownloadStatus.Completed);
        await DownloadTestBed.WaitUntilAsync(() => engine.Find(movie.Id)!.StatusMessage == "Category movies is paused by the scheduler");
        Assert.Equal(DownloadStatus.Queued, engine.Find(movie.Id)!.Status);
        Assert.Equal(["movies"], _bed.CreateEngine().PausedCategories);

        await engine.SetCategoryPausedAsync("movies", paused: false);
        await DownloadTestBed.WaitForAsync(engine, movie.Id, DownloadStatus.Completed);
    }

    // ---------------------------------------------------------------- post-processing switches

    [Fact]
    public async Task Temporary_folder_name_while_post_processing()
    {
        Set(new DownloadSettingsChange { TemporaryFolderName = true });
        var engine = await _bed.StartAsync();
        var release = Simple("Temp.Name.2024-GRP");

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);

        var job = Assert.Single(_bed.PostProcessor.Jobs);
        Assert.Equal("_UNPACK_Temp.Name.2024-GRP", Path.GetFileName(job.TargetFolder));
        Assert.Equal(Path.Combine(_bed.Settings.CompleteFolder, "movies", "Temp.Name.2024-GRP"), done.OutputPath);
        Assert.True(File.Exists(Path.Combine(done.OutputPath!, "data.mkv")));
        Assert.False(Directory.Exists(job.TargetFolder));
    }

    [Fact]
    public async Task Only_verified_jobs_unless_switched_off_and_never_while_par2_volumes_are_held_back()
    {
        var engine = await _bed.StartAsync();
        var plain = await engine.AddAsync(DownloadTestBed.Request(Simple("Strict.2024-GRP")));
        await DownloadTestBed.WaitForAsync(engine, plain.Id, DownloadStatus.Completed);

        Set(new DownloadSettingsChange { OnlyVerified = false });
        var lenient = await engine.AddAsync(DownloadTestBed.Request(Simple("Lenient.2024-GRP")));
        await DownloadTestBed.WaitForAsync(engine, lenient.Id, DownloadStatus.Completed);
        var held = await engine.AddAsync(DownloadTestBed.Request(_bed.Release("Held.2024-GRP", ("m.par2", 300, 4000), ("m.mkv", 8000, 4000), ("m.vol00+01.par2", 4000, 4000))));
        await DownloadTestBed.WaitForAsync(engine, held.Id, DownloadStatus.Completed);

        var jobs = _bed.PostProcessor.Jobs.ToList();
        Assert.Equal([false, true, false], jobs.Select(j => j.ContinueWhenUnverified));
    }

    [Fact]
    public async Task Warnings_of_post_processing_show_on_the_completed_download()
    {
        _bed.PostProcessor.Handler = (job, _, _) => Task.FromResult(FakePostProcessor.CopyAll(job) with { Warning = "Unwanted files: x.exe." });
        var engine = await _bed.StartAsync();
        var added = await engine.AddAsync(DownloadTestBed.Request(Simple("Warned.2024-GRP")));

        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        Assert.Equal("Unwanted files: x.exe.", done.StatusMessage);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task Post_processing_workers_run_side_by_side(int workers, int expectedAtOnce)
    {
        Set(new DownloadSettingsChange { PostProcessWorkers = workers });
        var running = 0;
        var most = 0;
        _bed.PostProcessor.Handler = async (job, _, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await Task.Delay(400, ct);
            Interlocked.Decrement(ref running);
            return FakePostProcessor.CopyAll(job);
        };
        var engine = await _bed.StartAsync();

        var a = await engine.AddAsync(DownloadTestBed.Request(Simple("Pp.One.2024-GRP")));
        var b = await engine.AddAsync(DownloadTestBed.Request(Simple("Pp.Two.2024-GRP")));
        await DownloadTestBed.WaitForAsync(engine, a.Id, DownloadStatus.Completed);
        await DownloadTestBed.WaitForAsync(engine, b.Id, DownloadStatus.Completed);

        Assert.Equal(expectedAtOnce, Volatile.Read(ref most));
        Assert.Equal(2, _bed.PostProcessor.Jobs.Count);
    }

    // ---------------------------------------------------------------- history

    [Fact]
    public async Task History_cleanup_by_number_and_age_keeps_failed_ones_when_asked()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        _bed.Options.Time = clock;
        var engine = await _bed.StartAsync();
        var ids = new List<int>();
        foreach (var title in new[] { "H1.2024-GRP", "H2.2024-GRP", "H3.2024-GRP" })
        {
            var added = await engine.AddAsync(DownloadTestBed.Request(Simple(title)));
            await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
            ids.Add(added.Id);
        }

        Set(new DownloadSettingsChange { UnwantedAction = "fail", UnwantedExtensions = ".exe" });
        var failed = await engine.AddAsync(DownloadTestBed.Request(_bed.Release("Bad.2024-GRP", ("x.exe", 100, 4000))));
        Assert.Equal(DownloadStatus.Failed, failed.Status);
        await engine.CleanupHistoryAsync();
        Assert.Equal(4, engine.History(1, 10).Total); // nothing set: keep all

        Set(new DownloadSettingsChange { HistoryKeepItems = 2 });
        Assert.Equal(1, await engine.CleanupHistoryAsync());
        Assert.Equal(["Bad.2024-GRP", "H3.2024-GRP", "H2.2024-GRP"], engine.History(1, 10).Items.Select(i => i.Title));

        Set(new DownloadSettingsChange { HistoryKeepItems = 0, HistoryKeepDays = 3 });
        clock.Advance(TimeSpan.FromDays(4));
        Assert.Equal(2, await engine.CleanupHistoryAsync());
        Assert.Equal(["Bad.2024-GRP"], engine.History(1, 10).Items.Select(i => i.Title));

        Set(new DownloadSettingsChange { HistoryKeepFailed = false });
        Assert.Equal(1, await engine.CleanupHistoryAsync());
        Assert.Equal(0, engine.History(1, 10).Total);

        var task = new DownloadHistoryCleanupTask(_bed.Settings);
        Assert.Equal(TimeSpan.FromHours(1), task.Interval);
        Set(new DownloadSettingsChange { HistoryKeepDays = 0 });
        Assert.Null(task.Interval);
    }

    // ---------------------------------------------------------------- helpers

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>RAR 4 signature, main header, and a file header with the "password" flag.</summary>
    internal static byte[] EncryptedRar4() =>
    [
        0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00,
        0x00, 0x00, 0x73, 0x00, 0x00, 0x0D, 0x00, 0, 0, 0, 0, 0, 0,
        0x00, 0x00, 0x74, 0x04, 0x80, 0x20, 0x00, 0x10, 0x00, 0x00, 0x00,
    ];

    /// <summary>Replaces the start of a file of the release (its first article) with the given bytes.</summary>
    private void PutAtStart(TestRelease release, string name, byte[] head)
    {
        var file = release[name];
        head.CopyTo(file.Content, 0);
        var segment = Math.Min(4000, file.Content.Length);
        _bed.Articles.Articles[file.MessageIds[0]] = TestReleases.EncodeYenc(
            file.Content[..segment], name, 1, file.MessageIds.Count, file.Content.Length, 1);
    }

    public void Dispose() => _bed.Dispose();
}
