// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Data;
using StreamZH.Library.PostProcessing;
using StreamZH.Library.SceneNumbering;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Library.SceneNumbering;
using StreamZH.Tests.PostProcessing;

namespace StreamZH.Tests.Host;

/// <summary>A StreamZH instance with its own temporary data directory.</summary>
public sealed class StreamZhFactory(ITmdbClient? tmdb = null) : WebApplicationFactory<Program>
{
    public const string OwnerName = "johndoe";
    public const string OwnerPassword = "correct horse battery";

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-host-{Guid.NewGuid():N}");

    public string ApiKey => Services.GetRequiredService<ConfigRepository>().Get(ConfigKeys.ApiKey)!;

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

    /// <summary>Client that has completed the first-run setup and is logged in as Owner.</summary>
    public async Task<HttpClient> CreateOwnerClientAsync()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = OwnerName, password = OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Client signed in as a new account with this role (the owner must exist already, e.g. via <see cref="CreateOwnerClientAsync"/>).</summary>
    public async Task<HttpClient> CreateUserClientAsync(string username, StreamZH.Core.Users.UserRole role)
    {
        const string password = "a long test password";
        var passwords = Services.GetRequiredService<StreamZH.Host.Auth.PasswordService>();
        Services.GetRequiredService<UserRepository>().Create(username, passwords.Hash(password), role);
        var client = CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password })).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Client that sends the API key (X-Api-Key) and nothing else.</summary>
    public HttpClient CreateApiKeyClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
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