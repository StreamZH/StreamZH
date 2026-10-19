// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Sockets;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using StreamZH.Core.Downloads;
using StreamZH.Tests.Library.Search;
using StreamZH.Torrent;
using TorrentState = StreamZH.Core.Downloads.TorrentState;

namespace StreamZH.Tests.Torrents;

/// <summary>
/// The built-in engine end to end on localhost: a plain MonoTorrent engine seeds generated data through a local HTTP
/// tracker, StreamZH's engine downloads it through <see cref="ITorrentClient"/>.
/// </summary>
public sealed class TorrentEngineTests : IAsyncLifetime
{
    private static readonly DownloadClientDefinition Definition = new(1, "StreamZH", DownloadClientDefinition.BuiltInTorrent, DownloadProtocol.Torrent, true, 1, new Dictionary<string, System.Text.Json.JsonElement>());

    private readonly TorrentEngineTestBed _bed = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _bed.DisposeAsync();

    [Fact]
    public async Task Downloads_a_torrent_from_a_local_seeder_byte_for_byte()
    {
        var announce = _bed.StartTracker();
        var data = _bed.CreateData("Generated.Movie.2026.1080p.mkv", 3 * 1024 * 1024 + 123);
        var torrent = await TorrentEngineTestBed.CreateTorrentAsync(data, announce);
        await _bed.SeedAsync(torrent, data);

        var engine = _bed.CreateEngine();
        var client = ClientFor(engine);
        var hash = await client.AddAsync(Definition, new TorrentAddRequest("Generated Movie", torrent, null, "movies"), CancellationToken.None);
        Assert.True(TorrentStateStore.IsHash(hash));
        Assert.True(engine.IsRunning);

        var done = await TorrentEngineTestBed.WaitForAsync(engine, hash, s => s.State == TorrentState.Seeding, "the download to finish");
        var target = Path.Combine(_bed.DownloadFolder, "movies", "Generated.Movie.2026.1080p.mkv");
        Assert.True(File.Exists(target));
        Assert.Equal(TorrentEngineTestBed.Sha256(data), TorrentEngineTestBed.Sha256(target));

        Assert.Equal("Generated.Movie.2026.1080p.mkv", done.Name);
        Assert.Equal("movies", done.Category);
        Assert.Equal(new FileInfo(data).Length, done.Size);
        Assert.Equal(done.Size, done.Downloaded);
        Assert.Equal(1, done.Progress);
        Assert.True(done.IsDone);
        Assert.Equal(target, done.ContentPath);
        Assert.Equal(Path.Combine(_bed.DownloadFolder, "movies"), done.SavePath);
        Assert.NotNull(done.CompletedOn);
        Assert.Null(done.Eta);

        // The category filter of the tracker service.
        Assert.Single(await client.GetTorrentsAsync(Definition, "movies", CancellationToken.None));
        Assert.Empty(await client.GetTorrentsAsync(Definition, "series", CancellationToken.None));

        // Adding the same torrent again keeps the one there is.
        Assert.Equal(hash, await client.AddAsync(Definition, new TorrentAddRequest("Again", torrent, null, "movies"), CancellationToken.None));
        Assert.Single(await client.GetTorrentsAsync(Definition, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_magnet_link_fetches_the_metadata_from_the_seeder()
    {
        var announce = _bed.StartTracker();
        var data = _bed.CreateData("Generated.Show.S01", 700 * 1024, 90 * 1024, 5 * 1024);
        var torrent = await TorrentEngineTestBed.CreateTorrentAsync(data, announce);
        var seeding = await _bed.SeedAsync(torrent, data);
        var infoHash = seeding.InfoHashes.V1!.ToHex().ToLowerInvariant();
        var magnet = $"magnet:?xt=urn:btih:{infoHash}&dn=Generated.Show.S01&tr={Uri.EscapeDataString(announce)}";

        var engine = _bed.CreateEngine();
        var hash = await ClientFor(engine).AddAsync(Definition, new TorrentAddRequest("Generated Show", null, magnet, "series"), CancellationToken.None);
        Assert.Equal(infoHash, hash);

        var done = await TorrentEngineTestBed.WaitForAsync(engine, hash, s => s.State == TorrentState.Seeding, "the magnet download to finish");
        var folder = Path.Combine(_bed.DownloadFolder, "series", "Generated.Show.S01");
        Assert.Equal(folder, done.ContentPath);
        foreach (var relative in new[] { "Generated.Show.S01.mkv", Path.Combine("Extras", "part1.bin"), Path.Combine("Extras", "part2.bin") })
        {
            Assert.Equal(TorrentEngineTestBed.Sha256(Path.Combine(data, relative)), TorrentEngineTestBed.Sha256(Path.Combine(folder, relative)));
        }

        Assert.Equal((700 + 90 + 5) * 1024, done.Size);
    }

    [Fact]
    public async Task Remove_keeps_or_deletes_the_data()
    {
        var announce = _bed.StartTracker();
        var data = _bed.CreateData("Generated.Pack", 300 * 1024, 40 * 1024);
        var torrent = await TorrentEngineTestBed.CreateTorrentAsync(data, announce);
        await _bed.SeedAsync(torrent, data);
        var engine = _bed.CreateEngine();
        var client = ClientFor(engine);
        var folder = Path.Combine(_bed.DownloadFolder, "other", "Generated.Pack");

        var hash = await client.AddAsync(Definition, new TorrentAddRequest("Pack", torrent, null, null), CancellationToken.None);
        await TorrentEngineTestBed.WaitForAsync(engine, hash, s => s.IsDone, "the download to finish");
        await client.RemoveAsync(Definition, hash, deleteData: false, CancellationToken.None);
        Assert.Empty(await client.GetTorrentsAsync(Definition, null, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(folder, "Generated.Pack.mkv")));
        Assert.False(File.Exists(Path.Combine(_bed.StateFolder, "files", $"{hash}.torrent")));

        // Added again over the existing files: checked, then seeding without downloading anything.
        hash = await client.AddAsync(Definition, new TorrentAddRequest("Pack", torrent, null, null), CancellationToken.None);
        var again = await TorrentEngineTestBed.WaitForAsync(engine, hash, s => s.State == TorrentState.Seeding, "the existing data to be checked");
        Assert.Equal(again.Size, again.Downloaded);

        await client.RemoveAsync(Definition, hash, deleteData: true, CancellationToken.None);
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(Path.Combine(_bed.DownloadFolder, "other")));

        // Unknown hashes are ignored.
        await client.RemoveAsync(Definition, new string('a', 40), deleteData: true, CancellationToken.None);
    }

    [Fact]
    public async Task The_seed_goal_stops_seeding_and_reports_completed()
    {
        var announce = _bed.StartTracker();
        var data = _bed.CreateData("Generated.Goal.bin", 256 * 1024);
        var torrent = await TorrentEngineTestBed.CreateTorrentAsync(data, announce);
        await _bed.SeedAsync(torrent, data);
        var engine = _bed.CreateEngine();

        // A ratio goal of 0 is met the moment the download is complete.
        var hash = await ClientFor(engine).AddAsync(Definition, new TorrentAddRequest("Goal", torrent, null, "movies", SeedRatio: 0), CancellationToken.None);
        var done = await TorrentEngineTestBed.WaitForAsync(engine, hash, s => s.State == TorrentState.Completed, "the seed goal");
        Assert.Equal(0, done.UploadSpeed);
        Assert.Contains(_bed.Logs.Lines, l => l.Contains("reached its seed goal", StringComparison.Ordinal));

        // Resume clears the goal for a moment; the next tick stops it again.
        Assert.True(await engine.ResumeAsync(hash, CancellationToken.None));
        await TorrentEngineTestBed.WaitForAsync(engine, hash, s => s.State == TorrentState.Completed, "the seed goal again");
    }

    [Fact]
    public async Task The_engine_default_seed_time_applies_when_the_torrent_has_no_goal()
    {
        // 0 is saved as "no goal" (the settings page says so).
        _bed.Settings.Save(_bed.Settings.Current with { SeedRatio = 0, SeedTimeMinutes = 0 });
        Assert.Null(_bed.Settings.Current.SeedRatio);
        Assert.Null(_bed.Settings.Current.SeedTimeMinutes);

        _bed.Settings.Save(_bed.Settings.Current with { SeedRatio = null, SeedTimeMinutes = 1 });
        var announce = _bed.StartTracker();
        var data = _bed.CreateData("Generated.Time.bin", 128 * 1024);
        var torrent = await TorrentEngineTestBed.CreateTorrentAsync(data, announce);
        await _bed.SeedAsync(torrent, data);
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var engine = _bed.CreateEngine(time);

        var hash = await ClientFor(engine).AddAsync(Definition, new TorrentAddRequest("Time", torrent, null, "movies"), CancellationToken.None);
        await TorrentEngineTestBed.WaitForAsync(engine, hash, s => s.State == TorrentState.Seeding, "seeding");
        await engine.TickAsync(CancellationToken.None);
        time.Now += TimeSpan.FromMinutes(2);
        await TorrentEngineTestBed.WaitForAsync(engine, hash, s => s.State == TorrentState.Completed, "the seed time goal");

        // A torrent's own goal wins over the default: -1 = seed without a goal.
        var other = _bed.CreateData("Generated.Forever.bin", 64 * 1024);
        var otherTorrent = await TorrentEngineTestBed.CreateTorrentAsync(other, announce);
        await _bed.SeedAsync(otherTorrent, other);
        var forever = await ClientFor(engine).AddAsync(Definition, new TorrentAddRequest("Forever", otherTorrent, null, "movies", SeedRatio: -1, SeedTimeMinutes: -1), CancellationToken.None);
        await TorrentEngineTestBed.WaitForAsync(engine, forever, s => s.State == TorrentState.Seeding, "seeding without a goal");
        await Task.Delay(500);
        await TorrentEngineTestBed.WaitForAsync(engine, forever, s => s.State == TorrentState.Seeding, "still seeding");
    }

    [Fact]
    public async Task Seeding_time_is_counted_until_the_time_goal()
    {
        var announce = _bed.StartTracker();
        var data = _bed.CreateData("Generated.Clock.bin", 64 * 1024);
        var torrent = await TorrentEngineTestBed.CreateTorrentAsync(data, announce);
        await _bed.SeedAsync(torrent, data);
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var engine = _bed.CreateEngine(time);

        var hash = await ClientFor(engine).AddAsync(Definition, new TorrentAddRequest("Clock", torrent, null, "movies", SeedTimeMinutes: 30), CancellationToken.None);
        await TorrentEngineTestBed.WaitForAsync(engine, hash, s => s.State == TorrentState.Seeding, "seeding");
        await engine.TickAsync(CancellationToken.None);
        for (var i = 0; i < 4; i++)
        {
            time.Now += TimeSpan.FromMinutes(5);
            await engine.TickAsync(CancellationToken.None);
        }

        var status = (await engine.GetTorrentsAsync(null, CancellationToken.None)).Single();
        Assert.Equal(TorrentState.Seeding, status.State);
        Assert.InRange(status.SeedingTime.TotalMinutes, 19.9, 20.5);

        // A gap longer than ten minutes (sleep, clock change) is not counted.
        time.Now += TimeSpan.FromHours(3);
        await engine.TickAsync(CancellationToken.None);
        Assert.Equal(TorrentState.Seeding, (await engine.GetTorrentsAsync(null, CancellationToken.None)).Single().State);

        for (var i = 0; i < 2; i++)
        {
            time.Now += TimeSpan.FromMinutes(6);
            await engine.TickAsync(CancellationToken.None);
        }

        status = (await engine.GetTorrentsAsync(null, CancellationToken.None)).Single();
        Assert.Equal(TorrentState.Completed, status.State);
        Assert.InRange(status.SeedingTime.TotalMinutes, 30, 33);
    }

    [Fact]
    public async Task Torrents_survive_a_restart_with_fast_resume()
    {
        var announce = _bed.StartTracker();
        var data = _bed.CreateData("Generated.Restart.bin", 1024 * 1024);
        var torrent = await TorrentEngineTestBed.CreateTorrentAsync(data, announce);
        await _bed.SeedAsync(torrent, data);
        var paused = await TorrentEngineTestBed.CreateTorrentAsync(_bed.CreateData("Generated.Paused.bin", 64 * 1024), announce);

        var first = _bed.CreateEngine();
        var hash = await ClientFor(first).AddAsync(Definition, new TorrentAddRequest("Restart", torrent, null, "movies"), CancellationToken.None);
        var pausedHash = await ClientFor(first).AddAsync(Definition, new TorrentAddRequest("Paused", paused, null, "series", Paused: true, SeedRatio: 2), CancellationToken.None);
        var done = await TorrentEngineTestBed.WaitForAsync(first, hash, s => s.State == TorrentState.Seeding, "the download to finish");
        await first.StopAsync(CancellationToken.None);
        Assert.False(first.IsRunning);
        Assert.True(File.Exists(Path.Combine(_bed.StateFolder, "state.json")));
        var fastResume = Path.Combine(_bed.StateFolder, "cache", "fastresume", $"{hash.ToUpperInvariant()}.fresume");
        Assert.True(File.Exists(fastResume));

        // A new engine (StreamZH restarted) knows both torrents before it runs.
        var second = _bed.CreateEngine();
        var offline = await ClientFor(second).GetTorrentsAsync(Definition, null, CancellationToken.None);
        Assert.Equal(2, offline.Count);
        var restored = offline.Single(t => t.Hash == hash);
        Assert.Equal(TorrentState.Completed, restored.State);
        Assert.Equal(done.Size, restored.Downloaded);
        Assert.Equal(done.CompletedOn, restored.CompletedOn);
        Assert.Equal(TorrentState.Paused, offline.Single(t => t.Hash == pausedHash).State);

        await second.StartAsync(CancellationToken.None);
        var seeding = await TorrentEngineTestBed.WaitForAsync(second, hash, s => s.State == TorrentState.Seeding, "seeding after the restart");
        Assert.Equal(done.Size, seeding.Downloaded);
        Assert.Equal(TorrentState.Paused, (await second.GetTorrentsAsync(null, CancellationToken.None)).Single(t => t.Hash == pausedHash).State);

        Assert.True(File.Exists(Path.Combine(_bed.StateFolder, "files", $"{hash}.torrent")));
        Assert.DoesNotContain(_bed.Logs.Lines, l => l.Contains("could not be loaded", StringComparison.Ordinal));

        // Removing it while the engine is off still cleans up the copy and the fast-resume data.
        await second.StopAsync(CancellationToken.None);
        Assert.True(await second.RemoveAsync(hash, deleteData: true, CancellationToken.None));
        Assert.False(File.Exists(fastResume));
        Assert.False(File.Exists(Path.Combine(_bed.DownloadFolder, "movies", "Generated.Restart.bin")));
        Assert.Single(await second.GetTorrentsAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Pause_and_resume()
    {
        var announce = _bed.StartTracker();
        var data = _bed.CreateData("Generated.Pause.bin", 512 * 1024);
        var torrent = await TorrentEngineTestBed.CreateTorrentAsync(data, announce);
        var engine = _bed.CreateEngine();

        // Nobody seeds yet: the torrent waits for peers.
        var hash = await ClientFor(engine).AddAsync(Definition, new TorrentAddRequest("Pause", torrent, null, "movies"), CancellationToken.None);
        Assert.True(await engine.PauseAsync(hash, CancellationToken.None));
        Assert.Equal(TorrentState.Paused, (await engine.GetTorrentsAsync(null, CancellationToken.None)).Single().State);
        Assert.False(await engine.PauseAsync(new string('b', 40), CancellationToken.None));

        await _bed.SeedAsync(torrent, data);
        Assert.True(await engine.ResumeAsync(hash, CancellationToken.None));
        await TorrentEngineTestBed.WaitForAsync(engine, hash, s => s.State == TorrentState.Seeding, "the download after resume");
    }

    [Fact]
    public async Task Tracker_problems_never_show_the_passkey()
    {
        const string passkey = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        var closed = TorrentEngineTestBed.FreePort();
        var data = _bed.CreateData("Generated.Private.bin", 64 * 1024);
        var torrent = await TorrentEngineTestBed.CreateTorrentAsync(data, $"http://127.0.0.1:{closed}/{passkey}/announce?passkey={passkey}");
        var engine = _bed.CreateEngine();
        var hash = await ClientFor(engine).AddAsync(Definition, new TorrentAddRequest("Private", torrent, null, "movies"), CancellationToken.None);

        TorrentProblem? problem = null;
        await TorrentEngineTestBed.WaitUntilAsync(
            () =>
            {
                engine.TickAsync(CancellationToken.None).GetAwaiter().GetResult();
                problem = engine.Status().Problems.FirstOrDefault(p => p.Kind == "tracker");
                return problem is not null;
            },
            "a tracker problem");
        Assert.Equal(hash, problem!.Hash);
        Assert.StartsWith($"http://127.0.0.1:{closed}: ", problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(passkey, problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_bed.Logs.Lines, l => l.Contains(passkey, StringComparison.Ordinal));

        var notices = new List<StreamZH.Host.Status.OngoingNotice>();
        engine.Wanted = true;
        StreamZH.Host.Status.TorrentEngineNotices.Add(notices, engine.Status());
        var notice = Assert.Single(notices, n => n.Key == StreamZH.Host.Status.TorrentEngineNotices.TrackersKey);
        Assert.DoesNotContain(passkey, notice.Sub, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_taken_listen_port_is_reported()
    {
        var blocker = new TcpListener(IPAddress.Loopback, _bed.Settings.Current.ListenPort);
        blocker.Start();
        try
        {
            var engine = _bed.CreateEngine();
            Assert.False(engine.ListenPortAvailable());
            await Assert.ThrowsAsync<DownloadClientException>(() => ClientFor(engine).TestAsync(Definition, CancellationToken.None));

            // The listener only starts while a torrent runs.
            var data = _bed.CreateData("Generated.Port.bin", 16 * 1024);
            var torrent = await TorrentEngineTestBed.CreateTorrentAsync(data, null);
            await ClientFor(engine).AddAsync(Definition, new TorrentAddRequest("Port", torrent, null, "movies"), CancellationToken.None);
            await TorrentEngineTestBed.WaitUntilAsync(() => engine.Status().PortState == "notFree", "the port to be reported");

            engine.Wanted = true;
            var notices = new List<StreamZH.Host.Status.OngoingNotice>();
            StreamZH.Host.Status.TorrentEngineNotices.Add(notices, engine.Status());
            var notice = Assert.Single(notices, n => n.Key == StreamZH.Host.Status.TorrentEngineNotices.PortKey);
            Assert.Equal(StreamZH.Host.Status.NoticeLevels.Error, notice.Level);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task The_engine_listens_only_on_the_configured_port()
    {
        var engine = _bed.CreateEngine();
        var info = await ClientFor(engine).TestAsync(Definition, CancellationToken.None);
        Assert.Equal("StreamZH", info.Name);
        Assert.Contains("MonoTorrent", info.Version, StringComparison.Ordinal);
        Assert.False(engine.IsRunning);

        var data = _bed.CreateData("Generated.Listen.bin", 16 * 1024);
        await ClientFor(engine).AddAsync(Definition, new TorrentAddRequest("Listen", await TorrentEngineTestBed.CreateTorrentAsync(data, null), null, "movies"), CancellationToken.None);
        await TorrentEngineTestBed.WaitUntilAsync(() => engine.Status().PortState == "listening", "the listener");

        // Peers can connect on the configured port.
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, _bed.Settings.Current.ListenPort);
        Assert.True(probe.Connected);

        await engine.StopAsync(CancellationToken.None);
        Assert.Equal("stopped", engine.Status().PortState);
        Assert.True(engine.ListenPortAvailable());
    }

    [Fact]
    public async Task Without_a_bind_address_ipv4_and_ipv6_share_the_port()
    {
        _bed.Settings.Save(_bed.Settings.Current with { BindAddress = string.Empty });
        var engine = _bed.CreateEngine();
        var data = _bed.CreateData("Generated.Any.bin", 16 * 1024);
        await ClientFor(engine).AddAsync(Definition, new TorrentAddRequest("Any", await TorrentEngineTestBed.CreateTorrentAsync(data, null), null, "movies"), CancellationToken.None);
        await TorrentEngineTestBed.WaitUntilAsync(() => engine.Status().PortState == "listening", "the listeners");

        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, _bed.Settings.Current.ListenPort);
        Assert.True(probe.Connected);

        // Our own listener does not count as "taken".
        Assert.True(engine.ListenPortAvailable());
    }

    [Fact]
    public async Task Invalid_input_is_rejected()
    {
        var engine = _bed.CreateEngine();
        var client = ClientFor(engine);
        await Assert.ThrowsAsync<DownloadClientException>(() => client.AddAsync(Definition, new TorrentAddRequest("Bad", [1, 2, 3], null, "movies"), CancellationToken.None));
        await Assert.ThrowsAsync<DownloadClientException>(() => client.AddAsync(Definition, new TorrentAddRequest("Bad", null, "https://tracker.example.org/x.torrent", "movies"), CancellationToken.None));
        await Assert.ThrowsAsync<DownloadClientException>(() => client.AddAsync(Definition, new TorrentAddRequest("Bad", null, "magnet:?dn=nothing", "movies"), CancellationToken.None));
        await Assert.ThrowsAsync<DownloadClientException>(() => client.AddAsync(Definition, new TorrentAddRequest("Bad", null, null, "movies"), CancellationToken.None));
        var torrent = await TorrentEngineTestBed.CreateTorrentAsync(_bed.CreateData("Generated.Path.bin", 1024), null);
        await Assert.ThrowsAsync<DownloadClientException>(() => client.AddAsync(Definition, new TorrentAddRequest("Relative", torrent, null, "movies", SavePath: "relative/folder"), CancellationToken.None));
        Assert.Empty(await client.GetTorrentsAsync(Definition, null, CancellationToken.None));
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void Settings_become_monotorrent_settings_without_anything_extra()
    {
        var options = TorrentEngineOptions.Default with { DownloadLimitKBps = 500, UploadLimitKBps = 100, MaxConnections = 77 };
        var settings = TorrentEngine.BuildEngineSettings(options, _bed.Root);
        Assert.False(settings.AllowPortForwarding);
        Assert.False(settings.AllowLocalPeerDiscovery);
        Assert.All(settings.ListenEndPoints.Values, e => Assert.Equal(TorrentEngineOptions.DefaultListenPort, e.Port));
        Assert.Equal(TorrentEngineOptions.DefaultListenPort, settings.DhtEndPoint!.Port);
        Assert.Equal(500 * 1024, settings.MaximumDownloadRate);
        Assert.Equal(100 * 1024, settings.MaximumUploadRate);
        Assert.Equal(77, settings.MaximumConnections);
        Assert.Equal(EncryptionType.RC4Full, settings.AllowedEncryption[0]);
        Assert.Contains(EncryptionType.PlainText, settings.AllowedEncryption);

        var locked = TorrentEngine.BuildEngineSettings(options with { Dht = false, Encryption = TorrentEncryptionMode.Require, BindAddress = "127.0.0.1", PortForwarding = true }, _bed.Root);
        Assert.Null(locked.DhtEndPoint);
        Assert.DoesNotContain(EncryptionType.PlainText, locked.AllowedEncryption);
        Assert.Equal(IPAddress.Loopback, Assert.Single(locked.ListenEndPoints.Values).Address);
        Assert.True(locked.AllowPortForwarding);

        var v6 = TorrentEngine.BuildEngineSettings(options with { BindAddress = "::1" }, _bed.Root);
        Assert.Null(v6.DhtEndPoint);
        Assert.Equal(AddressFamily.InterNetworkV6, Assert.Single(v6.ListenEndPoints.Values).AddressFamily);

        var privateTorrent = TorrentEngine.BuildTorrentSettings(options, isPrivate: true);
        Assert.False(privateTorrent.AllowDht);
        Assert.False(privateTorrent.AllowPeerExchange);
        Assert.True(TorrentEngine.BuildTorrentSettings(options, isPrivate: false).AllowPeerExchange);
        Assert.Equal(options.MaxConnectionsPerTorrent, privateTorrent.MaximumConnections);
    }

    [Fact]
    public void Settings_are_validated_and_stored()
    {
        Assert.Empty(TorrentEngineOptions.Default.Validate());
        var bad = TorrentEngineOptions.Default with
        {
            ListenPort = 80,
            BindAddress = "not-an-ip",
            MaxConnections = 0,
            MaxConnectionsPerTorrent = 100_000,
            DownloadLimitKBps = -1,
            UploadLimitKBps = long.MaxValue,
            SeedRatio = -2,
            SeedTimeMinutes = -5,
        };
        var errors = bad.Validate();
        Assert.Equal(
            ["listenPort", "bindAddress", "maxConnections", "maxConnectionsPerTorrent", "downloadLimitKBps", "uploadLimitKBps", "seedRatio", "seedTimeMinutes"],
            errors.Keys.ToArray());
        Assert.Contains("bindAddress", (TorrentEngineOptions.Default with { BindAddress = "239.1.2.3" }).Validate().Keys);
        Assert.Throws<ArgumentException>(() => _bed.Settings.Save(bad));

        var saved = TorrentEngineOptions.Default with { ListenPort = 50000, SeedRatio = null, SeedTimeMinutes = 90, Encryption = TorrentEncryptionMode.Allow };
        _bed.Settings.Save(saved);
        Assert.Equal(saved, new TorrentEngineSettings(_bed.Config).Current);
    }

    [Fact]
    public void Tracker_urls_are_redacted()
    {
        Assert.Equal("https://tracker.example.org", TrackerUrls.Redact("https://tracker.example.org/0123456789abcdef/announce"));
        Assert.Equal("http://tracker.example.org:2710", TrackerUrls.Redact("http://user:secret@tracker.example.org:2710/announce?passkey=0123"));
        Assert.Equal("udp://tracker.example.org:6969", TrackerUrls.Redact(new Uri("udp://tracker.example.org:6969/announce")));
        Assert.Equal("tracker", TrackerUrls.Redact("not a url"));
        var scrubbed = TrackerUrls.Scrub(
            "Announce to https://tracker.example.org/0123456789abcdef/announce failed (/0123456789abcdef/announce)",
            [new Uri("https://tracker.example.org/0123456789abcdef/announce")]);
        Assert.DoesNotContain("0123456789abcdef", scrubbed, StringComparison.Ordinal);
    }

    private BuiltInTorrentClient ClientFor(TorrentEngine engine) => new(engine, _bed.Settings, _bed.SavePaths);
}
