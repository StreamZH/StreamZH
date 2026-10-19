// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Downloads;
using StreamZH.Library.Downloads.Torrent;

namespace StreamZH.Tests.Torrents;

public sealed class TorrentClientTests
{
    private static readonly DirectHttpClientFactory Http = new();

    private static DownloadClientDefinition Client(string implementation, Dictionary<string, JsonElement> settings) =>
        new(1, implementation, implementation, DownloadProtocol.Torrent, true, 1, settings);

    [Fact]
    public void Info_hash_and_magnet_links()
    {
        var file = TorrentFixtures.Create("Generated.Sample.2024.1080p");
        var hash = TorrentMetadata.InfoHash(file);
        Assert.Matches("^[0-9a-f]{40}$", hash);
        Assert.Equal("Generated.Sample.2024.1080p", TorrentMetadata.Name(file));
        Assert.True(TorrentMetadata.LooksLikeTorrent(file));
        Assert.False(TorrentMetadata.LooksLikeTorrent("<html>limit</html>"u8.ToArray()));
        Assert.Throws<FormatException>(() => TorrentMetadata.InfoHash("d3:foo3:bare"u8.ToArray()));

        var magnet = TorrentMetadata.ParseMagnet(TorrentFixtures.Magnet("Generated.Sample.2024.1080p"))!;
        Assert.Equal(hash, magnet.InfoHash);
        Assert.Equal("Generated.Sample.2024.1080p", magnet.Name);
        Assert.Equal(["http://tracker.example.org/announce"], magnet.Trackers);

        // Base32 hashes (older magnet links) become hex.
        Assert.Equal("6a1b2ff6f5e0a1d1a9ba9c1cc7d8f8f0a9b8c7d6",
            TorrentMetadata.ParseMagnet("magnet:?xt=urn:btih:NINS75XV4CQ5DKN2TQOMPWHY6CU3RR6W&dn=x")!.InfoHash);
        Assert.Null(TorrentMetadata.ParseMagnet("magnet:?xt=urn:btmh:1220abcd"));
        Assert.Null(TorrentMetadata.ParseMagnet("https://example.org/file.torrent"));
    }

    [Theory]
    [InlineData("v4.6.7", "2.9.3")]
    [InlineData("v5.0.4", "2.11.2")]
    public async Task QBittorrent_logs_in_creates_the_category_adds_lists_and_removes(string version, string api)
    {
        await using var fake = new FakeQBittorrent(version, api) { Username = "admin", Password = "secret-pass" };
        await fake.StartAsync();
        var client = new QBittorrentClient(Http);
        var definition = Client(DownloadClientDefinition.QBittorrent, fake.Settings("admin", "secret-pass"));

        var info = await client.TestAsync(definition, CancellationToken.None);
        Assert.Equal(("qBittorrent", version.TrimStart('v')), (info.Name, info.Version));
        Assert.Contains("streamzh", fake.Categories);
        Assert.Equal(1, fake.Logins);

        var file = TorrentFixtures.Create("Generated.Movie.2024.1080p.WEB-GRP");
        var hash = await client.AddAsync(definition, new TorrentAddRequest("Generated.Movie.2024.1080p.WEB-GRP", file, null, "streamzh", SeedRatio: 1.5, SeedTimeMinutes: 4320), CancellationToken.None);
        Assert.Equal(TorrentMetadata.InfoHash(file), hash);
        var added = fake.Torrent(hash);
        Assert.Equal(("streamzh", 1.5, 4320), (added.Category, added.RatioLimit!.Value, added.SeedTimeLimit!.Value));

        var magnetHash = await client.AddAsync(definition, new TorrentAddRequest("Magnet", null, TorrentFixtures.Magnet("Generated.Show.S01E01"), "streamzh", Paused: true), CancellationToken.None);
        Assert.Equal("true", fake.LastAdd[api == "2.11.2" ? "stopped" : "paused"]);
        Assert.True(fake.Torrent(magnetHash).FromMagnet);

        fake.Torrent(hash).Phase = FakePhase.Seeding;
        fake.Torrent(hash).Ratio = 0.42;
        fake.Torrent(hash).SeedingSeconds = 600;
        var torrents = await client.GetTorrentsAsync(definition, "streamzh", CancellationToken.None);
        var seeding = Assert.Single(torrents, t => t.Hash == hash);
        Assert.Equal((TorrentState.Seeding, 0.42, "/downloads/Generated.Movie.2024.1080p.WEB-GRP"), (seeding.State, seeding.Ratio, seeding.ContentPath));
        Assert.Equal((seeding.Size, TimeSpan.FromMinutes(10), 7, 2), (seeding.Downloaded, seeding.SeedingTime, seeding.Seeders, seeding.Peers));
        Assert.Equal(TorrentState.Paused, torrents.Single(t => t.Hash == magnetHash).State);

        // A restart of qBittorrent drops the session: StreamZH logs in again.
        fake.ExpireSession();
        Assert.Equal(2, (await client.GetTorrentsAsync(definition, null, CancellationToken.None)).Count);
        Assert.Equal(2, fake.Logins);

        await client.RemoveAsync(definition, hash, deleteData: true, CancellationToken.None);
        Assert.Equal((hash, true), Assert.Single(fake.Removed));
    }

    [Fact]
    public async Task QBittorrent_wrong_password_and_states()
    {
        await using var fake = new FakeQBittorrent { Username = "admin", Password = "right" };
        await fake.StartAsync();
        var client = new QBittorrentClient(Http);
        var error = await Assert.ThrowsAsync<DownloadClientException>(() =>
            client.TestAsync(Client(DownloadClientDefinition.QBittorrent, fake.Settings("admin", "wrong")), CancellationToken.None));
        Assert.Equal(DownloadClientCatalog.Password, error.Field);

        var noLogin = await Assert.ThrowsAsync<DownloadClientException>(() =>
            client.TestAsync(Client(DownloadClientDefinition.QBittorrent, fake.Settings()), CancellationToken.None));
        Assert.Equal(DownloadClientCatalog.Username, noLogin.Field);

        Assert.Equal(TorrentState.Completed, QBittorrentClient.MapState("pausedUP"));
        Assert.Equal(TorrentState.Completed, QBittorrentClient.MapState("stoppedUP"));
        Assert.Equal(TorrentState.Seeding, QBittorrentClient.MapState("stalledUP"));
        Assert.Equal(TorrentState.Stalled, QBittorrentClient.MapState("stalledDL"));
        Assert.Equal(TorrentState.Error, QBittorrentClient.MapState("missingFiles"));
        Assert.Equal(TorrentState.Downloading, QBittorrentClient.MapState("metaDL"));
        Assert.Equal(TorrentState.Downloading, QBittorrentClient.MapState("someFutureState"));
    }

    [Fact]
    public async Task Unreachable_client_names_the_host()
    {
        var settings = new Dictionary<string, JsonElement>
        {
            [DownloadClientCatalog.Host] = JsonSerializer.SerializeToElement("127.0.0.1"),
            [DownloadClientCatalog.Port] = JsonSerializer.SerializeToElement(1),
        };
        var error = await Assert.ThrowsAsync<DownloadClientException>(() =>
            new TransmissionClient(Http).TestAsync(Client(DownloadClientDefinition.Transmission, settings), CancellationToken.None));
        Assert.Equal(DownloadClientCatalog.Host, error.Field);
        Assert.Contains("Could not connect to Transmission", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transmission_session_handshake_labels_seed_limits_and_states()
    {
        await using var fake = new FakeTransmission { Username = "rpc", Password = "rpc-pass" };
        await fake.StartAsync();
        var client = new TransmissionClient(Http);
        var definition = Client(DownloadClientDefinition.Transmission, fake.Settings("rpc", "rpc-pass", "/transmission/"));

        var info = await client.TestAsync(definition, CancellationToken.None);
        Assert.Equal(("Transmission", "4.0.6"), (info.Name, info.Version));
        Assert.Equal(1, fake.Handshakes);

        var file = TorrentFixtures.Create("Generated.Show.S01.1080p.WEB-GRP");
        var hash = await client.AddAsync(definition, new TorrentAddRequest("x", file, null, "streamzh", SeedRatio: 2, SeedTimeMinutes: 60), CancellationToken.None);
        var torrent = fake.Torrent(hash);
        Assert.Equal(("streamzh", 2.0, 60), (torrent.Category, torrent.RatioLimit!.Value, torrent.SeedTimeLimit!.Value));
        Assert.Equal("streamzh", fake.LastAdd!["labels"]![0]!.GetValue<string>());

        // Transmission changes the session id now and then: 409 → repeat with the new one.
        fake.SessionId = "session-2";
        torrent.Phase = FakePhase.Stopped;
        torrent.Ratio = 2.01;
        var listed = Assert.Single(await client.GetTorrentsAsync(definition, "streamzh", CancellationToken.None));
        Assert.Equal((TorrentState.Completed, "/downloads/Generated.Show.S01.1080p.WEB-GRP"), (listed.State, listed.ContentPath));
        Assert.Equal(2, fake.Handshakes);
        Assert.Empty(await client.GetTorrentsAsync(definition, "other-label", CancellationToken.None));

        // Adding the same torrent again answers "torrent-duplicate" with the same hash.
        Assert.Equal(hash, await client.AddAsync(definition, new TorrentAddRequest("x", file, null, "streamzh"), CancellationToken.None));

        torrent.Phase = FakePhase.Error;
        Assert.Contains("No data found", Assert.Single(await client.GetTorrentsAsync(definition, null, CancellationToken.None)).Error, StringComparison.Ordinal);

        await client.RemoveAsync(definition, hash, deleteData: false, CancellationToken.None);
        Assert.Equal((hash, false), Assert.Single(fake.Removed));

        var wrong = await Assert.ThrowsAsync<DownloadClientException>(() =>
            client.TestAsync(Client(DownloadClientDefinition.Transmission, fake.Settings("rpc", "nope", "/transmission/")), CancellationToken.None));
        Assert.Equal(DownloadClientCatalog.Password, wrong.Field);

        Assert.Equal(TorrentState.Paused, TransmissionClient.MapState(0, 100, 50, 0));
        Assert.Equal(TorrentState.Seeding, TransmissionClient.MapState(6, 100, 0, 0));
        Assert.Equal(TorrentState.Checking, TransmissionClient.MapState(2, 100, 0, 0));
        Assert.Equal(TorrentState.Queued, TransmissionClient.MapState(3, 100, 50, 0));
    }

    [Fact]
    public async Task Transmission_without_labels_refuses_a_category()
    {
        await using var fake = new FakeTransmission(rpcVersion: 15, version: "2.94 (d8e60ee44f)");
        await fake.StartAsync();
        var error = await Assert.ThrowsAsync<DownloadClientException>(() =>
            new TransmissionClient(Http).TestAsync(Client(DownloadClientDefinition.Transmission, fake.Settings(urlBase: "/transmission/")), CancellationToken.None));
        Assert.Equal(DownloadClientCatalog.Category, error.Field);

        var info = await new TransmissionClient(Http).TestAsync(
            Client(DownloadClientDefinition.Transmission, fake.Settings(urlBase: "/transmission/", category: string.Empty)), CancellationToken.None);
        Assert.Equal("2.94", info.Version);
    }

    [Fact]
    public async Task Deluge_logs_in_connects_the_daemon_and_uses_labels()
    {
        await using var fake = new FakeDeluge { Password = "deluge-pass" };
        await fake.StartAsync();
        var client = new DelugeClient(Http);
        var definition = Client(DownloadClientDefinition.Deluge, fake.Settings(password: "deluge-pass"));

        var info = await client.TestAsync(definition, CancellationToken.None);
        Assert.Equal(("Deluge", "2.1.1"), (info.Name, info.Version));
        Assert.True(fake.DaemonConnected);
        Assert.Contains("streamzh", fake.Labels);

        var file = TorrentFixtures.Create("Generated.Movie.2023.2160p");
        var hash = await client.AddAsync(definition, new TorrentAddRequest("x", file, null, "streamzh", SeedRatio: 1.0), CancellationToken.None);
        var torrent = fake.Torrent(hash);
        Assert.Equal(("streamzh", 1.0), (torrent.Category, torrent.RatioLimit!.Value));
        Assert.False(fake.LastAddOptions!["remove_at_ratio"]!.GetValue<bool>());

        torrent.Phase = FakePhase.Stopped;
        torrent.Ratio = 1.2;
        fake.ExpireSession();
        var listed = Assert.Single(await client.GetTorrentsAsync(definition, "streamzh", CancellationToken.None));
        Assert.Equal((TorrentState.Completed, 1.2), (listed.State, listed.Ratio));
        Assert.Equal(2, fake.Logins);

        await client.RemoveAsync(definition, hash, deleteData: true, CancellationToken.None);
        Assert.Equal((hash, true), Assert.Single(fake.Removed));

        var magnetHash = await client.AddAsync(definition, new TorrentAddRequest("m", null, TorrentFixtures.Magnet("Generated.Magnet"), null), CancellationToken.None);
        Assert.True(fake.Torrent(magnetHash).FromMagnet);

        var wrong = await Assert.ThrowsAsync<DownloadClientException>(() =>
            client.TestAsync(Client(DownloadClientDefinition.Deluge, fake.Settings(password: "nope")), CancellationToken.None));
        Assert.Equal(DownloadClientCatalog.Password, wrong.Field);

        fake.LabelPlugin = false;
        var noLabels = await Assert.ThrowsAsync<DownloadClientException>(() => client.TestAsync(definition, CancellationToken.None));
        Assert.Equal(DownloadClientCatalog.Category, noLabels.Field);

        Assert.Equal(TorrentState.Completed, DelugeClient.MapState("Paused", finished: true));
        Assert.Equal(TorrentState.Paused, DelugeClient.MapState("Paused", finished: false));
        Assert.Equal(TorrentState.Seeding, DelugeClient.MapState("Queued", finished: true));
    }
}
