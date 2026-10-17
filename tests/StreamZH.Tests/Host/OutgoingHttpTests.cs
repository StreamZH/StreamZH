// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Indexers;
using StreamZH.Data;
using StreamZH.Host.General;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Host;

/// <summary>Settings → General → Proxy and certificate validation on the HttpClients from IHttpClientFactory.</summary>
public sealed class OutgoingHttpTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();

    [Fact]
    public async Task Named_and_typed_clients_go_through_the_proxy_live()
    {
        using var proxy = new FakeHttpProxy();
        var owner = await _factory.CreateOwnerClientAsync();
        var save = await owner.PutAsJsonAsync("/api/v1/settings/general", new
        {
            proxyEnabled = true,
            proxyType = "http",
            proxyHostname = "127.0.0.1",
            proxyPort = proxy.Port,
            proxyBypassLocalAddresses = false,
            proxyBypassFilter = "*.direct.example",
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var factory = _factory.Services.GetRequiredService<IHttpClientFactory>();

        // The indexer client brings its own SocketsHttpHandler; TMDB (typed), the update check and the default client use the factory's.
        foreach (var name in new[] { nameof(IIndexerClient), nameof(ITmdbClient), UpdateChecker.HttpClientName, string.Empty })
        {
            var body = await factory.CreateClient(name).GetStringAsync(new Uri($"http://indexer.example/api?t=caps&client={name}"));
            Assert.Equal("proxied", body);
            Assert.Contains($"GET http://indexer.example/api?t=caps&client={name} HTTP/1.1", proxy.RequestLines);
        }

        // Off again: no more requests reach the proxy (the system proxy decides, as before).
        await owner.PutAsJsonAsync("/api/v1/settings/general", new { proxyEnabled = false });
        var webProxy = _factory.Services.GetRequiredService<SettingsWebProxy>();
        Assert.Equal(HttpClient.DefaultProxy.GetProxy(new Uri("http://indexer.example/")), webProxy.GetProxy(new Uri("http://indexer.example/")));
    }

    [Fact]
    public async Task Bypass_filter_and_local_addresses_go_direct()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        await owner.PutAsJsonAsync("/api/v1/settings/general", new
        {
            proxyEnabled = true,
            proxyType = "socks5",
            proxyHostname = "proxy.example",
            proxyPort = 1080,
            proxyUsername = "john",
            proxyPassword = "secret",
            proxyBypassFilter = "*.direct.example, 203.0.113.*",
        });

        var proxy = _factory.Services.GetRequiredService<SettingsWebProxy>();
        Assert.Equal(new Uri("socks5://proxy.example:1080"), proxy.GetProxy(new Uri("https://api.themoviedb.org/3/")));
        Assert.Null(proxy.GetProxy(new Uri("https://www.direct.example/")));
        Assert.Null(proxy.GetProxy(new Uri("http://203.0.113.7/")));
        Assert.Null(proxy.GetProxy(new Uri("http://192.168.1.2:9696/")));
        Assert.Null(proxy.GetProxy(new Uri("http://nas:5076/")));
        Assert.Equal("john", proxy.Credentials!.GetCredential(new Uri("socks5://proxy.example:1080"), "Basic")!.UserName);

        var settings = await owner.GetFromJsonAsync<StreamZH.Host.Api.GeneralSettingsResource>("/api/v1/settings/general");
        Assert.True(settings!.ProxyPasswordSet);
        Assert.DoesNotContain("secret", await owner.GetStringAsync("/api/v1/settings/general"), StringComparison.Ordinal);

        await owner.PutAsJsonAsync("/api/v1/settings/general", new { proxyBypassLocalAddresses = false });
        Assert.Equal(new Uri("socks5://proxy.example:1080"), proxy.GetProxy(new Uri("http://192.168.1.2:9696/")));
    }

    [Fact]
    public async Task Proxy_needs_a_host_name()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var response = await owner.PutAsJsonAsync("/api/v1/settings/general", new { proxyEnabled = true, proxyHostname = "http://proxy:8080" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("proxyHostname", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        response = await owner.PutAsJsonAsync("/api/v1/settings/general", new { proxyEnabled = true, proxyHostname = "proxy", proxyType = "ftp", proxyBypassFilter = "a b" });
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("proxyType", text, StringComparison.Ordinal);
        Assert.Contains("proxyBypassFilter", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Certificate_validation_follows_the_setting()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var validator = _factory.Services.GetRequiredService<CertificateValidator>();

        Assert.True(validator.Validate("api.example.com", SslPolicyErrors.None));
        Assert.False(validator.Validate("nas", SslPolicyErrors.RemoteCertificateChainErrors));

        await owner.PutAsJsonAsync("/api/v1/settings/general", new { certificateValidation = "disabledForLocalAddresses" });
        Assert.True(validator.Validate("nas", SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(validator.Validate("192.168.1.10", SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(validator.Validate("api.example.com", SslPolicyErrors.RemoteCertificateChainErrors));

        await owner.PutAsJsonAsync("/api/v1/settings/general", new { certificateValidation = "disabled" });
        Assert.True(validator.Validate("api.example.com", SslPolicyErrors.RemoteCertificateChainErrors));

        // The filter puts the check on socket handlers and leaves a client's own proxy choice alone.
        var filter = _factory.Services.GetServices<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter>().OfType<OutgoingHttpFilter>().Single();
        var handler = new SocketsHttpHandler();
        filter.Apply(handler);
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.IsType<SettingsWebProxy>(handler.Proxy);

        var direct = new SocketsHttpHandler { UseProxy = false };
        filter.Apply(direct);
        Assert.Null(direct.Proxy);
        handler.Dispose();
        direct.Dispose();
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>A minimal HTTP proxy: records the request line, answers "proxied".</summary>
    private sealed class FakeHttpProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeHttpProxy()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentBag<string> RequestLines { get; } = [];

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var first = await reader.ReadLineAsync();
                        while (await reader.ReadLineAsync() is { Length: > 0 })
                        {
                        }

                        if (first is not null)
                        {
                            RequestLines.Add(first);
                        }

                        var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nConnection: close\r\n\r\nproxied");
                        await stream.WriteAsync(response);
                        await stream.FlushAsync();
                    }
                });
            }
        }
    }
}
