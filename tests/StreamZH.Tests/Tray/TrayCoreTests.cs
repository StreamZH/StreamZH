// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text;
using System.Text.Json;
using StreamZH.Core.Tray;
using StreamZH.Tray.Core;

namespace StreamZH.Tests.Tray;

/// <summary>The tray icon's logic without Windows: texts, state, notifications, files, command lines and HTTP calls.</summary>
public sealed class TrayCoreTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"streamzh-tray-{Guid.NewGuid():N}");

    public TrayCoreTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(0.0, "0.0 %")]
    [InlineData(0.473, "47.3 %")]
    [InlineData(0.47399, "47.3 %")]
    [InlineData(0.4735, "47.3 %")]
    [InlineData(0.9996, "99.9 %")]
    [InlineData(0.999999, "99.9 %")]
    [InlineData(1.0, "100.0 %")]
    [InlineData(0.001, "0.1 %")]
    [InlineData(0.0009, "0.0 %")]
    [InlineData(0.7, "70.0 %")]
    [InlineData(1.5, "100.0 %")]
    [InlineData(-0.2, "0.0 %")]
    [InlineData(double.NaN, "0.0 %")]
    public void Percent_has_one_decimal_and_never_rounds_up_to_done(double fraction, string expected) =>
        Assert.Equal(expected, StatusText.Percent(fraction));

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(0.2, "1 s")]
    [InlineData(45, "45 s")]
    [InlineData(59.4, "1 min 00 s")]
    [InlineData(60, "1 min 00 s")]
    [InlineData(725, "12 min 05 s")]
    [InlineData(3599, "59 min 59 s")]
    [InlineData(3600, "1 h 00 min")]
    [InlineData(3725, "1 h 02 min")]
    [InlineData(86399, "23 h 59 min")]
    [InlineData(86400, "1 d 00 h")]
    [InlineData(2 * 86400 + 3 * 3600 + 59, "2 d 03 h")]
    [InlineData(-5, "0 s")]
    [InlineData(double.PositiveInfinity, "0 s")]
    public void Duration_is_rounded_up_to_whole_seconds(double seconds, string expected) =>
        Assert.Equal(expected, StatusText.Duration(seconds));

    [Fact]
    public void Status_line_and_tooltip()
    {
        Assert.Equal("Running · 2 downloads · 47.3 % · 12 min 05 s", StatusText.Line(Running(Status(downloads: 2, progress: 0.4734, eta: 724.2))));
        Assert.Equal("Running · 1 download · 0.0 %", StatusText.Line(Running(Status(downloads: 1, progress: 0, eta: null))));
        Assert.Equal("Running · no downloads", StatusText.Line(Running(Status())));
        Assert.Equal("Paused", StatusText.Line(new TrayState(TrayStateKind.Paused, Status(paused: true))));
        Assert.Equal("Paused · 3 downloads · 12.5 %", StatusText.Line(new TrayState(TrayStateKind.Paused, Status(downloads: 3, progress: 0.125, eta: 99, paused: true))));
        Assert.Equal("Running · no downloads · 1 error · 2 warnings", StatusText.Line(Running(Status(warnings: 2, errors: 1))));
        Assert.Equal("Running · no downloads · 1 warning", StatusText.Line(Running(Status(warnings: 1))));
        Assert.Equal("Not running", StatusText.Line(new TrayState(TrayStateKind.NotRunning)));
        Assert.Equal("Starting…", StatusText.Line(new TrayState(TrayStateKind.Starting)));
        Assert.Equal("Stopping…", StatusText.Line(new TrayState(TrayStateKind.Stopping)));
        Assert.Equal("Not responding", StatusText.Line(new TrayState(TrayStateKind.NotResponding)));
        Assert.Equal("Running · no status (no access to tray.token)", StatusText.Line(new TrayState(TrayStateKind.NoAccess, TokenProblem: TokenProblem.NoAccess)));
        Assert.Contains("tray.token missing", StatusText.Line(new TrayState(TrayStateKind.NoAccess, TokenProblem: TokenProblem.Missing)), StringComparison.Ordinal);

        Assert.Equal("StreamZH · Not running", StatusText.Tooltip(new TrayState(TrayStateKind.NotRunning)));
        Assert.Equal("Home · Running · no downloads", StatusText.Tooltip(Running(Status() with { InstanceName = "Home" })));
        var longName = StatusText.Tooltip(Running(Status(downloads: 12345, progress: 0.5, eta: 100) with { InstanceName = new string('n', 200) }));
        Assert.Equal(StatusText.MaxTooltip, longName.Length);
        Assert.EndsWith("…", longName, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_colour()
    {
        Assert.Equal(StatusDot.Green, Running(Status()).Dot);
        Assert.Equal(StatusDot.Orange, Running(Status(warnings: 1)).Dot);
        Assert.Equal(StatusDot.Orange, Running(Status(errors: 1)).Dot);
        Assert.Equal(StatusDot.Orange, new TrayState(TrayStateKind.Paused, Status(paused: true)).Dot);
        Assert.Equal(StatusDot.Orange, new TrayState(TrayStateKind.Starting).Dot);
        Assert.Equal(StatusDot.Orange, new TrayState(TrayStateKind.NoAccess).Dot);
        Assert.Equal(StatusDot.Red, new TrayState(TrayStateKind.NotRunning).Dot);
        Assert.Equal(StatusDot.Red, new TrayState(TrayStateKind.NotResponding).Dot);
    }

    [Fact]
    public void State_from_the_process_and_the_answer()
    {
        var clock = new ManualClock();
        var resolver = new TrayStateResolver(clock);
        var ok = new ProbeResult(ProbeOutcome.Ok, Status());
        var silent = new ProbeResult(ProbeOutcome.Unreachable);

        Assert.Equal(TrayStateKind.NotRunning, resolver.Resolve(ProcessState.Stopped, null, TokenProblem.None).Kind);
        Assert.Equal(TrayStateKind.Starting, resolver.Resolve(ProcessState.StartPending, null, TokenProblem.None).Kind);
        Assert.Equal(TrayStateKind.Stopping, resolver.Resolve(ProcessState.StopPending, silent, TokenProblem.None).Kind);
        Assert.Equal(TrayStateKind.Running, resolver.Resolve(ProcessState.Running, ok, TokenProblem.None).Kind);
        Assert.Equal(TrayStateKind.Paused, resolver.Resolve(ProcessState.Running, new ProbeResult(ProbeOutcome.Ok, Status(paused: true)), TokenProblem.None).Kind);

        var refused = resolver.Resolve(ProcessState.Running, new ProbeResult(ProbeOutcome.Unauthorized), TokenProblem.NoAccess);
        Assert.Equal(TrayStateKind.NoAccess, refused.Kind);
        Assert.Equal(TokenProblem.NoAccess, refused.TokenProblem);

        // Running for Windows but not listening yet: starting, then not responding after the grace time.
        Assert.Equal(TrayStateKind.Starting, resolver.Resolve(ProcessState.Running, silent, TokenProblem.None).Kind);
        clock.Advance(TrayStateResolver.StartGrace - TimeSpan.FromSeconds(1));
        Assert.Equal(TrayStateKind.Starting, resolver.Resolve(ProcessState.Running, silent, TokenProblem.None).Kind);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TrayStateKind.NotResponding, resolver.Resolve(ProcessState.Running, silent, TokenProblem.None).Kind);

        // An answer resets it; a restart from the menu starts the grace time again.
        Assert.Equal(TrayStateKind.Running, resolver.Resolve(ProcessState.Running, ok, TokenProblem.None).Kind);
        clock.Advance(TimeSpan.FromMinutes(10));
        resolver.Restarting();
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TrayStateKind.Starting, resolver.Resolve(ProcessState.Running, silent, TokenProblem.None).Kind);
    }

    [Fact]
    public void Notifications_only_for_downloads_finished_while_watching()
    {
        var tracker = new NotificationTracker();
        Assert.Null(tracker.Query.Run);
        Assert.Null(tracker.Query.After);

        // First contact: what finished before is not announced.
        Assert.Empty(tracker.Take(Status(run: "a", last: 5, finished: [Finished(4), Finished(5)])));
        Assert.Equal("a", tracker.Query.Run);
        Assert.Equal(5, tracker.Query.After);

        Assert.Empty(tracker.Take(Status(run: "a", last: 5)));
        var fresh = tracker.Take(Status(run: "a", last: 7, finished: [Finished(5), Finished(7), Finished(6)]));
        Assert.Equal([6L, 7L], fresh.Select(f => f.Number));
        Assert.Equal("a", tracker.Query.Run);
        Assert.Equal(7, tracker.Query.After);

        // StreamZH restarted: its numbering starts again, all it reports is new.
        fresh = tracker.Take(Status(run: "b", last: 1, finished: [Finished(1)]));
        Assert.Single(fresh);
        Assert.Equal("b", tracker.Query.Run);
        Assert.Equal(1, tracker.Query.After);
    }

    [Fact]
    public void Notification_texts()
    {
        var single = NotificationTracker.Messages([Finished(1, "Movie.2024.1080p"), Finished(2, "Show.S01E01", failed: true, error: "Not enough repair blocks")]);
        Assert.Equal(2, single.Count);
        Assert.Equal(("Download finished", "Movie.2024.1080p", false), single[0]);
        Assert.Equal("Download failed", single[1].Title);
        Assert.Equal($"Show.S01E01{Environment.NewLine}Not enough repair blocks", single[1].Text);
        Assert.True(single[1].Error);

        var many = NotificationTracker.Messages([Finished(1), Finished(2), Finished(3), Finished(4, failed: true)]);
        Assert.Equal([("Downloads finished, some failed", "3 downloads finished, 1 failed.", true)], many);
        Assert.Equal("4 downloads finished.", NotificationTracker.Messages([Finished(1), Finished(2), Finished(3), Finished(4)])[0].Text);
        Assert.Equal(255, NotificationTracker.Messages([Finished(1, new string('x', 400))])[0].Text.Length);
    }

    [Fact]
    public void Data_folder_from_the_command_line()
    {
        var custom = Path.Combine(_data, "custom");
        Assert.Equal(Path.GetFullPath(custom), TrayFiles.DataDirectory(["--data", custom]));
        Assert.Equal(Path.GetFullPath(custom), TrayFiles.DataDirectory([$"--data={custom}"]));
        Assert.Equal(StreamZH.Core.AppPaths.Resolve().DataDirectory, TrayFiles.DataDirectory([]));
    }

    [Fact]
    public void Reads_address_and_token_from_the_data_folder()
    {
        var missing = TrayFiles.Load(_data);
        Assert.False(missing.EndpointKnown);
        Assert.Equal("http://127.0.0.1:9797", missing.Endpoint.Url);
        Assert.Equal("http://localhost:9797/", missing.Endpoint.DashboardUrl);
        Assert.Null(missing.Token);
        Assert.Equal(TokenProblem.Missing, missing.TokenProblem);
        Assert.Equal(Path.Combine(_data, "logs"), missing.LogDirectory);

        var endpoint = new TrayEndpoint("http://127.0.0.1:8080/streamzh/", "http://localhost:8080/streamzh/", "0.8.0", "windowsService", 42, @"D:\StreamZH\logs", DateTimeOffset.UnixEpoch);
        File.WriteAllText(Path.Combine(_data, TrayContract.EndpointFile), JsonSerializer.Serialize(endpoint, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(_data, TrayContract.TokenFile), "abc123\r\n");

        var loaded = TrayFiles.Load(_data);
        Assert.True(loaded.EndpointKnown);
        Assert.Equal("http://127.0.0.1:8080/streamzh", loaded.Endpoint.Url);
        Assert.Equal("windowsService", loaded.Endpoint.RunningAs);
        Assert.Equal(@"D:\StreamZH\logs", loaded.LogDirectory);
        Assert.Equal("abc123", loaded.Token);
        Assert.Equal(TokenProblem.None, loaded.TokenProblem);

        // Broken or foreign files fall back to the defaults.
        File.WriteAllText(Path.Combine(_data, TrayContract.EndpointFile), "{ not json");
        Assert.False(TrayFiles.Load(_data).EndpointKnown);
        File.WriteAllText(Path.Combine(_data, TrayContract.EndpointFile), """{ "url": "file:///etc/passwd" }""");
        Assert.False(TrayFiles.Load(_data).EndpointKnown);
        File.WriteAllText(Path.Combine(_data, TrayContract.TokenFile), "  ");
        Assert.Equal(TokenProblem.Missing, TrayFiles.Load(_data).TokenProblem);
    }

    [Fact]
    public void Unreadable_token_is_reported_as_no_access()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // root reads every file; the Windows ACL is set by StreamZH itself
        }

        var token = Path.Combine(_data, TrayContract.TokenFile);
        File.WriteAllText(token, "secret");
        File.SetUnixFileMode(token, UnixFileMode.None);
        Assert.Equal(TokenProblem.NoAccess, TrayFiles.Load(_data).TokenProblem);
    }

    [Theory]
    [InlineData(@"C:\Tools\StreamZH\StreamZH.Tray.exe", "\"C:\\Tools\\StreamZH\\StreamZH.Tray.exe\"")]
    [InlineData(@"D:\Data\", "\"D:\\Data\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    [InlineData("", "\"\"")]
    public void Quote_for_the_windows_command_line(string argument, string expected) =>
        Assert.Equal(expected, TrayCommandLine.Quote(argument));

    [Fact]
    public void Run_entry_passes_the_data_folder_only_when_it_is_not_the_default()
    {
        var exe = Path.Combine(_data, "StreamZH.Tray.exe");
        var defaultData = Path.Combine(_data, "default");
        Assert.Equal($"\"{exe}\"", TrayCommandLine.RunEntry(exe, null, defaultData));
        Assert.Equal($"\"{exe}\"", TrayCommandLine.RunEntry(exe, defaultData + Path.DirectorySeparatorChar, defaultData));
        var other = Path.Combine(_data, "other");
        Assert.Equal($"\"{exe}\" --data \"{other}\"", TrayCommandLine.RunEntry(exe, other, defaultData));
        Assert.Equal(string.Empty, TrayCommandLine.HostArguments(defaultData, defaultData));
        Assert.Equal($"--data \"{other}\"", TrayCommandLine.HostArguments(other, defaultData));
    }

    [Fact]
    public async Task Client_sends_the_token_and_reads_the_status()
    {
        var handler = new FakeHandler(request =>
        {
            Assert.Equal("abc", request.Headers.GetValues(TrayContract.Header).Single());
            return Json(Status(downloads: 2, progress: 0.5, eta: 60));
        });
        using var client = new TrayClient(new HttpClient(handler));
        var connection = Connection("http://127.0.0.1:9797/streamzh", "abc");

        var first = await client.StatusAsync(connection, null, null);
        Assert.Equal(ProbeOutcome.Ok, first.Outcome);
        Assert.Equal(2, first.Status!.Downloads);
        Assert.Equal("http://127.0.0.1:9797/streamzh/api/v1/tray/status", handler.Requests[0].RequestUri!.ToString());

        await client.StatusAsync(connection, "run 1", 7);
        Assert.Equal("http://127.0.0.1:9797/streamzh/api/v1/tray/status?run=run%201&after=7", handler.Requests[1].RequestUri!.AbsoluteUri);

        Assert.True(await client.PauseAsync(connection));
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.EndsWith("/api/v1/tray/pause", handler.Requests[2].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_tells_refused_unreachable_and_foreign_answers_apart()
    {
        var connection = Connection("http://127.0.0.1:9797", null);
        using var refused = new TrayClient(new HttpClient(new FakeHandler(r =>
        {
            Assert.False(r.Headers.Contains(TrayContract.Header));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        })));
        Assert.Equal(ProbeOutcome.Unauthorized, (await refused.StatusAsync(connection, null, null)).Outcome);
        Assert.False(await refused.ShutdownAsync(connection));

        using var down = new TrayClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("Connection refused"))));
        Assert.Equal(ProbeOutcome.Unreachable, (await down.StatusAsync(connection, null, null)).Outcome);
        Assert.False(await down.RestartAsync(connection));

        using var old = new TrayClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Equal(ProbeOutcome.Failed, (await old.StatusAsync(connection, null, null)).Outcome);

        using var html = new TrayClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "text/html") })));
        Assert.Equal(ProbeOutcome.Failed, (await html.StatusAsync(connection, null, null)).Outcome);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9797/", true)]
    [InlineData("https://localhost:9798/", true)]
    [InlineData("http://[::1]:9797/", true)]
    [InlineData("https://192.168.1.5:9798/", false)]
    [InlineData("https://example.com/", false)]
    public void Certificate_errors_are_only_ignored_on_loopback(string url, bool loopback) =>
        Assert.Equal(loopback, TrayClient.IsLoopback(new Uri(url)));

    private static TrayState Running(TrayStatus status) => new(TrayStateKind.Running, status);

    private static TrayStatus Status(
        int downloads = 0,
        double progress = 0,
        double? eta = null,
        bool paused = false,
        int warnings = 0,
        int errors = 0,
        string run = "run",
        long last = 0,
        IReadOnlyList<TrayFinishedDownload>? finished = null) =>
        new("0.8.0", "windowsService", "StreamZH", run, paused, null, downloads, 1000, (long)(progress * 1000), progress, 0, eta, warnings, errors, finished ?? [], last);

    private static TrayFinishedDownload Finished(long number, string? title = null, bool failed = false, string? error = null) =>
        new(number, (int)number, title ?? $"Download {number}", failed, error, DateTimeOffset.UnixEpoch);

    private TrayConnection Connection(string url, string? token) =>
        new(_data, TrayFiles.Default(_data) with { Url = url }, true, token, token is null ? TokenProblem.Missing : TokenProblem.None);

    private static HttpResponseMessage Json(TrayStatus status) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
