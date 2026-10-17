// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;
using StreamZH.Usenet.Nzb;

namespace StreamZH.Tests.Library.Downloads;

public sealed class DownloadEngineTests : IDisposable
{
    private readonly DownloadTestBed _bed = new();

    private TestRelease Movie(string title = "Dune.2021.German.DL.1080p.BluRay.x264-GRP") => _bed.Release(
        title,
        ("movie.par2", 900, 4000),
        ("movie.mkv", 26_000, 4000),
        ("movie.nfo", 300, 4000),
        ("movie.vol00+01.par2", 6000, 4000),
        ("movie.vol01+02.par2", 12000, 4000));

    [Fact]
    public async Task Downloads_all_files_with_correct_bytes_and_skips_par2_recovery()
    {
        _bed.Articles.MaxConnections = 1;
        var release = Movie();
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        Assert.Equal(DownloadStatus.Queued, added.Status);
        Assert.Equal("Alpha", added.Indexer);
        Assert.Equal(Path.Combine(_bed.Settings.NzbQueueFolder, $"{added.Id}.nzb"), _bed.Repository.Find(added.Id)!.NzbPath);

        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);

        var target = Path.Combine(_bed.Settings.CompleteFolder, "movies", release.Title);
        Assert.Equal(target, done.OutputPath);
        Assert.Equal(release["movie.mkv"].Content, await File.ReadAllBytesAsync(Path.Combine(target, "movie.mkv")));
        Assert.Equal(release["movie.nfo"].Content, await File.ReadAllBytesAsync(Path.Combine(target, "movie.nfo")));
        Assert.Equal(1, done.Progress);
        Assert.Equal(0, done.MissingArticles);
        Assert.NotNull(done.Completed);

        // The small par2 index comes first, recovery volumes are never asked for when nothing is missing.
        Assert.Equal(release["movie.par2"].MessageIds[0], _bed.Articles.Fetched.First());
        Assert.DoesNotContain(_bed.Articles.Fetched, id => id.Contains(".vol", StringComparison.Ordinal));
        Assert.All(release["movie.mkv"].MessageIds, id => Assert.Equal(1, _bed.Articles.Count(id)));

        var job = Assert.Single(_bed.PostProcessor.Jobs);
        Assert.Equal(Path.Combine(_bed.Settings.IncompleteFolder, release.Title), job.WorkFolder);
        Assert.False(Directory.Exists(job.WorkFolder));
        Assert.False(File.Exists(Path.Combine(_bed.Settings.NzbQueueFolder, $"{added.Id}.nzb")));

        Assert.Empty(engine.Active());
        var (history, total) = engine.History(1, 10);
        Assert.Equal(1, total);
        Assert.Equal(DownloadStatus.Completed, history[0].Status);

        await DownloadTestBed.WaitUntilAsync(() => _bed.History.Page(1, 10).Total == 1);
        var entry = Assert.Single(_bed.History.Page(1, 10).Records);
        Assert.Equal(HistoryEventType.DownloadCompleted, entry.EventType);
        Assert.Equal(release.Title, entry.SourceTitle);
        Assert.Equal(added.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.Data["downloadId"]);
        Assert.Contains(added.Id, _bed.Changed);

        var files = engine.Files(added.Id)!;
        Assert.Equal(5, files.Count);
        Assert.Equal(NzbFileKind.Par2Recovery, files[3].Kind);
        Assert.False(files[3].Wanted);
    }

    [Fact]
    public async Task Resumes_after_a_restart_without_fetching_finished_segments_again()
    {
        _bed.Articles.MaxConnections = 1;
        var release = _bed.Release("Restart.Test.2024.1080p.WEB-GRP", ("data.mkv", 40_000, 4000));
        var ids = release["data.mkv"].MessageIds;
        _bed.Articles.Block = id => id == ids[5];

        var first = await _bed.StartAsync();
        var added = await first.AddAsync(DownloadTestBed.Request(release));
        await DownloadTestBed.WaitUntilAsync(() => _bed.Articles.Fetched.Contains(ids[5]));
        await first.StopAsync(CancellationToken.None);

        var saved = _bed.Repository.Find(added.Id)!;
        Assert.Equal(DownloadStatus.Downloading, saved.Status);
        Assert.Equal(5, Enumerable.Range(0, 10).Count(i => (_bed.Repository.Files(added.Id)[0].Done[i / 8] & (1 << (i % 8))) != 0));

        _bed.Articles.Release();
        var second = _bed.CreateEngine();
        Assert.Equal(DownloadStatus.Queued, second.Find(added.Id)!.Status); // was downloading: back in the queue
        await second.StartAsync(CancellationToken.None);
        var done = await DownloadTestBed.WaitForAsync(second, added.Id, DownloadStatus.Completed);

        Assert.All(ids.Take(5), id => Assert.Equal(1, _bed.Articles.Count(id)));
        Assert.Equal(2, _bed.Articles.Count(ids[5])); // cancelled by the shutdown, fetched again
        Assert.Equal(release["data.mkv"].Content, await File.ReadAllBytesAsync(Path.Combine(done.OutputPath!, "data.mkv")));
    }

    [Fact]
    public async Task Pause_and_resume_a_download_and_the_whole_queue()
    {
        _bed.Articles.MaxConnections = 1;
        _bed.Articles.Delay = TimeSpan.FromMilliseconds(30);
        var release = _bed.Release("Pause.Test.2024.1080p.WEB-GRP", ("data.mkv", 80_000, 4000));
        var engine = await _bed.StartAsync();
        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Downloading);

        Assert.Equal(DownloadActionResult.Ok, await engine.PauseAsync(added.Id));
        Assert.Equal(DownloadStatus.Paused, engine.Find(added.Id)!.Status);
        await Task.Delay(100);
        var fetched = _bed.Articles.Fetched.Count;
        await Task.Delay(300);
        Assert.Equal(fetched, _bed.Articles.Fetched.Count);
        Assert.Equal(DownloadStatus.Paused, _bed.Repository.Find(added.Id)!.Status);

        Assert.Equal(DownloadActionResult.Ok, await engine.ResumeAsync(added.Id));
        await DownloadTestBed.WaitUntilAsync(() => _bed.Articles.Fetched.Count > fetched);

        await engine.PauseAllAsync();
        Assert.True(engine.Status().Paused);
        Assert.Equal("true", _bed.Config.Get(ConfigKeys.DownloadsPaused));
        await Task.Delay(150);
        fetched = _bed.Articles.Fetched.Count;
        await Task.Delay(300);
        Assert.Equal(fetched, _bed.Articles.Fetched.Count);

        await engine.ResumeAllAsync();
        Assert.False(engine.Status().Paused);
        _bed.Articles.Delay = TimeSpan.Zero;
        await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);

        Assert.Equal(DownloadActionResult.NotFound, await engine.PauseAsync(999));
        Assert.Equal(DownloadActionResult.Invalid, await engine.PauseAsync(added.Id));
    }

    [Fact]
    public async Task Delete_with_files_removes_the_incomplete_folder_and_the_nzb()
    {
        _bed.Articles.MaxConnections = 2;
        var release = _bed.Release("Delete.Test.2024.1080p.WEB-GRP", ("data.mkv", 40_000, 4000));
        var ids = release["data.mkv"].MessageIds;
        _bed.Articles.Block = id => id == ids[4];
        var engine = await _bed.StartAsync();
        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        var work = Path.Combine(_bed.Settings.IncompleteFolder, release.Title);
        await DownloadTestBed.WaitUntilAsync(() => File.Exists(Path.Combine(work, "data.mkv")) && _bed.Articles.Fetched.Contains(ids[4]));

        Assert.Equal(DownloadActionResult.Ok, await engine.DeleteAsync(added.Id, deleteFiles: true));

        Assert.False(Directory.Exists(work));
        Assert.False(File.Exists(Path.Combine(_bed.Settings.NzbQueueFolder, $"{added.Id}.nzb")));
        Assert.Null(engine.Find(added.Id));
        Assert.Null(_bed.Repository.Find(added.Id));
        Assert.Empty(engine.Active());
        Assert.Equal(DownloadActionResult.NotFound, await engine.DeleteAsync(added.Id, deleteFiles: true));
    }

    [Fact]
    public async Task Speed_limit_is_roughly_respected()
    {
        _bed.Settings.Update(null, null, null, null, 100);
        _bed.Articles.MaxConnections = 4;
        var release = _bed.Release("Speed.Test.2024.1080p.WEB-GRP", ("data.mkv", 300 * 1024, 10 * 1024));
        var engine = await _bed.StartAsync();

        var watch = Stopwatch.StartNew();
        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        watch.Stop();

        // 300 KB at 100 KB/s with a 50 KB burst: about 2.5 s; without the limit it takes a few milliseconds.
        Assert.InRange(watch.Elapsed.TotalSeconds, 2.0, 15);
        Assert.Equal(100, engine.Status().SpeedLimitKBps);
    }

    [Fact]
    public async Task Missing_articles_are_counted_and_repaired_with_just_enough_par2_volumes()
    {
        _bed.Articles.MaxConnections = 2;
        var release = Movie();
        _bed.Articles.Missing[release["movie.mkv"].MessageIds[2]] = 0;
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);

        Assert.Equal(1, done.MissingArticles);

        // About 4 KB missing: the smaller volume (6 KB) covers it, the bigger one stays on the server.
        Assert.All(release["movie.vol00+01.par2"].MessageIds, id => Assert.Equal(1, _bed.Articles.Count(id)));
        Assert.All(release["movie.vol01+02.par2"].MessageIds, id => Assert.Equal(0, _bed.Articles.Count(id)));

        // The hole is still there in the written file: zeros where the missing article belongs, full size.
        var mkv = await File.ReadAllBytesAsync(Path.Combine(done.OutputPath!, "movie.mkv"));
        Assert.Equal(26_000, mkv.Length);
        Assert.All(mkv.AsSpan(8000, 4000).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Fails_early_when_more_is_missing_than_can_be_repaired()
    {
        _bed.Articles.MaxConnections = 1;
        var release = _bed.Release("Broken.Release.2024.1080p.WEB-GRP", ("data.mkv", 40_000, 4000), ("data.nfo", 200, 4000));
        var ids = release["data.mkv"].MessageIds;
        _bed.Articles.Missing[ids[0]] = 0;
        _bed.Articles.Missing[ids[1]] = 0;
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        var failed = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Failed);

        Assert.Contains("articles are missing", failed.Error, StringComparison.Ordinal);
        Assert.Contains("no par2 files", failed.Error, StringComparison.Ordinal);
        Assert.Equal(2, failed.MissingArticles);
        Assert.True(_bed.Articles.Fetched.Count < 11, "the download should stop before fetching everything");
        Assert.Empty(_bed.PostProcessor.Jobs);
        Assert.Empty(engine.Active());

        await DownloadTestBed.WaitUntilAsync(() => _bed.History.Page(1, 10).Total == 1);
        var entry = Assert.Single(_bed.History.Page(1, 10).Records);
        Assert.Equal(HistoryEventType.DownloadFailed, entry.EventType);
        Assert.Equal(failed.Error, entry.Data["error"]);

        // Retry asks for the missing articles again; this time the server has them.
        _bed.Articles.Missing.Clear();
        Assert.Equal(DownloadActionResult.Ok, await engine.RetryAsync(added.Id));
        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        Assert.Equal(0, done.MissingArticles);
        Assert.Null(done.Error);
        Assert.Equal(release["data.mkv"].Content, await File.ReadAllBytesAsync(Path.Combine(done.OutputPath!, "data.mkv")));
        Assert.Equal(DownloadActionResult.Invalid, await engine.RetryAsync(added.Id));
    }

    [Fact]
    public async Task Server_trouble_waits_and_retries_instead_of_failing()
    {
        _bed.Articles.MaxConnections = 1;
        _bed.Articles.ServerFailures = 3;
        var release = _bed.Release("Server.Down.2024.1080p.WEB-GRP", ("data.mkv", 8000, 4000));
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        await DownloadTestBed.WaitUntilAsync(() => engine.Status().StatusMessage?.Contains("Cannot reach", StringComparison.Ordinal) == true);
        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);

        Assert.Equal(0, done.MissingArticles);
        Assert.Null(engine.Status().StatusMessage);
    }

    [Fact]
    public async Task Without_abort_a_hopeless_download_is_fetched_completely_and_post_processing_decides()
    {
        _bed.Articles.MaxConnections = 1;
        _bed.Settings.Update(null, null, null, null, null, abortHopeless: false);
        var release = _bed.Release("Broken.Release.2024.1080p.WEB-GRP", ("data.mkv", 40_000, 4000), ("data.nfo", 200, 4000));
        var ids = release["data.mkv"].MessageIds;
        _bed.Articles.Missing[ids[0]] = 0;
        _bed.Articles.Missing[ids[1]] = 0;
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);

        Assert.Equal(2, done.MissingArticles);
        Assert.Equal(11, _bed.Articles.Fetched.Count);
        Assert.Single(_bed.PostProcessor.Jobs);
    }

    [Fact]
    public async Task Keep_files_leaves_the_download_folder_in_place()
    {
        _bed.Settings.Update(null, null, null, null, null, keepFiles: true);
        var release = _bed.Release("Keep.Files.2024.1080p.WEB-GRP", ("data.mkv", 8000, 4000), ("data.par2", 500, 4000));
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);

        var job = Assert.Single(_bed.PostProcessor.Jobs);
        Assert.True(job.KeepFiles);
        Assert.True(File.Exists(Path.Combine(job.WorkFolder, "data.par2")));
        Assert.True(File.Exists(Path.Combine(done.OutputPath!, "data.mkv")));
        Assert.False(File.Exists(Path.Combine(_bed.Settings.NzbQueueFolder, $"{added.Id}.nzb")));
    }

    [Fact]
    public async Task The_free_space_setting_moves_the_pause_threshold()
    {
        _bed.FreeSpace.Free = 500L * 1024 * 1024;
        _bed.Settings.Update(null, null, null, null, null, minFreeSpaceGb: 0.25);
        var release = _bed.Release("Small.Disk.2024.1080p.WEB-GRP", ("data.mkv", 8000, 4000));
        var engine = await _bed.StartAsync();

        var added = await engine.AddAsync(DownloadTestBed.Request(release));
        await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        Assert.False(engine.Status().Paused);

        _bed.FreeSpace.Free = 3L * 1024 * 1024 * 1024;
        _bed.Settings.Update(null, null, null, null, null, minFreeSpaceGb: 5);
        await DownloadTestBed.WaitUntilAsync(() => engine.Status().Paused);
        Assert.Contains("at least 5 GB", engine.Status().StatusMessage, StringComparison.Ordinal);

        _bed.Settings.Update(null, null, null, null, null, minFreeSpaceGb: 0);
        await DownloadTestBed.WaitUntilAsync(() => !engine.Status().Paused);
    }

    [Fact]
    public void Expert_settings_default_to_todays_behaviour_and_are_validated()
    {
        var settings = _bed.Settings;
        Assert.Equal((1d, 1024L * 1024 * 1024, false, true, 3), (settings.MinFreeSpaceGb, settings.MinFreeSpaceBytes, settings.KeepFiles, settings.AbortHopeless, settings.ArticleTries));

        var error = Assert.Throws<ValidationFailedException>(() => settings.Update(null, null, null, null, null, minFreeSpaceGb: double.NaN, articleTries: 0));
        Assert.Equal(["articleTries", "minFreeSpaceGb"], error.Errors.Keys.Order());

        settings.Update(null, null, null, null, null, 0.5, true, false, 7);
        var reloaded = new DownloadSettings(_bed.Config, _bed.Paths);
        Assert.Equal((0.5, true, false, 7), (reloaded.MinFreeSpaceGb, reloaded.KeepFiles, reloaded.AbortHopeless, reloaded.ArticleTries));

        // Unchanged fields stay as they are.
        settings.Update(null, null, null, null, 100);
        Assert.Equal((0.5, true, false, 7), (settings.MinFreeSpaceGb, settings.KeepFiles, settings.AbortHopeless, settings.ArticleTries));
    }

    [Fact]
    public async Task Low_disk_space_pauses_the_queue_until_space_is_free_again()
    {
        _bed.FreeSpace.Free = 500L * 1024 * 1024;
        var release = _bed.Release("Disk.Full.2024.1080p.WEB-GRP", ("data.mkv", 8000, 4000));
        var engine = await _bed.StartAsync();
        var added = await engine.AddAsync(DownloadTestBed.Request(release));

        await DownloadTestBed.WaitUntilAsync(() => engine.Status().Paused);
        var status = engine.Status();
        Assert.Contains("500 MB free", status.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(500L * 1024 * 1024, status.FreeSpaceBytes);
        await Task.Delay(200);
        Assert.Empty(_bed.Articles.Fetched);
        Assert.Contains(0, _bed.Changed);

        _bed.FreeSpace.Free = 50L * 1024 * 1024 * 1024;
        await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        Assert.False(engine.Status().Paused);
    }

    [Fact]
    public async Task Post_processing_states_are_shown_and_lead_to_completed()
    {
        var verify = new SemaphoreSlim(0);
        var proceed = new SemaphoreSlim(0);
        _bed.PostProcessor.Handler = async (job, progress, ct) =>
        {
            progress.Report(new PostProcessProgress(PostProcessStage.Verifying, 0.5, null));
            verify.Release();
            await proceed.WaitAsync(ct);
            progress.Report(new PostProcessProgress(PostProcessStage.Repairing, 0.25, "Repairing 3 blocks"));
            verify.Release();
            await proceed.WaitAsync(ct);
            progress.Report(new PostProcessProgress(PostProcessStage.Extracting, 0.75, "Extracting part 1 of 2"));
            verify.Release();
            await proceed.WaitAsync(ct);
            var result = FakePostProcessor.CopyAll(job);
            return result with { Repaired = true };
        };

        var release = _bed.Release("PostProcess.Test.2024.1080p.WEB-GRP", ("data.mkv", 8000, 4000));
        var engine = await _bed.StartAsync();
        var added = await engine.AddAsync(DownloadTestBed.Request(release, DownloadCategory.Series));

        await verify.WaitAsync(TimeSpan.FromSeconds(20));
        var item = engine.Find(added.Id)!;
        Assert.Equal((DownloadStatus.Verifying, 0.5, "Verifying"), (item.Status, item.StepProgress, item.StatusMessage));
        proceed.Release();

        await verify.WaitAsync(TimeSpan.FromSeconds(20));
        item = engine.Find(added.Id)!;
        Assert.Equal((DownloadStatus.Repairing, 0.25, "Repairing 3 blocks"), (item.Status, item.StepProgress, item.StatusMessage));
        proceed.Release();

        await verify.WaitAsync(TimeSpan.FromSeconds(20));
        item = engine.Find(added.Id)!;
        Assert.Equal((DownloadStatus.Extracting, 0.75, "Extracting part 1 of 2"), (item.Status, item.StepProgress, item.StatusMessage));
        await DownloadTestBed.WaitUntilAsync(() => _bed.Progress.Any(p => p.Items.Any(i => i.Status == DownloadStatus.Extracting)));
        proceed.Release();

        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);
        Assert.Equal(Path.Combine(_bed.Settings.CompleteFolder, "series", release.Title), done.OutputPath);
        Assert.Equal("Repaired with par2", done.StatusMessage);
        Assert.Null(done.StepProgress);
    }

    [Fact]
    public async Task Failed_post_processing_fails_the_download_with_its_error()
    {
        _bed.PostProcessor.Handler = (job, _, _) =>
            Task.FromResult(new PostProcessResult(false, "Wrong password for the archive.", job.TargetFolder, [], false));
        var release = _bed.Release("Locked.2024.1080p.WEB-GRP", ("data.rar", 8000, 4000));
        var engine = await _bed.StartAsync();
        var added = await engine.AddAsync(DownloadTestBed.Request(release) with { Password = "secret" });

        var failed = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Failed);

        Assert.Equal("Wrong password for the archive.", failed.Error);
        Assert.Equal("secret", _bed.PostProcessor.Jobs.Single().Password);
        Assert.True(Directory.Exists(Path.Combine(_bed.Settings.IncompleteFolder, release.Title)), "failed downloads keep their files");
        Assert.True(File.Exists(Path.Combine(_bed.Settings.NzbQueueFolder, $"{added.Id}.nzb")), "failed downloads keep the NZB for a retry");
    }

    [Fact]
    public async Task Failed_repair_fetches_the_held_back_recovery_volumes_and_tries_again()
    {
        var runs = 0;
        _bed.PostProcessor.Handler = (job, _, _) => Task.FromResult(Interlocked.Increment(ref runs) == 1
            ? new PostProcessResult(false, "Not enough recovery blocks to repair: 3 needed, 1 available.", job.TargetFolder, [], false)
            : FakePostProcessor.CopyAll(job) with { Repaired = true });
        var release = Movie("Needs.Repair.2024.1080p.WEB-GRP");
        var engine = await _bed.StartAsync();
        var added = await engine.AddAsync(DownloadTestBed.Request(release));

        var done = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Completed);

        Assert.Equal(2, runs);
        Assert.All(release.Files.Where(f => f.Name.Contains(".vol", StringComparison.Ordinal)).SelectMany(f => f.MessageIds),
            id => Assert.Equal(1, _bed.Articles.Count(id)));
        Assert.True(engine.Files(added.Id)!.All(f => f.Wanted));
        Assert.Equal(done.TotalBytes, done.DownloadedBytes);
    }

    [Fact]
    public async Task Other_post_processing_errors_fail_without_fetching_recovery_volumes()
    {
        _bed.PostProcessor.Handler = (job, _, _) =>
            Task.FromResult(new PostProcessResult(false, "Archive is corrupt.", job.TargetFolder, [], false));
        var release = Movie("Corrupt.Archive.2024.1080p.WEB-GRP");
        var engine = await _bed.StartAsync();
        var added = await engine.AddAsync(DownloadTestBed.Request(release));

        var failed = await DownloadTestBed.WaitForAsync(engine, added.Id, DownloadStatus.Failed);

        Assert.Equal("Archive is corrupt.", failed.Error);
        Assert.Single(_bed.PostProcessor.Jobs);
        Assert.DoesNotContain(_bed.Articles.Fetched, id => id.Contains(".vol", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Higher_priority_and_position_decide_the_order()
    {
        _bed.Articles.MaxConnections = 1;
        var engine = await _bed.StartAsync();
        await engine.PauseAllAsync();

        var a = await engine.AddAsync(DownloadTestBed.Request(_bed.Release("A.2024.1080p.WEB-GRP", ("a.mkv", 8000, 4000))));
        var b = await engine.AddAsync(DownloadTestBed.Request(_bed.Release("B.2024.1080p.WEB-GRP", ("b.mkv", 8000, 4000))));
        var c = await engine.AddAsync(DownloadTestBed.Request(_bed.Release("C.2024.1080p.WEB-GRP", ("c.mkv", 8000, 4000)), priority: 1));
        Assert.Equal([c.Id, a.Id, b.Id], engine.Active().Select(i => i.Id));

        var moved = await engine.UpdateAsync(b.Id, null, DownloadCategory.Other, 0);
        Assert.Equal(DownloadCategory.Other, moved!.Category);
        Assert.Equal([c.Id, b.Id, a.Id], engine.Active().Select(i => i.Id));
        Assert.Null(await engine.UpdateAsync(999, 1, null, null));

        var completed = new List<int>();
        _bed.Events.Subscribe<DownloadChanged>((e, _) =>
        {
            lock (completed)
            {
                if (engine.Find(e.DownloadId)?.Status == DownloadStatus.Completed && !completed.Contains(e.DownloadId))
                {
                    completed.Add(e.DownloadId);
                }
            }

            return ValueTask.CompletedTask;
        });

        await engine.ResumeAllAsync();
        await DownloadTestBed.WaitForAsync(engine, a.Id, DownloadStatus.Completed);
        await DownloadTestBed.WaitForAsync(engine, b.Id, DownloadStatus.Completed);
        await DownloadTestBed.WaitForAsync(engine, c.Id, DownloadStatus.Completed);

        await DownloadTestBed.WaitUntilAsync(() => completed.Count == 3);
        Assert.StartsWith("C.2024", _bed.Articles.Fetched.First(), StringComparison.Ordinal);
        Assert.Equal([c.Id, b.Id, a.Id], completed);
        Assert.Equal(Path.Combine(_bed.Settings.CompleteFolder, "other", "B.2024.1080p.WEB-GRP"), engine.Find(b.Id)!.OutputPath);
    }

    [Fact]
    public async Task Invalid_nzb_files_are_rejected_with_a_readable_error()
    {
        var engine = _bed.CreateEngine();

        var empty = await Assert.ThrowsAsync<LibraryException>(() => engine.AddAsync(new DownloadRequest([], "x", DownloadCategory.Other, MediaLink.None)));
        Assert.Equal("The NZB file is empty.", empty.Message);

        var html = await Assert.ThrowsAsync<LibraryException>(() => engine.AddAsync(new DownloadRequest(
            "<html><body>Error</body></html>"u8.ToArray(), "x", DownloadCategory.Other, MediaLink.None)));
        Assert.StartsWith("This is not a valid NZB file", html.Message, StringComparison.Ordinal);

        var noSegments = await Assert.ThrowsAsync<LibraryException>(() => engine.AddAsync(new DownloadRequest(
            """<nzb xmlns="http://www.newzbin.com/DTD/2003/nzb"><file subject="x"/></nzb>"""u8.ToArray(), "x", DownloadCategory.Other, MediaLink.None)));
        Assert.Contains("no files with segments", noSegments.Message, StringComparison.Ordinal);
        Assert.Empty(engine.Active());
    }

    [Fact]
    public async Task Same_titles_get_their_own_work_folders_and_links_are_kept()
    {
        var engine = _bed.CreateEngine();
        await engine.PauseAllAsync();
        var release = _bed.Release("Twice.2024.1080p.WEB-GRP", ("data.mkv", 8000, 4000));
        var link = new MediaLink(StreamZH.Core.Media.MediaType.Series, null, 7, [11, 12]);

        var first = await engine.AddAsync(DownloadTestBed.Request(release) with { Link = link });
        var second = await engine.AddAsync(DownloadTestBed.Request(release));

        Assert.Equal(Path.Combine(_bed.Settings.IncompleteFolder, "Twice.2024.1080p.WEB-GRP"), _bed.Repository.Find(first.Id)!.WorkFolder);
        Assert.Equal(Path.Combine(_bed.Settings.IncompleteFolder, "Twice.2024.1080p.WEB-GRP (2)"), _bed.Repository.Find(second.Id)!.WorkFolder);

        // A new engine (restart) reads the queue back with everything it needs.
        var restarted = _bed.CreateEngine();
        var item = restarted.Find(first.Id)!;
        Assert.Equal(7, item.Link.SeriesId);
        Assert.Equal([11, 12], item.Link.EpisodeIds);
        Assert.Equal(2, restarted.Active().Count);
        Assert.True(restarted.Status().Paused);
    }

    public void Dispose() => _bed.Dispose();
}
