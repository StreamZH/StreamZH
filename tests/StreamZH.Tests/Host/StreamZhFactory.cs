// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Data;
using StreamZH.Host.Security;
using StreamZH.Library.PostProcessing;
using StreamZH.Library.SceneNumbering;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Library.SceneNumbering;
using StreamZH.Tests.PostProcessing;

namespace StreamZH.Tests.Host;

/// <summary>
/// A StreamZH instance with its own temporary data directory. Its clients behave like the web app (0.12): they send
/// X-StreamZH-Request (cross-site protection) and come from this computer (127.0.0.1, the test server leaves the remote
/// address empty), so first-run setup works without the setup code. <see cref="CreateBrowserlessClient"/> sends no header.
/// </summary>
public sealed class StreamZhFactory(ITmdbClient? tmdb = null) : WebApplicationFactory<Program>
{
    public const string OwnerName = "johndoe";
    public const string OwnerPassword = "correct horse battery";

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-host-{Guid.NewGuid():N}");

    public string ApiKey => Services.GetRequiredService<ConfigRepository>().Get(ConfigKeys.ApiKey)!;

    /// <summary>The one-time setup code (null once the owner exists).</summary>
    public string? SetupCode => Services.GetRequiredService<SetupCode>().Current;

    /// <summary>Answers the tool downloads (par2, 7-Zip); unknown URLs get 404, so tests never reach GitHub.</summary>
    public FakeToolDownloads ToolDownloads { get; } = new();

    /// <summary>Answers TheXEM requests (no mappings unless a test adds some), so tests never reach thexem.info.</summary>
    public FakeXem Xem { get; } = new();

    /// <summary>The packages the tool installer uses; null = the official ones.</summary>
    public ToolCatalog? ToolCatalog { get; set; }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("data", DataDirectory);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new LoopbackWhenEmpty());
            if (tmdb is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(tmdb));
            }

            // No automatic installation at startup; downloads go to the fake.
            services.Replace(ServiceDescriptor.Singleton(new ToolAutoInstallOptions(TimeSpan.Zero, Enabled: false)));
            services.AddHttpClient(ToolInstaller.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ToolDownloads);
            services.AddHttpClient(XemClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Xem);
            if (ToolCatalog is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(ToolCatalog));
            }
        });
    }

    /// <summary>Like the web app: every request carries X-StreamZH-Request.</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(CrossSiteProtection.Header, "1");
    }

    /// <summary>A client without the web app's header (other sites, scripts): for the cross-site protection tests.</summary>
    public HttpClient CreateBrowserlessClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Remove(CrossSiteProtection.Header);
        return client;
    }

    /// <summary>Client that has completed the first-run setup and is logged in as Owner.</summary>
    public async Task<HttpClient> CreateOwnerClientAsync()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = OwnerName, password = OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>The test server sets no remote address; requests count as coming from this computer unless a test sets one.</summary>
    private sealed class LoopbackWhenEmpty : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, Func<Task> nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                context.Connection.LocalIpAddress ??= IPAddress.Loopback;
                return nextMiddleware();
            });
            next(app);
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}