// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StreamZH.Core.Tray;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;
using StreamZH.Host.Hubs;
using StreamZH.Host.Security;

namespace StreamZH.Tests.Host;

/// <summary>
/// 0.12 security fixes (review S1): each test reproduces a reviewer's exploit and shows it is refused now, and that the
/// web app, scripts with the API key and the tray keep working.
/// </summary>
public sealed class SecurityApiTests : IDisposable
{
    private const string Lan = "192.168.1.20";

    private readonly StreamZhFactory _factory = new();
    private readonly RemoteAddress _remote = new();
    private readonly WebApplicationFactory<Program> _app;

    public SecurityApiTests()
    {
        _app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(_remote)));
    }

    // ---- S1-01 / S1-07: cross-site requests ----

    [Fact]
    public async Task Other_web_pages_cannot_change_anything_through_the_local_network_access()
    {
        var owner = await OwnerAsync();
        await SaveAsync(owner, new { authenticationRequired = "disabledForLocalAddresses" });
        _remote.Address = IPAddress.Parse(Lan);
        var victim = Browserless();

        // Reads still work without sign-in from the LAN.
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/v1/system/status")).StatusCode);

        // The reviewer's exploits: bodyless POSTs, the raw-body restore and the multipart NZB upload from a foreign page.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(victim, HttpMethod.Post, "/api/v1/system/restart")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(victim, HttpMethod.Post, "/api/v1/system/shutdown", null, ("Sec-Fetch-Site", "cross-site"), ("Origin", "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(victim, HttpMethod.Post, "/api/v1/system/backup", null, ("Sec-Fetch-Site", "same-site"), ("Origin", "http://localhost:8080"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(victim, HttpMethod.Post, "/api/v1/system/backup/restore?name=x.zip", new StringContent("PK", Encoding.UTF8, "text/plain"), ("Origin", "null"))).StatusCode);
        using (var form = new MultipartFormDataContent { { new ByteArrayContent("<nzb/>"u8.ToArray()), "file", "x.nzb" } })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(victim, HttpMethod.Post, "/api/v1/download", form, ("Sec-Fetch-Site", "cross-site"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(victim, HttpMethod.Delete, "/api/v1/system/backup/restore")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(victim, HttpMethod.Put, "/api/v1/settings/ui", JsonContent.Create(new { timeFormat = "HH:mm" }), ("Origin", "http://evil.example"))).StatusCode);

        // StreamZH's own pages: the browser says same-origin (or none), the Origin is this host, or the web app's header.
        Assert.True((await Send(victim, HttpMethod.Post, "/api/v1/system/backup", null, ("Sec-Fetch-Site", "same-origin"))).IsSuccessStatusCode);
        Assert.True((await Send(victim, HttpMethod.Post, "/api/v1/system/backup", null, ("Origin", "http://localhost"))).IsSuccessStatusCode);
        Assert.True((await Send(victim, HttpMethod.Post, "/api/v1/system/backup", null, (CrossSiteProtection.Header, "1"))).IsSuccessStatusCode);

        // The restore takes application/zip only (a type other pages cannot send without CORS).
        Assert.Equal(
            HttpStatusCode.UnsupportedMediaType,
            (await Send(victim, HttpMethod.Post, "/api/v1/system/backup/restore?name=x.zip", new StringContent("PK", Encoding.UTF8, "text/plain"), (CrossSiteProtection.Header, "1"))).StatusCode);
    }

    [Fact]
    public async Task Cookie_basic_and_external_sign_ins_are_protected_too_but_the_api_key_is_not_affected()
    {
        // Cookie (S1-07: a sibling site or another port on the same host can send it, SameSite does not stop that).
        var owner = await OwnerAsync();
        owner.DefaultRequestHeaders.Remove(CrossSiteProtection.Header);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(owner, HttpMethod.Post, "/api/v1/system/backup", null, ("Sec-Fetch-Site", "same-site"), ("Origin", "http://media.localhost"))).StatusCode);
        Assert.True((await Send(owner, HttpMethod.Post, "/api/v1/system/backup", null, (CrossSiteProtection.Header, "1"))).IsSuccessStatusCode);

        // API key scripts send no Origin and no browser headers.
        var script = Browserless();
        script.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await script.PutAsJsonAsync("/api/v1/settings/ui", new { timeFormat = "HH:mm" })).StatusCode);

        // Basic.
        await SaveAsync(owner, new { authenticationMethod = "basic" }, header: true);
        var basic = Browserless();
        basic.DefaultRequestHeaders.Authorization = Basic(StreamZhFactory.OwnerName, StreamZhFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.OK, (await basic.GetAsync("/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(basic, HttpMethod.Post, "/api/v1/system/backup", null, ("Origin", "http://evil.example"))).StatusCode);
        Assert.True((await Send(basic, HttpMethod.Post, "/api/v1/system/backup", null, (CrossSiteProtection.Header, "1"))).IsSuccessStatusCode);

        // External (a reverse proxy's single sign-on cookie is ambient as well).
        await SaveAsync(basic, new { authenticationMethod = "external" }, header: true);
        _remote.Address = IPAddress.Parse("10.0.0.2");
        var proxied = Browserless();
        Assert.Equal(HttpStatusCode.OK, (await proxied.GetAsync("/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(proxied, HttpMethod.Post, "/api/v1/system/backup", null, ("Origin", "https://evil.example"))).StatusCode);
        Assert.True((await Send(proxied, HttpMethod.Post, "/api/v1/system/backup", null, (CrossSiteProtection.Header, "1"))).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Live_connection_refuses_foreign_origins_and_takes_the_api_key_in_the_address()
    {
        var owner = await OwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/hub/negotiate?negotiateVersion=1", null)).StatusCode);

        owner.DefaultRequestHeaders.Remove(CrossSiteProtection.Header);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(owner, HttpMethod.Post, "/hub/negotiate?negotiateVersion=1", null, ("Origin", "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(owner, HttpMethod.Get, "/hub?id=abc", null, ("Origin", "https://evil.example"))).StatusCode);

        // S1-04: ?apikey= only for /hub (WebSocket clients cannot send headers), nowhere else.
        var script = Browserless();
        Assert.Equal(HttpStatusCode.OK, (await script.PostAsync($"/hub/negotiate?negotiateVersion=1&apikey={_factory.ApiKey}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync($"/api/v1/system/status?apikey={_factory.ApiKey}")).StatusCode);
        script.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/api/v1/system/status")).StatusCode);
    }

    // ---- S1-01: DNS rebinding ----

    [Fact]
    public async Task Sign_in_free_access_only_for_host_names_an_attacker_cannot_point_at_streamzh()
    {
        var owner = await OwnerAsync();
        await SaveAsync(owner, new { authenticationRequired = "disabledForLocalAddresses" });
        _remote.Address = IPAddress.Parse(Lan);
        var lan = _app.CreateClient();

        foreach (var host in new[] { "localhost", "192.168.1.5:9797", "nas:9797", "nas.local", "[fd00::1]:9797", "203.0.113.7" })
        {
            Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync($"http://{host}/api/v1/system/status")).StatusCode);
        }

        // The rebinding attack: the attacker's name now points at StreamZH's address.
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync("http://rebind.attacker.example:9797/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync("http://streamzh.example.com/api/v1/system/status")).StatusCode);

        await SaveAsync(owner, new { allowedHostNames = "streamzh.example.com, *.home.example.net", applicationUrl = "https://media.example.org" });
        Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("http://streamzh.example.com/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("http://tv.home.example.net/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("http://media.example.org/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync("http://home.example.net.attacker.example/api/v1/system/status")).StatusCode);

        var settings = await owner.GetFromJsonAsync<GeneralSettingsResource>("/api/v1/settings/general");
        Assert.Equal("streamzh.example.com, *.home.example.net", settings!.AllowedHostNames);
        var invalid = await owner.PutAsJsonAsync("/api/v1/settings/general", new { allowedHostNames = "http://x.example/" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("allowedHostNames", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // External mode as well.
        await SaveAsync(owner, new { authenticationMethod = "external" });
        _remote.Address = IPAddress.Parse("10.0.0.2");
        Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("http://localhost/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync("http://rebind.attacker.example/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task External_trusts_only_the_configured_proxies_and_can_require_the_user_header()
    {
        var owner = await OwnerAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/settings/general", new { trustedProxies = "proxy.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/settings/general", new { externalUserHeader = "Remote User" })).StatusCode);
        await SaveAsync(owner, new { authenticationMethod = "external", trustedProxies = "10.0.0.2", externalUserHeader = "Remote-User" });

        _remote.Address = IPAddress.Parse("10.0.0.3");
        var other = _app.CreateClient();
        other.DefaultRequestHeaders.Add("Remote-User", "alice");
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/system/status")).StatusCode);

        _remote.Address = IPAddress.Parse("10.0.0.2");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateClient().GetAsync("/api/v1/system/status")).StatusCode);
        var proxied = _app.CreateClient();
        proxied.DefaultRequestHeaders.Add("Remote-User", "alice");
        Assert.Equal(HttpStatusCode.OK, (await proxied.GetAsync("/api/v1/system/status")).StatusCode);
        Assert.Equal("alice", (await proxied.GetFromJsonAsync<AuthState>("/api/v1/auth/state"))!.User!.Username);

        var settings = await proxied.GetFromJsonAsync<GeneralSettingsResource>("/api/v1/settings/general");
        Assert.Equal("10.0.0.2/32", settings!.TrustedProxies);
        Assert.Equal("Remote-User", settings.ExternalUserHeader);
    }

    [Fact]
    public async Task Only_the_owner_changes_who_gets_in_without_signing_in()
    {
        var owner = await OwnerAsync();
        await SaveAsync(owner, new { trustedProxies = "10.0.0.2" });
        _app.Services.GetRequiredService<UserRepository>().Create("janedoe", _app.Services.GetRequiredService<PasswordService>().Hash("long enough password"), UserRole.Admin);
        var admin = _app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Login(admin, "janedoe", "long enough password")).StatusCode);

        foreach (var change in new object[]
        {
            new { allowedHostNames = "attacker.example" },
            new { trustedProxies = "0.0.0.0/0" },
            new { trustedLocalNetworks = "0.0.0.0/0" },
            new { externalUserHeader = "X-User" },
        })
        {
            var refused = await admin.PutAsJsonAsync("/api/v1/settings/general", change);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        // Unchanged values (a whole section saved) are fine; the API key (an Admin since 0.12) is refused too.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/general", new { trustedProxies = "10.0.0.2", allowedHostNames = "" })).StatusCode);
        var script = _app.CreateClient();
        script.DefaultRequestHeaders.Add("X-Api-Key", _factory.ApiKey);
        Assert.Equal(HttpStatusCode.BadRequest, (await script.PutAsJsonAsync("/api/v1/settings/general", new { trustedLocalNetworks = "0.0.0.0/0" })).StatusCode);
        Assert.Equal("10.0.0.2/32", (await owner.GetFromJsonAsync<GeneralSettingsResource>("/api/v1/settings/general"))!.TrustedProxies);
    }

    // ---- S1-06: trusted local networks ----

    [Fact]
    public async Task Trusted_local_networks_replace_the_private_ranges()
    {
        var owner = await OwnerAsync();
        await SaveAsync(owner, new { authenticationRequired = "disabledForLocalAddresses", trustedLocalNetworks = "192.168.1.0/24" });

        _remote.Address = IPAddress.Parse(Lan);
        Assert.Equal(HttpStatusCode.OK, (await _app.CreateClient().GetAsync("/api/v1/system/status")).StatusCode);
        _remote.Address = IPAddress.Parse("10.0.0.5");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateClient().GetAsync("/api/v1/system/status")).StatusCode);
        _remote.Address = IPAddress.Parse("172.17.0.1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateClient().GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Theory]
    [InlineData("0::/system.slice/docker-1234.scope", true)]
    [InlineData("12:pids:/kubepods/besteffort/pod1/abc", true)]
    [InlineData("0::/user.slice/user-1000.slice/libpod-abc.scope", true)]
    [InlineData("0::/init.scope", false)]
    public void Container_cgroups(string cgroup, bool container) => Assert.Equal(container, ContainerDetection.IsContainerCgroup(cgroup));

    [Theory]
    [InlineData("192.168.1.5/24", "192.168.1.0/24")]
    [InlineData("10.0.0.1", "10.0.0.1/32")]
    [InlineData("fd00::1/8", "fd00::/8")]
    [InlineData("::1, 127.0.0.0/8", "::1/128, 127.0.0.0/8")]
    public void Network_lists(string text, string normal)
    {
        var list = NetworkList.Parse(text, out var problem);
        Assert.Null(problem);
        Assert.Equal(normal, list.Format());
    }

    // ---- S1-05 / S1-09: throttling ----

    [Fact]
    public async Task Basic_failures_are_throttled_per_account_and_repeated_headers_are_not_counted()
    {
        var owner = await OwnerAsync();
        await SaveAsync(owner, new { authenticationMethod = "basic" });

        // The browser repeats the same wrong credentials with every request: answered from memory, not counted.
        var repeating = Browserless();
        repeating.DefaultRequestHeaders.Authorization = Basic(StreamZhFactory.OwnerName, "wrong password");
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await repeating.GetAsync("/api/v1/system/status")).StatusCode);
        }

        var good = Browserless();
        good.DefaultRequestHeaders.Authorization = Basic(StreamZhFactory.OwnerName, StreamZhFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.OK, (await good.GetAsync("/api/v1/system/status")).StatusCode);

        // Guessing: ten different wrong passwords, then the account waits (also from another address).
        for (var i = 0; i < LoginThrottle.AccountLimit; i++)
        {
            var guess = Browserless();
            guess.DefaultRequestHeaders.Authorization = Basic(StreamZhFactory.OwnerName, $"guess {i}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await guess.GetAsync("/api/v1/system/status")).StatusCode);
        }

        var next = Browserless();
        next.DefaultRequestHeaders.Authorization = Basic(StreamZhFactory.OwnerName, "guess 99");
        var throttled = await next.GetAsync("/api/v1/system/status");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);

        _remote.Address = IPAddress.Parse("198.51.100.7");
        var elsewhere = Browserless();
        elsewhere.DefaultRequestHeaders.Authorization = Basic(StreamZhFactory.OwnerName, "guess 100");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await elsewhere.GetAsync("/api/v1/system/status")).StatusCode);

        // A browser that signed in before keeps working.
        Assert.Equal(HttpStatusCode.OK, (await good.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task Sign_in_page_counts_failures_per_account_across_addresses()
    {
        await OwnerAsync();
        for (var i = 0; i < LoginThrottle.AccountLimit; i++)
        {
            _remote.Address = IPAddress.Parse($"198.51.100.{i + 1}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(_app.CreateClient(), StreamZhFactory.OwnerName, "wrong password")).StatusCode);
        }

        _remote.Address = IPAddress.Parse("198.51.100.50");
        var throttled = await Login(_app.CreateClient(), StreamZhFactory.OwnerName, "another guess");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);

        // Other accounts are not affected.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(_app.CreateClient(), "janedoe", "wrong password")).StatusCode);
    }

    [Fact]
    public async Task Rate_limits_have_separate_buckets_and_count_ipv6_per_64()
    {
        await OwnerAsync();
        _remote.Address = IPAddress.Parse("2001:db8:1:2::10");
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(_app.CreateClient(), $"nobody{i}", "wrong password")).StatusCode);
        }

        // Another address of the same /64 shares the bucket, another /64 does not.
        _remote.Address = IPAddress.Parse("2001:db8:1:2::99");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(_app.CreateClient(), "nobody", "wrong password")).StatusCode);
        _remote.Address = IPAddress.Parse("2001:db8:1:3::1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(_app.CreateClient(), "nobody", "wrong password")).StatusCode);

        // Invitations have their own bucket.
        _remote.Address = IPAddress.Parse("2001:db8:1:2::10");
        var invite = await _app.CreateClient().PostAsJsonAsync("/api/v1/invites/unknown-token/accept", new { username = "x", password = "long enough password" });
        Assert.NotEqual(HttpStatusCode.TooManyRequests, invite.StatusCode);

        Assert.Equal(ClientAddress.Partition(IPAddress.Parse("2001:db8::1")), ClientAddress.Partition(IPAddress.Parse("2001:db8::ffff:1")));
        Assert.NotEqual(ClientAddress.Partition(IPAddress.Parse("2001:db8::1")), ClientAddress.Partition(IPAddress.Parse("2001:db8:0:1::1")));
        Assert.Equal("192.0.2.1", ClientAddress.Partition(IPAddress.Parse("::ffff:192.0.2.1")));
    }

    [Fact]
    public async Task Forwarded_client_address_and_https_are_believed_only_from_trusted_proxies()
    {
        var owner = await OwnerAsync();
        _factory.Services.GetRequiredService<UserRepository>().Create("janedoe", _factory.Services.GetRequiredService<PasswordService>().Hash("long enough password"), UserRole.Friend);

        // Not trusted: X-Forwarded-For changes nothing, all clients behind it share one bucket.
        _remote.Address = IPAddress.Parse("10.0.0.9");
        for (var i = 0; i < 10; i++)
        {
            var client = _app.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, $"nobody{i}", "wrong password")).StatusCode);
        }

        var shared = _app.CreateClient();
        shared.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.99");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(shared, "nobody", "wrong password")).StatusCode);

        var plain = _app.CreateClient();
        plain.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.98");
        plain.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        _remote.Address = IPAddress.Parse("10.0.0.10");
        var notSecure = await Login(plain, "janedoe", "long enough password");
        Assert.DoesNotContain("secure", Cookie(notSecure), StringComparison.OrdinalIgnoreCase);

        // Trusted: every client counts on its own, and HTTPS at the proxy makes the cookie Secure.
        await SaveAsync(owner, new { trustedProxies = "10.0.0.9, 10.0.0.10" });
        _remote.Address = IPAddress.Parse("10.0.0.9");
        for (var i = 0; i < 12; i++)
        {
            var client = _app.CreateClient();
            client.DefaultRequestHeaders.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, $"someone{i}", "wrong password")).StatusCode);
        }

        _remote.Address = IPAddress.Parse("10.0.0.10");
        var secure = await Login(plain, "janedoe", "long enough password");
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        Assert.Contains("secure", Cookie(secure), StringComparison.OrdinalIgnoreCase);
    }

    // ---- S1-10: cookies and sign-out ----

    [Fact]
    public async Task Cookie_is_for_the_browser_session_unless_remembered_and_sign_out_ends_it_on_the_server()
    {
        await OwnerAsync();
        _factory.Services.GetRequiredService<UserRepository>().Create("janedoe", _factory.Services.GetRequiredService<PasswordService>().Hash("long enough password"), UserRole.Friend);

        var session = await Login(_app.CreateClient(), "janedoe", "long enough password", rememberMe: false);
        var sessionCookie = Cookie(session);
        Assert.DoesNotContain("expires=", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/;", sessionCookie + ";", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", sessionCookie, StringComparison.OrdinalIgnoreCase);

        var browser = _app.CreateClient();
        var remembered = await Login(browser, "janedoe", "long enough password", rememberMe: true);
        Assert.Contains("expires=", Cookie(remembered), StringComparison.OrdinalIgnoreCase);

        // A copy of the cookie (stolen, or a second tab) works until the browser signs out.
        var copy = Browserless();
        copy.DefaultRequestHeaders.Add("Cookie", Cookie(remembered).Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await copy.GetAsync("/api/v1/system/status")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copy.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task Cookies_from_before_the_update_keep_working_and_get_a_session()
    {
        await OwnerAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var id = users.Create("janedoe", _factory.Services.GetRequiredService<PasswordService>().Hash("long enough password"), UserRole.Friend);

        // A 0.11 cookie: no session id claim.
        var options = _app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthSchemes.Cookie);
        var principal = UserClaims.Principal(users.FindById(id)!, AuthSchemes.Cookie);
        var old = options.TicketDataFormat.Protect(new AuthenticationTicket(principal, new AuthenticationProperties { IsPersistent = true, IssuedUtc = DateTimeOffset.UtcNow.AddDays(-20), ExpiresUtc = DateTimeOffset.UtcNow.AddDays(10) }, AuthSchemes.Cookie));

        var client = Browserless();
        client.DefaultRequestHeaders.Add("Cookie", $"StreamZH.Auth={old}");
        var response = await client.GetAsync("/api/v1/system/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("StreamZH.Auth=", Cookie(response), StringComparison.Ordinal);
        Assert.Equal(1, _factory.Services.GetRequiredService<SessionRepository>().Count(id));
    }

    // ---- S1-08: live connections ----

    [Fact]
    public async Task Live_connections_close_when_their_sign_in_ends()
    {
        var owner = await OwnerAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var id = users.Create("janedoe", _factory.Services.GetRequiredService<PasswordService>().Hash("long enough password"), UserRole.Friend);
        var store = _app.Services.GetRequiredService<SessionStore>();
        var connections = _app.Services.GetRequiredService<HubConnections>();
        await SaveAsync(owner, new { authenticationRequired = "disabledForLocalAddresses" });

        var revoked = new FakeHubContext(UserClaims.Principal(users.FindById(id)!, AuthSchemes.Cookie, store.Create(id, false)));
        var signedOut = new FakeHubContext(UserClaims.Principal(users.FindById(id)!, AuthSchemes.Cookie, store.Create(id, false)));
        var ownerId = (await owner.GetFromJsonAsync<AuthState>("/api/v1/auth/state"))!.User!.Id;
        var ownerSession = store.Create(ownerId, true);
        var still = new FakeHubContext(UserClaims.Principal(users.FindById(ownerId)!, AuthSchemes.Cookie, ownerSession));
        var local = new FakeHubContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "0"), new Claim(ClaimTypes.Role, "Owner")], AuthSchemes.Local)));
        foreach (var context in new[] { revoked, signedOut, still, local })
        {
            connections.Add(context);
        }

        // Signing out one session: closed at the next check (every 60 s).
        store.End(UserClaims.Session(signedOut.User!));
        Assert.Equal(1, connections.RevalidateAll());
        Assert.True(signedOut.Aborted);

        // The user is signed out everywhere by an admin: closed at once (no 60 s wait).
        Assert.True((await owner.PostAsync($"/api/v1/users/{id}/sessions/revoke", null)).IsSuccessStatusCode);
        Assert.True(revoked.Aborted);

        // Local access switched off: the sign-in-free connection closes.
        Assert.Equal(0, connections.RevalidateAll());
        await SaveAsync(owner, new { authenticationRequired = "enabled" });
        Assert.Equal(1, connections.RevalidateAll());
        Assert.True(local.Aborted);
        Assert.False(still.Aborted);
    }

    // ---- S1-11: security headers ----

    [Fact]
    public async Task Responses_carry_security_headers_and_svg_covers_are_sandboxed()
    {
        var owner = await OwnerAsync();
        var health = await _app.CreateClient().GetAsync("/health");
        Assert.Equal("DENY", health.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", health.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", health.Headers.GetValues("Referrer-Policy").Single());
        var policy = health.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self' ws: wss: http://localhost:* https://localhost:*", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", SecurityHeaders.ContentSecurityPolicy("evil;script-src *"), StringComparison.Ordinal);

        var noRedirect = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cookie = await Login(noRedirect, StreamZhFactory.OwnerName, StreamZhFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.OK, cookie.StatusCode);
        var svg = await noRedirect.GetAsync("/api/v1/mediacover/w342/abc.svg");
        Assert.Equal(SecurityHeaders.SandboxPolicy, svg.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", svg.Headers.GetValues("X-Content-Type-Options").Single());
        _ = owner;
    }

    // ---- S1-12: own password ----

    [Fact]
    public async Task Own_password_is_changed_with_the_current_one_not_reset()
    {
        var owner = await OwnerAsync();
        var me = (await owner.GetFromJsonAsync<AuthState>("/api/v1/auth/state"))!.User!.Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v1/users/{me}/password", new { password = "taken over password" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("/api/v1/me/password", new { currentPassword = StreamZhFactory.OwnerPassword, newPassword = "my new password 1" })).StatusCode);
    }

    // ---- S1-14 / S3-01: first-run setup ----

    [Fact]
    public async Task First_run_setup_from_another_device_needs_the_setup_code()
    {
        _remote.Address = IPAddress.Parse("192.168.1.30");
        var remote = _app.CreateClient();
        var state = await remote.GetFromJsonAsync<AuthState>("/api/v1/auth/state");
        Assert.True(state!.SetupRequired);
        Assert.True(state.SetupCodeRequired);

        var setupCode = _app.Services.GetRequiredService<SetupCode>();
        var code = setupCode.Current!;
        Assert.Equal(SetupCode.Display(code), File.ReadAllText(setupCode.FilePath).Trim());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(setupCode.FilePath));
        }

        // The tray icon on this computer shows it ("Show setup code").
        _remote.Address = IPAddress.Loopback;
        var tray = _app.CreateClient();
        tray.DefaultRequestHeaders.Add(TrayContract.Header, File.ReadAllText(Path.Combine(_factory.DataDirectory, TrayContract.TokenFile)));
        Assert.Equal(SetupCode.Display(code), (await tray.GetFromJsonAsync<TrayStatus>("/api/v1/tray/status"))!.SetupCode);

        _remote.Address = IPAddress.Parse("192.168.1.30");
        var missing = await remote.PostAsJsonAsync("/api/v1/auth/setup", new { username = "intruder", password = "12345678910" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("setupCode", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await remote.PostAsJsonAsync("/api/v1/auth/setup", new { username = "intruder", password = "12345678910", setupCode = "AAAA-BBBB-CCCC" })).StatusCode);
        Assert.True((await remote.GetFromJsonAsync<AuthState>("/api/v1/auth/state"))!.SetupRequired);

        // Typed with dashes or spaces and in lower case.
        var typed = SetupCode.Display(code).ToLowerInvariant().Replace('-', ' ');
        Assert.Equal(HttpStatusCode.OK, (await remote.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword, setupCode = typed })).StatusCode);
        Assert.False(File.Exists(setupCode.FilePath));
        Assert.Null(setupCode.Current);
        _remote.Address = IPAddress.Loopback;
        Assert.Null((await tray.GetFromJsonAsync<TrayStatus>("/api/v1/tray/status"))!.SetupCode);
    }

    [Fact]
    public async Task First_run_setup_on_this_computer_needs_no_code_but_through_a_proxy_it_does()
    {
        var proxied = _app.CreateClient();
        proxied.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");
        Assert.True((await proxied.GetFromJsonAsync<AuthState>("/api/v1/auth/state"))!.SetupCodeRequired);
        Assert.Equal(HttpStatusCode.BadRequest, (await proxied.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).StatusCode);

        var local = _app.CreateClient();
        Assert.False((await local.GetFromJsonAsync<AuthState>("/api/v1/auth/state"))!.SetupCodeRequired);
        Assert.Equal(HttpStatusCode.OK, (await local.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).StatusCode);
    }

    public void Dispose()
    {
        _app.Dispose();
        _factory.Dispose();
    }

    private async Task<HttpClient> OwnerAsync()
    {
        var client = _app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    private HttpClient Browserless()
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Remove(CrossSiteProtection.Header);
        return client;
    }

    private static async Task SaveAsync(HttpClient client, object change, bool header = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings/general") { Content = JsonContent.Create(change) };
        if (header)
        {
            request.Headers.Add(CrossSiteProtection.Header, "1");
        }

        var response = await client.SendAsync(request);
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

    private static Task<HttpResponseMessage> Login(HttpClient client, string username, string password, bool rememberMe = false) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { username, password, rememberMe });

    private static string Cookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(v => v.StartsWith("StreamZH.Auth=", StringComparison.Ordinal)) ?? string.Empty : string.Empty;

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

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

    private sealed class FakeHubContext(ClaimsPrincipal user) : HubCallerContext
    {
        public bool Aborted { get; private set; }

        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");

        public override string? UserIdentifier => null;

        public override ClaimsPrincipal? User => user;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() => Aborted = true;
    }
}
