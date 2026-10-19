// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Library;
using StreamZH.Library.Downloads;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Sabnzbd;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Migration;

public sealed class SabnzbdMigrationTests : IAsyncLifetime
{
    private const string Sab = MigrationSources.Sabnzbd;

    private readonly MigrationBed _bed = new();
    private readonly SabnzbdServersSection _servers = new();
    private readonly SabnzbdCategoriesSection _categories = new();
    private readonly SabnzbdDownloadsSection _downloads = new();

    private SabnzbdApiClient Client => _bed.Services.GetRequiredService<SabnzbdApiClient>();

    // ---- Connection ----

    [Fact]
    public async Task Probe_reads_the_version_and_checks_the_key()
    {
        var (connection, app) = await Client.ProbeAsync(" http://sab:8080/ ", StubSabnzbd.ApiKey, CancellationToken.None);

        Assert.Equal(new MigrationConnection("http://sab:8080", StubSabnzbd.ApiKey), connection);
        Assert.Equal(new MigrationAppInfo("SABnzbd", "4.3.3"), app);
        Assert.Equal(["version", "get_config"], _bed.Sabnzbd.Modes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wrong_key_is_an_api_key_error(bool forbidden)
    {
        _bed.Sabnzbd.ForbiddenOnWrongKey = forbidden;

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => Client.ProbeAsync("http://sab:8080", "wrong", CancellationToken.None));

        Assert.Equal(["apiKey"], error.Errors.Keys);
        Assert.Contains("rejected the API key", error.Errors["apiKey"][0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_url_base_is_found_with_or_without_sabnzbd_in_the_address()
    {
        _bed.Sabnzbd.Base = SabnzbdApiClient.UrlBase;

        var (found, _) = await Client.ProbeAsync("http://sab:8080", StubSabnzbd.ApiKey, CancellationToken.None);
        Assert.Equal("http://sab:8080/sabnzbd", found.Url);

        var (given, _) = await Client.ProbeAsync("http://sab:8080/sabnzbd/", StubSabnzbd.ApiKey, CancellationToken.None);
        Assert.Equal("http://sab:8080/sabnzbd", given.Url);

        _bed.Sabnzbd.Base = "/other";
        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => Client.ProbeAsync("http://sab:8080", StubSabnzbd.ApiKey, CancellationToken.None));
        Assert.Equal(["url"], error.Errors.Keys);
    }

    [Fact]
    public async Task Missing_url_and_key_are_field_errors()
    {
        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => Client.ProbeAsync("", null, CancellationToken.None));
        Assert.Equal(["apiKey", "url"], error.Errors.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("Config > General > Security", error.Errors["apiKey"][0], StringComparison.Ordinal);
    }

    // ---- Servers ----

    [Fact]
    public async Task Servers_preview_needs_passwords_and_halves_connections_while_side_by_side()
    {
        _bed.Hold.Set(true);

        var plan = await _servers.PreviewAsync(_bed.Context(Sab), CancellationToken.None);

        Assert.Equal(["servers:news.example.com", "servers:block.example.net", "servers:free.example.org"], plan.Items.Select(i => i.Key));
        Assert.All(plan.Items, i => Assert.Equal(MigrationAction.Create, i.Action));
        Assert.Equal([true, true, false], plan.Items.Select(i => i.NeedsSecret));
        Assert.Equal("Example News", plan.Items[0].Label);
        Assert.Contains("30 → 15 connections (half, while SABnzbd runs)", plan.Items[0].Detail, StringComparison.Ordinal);
        Assert.Contains("backup (priority 1)", plan.Items[1].Detail, StringComparison.Ordinal);
        Assert.Contains("switched off in SABnzbd", plan.Items[2].Detail, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings!, w => w.Contains("connection limit", StringComparison.Ordinal));

        var same = await _servers.PreviewAsync(
            _bed.Context(Sab, _bed.Request(Sab) with { Options = new Dictionary<string, string> { ["servers.connections"] = "same" } }), CancellationToken.None);
        Assert.Contains("30 connections", same.Items[0].Detail, StringComparison.Ordinal);
        Assert.Empty(_bed.Servers.All());
    }

    [Fact]
    public async Task Servers_without_a_password_are_added_switched_off_and_a_second_run_completes_them()
    {
        _bed.Hold.Set(true);
        var run = _bed.Run();
        await _servers.RunAsync(_bed.Context(Sab, _bed.Request(Sab) with { Exclude = ["servers:free.example.org"] }), run, CancellationToken.None);

        var report = run.Report();
        Assert.Equal((2, 0, 1), (report.Created, report.Updated, report.Skipped));
        Assert.Equal(2, report.Messages.Count(m => m.Level == MigrationMessageLevel.Warn));
        var main = Assert.Single(_bed.Servers.All(), s => s.Host == "news.example.com");
        Assert.Equal(("Example News", 563, true, "johndoe", "", 15, 0, false), (main.Name, main.Port, main.UseSsl, main.Username, main.Password, main.Connections, main.Priority, main.Enabled));
        Assert.Equal((4000, 60, CertificateCheck.Strict, 2, ServerFailureAction.Skip, "Main account"),
            (main.RetentionDays, main.TimeoutSeconds, main.CertificateCheck, main.ArticlesPerRequest, main.OnFailure, main.Notes));

        var block = Assert.Single(_bed.Servers.All(), s => s.Host == "block.example.net");
        Assert.Equal((443, 4, 1, CertificateCheck.Minimal, ServerFailureAction.IgnoreLonger, "2027-06-30", 500L * 1024 * 1024 * 1024),
            (block.Port, block.Connections, block.Priority, block.CertificateCheck, block.OnFailure, block.ExpiresOn, block.QuotaBytes!.Value));

        // Again with the passwords and all connections: the same servers are updated and switched on.
        var secrets = new Dictionary<string, string> { ["servers:news.example.com"] = "s3cret pass", ["servers:block.example.net"] = "block-pass" };
        var again = _bed.Run();
        await _servers.RunAsync(
            _bed.Context(Sab, _bed.Request(Sab) with { Secrets = secrets, Options = new Dictionary<string, string> { ["servers.connections"] = "same" } }), again, CancellationToken.None);

        Assert.Equal((1, 2), (again.Report().Created, again.Report().Updated));
        var all = _bed.Servers.All();
        Assert.Equal(3, all.Count);
        main = all.Single(s => s.Host == "news.example.com");
        Assert.Equal(("s3cret pass", 30, true), (main.Password, main.Connections, main.Enabled));
        Assert.True(all.Single(s => s.Host == "block.example.net").Enabled);

        // The open server has no login: it needs no password and stays as SABnzbd has it (off).
        var free = all.Single(s => s.Host == "free.example.org");
        Assert.Equal(("free.example.org", false, "", CertificateCheck.Off), (free.Name, free.Enabled, free.Password, free.CertificateCheck));

        // A third run without passwords keeps the stored ones.
        var plan = await _servers.PreviewAsync(_bed.Context(Sab), CancellationToken.None);
        Assert.All(plan.Items, i => Assert.Equal((MigrationAction.Update, false), (i.Action, i.NeedsSecret)));
        await _servers.RunAsync(_bed.Context(Sab), _bed.Run(), CancellationToken.None);
        Assert.Equal("s3cret pass", _bed.Servers.All().Single(s => s.Host == "news.example.com").Password);
        Assert.Equal(15, _bed.Servers.All().Single(s => s.Host == "news.example.com").Connections);
    }

    [Fact]
    public async Task A_stored_server_is_matched_by_host_port_and_user()
    {
        _bed.Servers.Create(UsenetServerServiceSchema() with { Name = "My provider", Host = "NEWS.example.com", Username = "johndoe", Password = "mine" });

        var plan = await _servers.PreviewAsync(_bed.Context(Sab), CancellationToken.None);
        var item = plan.Items[0];
        Assert.Equal((MigrationAction.Update, false), (item.Action, item.NeedsSecret));
        Assert.Contains("updates \"My provider\"", item.Detail, StringComparison.Ordinal);

        await _servers.RunAsync(_bed.Context(Sab, _bed.Request(Sab) with { Exclude = ["servers:block.example.net", "servers:free.example.org"] }), _bed.Run(), CancellationToken.None);
        var server = Assert.Single(_bed.Servers.All());
        Assert.Equal(("My provider", "mine", true), (server.Name, server.Password, server.Enabled));
    }

    // ---- Categories ----

    [Fact]
    public async Task Categories_map_to_movies_and_series_and_keep_folder_priority_and_post_processing()
    {
        var plan = await _categories.PreviewAsync(_bed.Context(Sab), CancellationToken.None);

        Assert.Equal(["categories:*", "categories:movies", "categories:tv", "categories:anime", "categories:audio"], plan.Items.Select(i => i.Key));
        Assert.Equal([MigrationAction.Skip, MigrationAction.Update, MigrationAction.Update, MigrationAction.Create, MigrationAction.Create], plan.Items.Select(i => i.Action));
        Assert.StartsWith("→ series", plan.Items[2].Detail, StringComparison.Ordinal);
        Assert.Contains("script notify.py not taken over", plan.Items[3].Detail, StringComparison.Ordinal);
        Assert.NotNull(plan.Items[4].Warning);

        var context = _bed.Context(Sab);
        var run = _bed.Run();
        await _categories.RunAsync(context, run, CancellationToken.None);
        Assert.Equal((2, 2), (run.Report().Created, run.Report().Updated));
        Assert.Contains(context.Paths.Checks(), c => c.Remote == "/data/anime" && c.Exists);

        var movies = _bed.Categories.Find("movies")!;
        Assert.Equal((string.Empty, (int?)0, PostProcessLevel.Full), (movies.Folder, movies.Priority, movies.PostProcessing));
        var series = _bed.Categories.Find("series")!;
        Assert.Equal(("tv", (int?)0, PostProcessLevel.Full), (series.Folder, series.Priority, series.PostProcessing));
        var anime = _bed.Categories.Find("anime")!;
        Assert.Equal((Path.Combine(_bed.Root, "anime"), (int?)1, PostProcessLevel.Unpack), (anime.Folder, anime.Priority, anime.PostProcessing));
        var audio = _bed.Categories.Find("audio")!;
        Assert.Equal((string.Empty, (int?)DownloadCategoryService.PausedPriority, PostProcessLevel.Repair), (audio.Folder, audio.Priority, audio.PostProcessing));

        // Idempotent: a second run updates.
        var again = _bed.Run();
        await _categories.RunAsync(_bed.Context(Sab), again, CancellationToken.None);
        Assert.Equal((0, 4), (again.Report().Created, again.Report().Updated));
        Assert.Equal(5, _bed.Categories.All().Count);
    }

    // ---- Download settings ----

    [Fact]
    public async Task Download_settings_preview_lists_what_is_taken_over_and_what_is_not()
    {
        var context = _bed.Context(Sab);
        var plan = await _downloads.PreviewAsync(context, CancellationToken.None);
        var items = plan.Items.ToDictionary(i => i.Key);

        Assert.Equal("line speed 50 MB/s, limit 80 % = 40 MB/s", items["downloads:speed"].Detail);
        Assert.Equal(MigrationAction.Update, items["downloads:complete_dir"].Action);
        Assert.Equal($"/data/downloads/complete → {Path.Combine(_bed.Root, "downloads", "complete")}", items["downloads:complete_dir"].Detail);
        Assert.Contains(context.Paths.Checks(), c => c.Remote == "/data/downloads/incomplete" && c.Exists && c.Mapped);

        Assert.Equal(MigrationAction.Link, items["downloads:fail_hopeless_jobs"].Action);
        Assert.Equal((MigrationAction.Update, "off"), (items["downloads:ignore_samples"].Action, items["downloads:ignore_samples"].Detail));
        Assert.Equal("fail: .exe, .com, .bat, .sh", items["downloads:unwanted_extensions"].Detail);
        Assert.Equal("pause", items["downloads:no_dupes"].Detail);
        Assert.Equal("5 GB", items["downloads:download_free"].Detail);

        // The scheduler: SABnzbd 1 = Monday … 7 = Sunday; percent speeds; servers and categories that come with this run.
        Assert.Equal(MigrationAction.Create, items["downloads:schedule:0"].Action);
        Assert.Equal("01:00 every day: pause", items["downloads:schedule:0"].Label);
        Assert.Equal("23:30 Sat, Sun: speedLimit 50%", items["downloads:schedule:2"].Detail);
        Assert.Equal("08:00 Mon, Tue, Wed, Thu, Fri: speedLimit 2048", items["downloads:schedule:3"].Detail);
        Assert.Contains("once the Usenet servers are taken over", items["downloads:schedule:4"].Detail, StringComparison.Ordinal);
        Assert.Equal(MigrationAction.Create, items["downloads:schedule:6"].Action);
        Assert.Contains("has no \"rss_scan\"", items["downloads:schedule:7"].Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationAction.Skip, items["downloads:schedule:8"].Action);

        // Not in StreamZH, or not while both run.
        foreach (var key in new[] { "downloads:direct_unpack", "downloads:dirscan_dir", "downloads:rss" })
        {
            Assert.Equal(MigrationAction.Skip, items[key].Action);
            Assert.NotNull(items[key].Reason);
        }

        Assert.Contains(plan.Warnings!, w => w.Contains("same download folders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Download_settings_run_and_rerun()
    {
        await _servers.RunAsync(_bed.Context(Sab), _bed.Run(), CancellationToken.None);
        await _categories.RunAsync(_bed.Context(Sab), _bed.Run(), CancellationToken.None);
        var run = _bed.Run();
        await _downloads.RunAsync(_bed.Context(Sab, _bed.Request(Sab) with { Exclude = ["downloads:schedule:1"] }), run, CancellationToken.None);

        var report = run.Report();
        Assert.Equal(0, report.Failed);
        var settings = _bed.Bed.Downloads;
        Assert.Equal((51200L, 40960L), (settings.MaxLineSpeedKBps, settings.SpeedLimitKBps));
        Assert.Equal(Path.Combine(_bed.Root, "downloads", "complete"), settings.CompleteFolder);
        Assert.Equal(Path.Combine(_bed.Root, "downloads", "incomplete"), settings.IncompleteFolder);
        Assert.False(settings.PostProcess.IgnoreSamples);
        Assert.Equal((UnwantedAction.Fail, ".exe, .com, .bat, .sh"), (settings.UnwantedAction, settings.UnwantedExtensionsText));
        Assert.Equal((DuplicateAction.Pause, EncryptedAction.Pause, 5d), (settings.DuplicateAction, settings.EncryptedAction, settings.MinFreeSpaceGb));
        Assert.Equal(".nfo, .sfv, .srr", settings.CleanupListText);
        Assert.Null(settings.WatchFolder);

        var block = _bed.Servers.All().Single(s => s.Host == "block.example.net");
        var rules = _bed.Schedules.All().Select(DownloadScheduleService.Describe).ToList();
        Assert.Equal(
            [
                "01:00 every day: pause",
                $"02:00 Mon, Tue, Wed, Thu, Fri: pauseServer {block.Id}",
                "04:15 every day: pauseCategory series",
                $"06:00 Mon, Tue, Wed, Thu, Fri: resumeServer {block.Id}",
                "08:00 Mon, Tue, Wed, Thu, Fri: speedLimit 2048",
                "23:30 Sat, Sun: speedLimit 50%",
            ],
            rules);

        // A second run changes nothing and adds no rule twice.
        var again = _bed.Run();
        await _downloads.RunAsync(_bed.Context(Sab), again, CancellationToken.None);
        Assert.Equal(7, _bed.Schedules.All().Count);
        Assert.Equal(0, again.Report().Failed);
        Assert.True(again.Report().Linked >= 6);
    }

    [Fact]
    public async Task Folders_StreamZH_cannot_see_are_left_alone()
    {
        var before = _bed.Bed.Downloads.CompleteFolder;
        var request = _bed.Request(Sab) with { PathMappings = [] };

        var plan = await _downloads.PreviewAsync(_bed.Context(Sab, request), CancellationToken.None);
        var complete = plan.Items.Single(i => i.Key == "downloads:complete_dir");
        Assert.Equal(MigrationAction.Skip, complete.Action);
        Assert.Contains("Add a path mapping for /data/downloads/complete", complete.Reason, StringComparison.Ordinal);

        await _downloads.RunAsync(_bed.Context(Sab, request), _bed.Run(), CancellationToken.None);
        Assert.Equal(before, _bed.Bed.Downloads.CompleteFolder);
    }

    [Theory]
    [InlineData("1 30 7 12345 pause", true, "07:30", "Monday,Tuesday,Wednesday,Thursday,Friday", "pause", "")]
    [InlineData("0 5 23 * resume", false, "23:05", "Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday", "resume", "")]
    [InlineData("1 0 6 7 pause_cat TV Shows", true, "06:00", "Sunday", "pause_cat", "TV Shows")]
    public void Scheduler_lines_are_read_like_sabnzbd(string line, bool enabled, string time, string days, string action, string argument)
    {
        var parsed = SabnzbdDownloadsSection.ParseSchedule(line)!.Value;
        Assert.Equal((enabled, time, days, action, argument),
            (parsed.Enabled, parsed.Time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), string.Join(",", parsed.Days), parsed.Action, parsed.Argument));
    }

    [Theory]
    [InlineData("50M", 50d * 1024 * 1024)]
    [InlineData("1.5G", 1.5 * 1024 * 1024 * 1024)]
    [InlineData("500K", 500d * 1024)]
    [InlineData("1000", 1000d)]
    [InlineData("", null)]
    [InlineData("lots", null)]
    public void Sabnzbd_units_are_1024_based(string text, double? bytes) => Assert.Equal(bytes, SabnzbdConfig.FromUnits(text));

    // ---- Whole run ----

    [Fact]
    public async Task A_whole_run_through_the_migration_service()
    {
        _bed.Hold.Set(true);
        var service = _bed.Service();
        var preview = await service.PreviewAsync(Sab, _bed.Request(Sab), CancellationToken.None);
        Assert.Equal(["servers", "categories", "downloads"], preview.Sections.Select(s => s.Key));
        Assert.Equal("SABnzbd", preview.App.AppName);
        Assert.Equal(["/nowhere/audio"], preview.Paths.Where(p => !p.Exists).Select(p => p.Remote));
        Assert.Single(preview.Warnings);

        var run = await service.StartAsync(Sab, _bed.Request(Sab) with { Secrets = new Dictionary<string, string> { ["servers:news.example.com"] = "pw" } }, "johndoe", CancellationToken.None);
        var done = await MigrationBed.WaitAsync(service, run.Id);

        Assert.Equal(MigrationRunStatus.Done, done.Status);
        Assert.Equal([3, 2, 0], done.Report.Sections.Select(s => s.Created).Take(2).Append(done.Report.Sections[2].Failed));
        Assert.True(_bed.Hold.IsOn, "The migration never switches the side-by-side mode off.");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _bed.DisposeAsync();

    private static UsenetServer UsenetServerServiceSchema() => StreamZH.Library.Usenet.UsenetServerService.Schema();
}
