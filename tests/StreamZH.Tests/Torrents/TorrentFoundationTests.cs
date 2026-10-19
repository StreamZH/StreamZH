// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Data;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Torrents;

public sealed class TorrentFoundationTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    [Fact]
    public void Download_clients_round_trip_with_their_settings()
    {
        var repo = new DownloadClientRepository(_bed.Database);
        var settings = new Dictionary<string, JsonElement>
        {
            ["host"] = JsonSerializer.SerializeToElement("192.168.1.10"),
            ["port"] = JsonSerializer.SerializeToElement(8080),
            ["useSsl"] = JsonSerializer.SerializeToElement(false),
            ["category"] = JsonSerializer.SerializeToElement("streamzh"),
        };
        var id = repo.Add(new DownloadClientDefinition(0, "qBittorrent", DownloadClientDefinition.QBittorrent, DownloadProtocol.Torrent, true, 1, settings) { RemoveCompleted = false });

        var client = repo.Find(id)!;
        Assert.Equal("192.168.1.10", client.Text("host"));
        Assert.Equal(8080, client.Number("port"));
        Assert.False(client.Flag("useSsl", true));
        Assert.True(client.Flag("missing", true));
        Assert.False(client.RemoveCompleted);
        Assert.Equal(DownloadProtocol.Torrent, client.Protocol);

        var mappings = new RemotePathMappingRepository(_bed.Database);
        mappings.Add("192.168.1.10", "/downloads", "/data/downloads");
        Assert.Single(mappings.All());
    }

    [Fact]
    public void Torznab_indexers_are_torrent_and_others_usenet()
    {
        var torznab = new IndexerDefinition(1, "Tracker", IndexerDefinition.Torznab, "http://192.168.1.10:9117", "/api", "key", [2000], [5000], 25, true, true, true);
        var newznab = torznab with { Implementation = IndexerDefinition.Newznab };
        Assert.Equal(DownloadProtocol.Torrent, torznab.Protocol);
        Assert.Equal(DownloadProtocol.Usenet, newznab.Protocol);
        Assert.Equal(1, torznab.MinimumSeeders);
    }

    [Fact]
    public void Torrent_status_progress_and_done()
    {
        var status = new TorrentStatus("abc", "Name", TorrentState.Seeding, 1000, 1000, 500, 0.5, 0, 10, null, 3, 1, "streamzh", "/downloads", "/downloads/Name", null, null, null, TimeSpan.FromMinutes(5));
        Assert.True(status.IsDone);
        Assert.Equal(1, status.Progress);
    }

    public void Dispose() => _bed.Dispose();
}
