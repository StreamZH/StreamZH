// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using StreamZH.Data;
using StreamZH.Library.Downloads.Torrent;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Indexers;
using StreamZH.Tests.Library.Migration;

namespace StreamZH.Tests.Torrents;

public sealed class TorrentMigrationTests : IAsyncLifetime
{
    private const string Clients = """
        [
          {
            "enable": true, "protocol": "torrent", "priority": 1, "removeCompletedDownloads": false, "removeFailedDownloads": true, "name": "qBittorrent",
            "fields": [
              { "order": 0, "name": "host", "value": "192.168.1.20" }, { "order": 1, "name": "port", "value": 8080 },
              { "order": 2, "name": "useSsl", "value": false }, { "order": 3, "name": "urlBase" },
              { "order": 4, "name": "apiKey", "value": "" }, { "order": 5, "name": "username", "value": "admin" }, { "order": 6, "name": "password", "value": "********" },
              { "order": 7, "name": "movieCategory", "value": "radarr" }, { "order": 11, "name": "initialState", "value": 0 }
            ],
            "implementationName": "qBittorrent", "implementation": "QBittorrent", "configContract": "QBittorrentSettings", "tags": [], "id": 1
          },
          {
            "enable": true, "protocol": "torrent", "priority": 2, "removeCompletedDownloads": true, "name": "Transmission",
            "fields": [
              { "name": "host", "value": "torrent-box" }, { "name": "port", "value": 9091 }, { "name": "useSsl", "value": false },
              { "name": "urlBase", "value": "/transmission/" }, { "name": "username", "value": "" }, { "name": "password", "value": "" },
              { "name": "movieCategory", "value": "radarr" }, { "name": "addPaused", "value": true }
            ],
            "implementationName": "Transmission", "implementation": "Transmission", "configContract": "TransmissionSettings", "tags": [], "id": 2
          },
          {
            "enable": true, "protocol": "torrent", "priority": 1, "name": "rTorrent",
            "fields": [ { "name": "host", "value": "localhost" }, { "name": "port", "value": 8080 } ],
            "implementation": "RTorrent", "tags": [], "id": 3
          },
          {
            "enable": true, "protocol": "torrent", "priority": 3, "name": "Deluge",
            "fields": [ { "name": "host", "value": "deluge-box" }, { "name": "port", "value": 8112 }, { "name": "password", "value": "********" } ],
            "implementation": "Deluge", "tags": [], "id": 4
          }
        ]
        """;

    private MigrationBed _bed = null!;

    public Task InitializeAsync()
    {
        _bed = new MigrationBed();
        _bed.Radarr.DownloadClientJson = Clients;
        _bed.Sonarr.DownloadClientJson = Clients.Replace("movieCategory", "tvCategory", StringComparison.Ordinal);
        Directory.CreateDirectory(Path.Combine(_bed.Root, "torrents"));
        _bed.Radarr.RemotePathMappingJson = """
            [
              { "host": "192.168.1.20", "remotePath": "/downloads/", "localPath": "/data/torrents/", "id": 1 },
              { "host": "torrent-box", "remotePath": "/mnt/t/", "localPath": "/elsewhere/", "id": 2 }
            ]
            """;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _bed.DisposeAsync();

    private DownloadClientService Service => _bed.Services.GetRequiredService<DownloadClientService>();

    [Fact]
    public async Task Radarr_torrent_clients_and_mappings_are_taken_over()
    {
        var section = new ArrDownloadClientsSection(MigrationSources.Radarr);
        var plan = await section.PreviewAsync(_bed.Context(MigrationSources.Radarr), CancellationToken.None);
        Assert.Equal(
            ["downloadClients:1", "downloadClients:2", "downloadClients:3", "downloadClients:4", "downloadClients:mapping:1", "downloadClients:mapping:2"],
            plan.Items.Select(i => i.Key));
        Assert.Equal(
            [MigrationAction.Create, MigrationAction.Create, MigrationAction.Skip, MigrationAction.Create, MigrationAction.Create, MigrationAction.Skip],
            plan.Items.Select(i => i.Action));
        Assert.Equal([true, false, false, true], plan.Items.Take(4).Select(i => i.NeedsSecret));
        Assert.Equal(ArrDownloadClientsSection.UnsupportedTorrentClient, plan.Items[2].Reason);
        Assert.Contains("does not exist", plan.Items[5].Reason, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings!, w => w.Contains("streamzh", StringComparison.Ordinal));

        var request = _bed.Request(MigrationSources.Radarr) with { Secrets = new Dictionary<string, string> { ["downloadClients:1"] = "typed-in" } };
        var run = _bed.Run();
        await section.RunAsync(_bed.Context(MigrationSources.Radarr, request), run, CancellationToken.None);
        var report = run.Report();
        Assert.Equal((4, 2, 0), (report.Created, report.Skipped, report.Failed));
        Assert.Contains(report.Messages, m => m.Level == MigrationMessageLevel.Warn && m.Text.Contains("switched off", StringComparison.Ordinal));

        var clients = Service.All();
        var qbit = clients.Single(c => c.Name == "qBittorrent");
        Assert.Equal(("192.168.1.20", 8080, "admin", "typed-in"), (qbit.Text("host"), qbit.Number("port"), qbit.Text("username"), qbit.Text("password")));
        Assert.Equal((true, false, "streamzh"), (qbit.Enable, qbit.RemoveCompleted, qbit.Text("category")));
        var transmission = clients.Single(c => c.Name == "Transmission");
        Assert.Equal(("/transmission/", true, 2), (transmission.Text("urlBase"), transmission.Flag("addPaused"), transmission.Priority));
        Assert.False(clients.Single(c => c.Name == "Deluge").Enable);

        var mapping = Assert.Single(_bed.Services.GetRequiredService<RemotePathMappingRepository>().All());
        Assert.Equal(("192.168.1.20", "/downloads/"), (mapping.Host, mapping.RemotePath));
        Assert.Equal(Path.Combine(_bed.Root, "torrents") + "/", mapping.LocalPath);

        // A second run (or Sonarr with the same clients) updates instead of adding.
        var again = await section.PreviewAsync(_bed.Context(MigrationSources.Radarr), CancellationToken.None);
        Assert.Equal(MigrationAction.Update, again.Items[0].Action);
        Assert.False(again.Items[0].NeedsSecret);
        Assert.Equal(MigrationAction.Link, again.Items[4].Action);

        var sonarr = new ArrDownloadClientsSection(MigrationSources.Sonarr);
        var sonarrRun = _bed.Run();
        await sonarr.RunAsync(_bed.Context(MigrationSources.Sonarr), sonarrRun, CancellationToken.None);
        Assert.Equal((0, 3), (sonarrRun.Report().Created, sonarrRun.Report().Updated));
        Assert.Equal(3, Service.All().Count);
        Assert.Equal("typed-in", Service.All().Single(c => c.Name == "qBittorrent").Text("password"));
    }

    [Fact]
    public async Task Left_out_items_are_skipped_and_old_apps_without_mappings_still_work()
    {
        _bed.Radarr.RemotePathMappingJson = "not used";
        var section = new ArrDownloadClientsSection(MigrationSources.Radarr);
        var request = _bed.Request(MigrationSources.Radarr) with { Exclude = ["downloadClients:2", "downloadClients:4", "downloadClients:1"] };
        var run = _bed.Run();
        await section.RunAsync(_bed.Context(MigrationSources.Radarr, request), run, CancellationToken.None);
        Assert.Equal(0, run.Report().Created);
        Assert.Empty(Service.All());
    }
}
