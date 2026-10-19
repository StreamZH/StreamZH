// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.General;

namespace StreamZH.Tests.Host;

/// <summary>Host settings: read from the database before Kestrel starts, --urls wins, URL base end to end.</summary>
public sealed class HostStartupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"streamzh-host-start-{Guid.NewGuid():N}");
    private readonly ConfigRepository _config;

    public HostStartupTests()
    {
        Directory.CreateDirectory(_dir);
        var database = new Database(new AppPaths(_dir).DatabaseFile);
        database.Migrate();
        _config = new ConfigRepository(database);
    }

    [Fact]
    public void Explicit_urls_come_from_the_command_line_environment_or_a_kestrel_section()
    {
        Assert.Null(HostStartup.ExplicitUrls(new ConfigurationBuilder().Build()));
        Assert.Equal("http://localhost:1234", HostStartup.ExplicitUrls(Config(("urls", "http://localhost:1234"))));
        Assert.NotNull(HostStartup.ExplicitUrls(Config(("Kestrel:Endpoints:Http:Url", "http://*:5000"))));
        Assert.Null(HostStartup.ExplicitUrls(Config(("http_ports", "8080"))));
    }

    [Fact]
    public void Stored_host_settings_decide_the_addresses_unless_urls_are_given()
    {
        _config.Set(ConfigKeys.BindAddress, "127.0.0.1");
        _config.Set(ConfigKeys.Port, "9900");
        _config.Set(ConfigKeys.UrlBase, "/streamzh");

        var running = WebApplication.CreateBuilder().UseStreamZhHost(_config, null);
        Assert.Null(running.Override);
        Assert.Equal(["http://127.0.0.1:9900"], running.Addresses);
        Assert.Equal("/streamzh", running.Values.UrlBase);
        Assert.Equal(1, running.Run);

        var overridden = WebApplication.CreateBuilder(["--urls", "http://localhost:5555"]).UseStreamZhHost(_config, null);
        Assert.Equal("http://localhost:5555", overridden.Override);
        Assert.Equal(["http://localhost:5555"], overridden.Addresses);
        Assert.Equal("/streamzh", overridden.Values.UrlBase);
    }

    [Fact]
    public void Https_without_a_usable_certificate_is_left_out_with_a_problem()
    {
        _config.Set(ConfigKeys.EnableSsl, "true");
        _config.Set(ConfigKeys.SslCertPath, Path.Combine(_dir, "missing.pfx"));

        var running = WebApplication.CreateBuilder().UseStreamZhHost(_config, null);

        Assert.Equal(["http://*:9797"], running.Addresses);
        Assert.Contains("HTTPS is off", running.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void After_a_failed_bind_the_restart_uses_the_last_good_values()
    {
        _config.Set(ConfigKeys.Port, "9900");
        var lastGood = new HostValues("*", 9797, string.Empty, false, 9798, null, null);

        var running = WebApplication.CreateBuilder().UseStreamZhHost(_config, new RestartState(1, lastGood, true, "Could not listen on http://*:9900"));

        Assert.Equal(lastGood, running.Values);
        Assert.True(running.Fallback);
        Assert.True(running.IsRestart);
        Assert.Equal(lastGood, running.Previous);
        Assert.Equal("Could not listen on http://*:9900", running.Problem);
    }

    [Fact]
    public async Task Kestrel_listens_on_the_stored_address_and_port()
    {
        var port = FreePort();
        _config.Set(ConfigKeys.BindAddress, "127.0.0.1");
        _config.Set(ConfigKeys.Port, port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var builder = WebApplication.CreateBuilder();
        builder.UseStreamZhHost(_config, null);
        await using var app = builder.Build();
        app.MapGet("/", () => "hello");
        await app.StartAsync();

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Assert.Equal("hello", await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/")));
        await app.StopAsync();
    }

    [Fact]
    public async Task A_restart_returns_the_state_for_the_next_run_and_a_busy_port_falls_back()
    {
        var port = FreePort();
        _config.Set(ConfigKeys.BindAddress, "127.0.0.1");
        _config.Set(ConfigKeys.Port, port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var first = Build(null, out var running);
        var lifecycle = first.Services.GetRequiredService<AppLifecycle>();
        var run = first.RunUntilStoppedAsync(running, new MemoryStream());
        Assert.True(lifecycle.Restart("test"));
        Assert.False(lifecycle.Shutdown("test"));
        var next = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(next);
        Assert.False(next.UseLastGood);
        Assert.Equal(port, next.LastGood.Port);

        // The new port is taken: the next run gives up on it and starts again with the last good values.
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        _config.Set(ConfigKeys.Port, ((IPEndPoint)blocker.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var second = Build(next, out running);
        var fallback = await second.RunUntilStoppedAsync(running, new MemoryStream()).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(fallback);
        Assert.True(fallback.UseLastGood);
        Assert.Equal(port, fallback.LastGood.Port);
        Assert.Contains("Could not listen", fallback.BindError, StringComparison.Ordinal);

        var third = Build(fallback, out running);
        Assert.True(running.Fallback);
        Assert.Equal(port, running.Values.Port);
        await third.DisposeAsync();
    }

    private WebApplication Build(RestartState? previous, out RunningHost running)
    {
        var builder = WebApplication.CreateBuilder();
        running = builder.UseStreamZhHost(_config, previous);
        builder.Services.AddSingleton<AppLifecycle>();
        return builder.Build();
    }

    [Fact]
    public void Index_page_gets_the_url_base()
    {
        const string html = "<!doctype html><html><head><meta charset=\"UTF-8\" /></head><body></body></html>";
        Assert.Contains("<head>\n    <base href=\"/streamzh/\" />", IndexPage.Inject(html, "/streamzh"), StringComparison.Ordinal);
        Assert.Contains("<base href=\"/\" />", IndexPage.Inject(html, string.Empty), StringComparison.Ordinal);
        Assert.Equal(1, CountOf(IndexPage.Inject(IndexPage.Inject(html, "/a"), "/b"), "<base "));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("streamzh", null)]
    [InlineData("/media/streamzh/", null)]
    [InlineData("/api", "used by StreamZH")]
    [InlineData("/Hub/x", "used by StreamZH")]
    [InlineData("/stream zh", "Use letters")]
    [InlineData("/a/../b", "Use letters")]
    public void Url_base_validation(string value, string? problem)
    {
        var result = HostSettings.ValidateUrlBase(value);
        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(problem, result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Url_base_works_end_to_end_for_pages_assets_api_and_signalr()
    {
        using var factory = new StreamZhFactory();
        var paths = new AppPaths(factory.DataDirectory);
        paths.EnsureCreated();
        var database = new Database(paths.DatabaseFile);
        database.Migrate();
        new ConfigRepository(database).Set(ConfigKeys.UrlBase, "/streamzh");

        var webRoot = Path.Combine(_dir, "wwwroot");
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html><head><title>StreamZH</title></head><body><script src=\"./assets/app.js\"></script></body></html>");
        File.WriteAllText(Path.Combine(webRoot, "assets", "app.js"), "console.log('app')");

        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(new WebRootFilter(webRoot))));
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/streamzh/", root.Headers.Location!.OriginalString);

        var deep = await client.GetAsync("/movies?x=1");
        Assert.Equal("/streamzh/movies?x=1", deep.Headers.Location!.OriginalString);

        foreach (var page in new[] { "/streamzh/", "/streamzh/index.html", "/streamzh/settings/general" })
        {
            var response = await client.GetAsync(page);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("<base href=\"/streamzh/\" />", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal("console.log('app')", await client.GetStringAsync("/streamzh/assets/app.js"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/streamzh/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/streamzh/api/v1/nothing")).StatusCode);

        var setup = await client.PostAsJsonAsync("/streamzh/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        // 0.12 (security review S1-10): the sign-in cookie belongs to the URL base only.
        Assert.Contains("path=/streamzh", setup.Headers.GetValues("Set-Cookie").First(), StringComparison.OrdinalIgnoreCase);
        var cookie = setup.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);

        var settings = await client.GetFromJsonAsync<GeneralSettingsResource>("/streamzh/api/v1/settings/general");
        Assert.Equal("/streamzh", settings!.UrlBase);
        Assert.False(settings.RestartRequired);

        var negotiate = await client.PostAsync("/streamzh/hub/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        Assert.Contains("connectionToken", await negotiate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, string.Empty, StringComparison.Ordinal).Length) / part.Length;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Serves the web root from a test folder instead of the built frontend.</summary>
    private sealed class WebRootFilter(string webRoot) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.ApplicationServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider = new PhysicalFileProvider(webRoot);
            next(app);
        };
    }
}
