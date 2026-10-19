// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Status;
using StreamZH.Library;
using StreamZH.Library.Indexers;
using StreamZH.Library.PostProcessing;
using StreamZH.Tests.Library;
using StreamZH.Tests.PostProcessing;

namespace StreamZH.Tests.Host;

public sealed class OngoingServiceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero));
    private readonly InProcessEventBus _events = new();
    private readonly FakeDownloadQueue _queue = new();
    private readonly FakeProbe _probe = new();
    private readonly Database _database;
    private readonly JobQueue _jobs;
    private readonly IndexerService _indexers;
    private readonly ConfigRepository _config;
    private readonly RootFolderRepository _rootFolders;
    private readonly List<OngoingChanged> _published = [];

    public OngoingServiceTests()
    {
        _database = new Database(_file);
        _database.Migrate();
        _jobs = new JobQueue(_events);
        _indexers = new IndexerService(new IndexerRepository(_database), new IndexerStatusRepository(_database), _time, NullLogger<IndexerService>.Instance);
        _config = new ConfigRepository(_database);
        _rootFolders = new RootFolderRepository(_database);
        AddUsenetServer("Default");
        _events.Subscribe<OngoingChanged>((e, _) =>
        {
            _published.Add(e);
            return ValueTask.CompletedTask;
        });
    }

    [Fact]
    public async Task Running_lists_post_processing_and_downloads_first_then_the_job_then_waiting_downloads()
    {
        _queue.Items.AddRange(
        [
            Item(1, DownloadStatus.Queued) with { TotalBytes = 4_509_715_660 },
            Item(2, DownloadStatus.Downloading) with
            {
                TotalBytes = 1000, DownloadedBytes = 345, SpeedBps = 29_779_558, Eta = TimeSpan.FromSeconds(170), MissingArticles = 3,
            },
            Item(3, DownloadStatus.Repairing) with { StepProgress = 0.42, StatusMessage = "Repairing 3 blocks" },
            Item(4, DownloadStatus.Paused) with { TotalBytes = 1000, DownloadedBytes = 500 },
        ]);
        var job = new Job("refresh-1", "Refreshing The Expanse", (_, _) => Task.CompletedTask);
        _jobs.Enqueue(job);
        _jobs.Enqueue(new Job("refresh-2", "Refreshing Andor", (_, _) => Task.CompletedTask));
        await _jobs.StartedAsync(job);

        var running = Service().Get().Running;

        Assert.Equal(["download-3", "download-2", "job", "download-1", "download-4"], running.Select(r => r.Key));

        var repair = running[0];
        Assert.Equal(OngoingKinds.PostProcess, repair.Kind);
        Assert.Equal("Repairing 3 blocks · 42 %", repair.Sub);
        Assert.Equal(0.42, repair.Progress);
        Assert.Null(repair.SpeedBps);

        var download = running[1];
        Assert.Equal(OngoingKinds.Download, download.Kind);
        Assert.Equal("34 % · 28.4 MB/s · 3 min left · 3 missing articles", download.Sub);
        Assert.Equal(0.345, download.Progress);
        Assert.Equal(29_779_558, download.SpeedBps);
        Assert.Equal(170, download.EtaSeconds);
        Assert.Equal("/activity", download.Link);

        var jobRow = running[2];
        Assert.Equal(OngoingKinds.Job, jobRow.Kind);
        Assert.Equal("Refreshing The Expanse", jobRow.Title);
        Assert.Equal("1 more queued", jobRow.Sub);
        Assert.Null(jobRow.Progress);

        Assert.Equal("Queued · 4.2 GB", running[3].Sub);
        Assert.Equal("Paused · 50 %", running[4].Sub);
    }

    [Fact]
    public void Without_the_download_engine_nothing_runs_and_no_server_notice_appears()
    {
        AddIndexer("A");

        var ongoing = Service(withQueue: false).Get();

        Assert.Empty(ongoing.Running);
        Assert.Empty(ongoing.Notices);
    }

    [Fact]
    public void Missing_usenet_server_is_reported_only_for_the_builtin_client()
    {
        AddIndexer("A");
        var source = new FakeArticleSource { MaxConnections = 0 };

        var notice = Assert.Single(Service(articleSource: source).Get().Notices);
        Assert.Equal("no-usenet-server", notice.Key);
        Assert.Equal(NoticeLevels.Warn, notice.Level);
        Assert.Equal("/settings/usenet", notice.Link);
        Assert.False(notice.Dismissible);

        _config.Set(OngoingService.DownloadClientKey, "builtin");
        Assert.Single(Service(articleSource: source).Get().Notices);

        source.MaxConnections = 30;
        Assert.Empty(Service(articleSource: source).Get().Notices);

        source.MaxConnections = 0;
        _config.Set(OngoingService.DownloadClientKey, "blackhole");
        Assert.Empty(Service(articleSource: source).Get().Notices);
    }

    [Fact]
    public void Usenet_server_table_is_used_when_there_is_no_article_source()
    {
        AddIndexer("A");
        using (var connection = _database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM UsenetServers";
            command.ExecuteNonQuery();
        }

        Assert.Equal(["no-usenet-server"], Service().Get().Notices.Select(n => n.Key));

        AddUsenetServer("Eweka");

        Assert.Empty(Service().Get().Notices);
    }

    [Fact]
    public void No_indexer_and_no_enabled_indexer()
    {
        var notice = Assert.Single(Service().Get().Notices);
        Assert.Equal("no-indexer", notice.Key);
        Assert.Equal("No indexer configured", notice.Title);
        Assert.Equal("/indexers", notice.Link);

        _indexers.Create(Valid("A") with { Enable = false });
        Assert.Equal("No indexer enabled", Assert.Single(Service().Get().Notices).Title);
    }

    [Fact]
    public void Indexer_in_back_off_shows_when_it_is_retried()
    {
        var indexer = AddIndexer("Treasure Maps");
        _indexers.RecordFailure(indexer.Id, new IndexerException("Request limit reached", 500, TimeSpan.FromMinutes(25)));

        var notice = Assert.Single(Service().Get().Notices);

        Assert.Equal($"indexer-paused-{indexer.Id}", notice.Key);
        Assert.StartsWith("Treasure Maps paused until ", notice.Title, StringComparison.Ordinal);
        Assert.Equal("Request limit reached · retry in 25 min", notice.Sub);
        Assert.False(notice.Dismissible);

        _time.Advance(TimeSpan.FromMinutes(26));
        Assert.Empty(Service().Get().Notices);
    }

    [Theory]
    [InlineData(3, "warn", "A: VIP expires in 3 days", true)]
    [InlineData(1, "warn", "A: VIP expires tomorrow", true)]
    [InlineData(0, "warn", "A: VIP expires today", true)]
    [InlineData(-2, "error", "A: VIP expired", false)]
    public void Vip_expiry_within_a_week(int days, string level, string title, bool dismissible)
    {
        var date = DateOnly.FromDateTime(_time.Now.UtcDateTime).AddDays(days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        _indexers.Create(Valid("A") with { VipExpiration = date });

        var notice = Assert.Single(Service().Get().Notices);

        Assert.Equal(level, notice.Level);
        Assert.Equal(title, notice.Title);
        Assert.Equal(dismissible, notice.Dismissible);
        Assert.Contains(date, notice.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void Vip_further_away_is_not_reported()
    {
        _indexers.Create(Valid("A") with { VipExpiration = "2026-10-25" });
        Assert.Empty(Service().Get().Notices);
    }

    [Fact]
    public void Root_folders_that_are_missing_or_read_only()
    {
        AddIndexer("A");
        var movies = _rootFolders.Add("/media/movies", MediaType.Movie);
        var series = _rootFolders.Add("/media/series", MediaType.Series);
        _rootFolders.Add("/media/fine", MediaType.Series);
        _probe.Folders["/media/movies"] = new FolderAccess(true, false, "Read-only file system");
        _probe.Folders["/media/series"] = new FolderAccess(false, false, null);

        var notices = Service().Get().Notices;

        Assert.Equal([$"root-readonly-{movies}", $"root-missing-{series}"], notices.Select(n => n.Key));
        Assert.Equal(NoticeLevels.Warn, notices[0].Level);
        Assert.Equal("Root folder is read-only", notices[0].Title);
        Assert.StartsWith("/media/movies", notices[0].Sub, StringComparison.Ordinal);
        Assert.Equal(NoticeLevels.Error, notices[1].Level);
    }

    [Fact]
    public void Low_disk_space_on_the_download_folders_once_per_drive()
    {
        AddIndexer("A");
        _config.Set(OngoingService.IncompleteFolderKey, "/downloads/incomplete");
        _config.Set(OngoingService.CompleteFolderKey, "/downloads/complete");
        _probe.Space["/downloads/incomplete"] = new DiskSpace("/", 6L * 1024 * 1024 * 1024);
        _probe.Space["/downloads/complete"] = new DiskSpace("/", 6L * 1024 * 1024 * 1024);

        var notice = Assert.Single(Service().Get().Notices);
        Assert.Equal("low-disk-incomplete", notice.Key);
        Assert.Equal("Low disk space: 6.0 GB free", notice.Title);
        Assert.Equal(NoticeLevels.Warn, notice.Level);

        _probe.Space["/downloads/complete"] = new DiskSpace("/mnt/data", 500L * 1024 * 1024);
        var notices = Service().Get().Notices;
        Assert.Equal(["low-disk-incomplete", "low-disk-complete"], notices.Select(n => n.Key));
        Assert.Equal(NoticeLevels.Error, notices[1].Level);

        _probe.Space["/downloads/incomplete"] = new DiskSpace("/", 60L * 1024 * 1024 * 1024);
        _probe.Space["/downloads/complete"] = new DiskSpace("/", 60L * 1024 * 1024 * 1024);
        Assert.Empty(Service().Get().Notices);
    }

    [Fact]
    public async Task Failed_downloads_are_dismissible_notices_for_a_day()
    {
        AddIndexer("A");
        var service = Service();
        await service.StartAsync(CancellationToken.None);

        _queue.Failed.Add(Item(7, DownloadStatus.Failed) with { Error = "Too many missing articles (12 %)", Completed = _time.Now });
        await _events.PublishAsync(new DownloadChanged(7));

        var notice = Assert.Single(service.Get().Notices);
        Assert.Equal("download-failed-7", notice.Key);
        Assert.Equal(NoticeLevels.Error, notice.Level);
        Assert.Equal("Download failed: Release 7", notice.Title);
        Assert.Equal("Too many missing articles (12 %)", notice.Sub);
        Assert.True(notice.Dismissible);
        Assert.Single(_published);

        _time.Advance(TimeSpan.FromHours(25));
        Assert.Empty(service.Get().Notices);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_retried_download_loses_its_failed_notice()
    {
        AddIndexer("A");
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        _queue.Failed.Add(Item(7, DownloadStatus.Failed));
        await _events.PublishAsync(new DownloadChanged(7));
        Assert.Single(service.Get().Notices);

        _queue.Failed.Clear();
        _queue.Items.Add(Item(7, DownloadStatus.Queued));
        await _events.PublishAsync(new DownloadChanged(7));

        Assert.Empty(service.Get().Notices);
        Assert.Equal(2, _published.Count);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ongoing_is_published_when_the_running_job_changes_not_for_every_enqueue()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        var first = new Job("a", "Job A", (_, _) => Task.CompletedTask);

        _jobs.Enqueue(first);
        Assert.Single(_published); // queue went from empty to busy
        _jobs.Enqueue(new Job("b", "Job B", (_, _) => Task.CompletedTask));
        _jobs.Enqueue(new Job("c", "Job C", (_, _) => Task.CompletedTask));
        Assert.Single(_published);

        await _jobs.StartedAsync(first);
        Assert.Equal(2, _published.Count);
        await _jobs.FinishedAsync(true);
        Assert.Equal(3, _published.Count);

        await _events.PublishAsync(new IndexersChanged());
        Assert.Equal(4, _published.Count);

        await service.StopAsync(CancellationToken.None);
        _jobs.Enqueue(new Job("d", "Job D", (_, _) => Task.CompletedTask));
        Assert.Equal(4, _published.Count);
    }

    [Fact]
    public async Task Tool_installation_is_a_running_row_and_a_failure_a_notice_until_the_tool_is_found()
    {
        AddIndexer("A");
        using var folder = new TempFolder();
        using var downloads = new FakeToolDownloads();
        var (package, _) = TestToolPackages.Par2();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        var environment = new ToolEnvironment(folder.Combine("app"), folder.Combine("bin"), OperatingSystem.IsWindows(), [])
        {
            InstalledDirectory = folder.Combine("tools"),
            FileExists = existing.Contains,
        };
        var settings = new FixedToolSettings();
        var runner = new ToolProcessRunner();
        var tools = new PostProcessTools(settings, runner, environment);
        using var installer = new ToolInstaller(
            new FakeHttpClientFactory(downloads), runner, environment, new ToolCatalog((tool, _) => tool == PostProcessTool.Par2 ? package : null),
            tools, settings, _events, TimeProvider.System, NullLogger<ToolInstaller>.Instance);

        downloads.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var install = installer.InstallAsync(PostProcessTool.Par2, CancellationToken.None);

        var running = Service(tools: tools, installer: installer).Get();
        var row = Assert.Single(running.Running);
        Assert.Equal("install-par2", row.Key);
        Assert.Equal(OngoingKinds.Install, row.Kind);
        Assert.Equal("Installing par2 1.5.0", row.Title);
        Assert.Equal("/settings/downloads#tools", row.Link);
        Assert.Empty(running.Notices);

        // The download is not there: 404.
        downloads.Gate.SetResult();
        var result = await install;
        Assert.False(result.Success);
        Assert.NotEmpty(_published);

        var failed = Service(tools: tools, installer: installer).Get();
        Assert.Empty(failed.Running);
        var notice = Assert.Single(failed.Notices);
        Assert.Equal("install-failed-par2", notice.Key);
        Assert.Equal(NoticeLevels.Warn, notice.Level);
        Assert.Equal("par2 could not be installed", notice.Title);
        Assert.Contains("HTTP 404", notice.Sub, StringComparison.Ordinal);
        Assert.EndsWith(". Settings → Tools", notice.Sub, StringComparison.Ordinal);
        Assert.Equal("/settings/downloads#tools", notice.Link);
        Assert.False(notice.Dismissible);

        // Installed by hand: the notice disappears.
        existing.Add(Path.Combine(folder.Combine("bin"), tools.ExecutableNames(PostProcessTool.Par2)[0]));
        Assert.Empty(Service(tools: tools, installer: installer).Get().Notices);
    }

    private OngoingService Service(bool withQueue = true, IArticleSource? articleSource = null, PostProcessTools? tools = null, ToolInstaller? installer = null)
    {
        var services = new ServiceCollection();
        if (withQueue)
        {
            services.AddSingleton<IDownloadQueue>(_queue);
        }

        if (tools is not null && installer is not null)
        {
            services.AddSingleton(tools);
            services.AddSingleton(installer);
        }

        if (articleSource is not null)
        {
            services.AddSingleton(articleSource);
        }

        return new OngoingService(_jobs, _indexers, _rootFolders, _config, _database, _probe, _events, _time, services.BuildServiceProvider());
    }

    private IndexerDefinition AddIndexer(string name) => _indexers.Create(Valid(name));

    private static IndexerDefinition Valid(string name) => new(
        0, name, IndexerDefinition.Newznab, "https://indexer.example", "/api", "key", [2000], [5000], 25, true, true, true);

    private DownloadItem Item(int id, DownloadStatus status) => new()
    {
        Id = id,
        Title = $"Release {id}",
        Status = status,
        Added = _time.Now.AddMinutes(id),
    };

    private void AddUsenetServer(string name)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UsenetServers (Name, Host, Port, UseSsl, Username, Password, Connections, Priority, Enabled, RetentionDays, Added)
            VALUES ($name, 'news.example', 563, 1, '', '', 10, 0, 1, 0, '2026-10-17')
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = name;
        command.Parameters.Add(parameter);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private sealed class FakeDownloadQueue : IDownloadQueue
    {
        public List<DownloadItem> Items { get; } = [];

        public List<DownloadItem> Failed { get; } = [];

        public IReadOnlyList<DownloadItem> Active() => Items;

        public DownloadItem? Find(int id) => Items.Concat(Failed).FirstOrDefault(i => i.Id == id);
    }

    private sealed class FakeArticleSource : IArticleSource
    {
        public int MaxConnections { get; set; }

        public Task<DecodedArticle> FetchAsync(string messageId, IReadOnlyList<string> groups, DateTimeOffset posted, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProbe : IStatusProbe
    {
        public Dictionary<string, FolderAccess> Folders { get; } = [];

        public Dictionary<string, DiskSpace> Space { get; } = [];

        public DiskSpace? FreeSpace(string folder) => Space.GetValueOrDefault(folder);

        public FolderAccess Access(string folder) => Folders.GetValueOrDefault(folder) ?? new FolderAccess(true, true, null);
    }
}
