// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Events;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Downloads;
using StreamZH.Library.Downloads.Torrent;
using StreamZH.Library.Indexers;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Sabnzbd;
using StreamZH.Library.Search;
using StreamZH.Library.Usenet;
using StreamZH.Tests.Library.Search;
using StreamZH.Tests.Torrents;
using StreamZH.Usenet.Pool;

namespace StreamZH.Tests.Library.Migration;

/// <summary>A database with the services the SABnzbd and indexer sections use, and fake SABnzbd/Radarr/Sonarr/Prowlarr.</summary>
public sealed class MigrationBed : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly UsenetArticleSource _articles;

    public MigrationBed()
    {
        Root = Path.Combine(Bed.DataDirectory, "sab");
        Directory.CreateDirectory(Path.Combine(Root, "downloads", "incomplete"));
        Directory.CreateDirectory(Path.Combine(Root, "downloads", "complete"));
        Directory.CreateDirectory(Path.Combine(Root, "anime"));

        _articles = new UsenetArticleSource(TimeProvider.System, NullLogger<UsenetArticleSource>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Bed.Database);
        services.AddSingleton(Bed.Config);
        services.AddSingleton(Bed.Downloads);
        services.AddSingleton<TimeProvider>(Bed.Time);
        services.AddSingleton<IEventBus>(Bed.Events);
        services.AddSingleton(new UsenetServerRepository(Bed.Database));
        services.AddSingleton(_articles);
        services.AddSingleton(sp => new UsenetServerService(sp.GetRequiredService<UsenetServerRepository>(), _articles, TimeProvider.System, NullLogger<UsenetServerService>.Instance));
        services.AddSingleton(new DownloadCategoryRepository(Bed.Database));
        services.AddSingleton<DownloadCategoryService>();
        services.AddSingleton(new DownloadScheduleRepository(Bed.Database));
        services.AddSingleton<DownloadScheduleService>();
        services.AddSingleton(new IndexerService(new IndexerRepository(Bed.Database), new IndexerStatusRepository(Bed.Database), TimeProvider.System, NullLogger<IndexerService>.Instance));
        services.AddSingleton(sp => new AutomationHold(Bed.Config, Bed.Time));
        services.AddSingleton(new DownloadClientRepository(Bed.Database));
        services.AddSingleton(new RemotePathMappingRepository(Bed.Database));
        services.AddSingleton(sp => new DownloadClientService(
            sp.GetRequiredService<DownloadClientRepository>(),
            [new ScriptedTorrentClient(), new ScriptedTorrentClient(Core.Downloads.DownloadClientDefinition.Transmission), new ScriptedTorrentClient(Core.Downloads.DownloadClientDefinition.Deluge)],
            NullLogger<DownloadClientService>.Instance));
        services.AddSingleton(new SabnzbdApiClient(new HttpClient(Sabnzbd), NullLogger<SabnzbdApiClient>.Instance));
        services.AddSingleton(sp => new ArrApiClient(new HttpClient(new ArrRouter(Radarr, Sonarr)), NullLogger<ArrApiClient>.Instance));
        services.AddSingleton(sp => new ProwlarrImportService(new HttpClient(Prowlarr), sp.GetRequiredService<IndexerService>(), NullLogger<ProwlarrImportService>.Instance));
        _provider = services.BuildServiceProvider();
    }

    public SearchTestBed Bed { get; } = new();

    public string Root { get; }

    public StubSabnzbd Sabnzbd { get; } = new();

    public StubArr Radarr { get; } = new("Radarr");

    public StubArr Sonarr { get; } = new("Sonarr");

    public StubProwlarr Prowlarr { get; } = new();

    public IServiceProvider Services => _provider;

    public UsenetServerService Servers => _provider.GetRequiredService<UsenetServerService>();

    public DownloadCategoryService Categories => _provider.GetRequiredService<DownloadCategoryService>();

    public DownloadScheduleService Schedules => _provider.GetRequiredService<DownloadScheduleService>();

    public IndexerService Indexers => _provider.GetRequiredService<IndexerService>();

    public AutomationHold Hold => _provider.GetRequiredService<AutomationHold>();

    public static string Url(string source) => source switch
    {
        MigrationSources.Sabnzbd => "http://sab:8080",
        MigrationSources.Radarr => "http://radarr:7878",
        MigrationSources.Sonarr => "http://sonarr:8989",
        _ => "http://prowlarr:9696",
    };

    public static string Key(string source) => source switch
    {
        MigrationSources.Sabnzbd => StubSabnzbd.ApiKey,
        MigrationSources.Prowlarr => StubProwlarr.ApiKey,
        _ => StubArr.ApiKey,
    };

    public MigrationRequest Request(string source) =>
        new(Url(source), Key(source), [new PathMapping("/data", Root)]);

    public MigrationContext Context(string source, MigrationRequest? request = null)
    {
        var r = request ?? Request(source);
        return new MigrationContext(source, new MigrationConnection(r.Url!, r.ApiKey!), new MigrationAppInfo(source, "1"), r, new PathMapper(r.PathMappings), _provider);
    }

    public MigrationSectionRun Run(string key = "section") => new(key, key, (_, _) => { });

    public MigrationService Service() =>
        new(
            Sections(),
            [new SabnzbdMigrationProbe(_provider.GetRequiredService<SabnzbdApiClient>()), new ArrMigrationProbe(_provider.GetRequiredService<ArrApiClient>())],
            new MigrationRunRepository(Bed.Database),
            _provider,
            Bed.Events,
            NullLogger<MigrationService>.Instance);

    public static IMigrationSection[] Sections()
    {
        var services = new ServiceCollection();
        services.AddSabnzbdMigration();
        using var provider = services.BuildServiceProvider();
        return [.. provider.GetServices<IMigrationSection>()];
    }

    public static async Task<MigrationRun> WaitAsync(MigrationService service, int id)
    {
        for (var i = 0; i < 400; i++)
        {
            var run = service.Find(id)!;
            if (run.Status is MigrationRunStatus.Done or MigrationRunStatus.Failed or MigrationRunStatus.Cancelled && run.Finished is not null)
            {
                return run;
            }

            await Task.Delay(25, CancellationToken.None);
        }

        throw new TimeoutException("Migration run did not finish.");
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _articles.DisposeAsync();
        Bed.Dispose();
    }

    /// <summary>Radarr and Sonarr by host name.</summary>
    private sealed class ArrRouter(StubArr radarr, StubArr sonarr) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.Host == "sonarr" ? sonarr : radarr;
            using var invoker = new HttpMessageInvoker(target, disposeHandler: false);
            return await invoker.SendAsync(request, cancellationToken);
        }
    }
}
