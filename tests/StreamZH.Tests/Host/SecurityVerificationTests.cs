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
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;
using StreamZH.Host.Security;

namespace StreamZH.Tests.Host;

/// <summary>
/// 0.12 verification review (V-03 … V-09, V-11): each test reproduces the reviewer's exploit against the fixed build.
/// </summary>
public sealed class SecurityVerificationTests : IDisposable
{
    private const string Lan = "192.168.1.20";

    private readonly StreamZhFactory _factory = new();
    private readonly RemoteAddress _remote = new();
    private readonly WebApplicationFactory<Program> _app;

    public SecurityVerificationTests()
    {
        _app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(_remote)));
    }

    public void Dispose()
    {
        _app.Dispose();
        _factory.Dispose();
    }

    // ---- V-03: setup without a code only by this computer's own names ----

    [Fact]
    public async Task Setup_from_this_computer_with_a_foreign_host_name_needs_the_code()
    {
        // The reviewer's DNS rebinding: a page on rebind.attacker.example, now resolving to 127.0.0.1, creates the owner.
        var rebound = _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://rebind.attacker.example:6871") });
        Assert.True((await rebound.GetFromJsonAsync<AuthState>("/api/v1/auth/state"))!.SetupCodeRequired);
        var attack = await Send(rebound, HttpMethod.Post, "/api/v1/auth/setup", JsonContent.Create(new { username = "attacker", password = "attacker password" }),
            ("Origin", "http://rebind.attacker.example:6871"), ("Sec-Fetch-Site", "same-origin"));
        Assert.Equal(HttpStatusCode.BadRequest, attack.StatusCode);
        Assert.Contains("setupCode", await attack.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // localhost, 127.0.0.1 and this computer's own name need no code.
        foreach (var host in new[] { "http://127.0.0.1:9797", $"http://{Dns.GetHostName().ToLowerInvariant()}:9797", "http://localhost" })
        {
            var local = _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(host) });
            Assert.False((await local.GetFromJsonAsync<AuthState>("/api/v1/auth/state"))!.SetupCodeRequired);
        }

        var owner = _app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).StatusCode);
    }

    // ---- V-04 / V-11: host settings are the owner's; Sec-Fetch-Site decides ----

    [Fact]
    public async Task Admins_cannot_change_the_application_url_or_where_streamzh_listens()
    {
        var owner = await OwnerAsync();
        var admin = await AdminAsync();

        // The owner bound StreamZH to this computer; the reviewer's Admin opened it to the network.
        await SaveAsync(owner, new { bindAddress = "127.0.0.1" });
        foreach (var (field, change) in new (string, object)[]
        {
            ("applicationUrl", new { applicationUrl = "https://attacker.example" }),
            ("bindAddress", new { bindAddress = "*", port = 6872 }),
            ("port", new { port = 6872 }),
            ("urlBase", new { urlBase = "/other" }),
            ("enableSsl", new { enableSsl = true }),
            ("sslCertPath", new { sslCertPath = "/tmp/attacker.pfx" }),
            ("sslCertPassword", new { sslCertPassword = "x" }),
        })
        {
            var response = await admin.PutAsJsonAsync("/api/v1/settings/general", change);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
            Assert.True(errors.TryGetProperty(field, out var error), $"{field}: {errors}");
            Assert.Equal(GeneralSettingsEndpoints.OwnerOnlyMessage, error[0].GetString());
        }

        // A whole host section sent back unchanged, with a new instance name, is fine for an Admin.
        var current = (await admin.GetFromJsonAsync<GeneralSettingsResource>("/api/v1/settings/general"))!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/general", new
        {
            instanceName = "Media",
            current.BindAddress,
            current.Port,
            current.UrlBase,
            current.ApplicationUrl,
            current.EnableSsl,
            current.SslPort,
            current.SslCertPath,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/settings/general", new { applicationUrl = "https://media.example.org" })).StatusCode);
    }

    [Fact]
    public async Task A_cross_site_request_is_refused_even_when_its_origin_is_the_application_url()
    {
        var owner = await OwnerAsync();
        await SaveAsync(owner, new { applicationUrl = "https://media.example.org", authenticationRequired = "disabledForLocalAddresses" });
        _remote.Address = IPAddress.Parse(Lan);
        var victim = Browserless();

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(victim, HttpMethod.Post, "/api/v1/system/updates/check", null, ("Sec-Fetch-Site", "cross-site"), ("Origin", "https://media.example.org"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(victim, HttpMethod.Post, "/api/v1/system/backup", null, ("Sec-Fetch-Site", "same-site"), ("Origin", "https://media.example.org"))).StatusCode);

        // Browsers without Sec-Fetch-Site: the Origin still counts.
        Assert.True((await Send(victim, HttpMethod.Post, "/api/v1/system/backup", null, ("Origin", "https://media.example.org"))).IsSuccessStatusCode);
        Assert.True((await Send(victim, HttpMethod.Post, "/api/v1/system/backup", null, ("Sec-Fetch-Site", "same-origin"), ("Origin", "https://media.example.org"))).IsSuccessStatusCode);
    }

    // ---- V-05: local names are not trusted by default ----

    [Fact]
    public async Task Local_names_any_device_can_answer_for_are_not_trusted_unless_allowed()
    {
        var owner = await OwnerAsync();
        await SaveAsync(owner, new { authenticationRequired = "disabledForLocalAddresses" });
        _remote.Address = IPAddress.Parse(Lan);
        var lan = _app.CreateClient();

        foreach (var host in new[] { "EVIL.LOCAL", "nas", "x.lan", "x.home.arpa" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync($"http://{host}:9797/api/v1/system/status")).StatusCode);
        }

        foreach (var host in new[] { "localhost", "app.localhost", "192.168.1.5:9797", "[fd00::1]:9797", Dns.GetHostName().ToLowerInvariant() })
        {
            Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync($"http://{host}/api/v1/system/status")).StatusCode);
        }

        await SaveAsync(owner, new { allowedHostNames = "nas, *.lan" });
        Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("http://nas:9797/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("http://x.lan/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync("http://evil.local/api/v1/system/status")).StatusCode);
    }

    // ---- V-06: a broken Host header ----

    /// <summary>
    /// "Host: xn--nas" made <c>Request.Host</c> throw in the security headers (500 and a stack trace per request). The
    /// test server cannot even send such a header, so the check is called the way the pipeline calls it.
    /// </summary>
    [Theory]
    [InlineData("xn--nas", false)]
    [InlineData("xn--nas:9797", false)]
    [InlineData("[::1", false)]
    [InlineData("a b", false)]
    [InlineData("nas:9797", true)]
    [InlineData("[::1]:9797", true)]
    [InlineData("xn--bcher-kva.example", true)]
    [InlineData("", true)]
    public async Task A_host_header_asp_net_cannot_read_is_refused_without_an_exception(string host, bool readable)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Host = host;
        context.Response.Body = new MemoryStream();

        Assert.Equal(readable, HostAccess.HasReadableHost(context));
        var reached = false;
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        HostAccess.UseHostHeaderCheck(app);
        app.Run(_ => { reached = true; return Task.CompletedTask; });
        await app.Build()(context);
        Assert.Equal(readable, reached);
        Assert.Equal(readable ? 200 : 400, context.Response.StatusCode);

        // The helpers the rest of the pipeline uses never throw either.
        _ = HostAccess.HostName(context);
        _ = StreamZH.Host.Security.SecurityHeaders.ContentSecurityPolicy(HostAccess.HostName(context));
    }

    [Theory]
    [InlineData("Example.COM:8080", "example.com", 8080)]
    [InlineData("[::1]:9797", "::1", 9797)]
    [InlineData("[::1]", "::1", null)]
    [InlineData("::1", "::1", null)]
    [InlineData("nas.", "nas", null)]
    [InlineData("xn--nas", "xn--nas", null)]
    [InlineData("host:99999", "", null)]
    [InlineData("a b", "", null)]
    [InlineData("[::1", "", null)]
    [InlineData("", "", null)]
    public void Host_headers_are_parsed_without_throwing(string header, string name, int? port) =>
        Assert.Equal((name, port), HostAccess.ParseHostHeader(header));

    // ---- V-07: owner lockout and the throttle reset ----

    [Fact]
    public async Task Guessing_from_one_network_does_not_lock_the_owner_out_of_another_or_a_signed_in_browser()
    {
        await OwnerAsync();
        _remote.Address = IPAddress.Parse("198.51.100.7");
        for (var i = 0; i < LoginThrottle.AccountLimit; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(_app.CreateClient(), StreamZhFactory.OwnerName, $"guess {i}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(_app.CreateClient(), StreamZhFactory.OwnerName, StreamZhFactory.OwnerPassword)).StatusCode);

        // The owner at home is not locked out.
        _remote.Address = IPAddress.Parse("203.0.113.40");
        var home = _app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Login(home, StreamZhFactory.OwnerName, StreamZhFactory.OwnerPassword)).StatusCode);

        // A browser signed in to the account signs in again even from the attacker's network, and its typos do not count.
        _remote.Address = IPAddress.Parse("198.51.100.8");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(home, StreamZhFactory.OwnerName, "typo")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(home, StreamZhFactory.OwnerName, StreamZhFactory.OwnerPassword)).StatusCode);
    }

    [Fact]
    public void The_throttle_is_bounded_without_resetting_an_account_under_attack()
    {
        var time = new ManualTime();
        var throttle = new LoginThrottle(time);
        for (var i = 0; i < LoginThrottle.AccountLimit; i++)
        {
            throttle.Failed("owner", "198.51.100.7");
        }

        // The reviewer's reset: thousands of other names and networks. The account under attack is used again and again.
        for (var i = 0; i < LoginThrottle.MaxKeys; i++)
        {
            throttle.Failed($"user{i}", $"2001:db8:{i / 1000:x}:{i % 1000:x}::/64");
            if (i % 500 == 0)
            {
                Assert.NotNull(throttle.RetryAfter("owner", "198.51.100.7"));
                throttle.Failed("owner", "198.51.100.7");
            }
        }

        Assert.True(throttle.Count <= LoginThrottle.MaxKeys + 3);
        Assert.NotNull(throttle.RetryAfter("owner", "198.51.100.7"));

        // Over all networks: the account waits after GlobalAccountLimit failures, except on a network it signed in from.
        var spread = new LoginThrottle(time);
        spread.Succeeded("owner", "203.0.113.40");
        for (var i = 0; i < LoginThrottle.GlobalAccountLimit; i++)
        {
            spread.Failed("owner", $"2001:db8:{i:x}::/64");
        }

        Assert.NotNull(spread.RetryAfter("owner", "2001:db8:ffff::/64"));
        Assert.Null(spread.RetryAfter("owner", "203.0.113.40"));
        time.Advance(LoginThrottle.Window);
        Assert.Null(spread.RetryAfter("owner", "2001:db8:ffff::/64"));
    }

    // ---- V-08: which header names the client ----

    [Fact]
    public void A_client_written_x_forwarded_for_does_not_win_over_the_proxy_x_real_ip()
    {
        var trusted = NetworkList.Parse("10.0.0.9");
        HttpContext Request(params (string Name, string Value)[] headers)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.9");
            foreach (var (name, value) in headers)
            {
                context.Request.Headers[name] = value;
            }

            return context;
        }

        // A proxy that sets only X-Real-IP and passes the client's own X-Forwarded-For on (the reviewer's case).
        var spoofed = Request(("X-Real-IP", "198.51.100.7"), ("X-Forwarded-For", "203.0.113.99"));
        Assert.Equal(ProxyForwarding.ClientSource.Conflict, ProxyForwarding.Apply(spoofed, trusted));
        Assert.Equal(IPAddress.Parse("10.0.0.9"), spoofed.Connection.RemoteIpAddress);

        var realIp = Request(("X-Real-IP", "198.51.100.7"), ("X-Forwarded-For", "203.0.113.99"));
        ProxyForwarding.Apply(realIp, trusted, AccessSettings.ClientIpHeaderRealIp);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), realIp.Connection.RemoteIpAddress);

        var forwardedFor = Request(("X-Real-IP", "203.0.113.99"), ("X-Forwarded-For", "203.0.113.1, 198.51.100.7"));
        ProxyForwarding.Apply(forwardedFor, trusted, AccessSettings.ClientIpHeaderForwardedFor);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), forwardedFor.Connection.RemoteIpAddress);

        var cloudflare = Request(("CF-Connecting-IP", "198.51.100.7"), ("X-Forwarded-For", "203.0.113.99"));
        ProxyForwarding.Apply(cloudflare, trusted, AccessSettings.ClientIpHeaderCloudflare);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), cloudflare.Connection.RemoteIpAddress);

        // Auto with both headers agreeing (nginx with both set), or only one of them: that one.
        var agreeing = Request(("X-Real-IP", "198.51.100.7"), ("X-Forwarded-For", "203.0.113.99, 198.51.100.7"));
        Assert.Equal(ProxyForwarding.ClientSource.Header, ProxyForwarding.Apply(agreeing, trusted));
        Assert.Equal(IPAddress.Parse("198.51.100.7"), agreeing.Connection.RemoteIpAddress);
        var onlyRealIp = Request(("X-Real-IP", "198.51.100.7"));
        ProxyForwarding.Apply(onlyRealIp, trusted);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), onlyRealIp.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task The_client_ip_header_is_an_owner_setting()
    {
        var owner = await OwnerAsync();
        var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/settings/general", new { clientIpHeader = "x-real-ip" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/settings/general", new { clientIpHeader = "x-client" })).StatusCode);
        await SaveAsync(owner, new { clientIpHeader = "x-real-ip" });
        Assert.Equal("x-real-ip", (await owner.GetFromJsonAsync<GeneralSettingsResource>("/api/v1/settings/general"))!.ClientIpHeader);
    }

    // ---- V-09: cookies from before 0.12 ----

    [Fact]
    public void A_cookie_from_before_the_update_is_replaced_and_its_copies_stop_working()
    {
        using var bed = new SessionBed();
        var issued = DateTimeOffset.UtcNow.AddDays(-3);
        var expires = issued.AddDays(30);

        var session = bed.Store.ReplaceLegacy(1, issued, expires, 0, persistent: true);
        Assert.NotNull(session);

        // Requests that were already on their way get the same session for a moment ...
        Assert.Equal(session, bed.Store.ReplaceLegacy(1, issued, expires, 0, persistent: true));

        // ... then a copy of the old cookie is refused, also after a restart.
        bed.Time.Advance(SessionStore.LegacyGrace);
        Assert.Null(bed.Store.ReplaceLegacy(1, issued, expires, 0, persistent: true));
        Assert.Null(new SessionStore(bed.Sessions, bed.Time).ReplaceLegacy(1, issued, expires, 0, persistent: true));

        // Another old cookie of the same account (another browser) gets its own session.
        Assert.NotNull(bed.Store.ReplaceLegacy(1, issued.AddHours(1), expires, 0, persistent: true));
        Assert.True(bed.Store.IsActive(session, 1));
    }

    private async Task<HttpClient> OwnerAsync()
    {
        var client = _app.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<HttpClient> AdminAsync()
    {
        _factory.Services.GetRequiredService<UserRepository>().Create("admin", _factory.Services.GetRequiredService<PasswordService>().Hash("admin password 1"), UserRole.Admin);
        var client = _app.CreateClient();
        (await Login(client, "admin", "admin password 1")).EnsureSuccessStatusCode();
        return client;
    }

    private HttpClient Browserless()
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Remove(CrossSiteProtection.Header);
        return client;
    }

    private static async Task SaveAsync(HttpClient client, object change)
    {
        var response = await client.PutAsJsonAsync("/api/v1/settings/general", change);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, HttpContent? content = null, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string username, string password) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { username, password, rememberMe = false });

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class SessionBed : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-sessions-{Guid.NewGuid():N}.db");

        public SessionBed()
        {
            var database = new Database(_file);
            database.Migrate();
            Sessions = new SessionRepository(database);
            Store = new SessionStore(Sessions, Time);
        }

        public ManualTime Time { get; } = new();

        public SessionRepository Sessions { get; }

        public SessionStore Store { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>Sets the remote address the test server leaves empty (null: this computer).</summary>
    private sealed class RemoteAddress : IStartupFilter
    {
        public IPAddress? Address { get; set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, Func<Task> nextMiddleware) =>
            {
                if (Address is not null)
                {
                    context.Connection.RemoteIpAddress = Address;
                }

                return nextMiddleware();
            });
            next(app);
        };
    }
}
