// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;

namespace StreamZH.Tests.Host;

/// <summary>Settings → General → Security: Forms, Basic, External and "Disabled for local addresses".</summary>
public sealed class AuthMethodTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();
    private readonly RemoteAddress _remote = new();
    private readonly WebApplicationFactory<Program> _app;

    public AuthMethodTests()
    {
        _app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(_remote)));
    }

    [Fact]
    public async Task Disabled_for_local_addresses_lets_the_lan_in_without_sign_in_but_not_the_internet()
    {
        var owner = await SetupAsync();
        await SaveAsync(owner, new { authenticationRequired = "disabledForLocalAddresses" });

        _remote.Address = IPAddress.Parse("192.168.1.20");
        var lan = _app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("/api/v1/system/status")).StatusCode);
        var state = await lan.GetFromJsonAsync<AuthState>("/api/v1/auth/state");
        Assert.True(state!.Authenticated);
        Assert.Equal("local", state.User!.Username);
        Assert.Equal("Owner", state.User.Role);
        Assert.False(state.CanSignOut);

        // A reverse proxy on the same machine: the forwarded client decides.
        _remote.Address = IPAddress.Loopback;
        var proxied = _app.CreateClient();
        proxied.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9, 10.0.0.2");
        Assert.Equal(HttpStatusCode.Unauthorized, (await proxied.GetAsync("/api/v1/system/status")).StatusCode);

        _remote.Address = IPAddress.Parse("203.0.113.9");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateClient().GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task Enabled_requires_sign_in_from_local_addresses_too()
    {
        await SetupAsync();
        _remote.Address = IPAddress.Parse("192.168.1.20");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateClient().GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task Basic_asks_the_browser_for_credentials_on_every_page()
    {
        var owner = await SetupAsync();
        Assert.True((await owner.GetFromJsonAsync<AuthState>("/api/v1/auth/state"))!.CanSignOut);
        await SaveAsync(owner, new { authenticationMethod = "basic" });

        var anonymous = _app.CreateClient();
        foreach (var path in new[] { "/", "/settings/general", "/api/v1/system/status" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.StartsWith("Basic realm=\"StreamZH\"", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);

        var wrong = _app.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = Basic(StreamZhFactory.OwnerName, "wrong password");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/v1/system/status")).StatusCode);

        var browser = _app.CreateClient();
        browser.DefaultRequestHeaders.Authorization = Basic(StreamZhFactory.OwnerName, StreamZhFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/system/status")).StatusCode);
        var state = await browser.GetFromJsonAsync<AuthState>("/api/v1/auth/state");
        Assert.Equal(StreamZhFactory.OwnerName, state!.User!.Username);
        Assert.False(state.CanSignOut);

        // The API key still works without Basic credentials.
        var script = _app.CreateClient();
        script.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task External_leaves_sign_in_to_the_reverse_proxy_and_warns()
    {
        var owner = await SetupAsync();
        await SaveAsync(owner, new { authenticationMethod = "external" });

        // A reverse proxy in the local network (no trusted proxies set: local addresses count, 0.12).
        _remote.Address = IPAddress.Parse("10.0.0.2");
        var proxied = _app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await proxied.GetAsync("/api/v1/settings/general")).StatusCode);
        var state = await proxied.GetFromJsonAsync<AuthState>("/api/v1/auth/state");
        Assert.Equal("external", state!.User!.Username);

        var ongoing = await proxied.GetStringAsync("/api/v1/status/ongoing");
        Assert.Contains("auth-external", ongoing, StringComparison.Ordinal);

        // 0.12 (security review S1-01): straight from the internet, past the proxy, nobody is let in.
        _remote.Address = IPAddress.Parse("203.0.113.9");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateClient().GetAsync("/api/v1/settings/general")).StatusCode);
    }

    [Fact]
    public async Task Command_line_auth_method_overrides_the_stored_one()
    {
        var owner = await SetupAsync();
        await SaveAsync(owner, new { authenticationMethod = "external" });

        using var forced = _factory.WithWebHostBuilder(b => b.UseSetting("auth-method", "forms"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await forced.CreateClient().GetAsync("/api/v1/system/status")).StatusCode);
        var api = forced.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        var settings = await api.GetFromJsonAsync<GeneralSettingsResource>("/api/v1/settings/general");
        Assert.Equal("external", settings!.AuthenticationMethod);
        Assert.Equal("forms", settings.AuthenticationMethodOverride);
    }

    [Fact]
    public async Task Friends_cannot_change_security_settings()
    {
        await SetupAsync();
        var users = _app.Services.GetRequiredService<StreamZH.Data.UserRepository>();
        var passwords = _app.Services.GetRequiredService<PasswordService>();
        users.Create("friend", passwords.Hash("friend password 1"), StreamZH.Core.Users.UserRole.Friend);

        var friend = _app.CreateClient();
        await friend.PostAsJsonAsync("/api/v1/auth/login", new { username = "friend", password = "friend password 1", rememberMe = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PutAsJsonAsync("/api/v1/settings/general", new { authenticationMethod = "external" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/settings/ui")).StatusCode);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.10", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    public void Local_addresses(string address, bool local) => Assert.Equal(local, LocalAddress.IsLocal(IPAddress.Parse(address)));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("nas", true)]
    [InlineData("nas.local", true)]
    [InlineData("192.168.1.5", true)]
    [InlineData("[::1]", true)]
    [InlineData("api.nzbgeek.info", false)]
    [InlineData("8.8.8.8", false)]
    public void Local_host_names(string host, bool local) => Assert.Equal(local, LocalAddress.IsLocalHost(host));

    public void Dispose()
    {
        _app.Dispose();
        _factory.Dispose();
    }

    private string ApiKey => _app.Services.GetRequiredService<StreamZH.Data.ConfigRepository>().Get(StreamZH.Data.ConfigKeys.ApiKey)!;

    private async Task<HttpClient> SetupAsync()
    {
        // The test server's default remote address is not this computer: setup needs the setup code (0.12).
        var client = _app.CreateClient();
        var code = _app.Services.GetRequiredService<StreamZH.Host.Security.SetupCode>().Current;
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword, setupCode = code });
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task SaveAsync(HttpClient client, object change)
    {
        var response = await client.PutAsJsonAsync("/api/v1/settings/general", change);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    /// <summary>Sets the remote address the test server leaves empty.</summary>
    private sealed class RemoteAddress : IStartupFilter
    {
        public IPAddress Address { get; set; } = IPAddress.Parse("203.0.113.1");

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, Func<Task> nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = Address;
                return nextMiddleware();
            });
            next(app);
        };
    }
}
