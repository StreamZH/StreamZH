// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Host;

/// <summary>A StreamZH instance with its own temporary data directory.</summary>
public sealed class StreamZhFactory(ITmdbClient? tmdb = null) : WebApplicationFactory<Program>
{
    public const string OwnerName = "johndoe";
    public const string OwnerPassword = "correct horse battery";

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-host-{Guid.NewGuid():N}");

    public string ApiKey => Services.GetRequiredService<ConfigRepository>().Get(ConfigKeys.ApiKey)!;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("data", DataDirectory);
        if (tmdb is not null)
        {
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(tmdb)));
        }
    }

    /// <summary>Client that has completed the first-run setup and is logged in as Owner.</summary>
    public async Task<HttpClient> CreateOwnerClientAsync()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = OwnerName, password = OwnerPassword });
        response.EnsureSuccessStatusCode();
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