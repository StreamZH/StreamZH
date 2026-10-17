// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Notifications;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Connect;

public sealed class ConnectDispatcherTests : IDisposable
{
    private readonly LibraryFixture _library = new();
    private readonly InProcessEventBus _events = new();
    private readonly RecordingSender _discord = new("discord");
    private readonly RecordingSender _plex = new("plex");
    private readonly ConnectionService _service;
    private readonly ConnectDispatcher _dispatcher;
    private int _ongoingChanged;

    public ConnectDispatcherTests()
    {
        _service = new ConnectionService(
            _library.Connections, [_discord, _plex], _library.Config, _events, TimeProvider.System, NullLogger<ConnectionService>.Instance);
        _dispatcher = new ConnectDispatcher(
            _events,
            _service,
            _library.Connections,
            new ConnectEventFactory(_library.Movies, _library.Series),
            new ConnectOptions([TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]),
            TimeProvider.System,
            NullLogger<ConnectDispatcher>.Instance);
        _dispatcher.StartAsync(default).GetAwaiter().GetResult();
        _events.Subscribe<OngoingChanged>((_, _) =>
        {
            Interlocked.Increment(ref _ongoingChanged);
            return ValueTask.CompletedTask;
        });
    }

    private ConnectionDefinition Add(string implementation, ConnectionTriggers triggers, object settings, bool enable = true, string? name = null) =>
        _service.Create(new ConnectionInput(
            name ?? implementation,
            implementation,
            enable,
            triggers,
            JsonSerializer.SerializeToElement(settings).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));

    private MediaImported Imported(bool upgrade = false) => new(
        MediaType.Movie, _library.Movie.Id, null, [], "Dune", [Path.Combine(_library.MovieFolder, "Dune.mkv")], _library.MovieFolder, upgrade,
        "Dune.2021.2160p.UHD.BluRay.x265-GROUP", 9);

    [Fact]
    public async Task Events_reach_the_connections_whose_triggers_ask_for_them()
    {
        Add("discord", new ConnectionTriggers(OnGrab: true, OnImport: true), new { webHookUrl = "https://discord/x" });
        Add("plex", new ConnectionTriggers(OnImport: true, OnUpgrade: true, OnRename: true), new { host = "plex", authToken = "t" });

        await _events.PublishAsync(new ReleaseGrabbed(MediaType.Movie, _library.Movie.Id, null, "Dune.2021.1080p.WEB-DL-FLUX", "NZBgeek"));
        await _events.PublishAsync(Imported());
        await _events.PublishAsync(Imported(upgrade: true));
        await _events.PublishAsync(new MediaRenamed(MediaType.Movie, _library.Movie.Id, null, _library.MovieFolder));
        await _dispatcher.WhenIdleAsync();

        Assert.Equal([ConnectEventType.Grab, ConnectEventType.Import], _discord.Received.Select(e => e.Type));
        Assert.Equal([ConnectEventType.Import, ConnectEventType.Upgrade, ConnectEventType.Rename], _plex.Received.Select(e => e.Type));

        var grab = _discord.Received[0];
        Assert.Equal("Dune (2021)", grab.Media!.Display);
        Assert.Equal("WEBDL-1080p", grab.Release!.Quality);
        Assert.Equal("FLUX", grab.Release.ReleaseGroup);
        Assert.Equal("NZBgeek", grab.Release.Indexer);
        Assert.Equal("Bluray-2160p", _plex.Received[1].Release!.Quality);
        Assert.Equal(_library.MovieFolder, _plex.Received[2].Folder);
    }

    [Fact]
    public async Task Failed_downloads_trigger_on_download_failure_and_completed_ones_do_not()
    {
        Add("discord", new ConnectionTriggers(OnDownloadFailure: true), new { webHookUrl = "https://discord/x" });
        var episodes = _library.Episodes;
        var item = new DownloadItem
        {
            Id = 5,
            Title = "Breaking.Bad.S01E02.720p.HDTV.x264-CTU",
            Status = DownloadStatus.Failed,
            Error = "Not enough repair blocks",
            Link = new MediaLink(MediaType.Series, null, _library.Show.Id, [episodes[1].Id]),
        };

        await _events.PublishAsync(new DownloadFinished(item with { Status = DownloadStatus.Completed }));
        await _events.PublishAsync(new DownloadFinished(item));
        await _dispatcher.WhenIdleAsync();

        var failed = Assert.Single(_discord.Received);
        Assert.Equal(ConnectEventType.DownloadFailure, failed.Type);
        Assert.Equal("Not enough repair blocks", failed.Message);
        Assert.Equal("Breaking Bad (2008) · S01E02 Episode 2 · Not enough repair blocks", failed.Summary);
    }

    [Fact]
    public async Task Disabled_connections_and_unsupported_triggers_are_skipped()
    {
        Add("discord", new ConnectionTriggers(OnGrab: true), new { webHookUrl = "https://discord/x" }, enable: false);
        var plex = Add("plex", new ConnectionTriggers(OnGrab: true, OnImport: true), new { host = "plex", authToken = "t" });
        Assert.False(plex.Triggers.OnGrab);

        await _events.PublishAsync(new ReleaseGrabbed(MediaType.Movie, _library.Movie.Id, null, "Dune.2021.1080p", "x"));
        await _dispatcher.WhenIdleAsync();

        Assert.Empty(_discord.Received);
        Assert.Empty(_plex.Received);
    }

    [Fact]
    public async Task Retryable_errors_are_retried_and_a_success_counts()
    {
        var connection = Add("discord", new ConnectionTriggers(OnImport: true), new { webHookUrl = "https://discord/x" });
        _discord.Failures.Enqueue(new ConnectException("server error 502", retryable: true));
        _discord.Failures.Enqueue(new ConnectException("timed out", retryable: true));

        await _events.PublishAsync(Imported());
        await _dispatcher.WhenIdleAsync();

        Assert.Equal(3, _discord.Attempts);
        Assert.Single(_discord.Received);
        var status = _library.Connections.Find(connection.Id)!.Status;
        Assert.False(status.Failing);
        Assert.NotNull(status.LastSuccess);
    }

    [Fact]
    public async Task A_final_failure_is_stored_until_the_next_success()
    {
        var connection = Add("plex", new ConnectionTriggers(OnImport: true), new { host = "plex", authToken = "t" });
        _plex.Failures.Enqueue(new ConnectException("unauthorized"));

        await _events.PublishAsync(Imported());
        await _dispatcher.WhenIdleAsync();

        Assert.Equal(1, _plex.Attempts);
        var failing = _library.Connections.Find(connection.Id)!.Status;
        Assert.True(failing.Failing);
        Assert.Equal("unauthorized", failing.LastError);
        Assert.True(_ongoingChanged > 0);

        // Retries are used up after 1 + 2 tries.
        for (var i = 0; i < 3; i++)
        {
            _plex.Failures.Enqueue(new ConnectException("not reachable", retryable: true));
        }

        await _events.PublishAsync(Imported());
        await _dispatcher.WhenIdleAsync();
        Assert.Equal(4, _plex.Attempts);
        Assert.Equal("not reachable", _library.Connections.Find(connection.Id)!.Status.LastError);

        await _events.PublishAsync(Imported());
        await _dispatcher.WhenIdleAsync();
        Assert.False(_library.Connections.Find(connection.Id)!.Status.Failing);
    }

    [Fact]
    public async Task Health_warnings_only_reach_connections_that_include_them()
    {
        Add("discord", new ConnectionTriggers(OnHealthIssue: true, OnHealthRestored: true), new { webHookUrl = "https://discord/x" });
        Add("discord", new ConnectionTriggers(OnHealthIssue: true, IncludeHealthWarnings: true), new { webHookUrl = "https://discord/y" }, name: "With warnings");
        Assert.True(_dispatcher.WantsHealth());

        _dispatcher.HealthChanged(new ConnectHealth("no-indexer", "warn", "No indexer configured", null, null), restored: false);
        _dispatcher.HealthChanged(new ConnectHealth("root-missing-1", "error", "Root folder not found", "/media", null), restored: false);
        _dispatcher.HealthChanged(new ConnectHealth("root-missing-1", "error", "Root folder not found", "/media", null), restored: true);
        await _dispatcher.WhenIdleAsync();

        Assert.Equal(4, _discord.Received.Count);
        Assert.Equal(1, _discord.Received.Count(e => e.Health!.Key == "no-indexer"));
        Assert.Equal(2, _discord.Received.Count(e => e.Type == ConnectEventType.HealthIssue && e.Health!.Key == "root-missing-1"));
        Assert.Equal(ConnectEventType.HealthRestored, Assert.Single(_discord.Received, e => e.Type == ConnectEventType.HealthRestored).Type);
    }

    [Fact]
    public void Names_are_unique_and_validated()
    {
        Add("discord", new ConnectionTriggers(), new { webHookUrl = "https://discord/x" }, name: "Alerts");

        var duplicate = Assert.Throws<ValidationFailedException>(() => Add("discord", new ConnectionTriggers(), new { webHookUrl = "https://d" }, name: "alerts"));
        Assert.True(duplicate.Errors.ContainsKey("name"));

        var unknown = Assert.Throws<ValidationFailedException>(() => Add("carrier-pigeon", new ConnectionTriggers(), new { }));
        Assert.True(unknown.Errors.ContainsKey("implementation"));

        var missing = Assert.Throws<ValidationFailedException>(() => Add("discord", new ConnectionTriggers(), new { }, name: "Other"));
        Assert.Equal(["Webhook URL is required."], missing.Errors["webHookUrl"]);
    }

    public void Dispose()
    {
        _dispatcher.StopAsync(default).GetAwaiter().GetResult();
        _library.Dispose();
    }

    private sealed class RecordingSender(string implementation) : IConnectionSender
    {
        private int _attempts;

        public string Implementation => implementation;

        public ConcurrentQueue<Exception> Failures { get; } = new();

        public List<ConnectEvent> Received { get; } = [];

        public int Attempts => _attempts;

        public Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            lock (Received)
            {
                Received.Add(connectEvent);
            }

            return Task.CompletedTask;
        }

        public Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken) => Task.FromResult("ok");
    }
}
