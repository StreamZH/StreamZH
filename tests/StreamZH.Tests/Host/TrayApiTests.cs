// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Tray;
using StreamZH.Host.General;
using StreamZH.Host.Status;
using StreamZH.Host.Tray;
using StreamZH.Library.Downloads;

namespace StreamZH.Tests.Host;

/// <summary>The tray token: only from this computer, only for /api/v1/tray, and the tray endpoints themselves.</summary>
public sealed class TrayApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly StreamZhFactory _factory = new();
    private readonly RemoteAddress _remote = new();
    private readonly RecordingLifecycle _lifecycle = new();
    private readonly WebApplicationFactory<Program> _app;

    public TrayApiTests()
    {
        _app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IStartupFilter>(_remote);
            s.Replace(ServiceDescriptor.Singleton<AppLifecycle>(_lifecycle));
        }));
    }

    public void Dispose()
    {
        _app.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Start_writes_the_token_and_the_address_into_the_data_folder()
    {
        await SetupAsync();
        var access = _app.Services.GetRequiredService<TrayAccess>();

        Assert.Equal(64, access.Token.Length);
        Assert.Equal(access.Token, File.ReadAllText(Path.Combine(_factory.DataDirectory, TrayContract.TokenFile)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(access.TokenPath));
        }

        var endpoint = JsonSerializer.Deserialize<TrayEndpoint>(File.ReadAllText(access.EndpointPath), Json)!;
        Assert.Equal("http://127.0.0.1:9797", endpoint.Url);
        Assert.Equal("http://localhost:9797/", endpoint.DashboardUrl);
        Assert.Equal(Environment.ProcessId, endpoint.ProcessId);
        Assert.Equal(Path.Combine(_factory.DataDirectory, "logs"), endpoint.LogDirectory);

        // Every start gets a new token; an old file is replaced.
        TrayAccess.WriteToken(access.TokenPath, "next");
        Assert.Equal("next", File.ReadAllText(access.TokenPath));
    }

    [Fact]
    public async Task Status_with_the_token_from_this_computer()
    {
        await SetupAsync();
        _remote.Address = IPAddress.Loopback;
        var tray = TrayClient(Token);

        var status = await tray.GetFromJsonAsync<TrayStatus>("/api/v1/tray/status", Json);
        Assert.NotNull(status);
        Assert.False(status.Paused);
        Assert.Equal(0, status.Downloads);
        Assert.Equal("StreamZH", status.InstanceName);
        Assert.Equal(_app.Services.GetRequiredService<TrayAccess>().Run, status.Run);
        Assert.Empty(status.Finished);

        // IPv6 loopback, and a connection to the computer's own LAN address from that same address.
        _remote.Address = IPAddress.IPv6Loopback;
        Assert.Equal(HttpStatusCode.OK, (await TrayClient(Token).GetAsync("/api/v1/tray/status")).StatusCode);
        _remote.Address = IPAddress.Parse("192.168.1.5");
        _remote.Local = IPAddress.Parse("192.168.1.5");
        Assert.Equal(HttpStatusCode.OK, (await TrayClient(Token).GetAsync("/api/v1/tray/status")).StatusCode);
    }

    [Fact]
    public async Task The_tray_client_reads_the_files_and_talks_to_streamzh()
    {
        await SetupAsync();
        _remote.Address = IPAddress.Loopback;
        var connection = StreamZH.Tray.Core.TrayFiles.Load(_factory.DataDirectory);
        Assert.True(connection.EndpointKnown);
        Assert.Equal(StreamZH.Tray.Core.TokenProblem.None, connection.TokenProblem);

        using var client = new StreamZH.Tray.Core.TrayClient(_app.CreateClient());
        var tracker = new StreamZH.Tray.Core.NotificationTracker();
        var probe = await client.StatusAsync(connection, tracker.Query.Run, tracker.Query.After);
        Assert.Equal(StreamZH.Tray.Core.ProbeOutcome.Ok, probe.Outcome);
        Assert.Empty(tracker.Take(probe.Status!));

        _app.Services.GetRequiredService<TrayAccess>().Remember(new DownloadItem { Id = 3, Title = "Dune.Part.Two.2024", Status = DownloadStatus.Completed });
        probe = await client.StatusAsync(connection, tracker.Query.Run, tracker.Query.After);
        Assert.Equal("Dune.Part.Two.2024", Assert.Single(tracker.Take(probe.Status!)).Title);

        Assert.True(await client.PauseAsync(connection));
        probe = await client.StatusAsync(connection, tracker.Query.Run, tracker.Query.After);
        var state = new StreamZH.Tray.Core.TrayStateResolver(TimeProvider.System).Resolve(StreamZH.Tray.Core.ProcessState.Running, probe, connection.TokenProblem);
        Assert.StartsWith("Paused", StreamZH.Tray.Core.StatusText.Line(state), StringComparison.Ordinal); // a new instance has warnings (no indexer, no server)
        Assert.True(await client.ResumeAsync(connection));

        var stale = connection with { Token = "old token" };
        Assert.Equal(StreamZH.Tray.Core.ProbeOutcome.Unauthorized, (await client.StatusAsync(stale, null, null)).Outcome);
    }

    [Fact]
    public async Task Wrong_or_missing_token_is_refused()
    {
        await SetupAsync();
        _remote.Address = IPAddress.Loopback;

        Assert.Equal(HttpStatusCode.Unauthorized, (await TrayClient("0123456789").GetAsync("/api/v1/tray/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TrayClient(Token + "x").PostAsync("/api/v1/tray/shutdown", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateClient().GetAsync("/api/v1/tray/status")).StatusCode);
        Assert.Empty(_lifecycle.Calls);
    }

    [Fact]
    public async Task Token_from_another_computer_or_through_a_proxy_is_refused()
    {
        await SetupAsync();

        // Even on the LAN with "Authentication: disabled for local addresses" off, the token only counts from here.
        _remote.Address = IPAddress.Parse("192.168.1.20");
        Assert.Equal(HttpStatusCode.Unauthorized, (await TrayClient(Token).GetAsync("/api/v1/tray/status")).StatusCode);
        _remote.Address = IPAddress.Parse("203.0.113.7");
        Assert.Equal(HttpStatusCode.Unauthorized, (await TrayClient(Token).PostAsync("/api/v1/tray/pause", null)).StatusCode);

        // A reverse proxy on this computer: loopback, but forwarded.
        _remote.Address = IPAddress.Loopback;
        var proxied = TrayClient(Token);
        proxied.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");
        Assert.Equal(HttpStatusCode.Unauthorized, (await proxied.GetAsync("/api/v1/tray/status")).StatusCode);
        var forwarded = TrayClient(Token);
        forwarded.DefaultRequestHeaders.Add("Forwarded", "for=127.0.0.1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forwarded.GetAsync("/api/v1/tray/status")).StatusCode);

        Assert.False(_app.Services.GetRequiredService<DownloadEngine>().Status().Paused);
    }

    [Theory]
    [InlineData("GET", "/api/v1/system/status")]
    [InlineData("GET", "/api/v1/status/ongoing")]
    [InlineData("GET", "/api/v1/download")]
    [InlineData("GET", "/api/v1/settings/general")]
    [InlineData("POST", "/api/v1/download/pause-all")]
    [InlineData("POST", "/api/v1/system/restart")]
    [InlineData("POST", "/api/v1/system/shutdown")]
    [InlineData("GET", "/api/v1/auth/state")]
    public async Task The_token_opens_no_other_endpoint(string method, string path)
    {
        await SetupAsync();
        _remote.Address = IPAddress.Loopback;

        var response = await TrayClient(Token).SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        if (path == "/api/v1/auth/state")
        {
            var state = await response.Content.ReadFromJsonAsync<StreamZH.Host.Api.AuthState>(Json);
            Assert.False(state!.Authenticated);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Empty(_lifecycle.Calls);
    }

    [Fact]
    public async Task Pause_resume_restart_and_shutdown()
    {
        await SetupAsync();
        _remote.Address = IPAddress.Loopback;
        var tray = TrayClient(Token);
        var engine = _app.Services.GetRequiredService<DownloadEngine>();

        Assert.Equal(HttpStatusCode.NoContent, (await tray.PostAsync("/api/v1/tray/pause", null)).StatusCode);
        Assert.True(engine.Status().Paused);
        Assert.True((await tray.GetFromJsonAsync<TrayStatus>("/api/v1/tray/status", Json))!.Paused);

        Assert.Equal(HttpStatusCode.NoContent, (await tray.PostAsync("/api/v1/tray/resume", null)).StatusCode);
        Assert.False(engine.Status().Paused);

        Assert.Equal(HttpStatusCode.Accepted, (await tray.PostAsync("/api/v1/tray/restart", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await tray.PostAsync("/api/v1/tray/shutdown", null)).StatusCode);
        Assert.Equal(["restart:tray icon"], _lifecycle.Calls);
    }

    [Fact]
    public async Task Owner_may_use_the_tray_endpoints_without_the_token()
    {
        var owner = await SetupAsync();
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/tray/status")).StatusCode);

        var withKey = _app.CreateClient();
        withKey.DefaultRequestHeaders.Add("X-Api-Key", _app.Services.GetRequiredService<StreamZH.Data.ConfigRepository>().Get(StreamZH.Data.ConfigKeys.ApiKey));
        Assert.Equal(HttpStatusCode.OK, (await withKey.GetAsync("/api/v1/tray/status")).StatusCode);
    }

    [Fact]
    public async Task Token_works_with_basic_authentication_too()
    {
        var owner = await SetupAsync();
        (await owner.PutAsJsonAsync("/api/v1/settings/general", new { authenticationMethod = "basic" })).EnsureSuccessStatusCode();
        _remote.Address = IPAddress.Loopback;

        Assert.Equal(HttpStatusCode.OK, (await TrayClient(Token).GetAsync("/api/v1/tray/status")).StatusCode);
        var refused = await TrayClient("wrong").GetAsync("/api/v1/tray/status");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TrayClient(Token).GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task Finished_downloads_are_numbered_per_run()
    {
        await SetupAsync();
        _remote.Address = IPAddress.Loopback;
        var access = _app.Services.GetRequiredService<TrayAccess>();
        var tray = TrayClient(Token);

        access.Remember(new DownloadItem { Id = 7, Title = "Movie.2024.1080p", Status = DownloadStatus.Completed });
        access.Remember(new DownloadItem { Id = 8, Title = "Show.S01E01", Status = DownloadStatus.Failed, Error = "Not enough repair blocks" });

        var first = (await tray.GetFromJsonAsync<TrayStatus>("/api/v1/tray/status", Json))!;
        Assert.Equal(2, first.LastFinished);
        Assert.Equal([1L, 2L], first.Finished.Select(f => f.Number));
        Assert.True(first.Finished[1].Failed);
        Assert.Equal("Not enough repair blocks", first.Finished[1].Error);

        var after = (await tray.GetFromJsonAsync<TrayStatus>($"/api/v1/tray/status?run={first.Run}&after=1", Json))!;
        Assert.Equal([8], after.Finished.Select(f => f.Id));

        // Another run (StreamZH restarted since): everything it remembers.
        var otherRun = (await tray.GetFromJsonAsync<TrayStatus>("/api/v1/tray/status?run=old&after=2", Json))!;
        Assert.Equal(2, otherRun.Finished.Count);

        for (var i = 0; i < TrayAccess.MaxFinished + 5; i++)
        {
            access.Remember(new DownloadItem { Id = 100 + i, Title = "x", Status = DownloadStatus.Completed });
        }

        var (items, last) = access.Finished(first.Run, 0);
        Assert.Equal(TrayAccess.MaxFinished, items.Count);
        Assert.Equal(TrayAccess.MaxFinished + 7, last);
    }

    [Fact]
    public void Summary_counts_the_whole_queue_and_the_time_left_at_the_current_speed()
    {
        DownloadItem Item(int id, DownloadStatus status, long total, long done) =>
            new() { Id = id, Title = $"d{id}", Status = status, TotalBytes = total, DownloadedBytes = done };

        var items = new[]
        {
            Item(1, DownloadStatus.Downloading, 1000, 400),
            Item(2, DownloadStatus.Queued, 1000, 0),
            Item(3, DownloadStatus.Extracting, 2000, 2000),
        };
        var notices = new[]
        {
            new OngoingNotice("a", NoticeLevels.Warn, "w", null, null, false),
            new OngoingNotice("b", NoticeLevels.Error, "e", null, null, false),
            new OngoingNotice("c", NoticeLevels.Info, "i", null, null, false),
            new OngoingNotice("d", NoticeLevels.Warn, "w2", null, null, false),
        };

        var status = TrayEndpoints.Summarize(items, new DownloadQueueStatus(100, false, null, 0, null), notices, "Home", "run", [], 0);
        Assert.Equal(3, status.Downloads);
        Assert.Equal(4000, status.TotalBytes);
        Assert.Equal(2400, status.DownloadedBytes);
        Assert.Equal(0.6, status.Progress, 6);
        Assert.Equal(16, status.EtaSeconds); // 1600 bytes left at 100 B/s
        Assert.Equal(2, status.Warnings);
        Assert.Equal(1, status.Errors);

        var paused = TrayEndpoints.Summarize(items, new DownloadQueueStatus(0, true, null, 0, "Paused by the scheduler."), [], "Home", "run", [], 0);
        Assert.True(paused.Paused);
        Assert.Null(paused.EtaSeconds);
        Assert.Equal("Paused by the scheduler.", paused.PausedReason);

        var empty = TrayEndpoints.Summarize([], new DownloadQueueStatus(0, false, null, 0, null), [], "Home", "run", [], 0);
        Assert.Equal(0, empty.Progress);
        Assert.Null(empty.EtaSeconds);
    }

    [Theory]
    [InlineData(new string[0], "*", 9797, "", "http://127.0.0.1:9797", "http://localhost:9797/")]
    [InlineData(new string[0], "*", 9797, "/streamzh", "http://127.0.0.1:9797/streamzh", "http://localhost:9797/streamzh/")]
    [InlineData(new[] { "http://[::]:8080" }, "*", 9797, "", "http://127.0.0.1:8080", "http://localhost:8080/")]
    [InlineData(new[] { "https://0.0.0.0:9798", "http://0.0.0.0:9797" }, "*", 9797, "/media", "http://127.0.0.1:9797/media", "http://localhost:9797/media/")]
    [InlineData(new[] { "https://localhost:5001" }, "*", 9797, "", "https://127.0.0.1:5001", "https://localhost:5001/")]
    [InlineData(new[] { "http://192.168.1.5:9797" }, "192.168.1.5", 9797, "", "http://192.168.1.5:9797", "http://192.168.1.5:9797/")]
    [InlineData(new[] { "http://[::1]:9797" }, "::1", 9797, "", "http://[::1]:9797", "http://[::1]:9797/")]
    [InlineData(new[] { "http://+:80" }, "*", 9797, "", "http://127.0.0.1:80", "http://localhost:80/")]
    [InlineData(new string[0], "::", 9000, "", "http://127.0.0.1:9000", "http://localhost:9000/")]
    public void Loopback_address_from_where_kestrel_listens(string[] addresses, string bind, int port, string urlBase, string url, string dashboard)
    {
        var values = new HostValues(bind, port, urlBase, false, 9798, null, null);
        Assert.Equal((url, dashboard), TrayAccess.LoopbackUrls(addresses, values));
    }

    private string Token => _app.Services.GetRequiredService<TrayAccess>().Token;

    private HttpClient TrayClient(string token)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Add(TrayContract.Header, token);
        return client;
    }

    private async Task<HttpClient> SetupAsync()
    {
        var client = _app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Sets the addresses the test server leaves empty.</summary>
    private sealed class RemoteAddress : IStartupFilter
    {
        public IPAddress Address { get; set; } = IPAddress.Parse("203.0.113.1");

        public IPAddress? Local { get; set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, Func<Task> nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = Address;
                context.Connection.LocalIpAddress = Local;
                return nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class RecordingLifecycle()
        : AppLifecycle(new FakeLifetime(), NullLogger<AppLifecycle>.Instance)
    {
        private int _stopping;

        public List<string> Calls { get; } = [];

        public override bool Restart(string requestedBy) => Record($"restart:{requestedBy}");

        public override bool Shutdown(string requestedBy) => Record($"shutdown:{requestedBy}");

        private bool Record(string call)
        {
            if (Interlocked.Exchange(ref _stopping, 1) == 1)
            {
                return false;
            }

            Calls.Add(call);
            return true;
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
