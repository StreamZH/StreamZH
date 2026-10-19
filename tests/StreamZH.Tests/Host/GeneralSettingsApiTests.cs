// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Host.Api;
using StreamZH.Host.General;
using StreamZH.Host.Logging;

namespace StreamZH.Tests.Host;

/// <summary>Settings → General and UI, restart, update check, log files.</summary>
public sealed class GeneralSettingsApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();

    [Fact]
    public async Task Defaults_keep_the_behaviour_of_before()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var settings = (await owner.GetFromJsonAsync<GeneralSettingsResource>("/api/v1/settings/general"))!;

        Assert.Equal("*", settings.BindAddress);
        Assert.Equal(9797, settings.Port);
        Assert.Equal(string.Empty, settings.UrlBase);
        Assert.False(settings.EnableSsl);
        Assert.Equal("forms", settings.AuthenticationMethod);
        Assert.Equal("enabled", settings.AuthenticationRequired);
        Assert.Equal("enabled", settings.CertificateValidation);
        Assert.False(settings.ProxyEnabled);
        Assert.True(settings.ProxyBypassLocalAddresses);
        Assert.Equal("Information", settings.LogLevel);
        Assert.Equal(1, settings.LogSizeLimit);
        Assert.Equal(5, settings.LogFileCount);
        Assert.Equal(7, settings.BackupInterval);
        Assert.Equal(28, settings.BackupRetention);
        Assert.False(settings.RestartRequired);
    }

    [Fact]
    public async Task Host_settings_are_validated_and_need_a_restart()
    {
        var owner = await _factory.CreateOwnerClientAsync();

        var invalid = await owner.PutAsJsonAsync("/api/v1/settings/general", new
        {
            bindAddress = "my-server",
            port = 70000,
            urlBase = "/api",
            applicationUrl = "streamzh.example.com",
            enableSsl = true,
            sslPort = 9797,
            authenticationMethod = "none",
            logLevel = "Loud",
            logSizeLimit = 0,
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var text = await invalid.Content.ReadAsStringAsync();
        foreach (var field in new[] { "bindAddress", "port", "urlBase", "applicationUrl", "sslCertPath", "authenticationMethod", "logLevel", "logSizeLimit" })
        {
            Assert.Contains($"\"{field}\"", text, StringComparison.Ordinal);
        }

        var saved = await owner.PutAsJsonAsync("/api/v1/settings/general", new { port = 9900, urlBase = "streamzh/", applicationUrl = "https://media.example.com/streamzh/" });
        var settings = (await saved.Content.ReadFromJsonAsync<GeneralSettingsResource>())!;
        Assert.Equal(9900, settings.Port);
        Assert.Equal("/streamzh", settings.UrlBase);
        Assert.Equal("https://media.example.com/streamzh", settings.ApplicationUrl);
        Assert.True(settings.RestartRequired);
        Assert.Contains("restart-required", await owner.GetStringAsync("/api/v1/status/ongoing"), StringComparison.Ordinal);

        // Back to the running values: no restart needed.
        settings = (await (await owner.PutAsJsonAsync("/api/v1/settings/general", new { port = 9797, urlBase = string.Empty })).Content.ReadFromJsonAsync<GeneralSettingsResource>())!;
        Assert.False(settings.RestartRequired);
    }

    [Fact]
    public async Task Logging_settings_apply_at_once()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        await owner.PutAsJsonAsync("/api/v1/settings/general", new { logLevel = "Debug", consoleLogLevel = "Warning", logSizeLimit = 5, logFileCount = 10 });

        Assert.Equal(LogLevel.Debug, _factory.Services.GetRequiredService<LogLevelSwitch>().Minimum);
        Assert.Equal(LogLevel.Warning, _factory.Services.GetRequiredService<ConsoleLogLevelSwitch>().Minimum);
        var files = _factory.Services.GetRequiredService<LogFileOptions>();
        Assert.Equal(5 * 1024 * 1024, files.MaxBytes);
        Assert.Equal(10, files.FileCount);
    }

    [Fact]
    public async Task Restart_and_shutdown_stop_the_host_once()
    {
        var lifecycle = new RecordingLifecycle();
        using var app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<AppLifecycle>(lifecycle))));
        var owner = app.CreateClient();
        (await owner.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync("/api/v1/system/restart", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync("/api/v1/system/shutdown", null)).StatusCode);
        Assert.Equal(["restart:johndoe"], lifecycle.Calls);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsync("/api/v1/system/restart", null)).StatusCode);
    }

    [Fact]
    public async Task Ui_settings_are_shared_by_all_browsers()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var defaults = (await owner.GetFromJsonAsync<UiSettingsResource>("/api/v1/settings/ui"))!;
        Assert.Equal("locale", defaults.ShortDateFormat);
        Assert.Equal("locale", defaults.TimeFormat);
        Assert.True(defaults.ShowRelativeDates);
        Assert.Contains("DD.MM.YYYY", defaults.ShortDateFormats);

        var invalid = await owner.PutAsJsonAsync("/api/v1/settings/ui", new { shortDateFormat = "yy", timeFormat = "hh" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var saved = await owner.PutAsJsonAsync("/api/v1/settings/ui", new { shortDateFormat = "DD.MM.YYYY", timeFormat = "HH:mm", showRelativeDates = false });
        var ui = (await saved.Content.ReadFromJsonAsync<UiSettingsResource>())!;
        Assert.Equal("DD.MM.YYYY", ui.ShortDateFormat);
        Assert.Equal("locale", ui.LongDateFormat);
        Assert.Equal("HH:mm", ui.TimeFormat);
        Assert.False(ui.ShowRelativeDates);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/settings/ui")).StatusCode);
    }

    [Fact]
    public async Task Update_check_handles_a_private_repository_and_new_releases()
    {
        var github = new FakeGitHub();
        using var app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient(UpdateChecker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => github)));
        var owner = app.CreateClient();
        (await owner.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();

        var before = (await owner.GetFromJsonAsync<UpdateInfo>("/api/v1/system/updates"))!;
        Assert.Null(before.LastCheck.Checked);
        Assert.Equal("manual", before.Mechanism);

        var info = (await (await owner.PostAsync("/api/v1/system/updates/check", null)).Content.ReadFromJsonAsync<UpdateInfo>())!;
        Assert.False(info.LastCheck.UpdateAvailable);
        Assert.Contains("No update information yet", info.LastCheck.Message, StringComparison.Ordinal);

        github.Release = """{"tag_name":"v99.1.0","html_url":"https://github.com/streamzh/streamzh/releases/tag/v99.1.0","published_at":"2027-01-01T00:00:00Z"}""";
        info = (await (await owner.PostAsync("/api/v1/system/updates/check", null)).Content.ReadFromJsonAsync<UpdateInfo>())!;
        Assert.True(info.LastCheck.UpdateAvailable);
        Assert.Equal("99.1.0", info.LastCheck.LatestVersion);
        Assert.Equal("api.github.com", github.LastHost);

        // A release whose link leaves github.com is not offered (the link becomes the "Download" button).
        foreach (var link in new[] { "https://github.com.evil.example/x", "http://github.com/streamzh/streamzh/releases/tag/v99.2.0", "https://evil.example/StreamZH.exe" })
        {
            github.Release = $$"""{"tag_name":"v99.2.0","html_url":"{{link}}","published_at":"2027-01-01T00:00:00Z"}""";
            info = (await (await owner.PostAsync("/api/v1/system/updates/check", null)).Content.ReadFromJsonAsync<UpdateInfo>())!;
            Assert.False(info.LastCheck.UpdateAvailable);
            Assert.Null(info.LastCheck.ReleaseUrl);
            Assert.Contains("unexpected link", info.LastCheck.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Update_check_validates_the_certificate_even_when_validation_is_disabled()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        (await owner.PutAsJsonAsync("/api/v1/settings/general", new { certificateValidation = "disabled" })).EnsureSuccessStatusCode();

        var handler = _factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(UpdateChecker.HttpClientName);
        while (handler is DelegatingHandler { InnerHandler: { } inner })
        {
            handler = inner;
        }

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        var callback = sockets.SslOptions.RemoteCertificateValidationCallback!;
        Assert.False(callback(new object(), null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(callback(new object(), null, null, System.Net.Security.SslPolicyErrors.None));
        Assert.IsType<SettingsWebProxy>(sockets.Proxy);
    }

    [Theory]
    [InlineData("0.7.0", "0.6.2", true)]
    [InlineData("0.6.2", "0.6.2", false)]
    [InlineData("0.6.10", "0.6.9", true)]
    [InlineData("1.0.0-beta.1", "0.9.0", true)]
    [InlineData("0.5", "0.6.0", false)]
    public void Version_comparison(string candidate, string current, bool newer) => Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public async Task System_info_shows_runtime_paths_disk_space_and_health()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var info = (await owner.GetFromJsonAsync<SystemInfo>("/api/v1/system/info"))!;

        Assert.Equal("console", info.RuntimeMode);
        Assert.Equal(_factory.DataDirectory, info.DataDirectory);
        Assert.True(info.DatabaseSize > 0);
        Assert.Equal("GPL-3.0-only", info.License);
        Assert.Contains(info.DiskSpace, d => d.Label == "Data folder" && d.FreeBytes > 0);
        Assert.Contains(info.Health, n => n.Key == "no-indexer");
    }

    [Fact]
    public async Task Log_viewer_filters_by_level_and_text_and_manages_files()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        await owner.PostAsJsonAsync("/api/v1/auth/login", new { username = "johndoe", password = "wrong password" });

        var warnings = (await owner.GetFromJsonAsync<LogTail>("/api/v1/system/logs?lines=50&level=Warn"))!;
        Assert.NotEmpty(warnings.Entries);
        Assert.All(warnings.Entries, e => Assert.Contains(e.Level, new[] { "Warn", "Error", "Fatal" }));

        var search = (await owner.GetFromJsonAsync<LogTail>("/api/v1/system/logs?search=failed%20LOGIN"))!;
        Assert.Single(search.Entries);
        Assert.Equal("LoginRequest", search.Entries[0].Logger);

        var files = (await owner.GetFromJsonAsync<List<LogFileInfo>>("/api/v1/system/logs/files"))!;
        Assert.Equal("streamzh.txt", files[0].Name);
        Assert.True(files[0].Current);

        var download = await owner.GetStringAsync("/api/v1/system/logs/files/streamzh.txt");
        Assert.Contains("Failed login", download, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/system/logs/files/..%2Fstreamzh.db")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/system/logs/files/streamzh.7.txt")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/v1/system/logs/files/streamzh.txt")).StatusCode);
        Assert.DoesNotContain(
            (await owner.GetFromJsonAsync<LogTail>("/api/v1/system/logs"))!.Entries,
            e => e.Message.Contains("Failed login", StringComparison.Ordinal));
    }

    [Fact]
    public void Log_files_rotate_with_the_configured_size_and_count()
    {
        var directory = Path.Combine(_factory.DataDirectory, "rotation");
        var options = new LogFileOptions { SizeLimitMb = 1, FileCount = 3 };
        using var provider = new FileLoggerProvider(directory, new LogLevelSwitch(LogLevel.Information), options);
        var logger = provider.CreateLogger("Test");
        var filler = new string('x', 10_000);
        for (var i = 0; i < 600; i++)
        {
            logger.LogInformation("{Filler}", filler);
        }

        Assert.Equal(["streamzh.txt", "streamzh.1.txt", "streamzh.2.txt", "streamzh.3.txt"], provider.Files().Select(f => f.Name));

        options.FileCount = 1;
        for (var i = 0; i < 120; i++)
        {
            logger.LogInformation("{Filler}", filler);
        }

        Assert.Equal(["streamzh.txt", "streamzh.1.txt"], provider.Files().Select(f => f.Name));
    }

    [Fact]
    public void Log_entries_keep_stack_traces_together()
    {
        var entries = FileLoggerProvider.Parse([
            "2026-10-17 12:00:00.000|Info|A|first",
            "2026-10-17 12:00:01.000|Error|B|second",
            "System.Exception: boom",
            "   at X.Y()",
        ]).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal("Error", entries[1].Level);
        Assert.Equal("System.Exception: boom" + Environment.NewLine + "   at X.Y()", entries[1].Details);
    }

    public void Dispose() => _factory.Dispose();

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

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public string? Release { get; set; }

        public string? LastHost { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastHost = request.RequestUri!.Host;
            return Task.FromResult(Release is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
